using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing
{
    /// <summary>
    /// Builds an exact boolean predicate tree for WHERE/HAVING clauses while also
    /// producing stable leaf ConditionInfo entries that downstream components can target.
    /// </summary>
    public class PredicateTreeBuilder
    {
        private int _nextConditionOrdinal = 1;

        public PredicateScope BuildScope(
            BooleanExpression? expression,
            ConditionSource source,
            string scopeId,
            string scopeLabel)
        {
            var scope = new PredicateScope
            {
                ScopeId = scopeId,
                ScopeLabel = scopeLabel,
                Source = source
            };

            if (expression == null)
                return scope;

            scope.Root = BuildExpression(expression, scope);
            return scope;
        }

        private PredicateExpression BuildExpression(BooleanExpression expression, PredicateScope scope)
        {
            return expression switch
            {
                BooleanBinaryExpression binary => new PredicateBinaryExpression
                {
                    Operator = binary.BinaryExpressionType == BooleanBinaryExpressionType.And
                        ? LogicalOp.And
                        : LogicalOp.Or,
                    Left = BuildExpression(binary.FirstExpression, scope),
                    Right = BuildExpression(binary.SecondExpression, scope),
                    Text = GetFragmentText(binary)
                },
                BooleanParenthesisExpression paren => BuildExpression(paren.Expression, scope),
                BooleanNotExpression notExpr => BuildNotExpression(notExpr, scope),
                _ => CreateLeafExpression(expression, scope)
            };
        }

        private PredicateExpression BuildNotExpression(BooleanNotExpression notExpr, PredicateScope scope)
        {
            if (notExpr.Expression is ExistsPredicate existsPred)
            {
                return CreateLeafExpression(
                    BuildExistsCondition(existsPred, scope, ComparisonOp.NotExists, GetFragmentText(existsPred.Subquery)),
                    GetFragmentText(notExpr),
                    scope);
            }

            if (notExpr.Expression is InPredicate inPred && inPred.Subquery != null)
            {
                return CreateLeafExpression(
                    BuildInCondition(inPred, scope, forceOperator: ComparisonOp.NotIn, subquerySql: GetFragmentText(inPred.Subquery)),
                    GetFragmentText(notExpr),
                    scope);
            }

            return new PredicateNotExpression
            {
                Inner = BuildExpression(notExpr.Expression, scope),
                Text = GetFragmentText(notExpr)
            };
        }

        private PredicateExpression CreateLeafExpression(BooleanExpression node, PredicateScope scope)
        {
            var condition = node switch
            {
                BooleanComparisonExpression comparison => BuildComparisonCondition(comparison, scope),
                InPredicate inPredicate => BuildInCondition(inPredicate, scope, forceOperator: null, subquerySql: inPredicate.Subquery != null ? GetFragmentText(inPredicate.Subquery) : string.Empty),
                BooleanTernaryExpression ternary when ternary.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween
                    => BuildBetweenCondition(ternary, scope),
                LikePredicate like => BuildLikeCondition(like, scope),
                BooleanIsNullExpression isNull => BuildNullCondition(isNull, scope),
                ExistsPredicate exists => BuildExistsCondition(exists, scope, ComparisonOp.Exists, GetFragmentText(exists.Subquery)),
                _ => BuildFallbackCondition(node, scope)
            };

            return CreateLeafExpression(condition, GetFragmentText(node), scope);
        }

        private PredicateExpression BuildDetachedPredicateExpression(BooleanExpression expression)
        {
            return expression switch
            {
                BooleanBinaryExpression binary => new PredicateBinaryExpression
                {
                    Operator = binary.BinaryExpressionType == BooleanBinaryExpressionType.And
                        ? LogicalOp.And
                        : LogicalOp.Or,
                    Left = BuildDetachedPredicateExpression(binary.FirstExpression),
                    Right = BuildDetachedPredicateExpression(binary.SecondExpression),
                    Text = GetFragmentText(binary)
                },
                BooleanParenthesisExpression paren => BuildDetachedPredicateExpression(paren.Expression),
                BooleanNotExpression notExpr => BuildDetachedNotExpression(notExpr),
                _ => CreateDetachedLeafExpression(expression)
            };
        }

        private PredicateExpression BuildDetachedNotExpression(BooleanNotExpression notExpr)
        {
            if (notExpr.Expression is ExistsPredicate existsPred)
            {
                return CreateDetachedLeafExpression(
                    BuildExistsCondition(existsPred, new PredicateScope(), ComparisonOp.NotExists, GetFragmentText(existsPred.Subquery)),
                    GetFragmentText(notExpr));
            }

            if (notExpr.Expression is InPredicate inPred && inPred.Subquery != null)
            {
                return CreateDetachedLeafExpression(
                    BuildInCondition(inPred, new PredicateScope(), forceOperator: ComparisonOp.NotIn, subquerySql: GetFragmentText(inPred.Subquery)),
                    GetFragmentText(notExpr));
            }

            return new PredicateNotExpression
            {
                Inner = BuildDetachedPredicateExpression(notExpr.Expression),
                Text = GetFragmentText(notExpr)
            };
        }

        private PredicateLeafExpression CreateDetachedLeafExpression(BooleanExpression node)
        {
            var condition = node switch
            {
                BooleanComparisonExpression comparison => BuildComparisonCondition(comparison, new PredicateScope()),
                InPredicate inPredicate => BuildInCondition(inPredicate, new PredicateScope(), forceOperator: null, subquerySql: inPredicate.Subquery != null ? GetFragmentText(inPredicate.Subquery) : string.Empty),
                BooleanTernaryExpression ternary when ternary.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween
                    => BuildBetweenCondition(ternary, new PredicateScope()),
                LikePredicate like => BuildLikeCondition(like, new PredicateScope()),
                BooleanIsNullExpression isNull => BuildNullCondition(isNull, new PredicateScope()),
                ExistsPredicate exists => BuildExistsCondition(exists, new PredicateScope(), ComparisonOp.Exists, GetFragmentText(exists.Subquery)),
                _ => BuildFallbackCondition(node, new PredicateScope())
            };

            return CreateDetachedLeafExpression(condition, GetFragmentText(node));
        }

        private static PredicateLeafExpression CreateDetachedLeafExpression(ConditionInfo condition, string text)
        {
            condition.ExpressionText = string.IsNullOrWhiteSpace(text) ? condition.ExpressionText : text;
            return new PredicateLeafExpression
            {
                Condition = condition,
                Text = condition.ExpressionText
            };
        }

        private PredicateLeafExpression CreateLeafExpression(ConditionInfo condition, string text, PredicateScope scope)
        {
            condition.ScopeId = scope.ScopeId;
            condition.ScopeLabel = scope.ScopeLabel;
            condition.Source = scope.Source;
            condition.Key = $"{scope.ScopeId}:p{_nextConditionOrdinal++}";
            condition.ExpressionText = string.IsNullOrWhiteSpace(text) ? condition.ExpressionText : text;
            scope.Conditions.Add(condition);

            return new PredicateLeafExpression
            {
                Condition = condition,
                Text = condition.ExpressionText
            };
        }

        private ConditionInfo BuildComparisonCondition(BooleanComparisonExpression node, PredicateScope scope)
        {
            var condition = new ConditionInfo
            {
                Operator = ConvertOperator(node.ComparisonType),
                LeftExpression = BuildScalarExpression(node.FirstExpression),
                RightExpression = BuildScalarExpression(node.SecondExpression)
            };

            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
            CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);

            if (IsColumnReference(node.SecondExpression))
            {
                ExtractColumnReference(node.SecondExpression, condition, isLeft: false);
                condition.IsColumnComparison = true;
            }
            else
            {
                condition.Value = ExtractLiteralValue(node.SecondExpression);
            }

            CollectReferencedColumns(node.SecondExpression, condition, isRightSide: true);

            return condition;
        }

        private ConditionInfo BuildInCondition(
            InPredicate node,
            PredicateScope scope,
            ComparisonOp? forceOperator,
            string subquerySql)
        {
            var op = forceOperator ?? (node.NotDefined ? ComparisonOp.NotIn : ComparisonOp.In);
            var condition = new ConditionInfo
            {
                Operator = op,
                IsNegated = op == ComparisonOp.NotIn,
                HasSubquery = node.Subquery != null,
                SubquerySql = subquerySql,
                LeftExpression = BuildScalarExpression(node.Expression)
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);
            CollectReferencedColumns(node.Expression, condition, isRightSide: false);

            if (node.Subquery == null)
            {
                foreach (var value in node.Values)
                {
                    condition.InValues.Add(ExtractLiteralValue(value));
                }
            }

            return condition;
        }

        private ConditionInfo BuildBetweenCondition(BooleanTernaryExpression node, PredicateScope scope)
        {
            var condition = new ConditionInfo
            {
                Operator = ComparisonOp.Between,
                IsNegated = node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween,
                LeftExpression = BuildScalarExpression(node.FirstExpression),
                RightExpression = BuildScalarExpression(node.SecondExpression)
            };

            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
            CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);
            condition.Value = ExtractLiteralValue(node.SecondExpression);
            condition.SecondValue = ExtractLiteralValue(node.ThirdExpression);
            return condition;
        }

        private ConditionInfo BuildLikeCondition(LikePredicate node, PredicateScope scope)
        {
            var condition = new ConditionInfo
            {
                Operator = ComparisonOp.Like,
                IsNegated = node.NotDefined,
                LeftExpression = BuildScalarExpression(node.FirstExpression),
                RightExpression = BuildScalarExpression(node.SecondExpression)
            };

            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
            CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);
            CollectReferencedColumns(node.SecondExpression, condition, isRightSide: true);
            condition.LikePattern = ExtractLiteralValue(node.SecondExpression);
            condition.Value = condition.LikePattern;
            return condition;
        }

        private ConditionInfo BuildNullCondition(BooleanIsNullExpression node, PredicateScope scope)
        {
            var condition = new ConditionInfo
            {
                Operator = node.IsNot ? ComparisonOp.IsNotNull : ComparisonOp.IsNull,
                IsNegated = node.IsNot,
                LeftExpression = BuildScalarExpression(node.Expression)
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);
            CollectReferencedColumns(node.Expression, condition, isRightSide: false);
            return condition;
        }

        private ConditionInfo BuildExistsCondition(
            ExistsPredicate node,
            PredicateScope scope,
            ComparisonOp op,
            string subquerySql)
        {
            return new ConditionInfo
            {
                Operator = op,
                IsNegated = op == ComparisonOp.NotExists,
                HasSubquery = true,
                SubquerySql = subquerySql
            };
        }

        private ConditionInfo BuildFallbackCondition(BooleanExpression node, PredicateScope scope)
        {
            return new ConditionInfo
            {
                Operator = ComparisonOp.Equal,
                Value = "1",
                ExpressionText = GetFragmentText(node)
            };
        }

        private static bool IsColumnReference(ScalarExpression expr)
        {
            return expr is ColumnReferenceExpression colRef && !IsCurrentDateTimeKeyword(colRef);
        }

        private void ExtractColumnReference(ScalarExpression expr, ConditionInfo condition, bool isLeft)
        {
            if (expr is ColumnReferenceExpression colRef)
            {
                if (IsCurrentDateTimeKeyword(colRef))
                    return;

                var parts = colRef.MultiPartIdentifier?.Identifiers;
                if (parts != null)
                {
                    if (isLeft)
                    {
                        if (parts.Count >= 2)
                        {
                            condition.TableAlias = parts[0].Value;
                            condition.ColumnName = parts[1].Value;
                        }
                        else if (parts.Count == 1)
                        {
                            condition.ColumnName = parts[0].Value;
                        }
                    }
                    else
                    {
                        if (parts.Count >= 2)
                        {
                            condition.RightTableAlias = parts[0].Value;
                            condition.RightColumnName = parts[1].Value;
                        }
                        else if (parts.Count == 1)
                        {
                            condition.RightColumnName = parts[0].Value;
                        }
                    }
                }

                return;
            }

            if (expr is FunctionCall funcCall)
            {
                var aggFunc = ParseAggregateFunction(funcCall.FunctionName?.Value ?? string.Empty);
                if (aggFunc.HasValue)
                {
                    condition.AggregateFunc = aggFunc;
                }

                foreach (var parameter in GetColumnBearingFunctionParameters(funcCall))
                {
                    ExtractColumnReference(parameter, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                }

                return;
            }

            if (expr is NullIfExpression nullIf)
            {
                ExtractColumnReference(nullIf.FirstExpression, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                ExtractColumnReference(nullIf.SecondExpression, condition, isLeft);
                return;
            }

            if (expr is CastCall castCall)
            {
                ExtractColumnReference(castCall.Parameter, condition, isLeft);
                return;
            }

            if (expr is TryCastCall tryCastCall)
            {
                ExtractColumnReference(tryCastCall.Parameter, condition, isLeft);
                return;
            }

            if (expr is ConvertCall convertCall)
            {
                ExtractColumnReference(convertCall.Parameter, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                if (convertCall.Style != null)
                {
                    ExtractColumnReference(convertCall.Style, condition, isLeft);
                }
                return;
            }

            if (expr is TryConvertCall tryConvertCall)
            {
                ExtractColumnReference(tryConvertCall.Parameter, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                if (tryConvertCall.Style != null)
                {
                    ExtractColumnReference(tryConvertCall.Style, condition, isLeft);
                }
                return;
            }

            if (expr is ParseCall parseCall)
            {
                ExtractColumnReference(parseCall.StringValue, condition, isLeft);
                return;
            }

            if (expr is TryParseCall tryParseCall)
            {
                ExtractColumnReference(tryParseCall.StringValue, condition, isLeft);
                return;
            }

            if (expr is SearchedCaseExpression searchedCase)
            {
                foreach (var whenClause in searchedCase.WhenClauses)
                {
                    ExtractColumnReference(whenClause.WhenExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;

                    ExtractColumnReference(whenClause.ThenExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                }

                if (searchedCase.ElseExpression != null)
                {
                    ExtractColumnReference(searchedCase.ElseExpression, condition, isLeft);
                }
                return;
            }

            if (expr is SimpleCaseExpression simpleCase)
            {
                ExtractColumnReference(simpleCase.InputExpression, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                foreach (var whenClause in simpleCase.WhenClauses)
                {
                    ExtractColumnReference(whenClause.WhenExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;

                    ExtractColumnReference(whenClause.ThenExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                }

                if (simpleCase.ElseExpression != null)
                {
                    ExtractColumnReference(simpleCase.ElseExpression, condition, isLeft);
                }
                return;
            }

            if (expr is BinaryExpression binary)
            {
                if (isLeft)
                {
                    condition.ExpressionText = GetFragmentText(expr);
                }

                ExtractColumnReference(binary.FirstExpression, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                ExtractColumnReference(binary.SecondExpression, condition, isLeft);
                return;
            }

            if (expr is ParenthesisExpression paren)
            {
                ExtractColumnReference(paren.Expression, condition, isLeft);
                return;
            }

            if (expr is UnaryExpression unary)
            {
                ExtractColumnReference(unary.Expression, condition, isLeft);
                return;
            }

            if (TryGetFirstColumnReference(expr, out var fallbackColumn))
            {
                AssignConditionColumnReference(fallbackColumn, condition, isLeft);
            }
        }

        private void ExtractColumnReference(BooleanExpression expr, ConditionInfo condition, bool isLeft)
        {
            switch (expr)
            {
                case BooleanComparisonExpression comparison:
                    ExtractColumnReference(comparison.FirstExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    ExtractColumnReference(comparison.SecondExpression, condition, isLeft);
                    return;

                case InPredicate inPredicate:
                    ExtractColumnReference(inPredicate.Expression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    foreach (var value in inPredicate.Values)
                    {
                        ExtractColumnReference(value, condition, isLeft);
                        if (HasResolvedConditionColumn(condition, isLeft))
                            return;
                    }
                    return;

                case BooleanTernaryExpression ternary:
                    ExtractColumnReference(ternary.FirstExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    ExtractColumnReference(ternary.SecondExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    ExtractColumnReference(ternary.ThirdExpression, condition, isLeft);
                    return;

                case LikePredicate like:
                    ExtractColumnReference(like.FirstExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    ExtractColumnReference(like.SecondExpression, condition, isLeft);
                    return;

                case BooleanIsNullExpression isNull:
                    ExtractColumnReference(isNull.Expression, condition, isLeft);
                    return;

                case BooleanBinaryExpression binary:
                    ExtractColumnReference(binary.FirstExpression, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
                    ExtractColumnReference(binary.SecondExpression, condition, isLeft);
                    return;

                case BooleanParenthesisExpression paren:
                    ExtractColumnReference(paren.Expression, condition, isLeft);
                    return;

                case BooleanNotExpression not:
                    ExtractColumnReference(not.Expression, condition, isLeft);
                    return;
            }
        }

        private static bool HasResolvedConditionColumn(ConditionInfo condition, bool isLeft)
        {
            return isLeft
                ? !string.IsNullOrWhiteSpace(condition.ColumnName)
                : !string.IsNullOrWhiteSpace(condition.RightColumnName);
        }

        private ScalarExpressionInfo? BuildScalarExpression(ScalarExpression? expr)
        {
            if (expr == null)
                return null;

            if (TryBuildKnownTextualFunctionExpression(expr, out var textualFunction))
                return textualFunction;

            return expr switch
            {
                ParameterlessCall parameterless => new FunctionScalarExpressionInfo
                {
                    Name = parameterless.ParameterlessCallType.ToString(),
                    Text = GetFragmentText(parameterless)
                },
                ColumnReferenceExpression colRef when IsCurrentDateTimeKeyword(colRef) => new FunctionScalarExpressionInfo
                {
                    Name = GetCurrentDateTimeKeyword(colRef),
                    Text = GetFragmentText(colRef)
                },
                ColumnReferenceExpression colRef => BuildColumnExpression(colRef),
                StringLiteral str => new LiteralScalarExpressionInfo
                {
                    Value = str.Value,
                    Kind = ScalarLiteralKind.String,
                    Text = GetFragmentText(str)
                },
                IntegerLiteral integer => new LiteralScalarExpressionInfo
                {
                    Value = integer.Value,
                    Kind = ScalarLiteralKind.Integer,
                    Text = GetFragmentText(integer)
                },
                NumericLiteral numeric => new LiteralScalarExpressionInfo
                {
                    Value = numeric.Value,
                    Kind = ScalarLiteralKind.Numeric,
                    Text = GetFragmentText(numeric)
                },
                RealLiteral real => new LiteralScalarExpressionInfo
                {
                    Value = real.Value,
                    Kind = ScalarLiteralKind.Real,
                    Text = GetFragmentText(real)
                },
                MoneyLiteral money => new LiteralScalarExpressionInfo
                {
                    Value = money.Value,
                    Kind = ScalarLiteralKind.Money,
                    Text = GetFragmentText(money)
                },
                NullLiteral => new LiteralScalarExpressionInfo
                {
                    Value = string.Empty,
                    Kind = ScalarLiteralKind.Null,
                    Text = "NULL"
                },
                DefaultLiteral => new LiteralScalarExpressionInfo
                {
                    Value = "DEFAULT",
                    Kind = ScalarLiteralKind.Default,
                    Text = "DEFAULT"
                },
                VariableReference variable => new LiteralScalarExpressionInfo
                {
                    Value = $"@{variable.Name}",
                    Kind = ScalarLiteralKind.Variable,
                    Text = GetFragmentText(variable)
                },
                ParenthesisExpression paren => BuildScalarExpression(paren.Expression),
                UnaryExpression unary => new UnaryScalarExpressionInfo
                {
                    Operator = unary.UnaryExpressionType.ToString(),
                    Operand = BuildScalarExpression(unary.Expression),
                    Text = GetFragmentText(unary)
                },
                FunctionCall func => new FunctionScalarExpressionInfo
                {
                    Name = func.FunctionName?.Value ?? string.Empty,
                    Arguments = func.Parameters.Select(BuildScalarExpression).Where(p => p != null).Cast<ScalarExpressionInfo>().ToList(),
                    Text = GetFragmentText(func)
                },
                NullIfExpression nullIf => new FunctionScalarExpressionInfo
                {
                    Name = "NULLIF",
                    Arguments = new List<ScalarExpressionInfo?>
                    {
                        BuildScalarExpression(nullIf.FirstExpression),
                        BuildScalarExpression(nullIf.SecondExpression)
                    }
                    .Where(a => a != null)
                    .Cast<ScalarExpressionInfo>()
                    .ToList(),
                    Text = GetFragmentText(nullIf)
                },
                CastCall cast => new FunctionScalarExpressionInfo
                {
                    Name = "CAST",
                    Arguments = BuildConvertArguments(cast.DataType, cast.Parameter, style: null),
                    Text = GetFragmentText(cast)
                },
                TryCastCall tryCast => new FunctionScalarExpressionInfo
                {
                    Name = "TRY_CAST",
                    Arguments = BuildConvertArguments(tryCast.DataType, tryCast.Parameter, style: null),
                    Text = GetFragmentText(tryCast)
                },
                ConvertCall convert => new FunctionScalarExpressionInfo
                {
                    Name = "CONVERT",
                    Arguments = BuildConvertArguments(convert.DataType, convert.Parameter, convert.Style),
                    Text = GetFragmentText(convert)
                },
                TryConvertCall tryConvert => new FunctionScalarExpressionInfo
                {
                    Name = "TRY_CONVERT",
                    Arguments = BuildConvertArguments(tryConvert.DataType, tryConvert.Parameter, tryConvert.Style),
                    Text = GetFragmentText(tryConvert)
                },
                ParseCall parse => new FunctionScalarExpressionInfo
                {
                    Name = "PARSE",
                    Arguments = BuildParseArguments(parse.DataType, parse.StringValue, parse.Culture),
                    Text = GetFragmentText(parse)
                },
                TryParseCall tryParse => new FunctionScalarExpressionInfo
                {
                    Name = "TRY_PARSE",
                    Arguments = BuildParseArguments(tryParse.DataType, tryParse.StringValue, tryParse.Culture),
                    Text = GetFragmentText(tryParse)
                },
                SearchedCaseExpression searchedCase => new CaseScalarExpressionInfo
                {
                    WhenClauses = searchedCase.WhenClauses
                        .Select(w => new CaseWhenClauseInfo
                        {
                            Predicate = BuildDetachedPredicateExpression(w.WhenExpression),
                            ThenExpression = BuildScalarExpression(w.ThenExpression)
                        })
                        .ToList(),
                    ElseExpression = searchedCase.ElseExpression == null ? null : BuildScalarExpression(searchedCase.ElseExpression),
                    Text = GetFragmentText(searchedCase)
                },
                SimpleCaseExpression simpleCase => new CaseScalarExpressionInfo
                {
                    InputExpression = BuildScalarExpression(simpleCase.InputExpression),
                    WhenClauses = simpleCase.WhenClauses
                        .Select(w => new CaseWhenClauseInfo
                        {
                            WhenExpression = BuildScalarExpression(w.WhenExpression),
                            ThenExpression = BuildScalarExpression(w.ThenExpression)
                        })
                        .ToList(),
                    ElseExpression = simpleCase.ElseExpression == null ? null : BuildScalarExpression(simpleCase.ElseExpression),
                    Text = GetFragmentText(simpleCase)
                },
                OdbcLiteral odbc => new LiteralScalarExpressionInfo
                {
                    Value = GetFragmentText(odbc),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetFragmentText(odbc)
                },
                BinaryExpression binary => new BinaryScalarExpressionInfo
                {
                    Operator = ConvertBinaryOperator(binary.BinaryExpressionType),
                    Left = BuildScalarExpression(binary.FirstExpression),
                    Right = BuildScalarExpression(binary.SecondExpression),
                    Text = GetFragmentText(binary)
                },
                _ => new LiteralScalarExpressionInfo
                {
                    Value = GetFragmentText(expr),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetFragmentText(expr)
                }
            };
        }

        private bool TryBuildKnownTextualFunctionExpression(
            ScalarExpression expr,
            out ScalarExpressionInfo expression)
        {
            expression = null!;
            if (expr is FunctionCall)
                return false;

            var text = GetFragmentText(expr);
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"^\s*(?<name>TRIM|LTRIM|RTRIM)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            if (!TryGetFirstColumnReference(expr, out var columnRef))
                return false;

            expression = new FunctionScalarExpressionInfo
            {
                Name = match.Groups["name"].Value.ToUpperInvariant(),
                Arguments = new List<ScalarExpressionInfo> { BuildColumnExpression(columnRef) },
                Text = text
            };
            return true;
        }

        private static ColumnScalarExpressionInfo BuildColumnExpression(ColumnReferenceExpression colRef)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            var alias = parts != null && parts.Count >= 2 ? parts[0].Value : string.Empty;
            var column = parts?.Count > 0 ? parts[^1].Value : string.Empty;

            return new ColumnScalarExpressionInfo
            {
                TableAlias = alias,
                ColumnName = column,
                Text = GetFragmentText(colRef)
            };
        }

        private static ScalarBinaryOperator ConvertBinaryOperator(BinaryExpressionType type)
        {
            return type switch
            {
                BinaryExpressionType.Add => ScalarBinaryOperator.Add,
                BinaryExpressionType.Subtract => ScalarBinaryOperator.Subtract,
                BinaryExpressionType.Multiply => ScalarBinaryOperator.Multiply,
                BinaryExpressionType.Divide => ScalarBinaryOperator.Divide,
                BinaryExpressionType.Modulo => ScalarBinaryOperator.Modulo,
                BinaryExpressionType.BitwiseAnd => ScalarBinaryOperator.BitwiseAnd,
                BinaryExpressionType.BitwiseOr => ScalarBinaryOperator.BitwiseOr,
                BinaryExpressionType.BitwiseXor => ScalarBinaryOperator.BitwiseXor,
                _ => ScalarBinaryOperator.Unknown
            };
        }

        private void CollectReferencedColumns(ScalarExpression expr, ConditionInfo condition, bool isRightSide)
        {
            switch (expr)
            {
                case ColumnReferenceExpression colRef when !IsCurrentDateTimeKeyword(colRef):
                    AddReferencedColumn(colRef, condition, isRightSide);
                    break;

                case FunctionCall funcCall:
                    foreach (var parameter in GetColumnBearingFunctionParameters(funcCall))
                    {
                        CollectReferencedColumns(parameter, condition, isRightSide);
                    }
                    break;

                case NullIfExpression nullIf:
                    CollectReferencedColumns(nullIf.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(nullIf.SecondExpression, condition, isRightSide);
                    break;

                case CastCall castCall:
                    CollectReferencedColumns(castCall.Parameter, condition, isRightSide);
                    break;

                case TryCastCall tryCastCall:
                    CollectReferencedColumns(tryCastCall.Parameter, condition, isRightSide);
                    break;

                case ConvertCall convertCall:
                    CollectReferencedColumns(convertCall.Parameter, condition, isRightSide);
                    if (convertCall.Style != null)
                    {
                        CollectReferencedColumns(convertCall.Style, condition, isRightSide);
                    }
                    break;

                case TryConvertCall tryConvertCall:
                    CollectReferencedColumns(tryConvertCall.Parameter, condition, isRightSide);
                    if (tryConvertCall.Style != null)
                    {
                        CollectReferencedColumns(tryConvertCall.Style, condition, isRightSide);
                    }
                    break;

                case ParseCall parseCall:
                    CollectReferencedColumns(parseCall.StringValue, condition, isRightSide);
                    if (parseCall.Culture != null)
                    {
                        CollectReferencedColumns(parseCall.Culture, condition, isRightSide);
                    }
                    break;

                case TryParseCall tryParseCall:
                    CollectReferencedColumns(tryParseCall.StringValue, condition, isRightSide);
                    if (tryParseCall.Culture != null)
                    {
                        CollectReferencedColumns(tryParseCall.Culture, condition, isRightSide);
                    }
                    break;

                case SearchedCaseExpression searchedCase:
                    foreach (var whenClause in searchedCase.WhenClauses)
                    {
                        CollectReferencedColumns(whenClause.WhenExpression, condition, isRightSide);
                        CollectReferencedColumns(whenClause.ThenExpression, condition, isRightSide);
                    }
                    if (searchedCase.ElseExpression != null)
                    {
                        CollectReferencedColumns(searchedCase.ElseExpression, condition, isRightSide);
                    }
                    break;

                case SimpleCaseExpression simpleCase:
                    CollectReferencedColumns(simpleCase.InputExpression, condition, isRightSide);
                    foreach (var whenClause in simpleCase.WhenClauses)
                    {
                        CollectReferencedColumns(whenClause.WhenExpression, condition, isRightSide);
                        CollectReferencedColumns(whenClause.ThenExpression, condition, isRightSide);
                    }
                    if (simpleCase.ElseExpression != null)
                    {
                        CollectReferencedColumns(simpleCase.ElseExpression, condition, isRightSide);
                    }
                    break;

                case BinaryExpression binary:
                    CollectReferencedColumns(binary.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(binary.SecondExpression, condition, isRightSide);
                    break;

                case ParenthesisExpression paren:
                    CollectReferencedColumns(paren.Expression, condition, isRightSide);
                    break;

                case UnaryExpression unary:
                    CollectReferencedColumns(unary.Expression, condition, isRightSide);
                    break;

                default:
                    foreach (var columnRef in CollectColumnReferences(expr))
                    {
                        AddReferencedColumn(columnRef, condition, isRightSide);
                    }
                    break;
            }
        }

        private void CollectReferencedColumns(BooleanExpression expr, ConditionInfo condition, bool isRightSide)
        {
            switch (expr)
            {
                case BooleanComparisonExpression comparison:
                    CollectReferencedColumns(comparison.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(comparison.SecondExpression, condition, isRightSide);
                    break;

                case InPredicate inPredicate:
                    CollectReferencedColumns(inPredicate.Expression, condition, isRightSide);
                    foreach (var value in inPredicate.Values)
                    {
                        CollectReferencedColumns(value, condition, isRightSide);
                    }
                    break;

                case BooleanTernaryExpression ternary:
                    CollectReferencedColumns(ternary.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(ternary.SecondExpression, condition, isRightSide);
                    CollectReferencedColumns(ternary.ThirdExpression, condition, isRightSide);
                    break;

                case LikePredicate like:
                    CollectReferencedColumns(like.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(like.SecondExpression, condition, isRightSide);
                    break;

                case BooleanIsNullExpression isNull:
                    CollectReferencedColumns(isNull.Expression, condition, isRightSide);
                    break;

                case BooleanBinaryExpression binary:
                    CollectReferencedColumns(binary.FirstExpression, condition, isRightSide);
                    CollectReferencedColumns(binary.SecondExpression, condition, isRightSide);
                    break;

                case BooleanParenthesisExpression paren:
                    CollectReferencedColumns(paren.Expression, condition, isRightSide);
                    break;

                case BooleanNotExpression not:
                    CollectReferencedColumns(not.Expression, condition, isRightSide);
                    break;
            }
        }

        private static void AddReferencedColumn(
            ColumnReferenceExpression colRef,
            ConditionInfo condition,
            bool isRightSide)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            if (parts == null || parts.Count == 0)
                return;

            var alias = parts.Count >= 2 ? parts[0].Value : string.Empty;
            var column = parts[^1].Value;

            if (condition.ReferencedColumns.Any(r =>
                    r.IsRightSide == isRightSide &&
                    r.ColumnName.Equals(column, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.TableAlias, alias, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            condition.ReferencedColumns.Add(new ConditionColumnReference
            {
                TableAlias = alias,
                ColumnName = column,
                IsRightSide = isRightSide
            });
        }

        private static bool TryGetFirstColumnReference(
            TSqlFragment fragment,
            out ColumnReferenceExpression column)
        {
            column = null!;
            var columns = CollectColumnReferences(fragment);
            if (columns.Count == 0)
                return false;

            column = columns[0];
            return true;
        }

        private static List<ColumnReferenceExpression> CollectColumnReferences(TSqlFragment fragment)
        {
            var visitor = new ColumnReferenceCollector();
            fragment.Accept(visitor);
            return visitor.Columns;
        }

        private static void AssignConditionColumnReference(
            ColumnReferenceExpression colRef,
            ConditionInfo condition,
            bool isLeft)
        {
            if (IsCurrentDateTimeKeyword(colRef))
                return;

            var parts = colRef.MultiPartIdentifier?.Identifiers;
            if (parts == null || parts.Count == 0)
                return;

            if (isLeft)
            {
                if (parts.Count >= 2)
                {
                    condition.TableAlias = parts[0].Value;
                    condition.ColumnName = parts[1].Value;
                }
                else if (parts.Count == 1)
                {
                    condition.ColumnName = parts[0].Value;
                }
            }
            else
            {
                if (parts.Count >= 2)
                {
                    condition.RightTableAlias = parts[0].Value;
                    condition.RightColumnName = parts[1].Value;
                }
                else if (parts.Count == 1)
                {
                    condition.RightColumnName = parts[0].Value;
                }
            }
        }

        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> Columns { get; } = new();

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                if (!IsCurrentDateTimeKeyword(node))
                {
                    Columns.Add(node);
                }
            }
        }

        private static AggregateFunction? ParseAggregateFunction(string name)
        {
            return name.ToUpperInvariant() switch
            {
                "COUNT" => AggregateFunction.Count,
                "SUM" => AggregateFunction.Sum,
                "AVG" => AggregateFunction.Avg,
                "MAX" => AggregateFunction.Max,
                "MIN" => AggregateFunction.Min,
                _ => null
            };
        }

        private static ComparisonOp ConvertOperator(BooleanComparisonType type) => type switch
        {
            BooleanComparisonType.Equals => ComparisonOp.Equal,
            BooleanComparisonType.NotEqualToBrackets => ComparisonOp.NotEqual,
            BooleanComparisonType.NotEqualToExclamation => ComparisonOp.NotEqual,
            BooleanComparisonType.GreaterThan => ComparisonOp.GreaterThan,
            BooleanComparisonType.GreaterThanOrEqualTo => ComparisonOp.GreaterThanOrEqual,
            BooleanComparisonType.LessThan => ComparisonOp.LessThan,
            BooleanComparisonType.LessThanOrEqualTo => ComparisonOp.LessThanOrEqual,
            _ => ComparisonOp.Equal
        };

        private static string ExtractLiteralValue(ScalarExpression expr)
        {
            return expr switch
            {
                NullLiteral => string.Empty,
                IntegerLiteral i => i.Value,
                NumericLiteral n => n.Value,
                StringLiteral s => s.Value,
                MoneyLiteral m => m.Value,
                RealLiteral r => r.Value,
                BinaryLiteral b => b.Value,
                OdbcLiteral o => GetFragmentText(o),
                ParameterlessCall p => GetFragmentText(p),
                ParseCall p => GetFragmentText(p),
                TryParseCall p => GetFragmentText(p),
                VariableReference v => $"@{v.Name}",
                FunctionCall f => GetFragmentText(f),
                ColumnReferenceExpression c => GetFragmentText(c),
                _ => GetFragmentText(expr)
            };
        }

        private List<ScalarExpressionInfo> BuildConvertArguments(
            DataTypeReference dataType,
            ScalarExpression parameter,
            ScalarExpression? style)
        {
            var args = new List<ScalarExpressionInfo>
            {
                new LiteralScalarExpressionInfo
                {
                    Value = GetFragmentText(dataType),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetFragmentText(dataType)
                }
            };

            var parameterExpression = BuildScalarExpression(parameter);
            if (parameterExpression != null)
            {
                args.Add(parameterExpression);
            }

            var styleExpression = BuildScalarExpression(style);
            if (styleExpression != null)
            {
                args.Add(styleExpression);
            }

            return args;
        }

        private List<ScalarExpressionInfo> BuildParseArguments(
            DataTypeReference dataType,
            ScalarExpression stringValue,
            ScalarExpression? culture)
        {
            var args = new List<ScalarExpressionInfo>
            {
                new LiteralScalarExpressionInfo
                {
                    Value = GetFragmentText(dataType),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetFragmentText(dataType)
                }
            };

            var valueExpression = BuildScalarExpression(stringValue);
            if (valueExpression != null)
            {
                args.Add(valueExpression);
            }

            var cultureExpression = BuildScalarExpression(culture);
            if (cultureExpression != null)
            {
                args.Add(cultureExpression);
            }

            return args;
        }

        private static IEnumerable<ScalarExpression> GetColumnBearingFunctionParameters(FunctionCall function)
        {
            var startIndex = IsDatePartFunction(function.FunctionName?.Value) ? 1 : 0;
            return function.Parameters.Skip(startIndex);
        }

        private static bool IsDatePartFunction(string? functionName)
        {
            return functionName?.ToUpperInvariant() is "DATEADD" or "DATEDIFF" or "DATEPART" or "DATENAME";
        }

        private static bool IsCurrentDateTimeKeyword(ColumnReferenceExpression colRef)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            return parts?.Count == 1 &&
                   GetCurrentDateTimeKeyword(colRef).Length > 0;
        }

        private static string GetCurrentDateTimeKeyword(ColumnReferenceExpression colRef)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            var token = parts?.Count == 1 ? parts[0].Value : string.Empty;
            var normalized = token.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            return normalized switch
            {
                "CURRENT_TIMESTAMP" or "CURRENTTIMESTAMP" => "CURRENT_TIMESTAMP",
                "CURRENT_DATE" or "CURRENTDATE" => "CURRENT_DATE",
                _ => string.Empty
            };
        }

        private static string GetFragmentText(TSqlFragment? node)
        {
            if (node?.ScriptTokenStream == null || node.FirstTokenIndex < 0 || node.LastTokenIndex < 0)
                return string.Empty;

            var tokens = new List<string>();
            for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
            {
                tokens.Add(node.ScriptTokenStream[i].Text);
            }

            return string.Join("", tokens).Trim();
        }
    }
}
