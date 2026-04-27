using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts all conditions from WHERE, HAVING clauses and JOIN ON conditions.
    /// Handles: comparisons, IN, BETWEEN, LIKE, IS NULL, EXISTS, ANY, ALL, 
    /// nested AND/OR, NOT, parenthesized groups, aggregate expressions.
    /// </summary>
    public class ConditionExtractorVisitor : TSqlFragmentVisitor
    {
        public List<ConditionInfo> Conditions { get; } = new();
        private readonly ConditionSource _source;
        private int _depth;

        public ConditionExtractorVisitor(ConditionSource source = ConditionSource.Where)
        {
            _source = source;
        }

        // ─── Comparison: =, <>, >, <, >=, <= ───────────────────────────
        public override void Visit(BooleanComparisonExpression node)
        {
            var condition = new ConditionInfo
            {
                Source = _source,
                Operator = ConvertOperator(node.ComparisonType),
                NestingDepth = _depth,
                LeftExpression = BuildScalarExpression(node.FirstExpression),
                RightExpression = BuildScalarExpression(node.SecondExpression)
            };

            // Left side
            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
            CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);

            // Right side — could be literal, column, or expression
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

            condition.ExpressionText = $"{GetNodeText(node.FirstExpression)} {OperatorToSql(node.ComparisonType)} {GetNodeText(node.SecondExpression)}";
            Conditions.Add(condition);

            // Don't call base — we handle children manually
        }

        // ─── IN / NOT IN ────────────────────────────────────────────────
        public override void Visit(InPredicate node)
        {
            var condition = new ConditionInfo
            {
                Source = _source,
                Operator = node.NotDefined ? ComparisonOp.NotIn : ComparisonOp.In,
                IsNegated = node.NotDefined,
                NestingDepth = _depth,
                LeftExpression = BuildScalarExpression(node.Expression)
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);
            CollectReferencedColumns(node.Expression, condition, isRightSide: false);

            // Check if IN has a subquery
            if (node.Subquery != null)
            {
                condition.HasSubquery = true;
                condition.ExpressionText = $"{GetNodeText(node.Expression)} {(node.NotDefined ? "NOT IN" : "IN")} (subquery)";
            }
            else
            {
                // Literal list: IN (1, 2, 3)
                foreach (var val in node.Values)
                {
                    condition.InValues.Add(ExtractLiteralValue(val));
                }
                condition.ExpressionText = $"{GetNodeText(node.Expression)} IN ({string.Join(", ", condition.InValues)})";
            }

            Conditions.Add(condition);
        }

        // ─── BETWEEN ────────────────────────────────────────────────────
        public override void Visit(BooleanTernaryExpression node)
        {
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between ||
                node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                var condition = new ConditionInfo
                {
                    Source = _source,
                    Operator = ComparisonOp.Between,
                    IsNegated = node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween,
                    NestingDepth = _depth,
                    LeftExpression = BuildScalarExpression(node.FirstExpression),
                    RightExpression = BuildScalarExpression(node.SecondExpression)
                };

                ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
                CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);
                condition.Value = ExtractLiteralValue(node.SecondExpression);
                condition.SecondValue = ExtractLiteralValue(node.ThirdExpression);
                condition.ExpressionText = $"{GetNodeText(node.FirstExpression)} BETWEEN {condition.Value} AND {condition.SecondValue}";

                Conditions.Add(condition);
            }

            base.Visit(node);
        }

        // ─── LIKE ───────────────────────────────────────────────────────
        public override void Visit(LikePredicate node)
        {
            var condition = new ConditionInfo
            {
                Source = _source,
                Operator = ComparisonOp.Like,
                IsNegated = node.NotDefined,
                NestingDepth = _depth,
                LeftExpression = BuildScalarExpression(node.FirstExpression),
                RightExpression = BuildScalarExpression(node.SecondExpression)
            };

            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
            CollectReferencedColumns(node.FirstExpression, condition, isRightSide: false);
            CollectReferencedColumns(node.SecondExpression, condition, isRightSide: true);
            condition.LikePattern = ExtractLiteralValue(node.SecondExpression);
            condition.Value = condition.LikePattern;
            condition.ExpressionText = $"{GetNodeText(node.FirstExpression)} LIKE '{condition.LikePattern}'";

            Conditions.Add(condition);
        }

        // ─── IS NULL / IS NOT NULL ──────────────────────────────────────
        public override void Visit(BooleanIsNullExpression node)
        {
            var condition = new ConditionInfo
            {
                Source = _source,
                Operator = node.IsNot ? ComparisonOp.IsNotNull : ComparisonOp.IsNull,
                IsNegated = node.IsNot,
                NestingDepth = _depth,
                LeftExpression = BuildScalarExpression(node.Expression)
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);
            CollectReferencedColumns(node.Expression, condition, isRightSide: false);
            condition.ExpressionText = $"{GetNodeText(node.Expression)} IS {(node.IsNot ? "NOT " : "")}NULL";

            Conditions.Add(condition);
        }

        // ─── EXISTS / NOT EXISTS ────────────────────────────────────────
        public override void Visit(ExistsPredicate node)
        {
            var condition = new ConditionInfo
            {
                Source = _source,
                Operator = ComparisonOp.Exists,
                HasSubquery = true,
                NestingDepth = _depth,
                ExpressionText = "EXISTS (subquery)"
            };

            Conditions.Add(condition);
            // Don't traverse into subquery — SubqueryExtractorVisitor handles that
        }

        // ─── NOT (wrapping) ────────────────────────────────────────────
        public override void Visit(BooleanNotExpression node)
        {
            // Mark that child conditions should be negated
            // We handle this by checking the node type context
            if (node.Expression is ExistsPredicate)
            {
                var condition = new ConditionInfo
                {
                    Source = _source,
                    Operator = ComparisonOp.NotExists,
                    IsNegated = true,
                    HasSubquery = true,
                    NestingDepth = _depth,
                    ExpressionText = "NOT EXISTS (subquery)"
                };
                Conditions.Add(condition);
            }
            else
            {
                base.Visit(node);
            }
        }

        // ─── AND / OR (logical connectives) ─────────────────────────────
        public override void Visit(BooleanBinaryExpression node)
        {
            // Visit left side
            node.FirstExpression.Accept(this);

            // Mark the next condition with the logical operator
            var prevCount = Conditions.Count;
            node.SecondExpression.Accept(this);

            // Update logical operator for conditions added from the right side
            for (int i = prevCount; i < Conditions.Count; i++)
            {
                if (i == prevCount) // Only the first condition from right side gets the connector
                {
                    Conditions[i].LogicalOperator = node.BinaryExpressionType == BooleanBinaryExpressionType.And
                        ? LogicalOp.And
                        : LogicalOp.Or;
                }
            }
        }

        // ─── Parenthesized expression ───────────────────────────────────
        public override void Visit(BooleanParenthesisExpression node)
        {
            _depth++;
            node.Expression.Accept(this);
            _depth--;
        }

        // ─── Never descend into nested SELECT bodies here.
        // SubqueryExtractorVisitor is responsible for those predicates.
        public override void Visit(ScalarSubquery node)
        {
            // Intentionally do nothing.
        }

        // ═════════════════════════════════════════════════════════════════
        // Helper methods
        // ═════════════════════════════════════════════════════════════════

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

        private static void AddReferencedColumn(ColumnReferenceExpression colRef, ConditionInfo condition, bool isRightSide)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            if (parts == null || parts.Count == 0)
                return;

            var alias = parts.Count >= 2 ? parts[0].Value : string.Empty;
            var column = parts[^1].Value;

            if (condition.ReferencedColumns.Any(r =>
                    r.IsRightSide == isRightSide &&
                    r.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                    r.ColumnName.Equals(column, StringComparison.OrdinalIgnoreCase)))
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
            }
            else if (expr is FunctionCall funcCall)
            {
                // Aggregate function: COUNT(x), SUM(x), etc.
                var aggFunc = ParseAggregateFunction(funcCall.FunctionName?.Value ?? "");
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
            }
            else if (expr is ParseCall parseCall)
            {
                ExtractColumnReference(parseCall.StringValue, condition, isLeft);
            }
            else if (expr is TryParseCall tryParseCall)
            {
                ExtractColumnReference(tryParseCall.StringValue, condition, isLeft);
            }
            else if (expr is BinaryExpression binExpr)
            {
                // Computed expression like: oi.quantity * oi.unit_price
                if (isLeft)
                {
                    condition.ExpressionText = GetNodeText(expr);
                }

                ExtractColumnReference(binExpr.FirstExpression, condition, isLeft);
                if (HasResolvedConditionColumn(condition, isLeft))
                    return;

                ExtractColumnReference(binExpr.SecondExpression, condition, isLeft);
            }
            else if (expr is ParenthesisExpression paren)
            {
                ExtractColumnReference(paren.Expression, condition, isLeft);
            }
            else if (expr is UnaryExpression unary)
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

        private bool IsColumnReference(ScalarExpression expr)
        {
            return expr is ColumnReferenceExpression colRef && !IsCurrentDateTimeKeyword(colRef);
        }

        private string ExtractLiteralValue(ScalarExpression expr)
        {
            return expr switch
            {
                StringLiteral str => str.Value,
                IntegerLiteral intLit => intLit.Value,
                NumericLiteral numLit => numLit.Value,
                RealLiteral realLit => realLit.Value,
                NullLiteral => "NULL",
                MoneyLiteral money => money.Value,
                BinaryLiteral bin => bin.Value,
                DefaultLiteral => "DEFAULT",
                // For expressions, try to get the text representation
                ParenthesisExpression paren => ExtractLiteralValue(paren.Expression),
                UnaryExpression unary => (unary.UnaryExpressionType == UnaryExpressionType.Negative ? "-" : "") + ExtractLiteralValue(unary.Expression),
                FunctionCall func => GetNodeText(func),
                ParameterlessCall parameterless => GetNodeText(parameterless),
                ParseCall parse => GetNodeText(parse),
                TryParseCall tryParse => GetNodeText(tryParse),
                _ => GetNodeText(expr)
            };
        }

        private ScalarExpressionInfo? BuildScalarExpression(ScalarExpression? expr)
        {
            if (expr == null)
                return null;

            return expr switch
            {
                ParameterlessCall parameterless => new FunctionScalarExpressionInfo
                {
                    Name = parameterless.ParameterlessCallType.ToString(),
                    Text = GetNodeText(parameterless)
                },
                ColumnReferenceExpression colRef when IsCurrentDateTimeKeyword(colRef) => new FunctionScalarExpressionInfo
                {
                    Name = GetCurrentDateTimeKeyword(colRef),
                    Text = GetNodeText(colRef)
                },
                ColumnReferenceExpression colRef => BuildColumnExpression(colRef),
                StringLiteral str => new LiteralScalarExpressionInfo
                {
                    Value = str.Value,
                    Kind = ScalarLiteralKind.String,
                    Text = GetNodeText(str)
                },
                IntegerLiteral integer => new LiteralScalarExpressionInfo
                {
                    Value = integer.Value,
                    Kind = ScalarLiteralKind.Integer,
                    Text = GetNodeText(integer)
                },
                NumericLiteral numeric => new LiteralScalarExpressionInfo
                {
                    Value = numeric.Value,
                    Kind = ScalarLiteralKind.Numeric,
                    Text = GetNodeText(numeric)
                },
                NullLiteral => new LiteralScalarExpressionInfo
                {
                    Value = string.Empty,
                    Kind = ScalarLiteralKind.Null,
                    Text = "NULL"
                },
                ParenthesisExpression paren => BuildScalarExpression(paren.Expression),
                UnaryExpression unary => new UnaryScalarExpressionInfo
                {
                    Operator = unary.UnaryExpressionType.ToString(),
                    Operand = BuildScalarExpression(unary.Expression),
                    Text = GetNodeText(unary)
                },
                FunctionCall func => new FunctionScalarExpressionInfo
                {
                    Name = func.FunctionName?.Value ?? string.Empty,
                    Arguments = func.Parameters.Select(BuildScalarExpression).Where(p => p != null).Cast<ScalarExpressionInfo>().ToList(),
                    Text = GetNodeText(func)
                },
                ParseCall parse => new FunctionScalarExpressionInfo
                {
                    Name = "PARSE",
                    Arguments = BuildParseArguments(parse.DataType, parse.StringValue, parse.Culture),
                    Text = GetNodeText(parse)
                },
                TryParseCall tryParse => new FunctionScalarExpressionInfo
                {
                    Name = "TRY_PARSE",
                    Arguments = BuildParseArguments(tryParse.DataType, tryParse.StringValue, tryParse.Culture),
                    Text = GetNodeText(tryParse)
                },
                BinaryExpression binary => new BinaryScalarExpressionInfo
                {
                    Operator = binary.BinaryExpressionType switch
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
                    },
                    Left = BuildScalarExpression(binary.FirstExpression),
                    Right = BuildScalarExpression(binary.SecondExpression),
                    Text = GetNodeText(binary)
                },
                _ => new LiteralScalarExpressionInfo
                {
                    Value = GetNodeText(expr),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetNodeText(expr)
                }
            };
        }

        private static ColumnScalarExpressionInfo BuildColumnExpression(ColumnReferenceExpression colRef)
        {
            var parts = colRef.MultiPartIdentifier?.Identifiers;
            return new ColumnScalarExpressionInfo
            {
                TableAlias = parts != null && parts.Count >= 2 ? parts[0].Value : string.Empty,
                ColumnName = parts?.Count > 0 ? parts[^1].Value : string.Empty
            };
        }

        private AggregateFunction? ParseAggregateFunction(string name)
        {
            return name.ToUpperInvariant() switch
            {
                "COUNT" => Models.AggregateFunction.Count,
                "SUM" => Models.AggregateFunction.Sum,
                "AVG" => Models.AggregateFunction.Avg,
                "MAX" => Models.AggregateFunction.Max,
                "MIN" => Models.AggregateFunction.Min,
                _ => null
            };
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
                    Value = GetNodeText(dataType),
                    Kind = ScalarLiteralKind.Other,
                    Text = GetNodeText(dataType)
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

        private ComparisonOp ConvertOperator(BooleanComparisonType type) => type switch
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

        private string OperatorToSql(BooleanComparisonType type) => type switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            _ => "="
        };

        private string GetNodeText(TSqlFragment node)
        {
            // Build text from tokens if available
            if (node.ScriptTokenStream != null && node.FirstTokenIndex >= 0 && node.LastTokenIndex >= 0)
            {
                var tokens = new List<string>();
                for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
                {
                    tokens.Add(node.ScriptTokenStream[i].Text);
                }
                return string.Join("", tokens).Trim();
            }
            return "";
        }
    }
}
