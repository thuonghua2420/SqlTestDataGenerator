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
            return expr is ColumnReferenceExpression;
        }

        private void ExtractColumnReference(ScalarExpression expr, ConditionInfo condition, bool isLeft)
        {
            if (expr is ColumnReferenceExpression colRef)
            {
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

                foreach (var parameter in funcCall.Parameters)
                {
                    ExtractColumnReference(parameter, condition, isLeft);
                    if (HasResolvedConditionColumn(condition, isLeft))
                        return;
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

            return expr switch
            {
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
                case ColumnReferenceExpression colRef:
                    AddReferencedColumn(colRef, condition, isRightSide);
                    break;

                case FunctionCall funcCall:
                    foreach (var parameter in funcCall.Parameters)
                    {
                        CollectReferencedColumns(parameter, condition, isRightSide);
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
                VariableReference v => $"@{v.Name}",
                FunctionCall f => GetFragmentText(f),
                ColumnReferenceExpression c => GetFragmentText(c),
                _ => GetFragmentText(expr)
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
