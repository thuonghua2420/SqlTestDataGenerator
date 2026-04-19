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
                NestingDepth = _depth
            };

            // Left side
            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);

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
                NestingDepth = _depth
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);

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
                    NestingDepth = _depth
                };

                ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
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
                NestingDepth = _depth
            };

            ExtractColumnReference(node.FirstExpression, condition, isLeft: true);
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
                NestingDepth = _depth
            };

            ExtractColumnReference(node.Expression, condition, isLeft: true);
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
            }
            else if (expr is FunctionCall funcCall)
            {
                // Aggregate function: COUNT(x), SUM(x), etc.
                var aggFunc = ParseAggregateFunction(funcCall.FunctionName?.Value ?? "");
                if (aggFunc.HasValue)
                {
                    condition.AggregateFunc = aggFunc;
                    // Extract column from function parameters
                    if (funcCall.Parameters.Count > 0)
                    {
                        ExtractColumnReference(funcCall.Parameters[0], condition, isLeft);
                    }
                }
            }
            else if (expr is BinaryExpression binExpr)
            {
                // Computed expression like: oi.quantity * oi.unit_price
                if (isLeft)
                {
                    condition.ExpressionText = GetNodeText(expr);
                    // Try to extract at least the first column reference
                    ExtractColumnReference(binExpr.FirstExpression, condition, isLeft);
                }
            }
        }

        private bool IsColumnReference(ScalarExpression expr)
        {
            return expr is ColumnReferenceExpression;
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
                _ => GetNodeText(expr)
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
