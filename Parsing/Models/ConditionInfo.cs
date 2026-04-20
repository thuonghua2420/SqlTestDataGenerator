namespace SqlTestDataGenerator.Parsing.Models
{
    /// <summary>
    /// Represents a single condition extracted from WHERE, HAVING, or JOIN ON clauses.
    /// </summary>
    public class ConditionInfo
    {
        /// <summary>Stable key for this predicate occurrence.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Logical scope identifier where this condition was found.</summary>
        public string ScopeId { get; set; } = string.Empty;

        /// <summary>Human-friendly scope label (for UI scenario descriptions).</summary>
        public string ScopeLabel { get; set; } = string.Empty;

        /// <summary>Table alias or name (e.g., "o" for orders)</summary>
        public string TableAlias { get; set; } = string.Empty;

        /// <summary>Column name (e.g., "order_date")</summary>
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>Comparison operator</summary>
        public ComparisonOp Operator { get; set; }

        /// <summary>The value being compared against (literal or reference)</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>For IN clauses, the list of values</summary>
        public List<string> InValues { get; set; } = new();

        /// <summary>If this condition references a subquery</summary>
        public bool HasSubquery { get; set; }

        /// <summary>Raw subquery SQL for EXISTS / IN (subquery) predicates.</summary>
        public string SubquerySql { get; set; } = string.Empty;

        /// <summary>The source clause: WHERE, HAVING, JOIN_ON</summary>
        public ConditionSource Source { get; set; }

        /// <summary>Logical connective with previous condition (AND/OR)</summary>
        public LogicalOp LogicalOperator { get; set; } = LogicalOp.And;

        /// <summary>Is this condition negated (NOT)</summary>
        public bool IsNegated { get; set; }

        /// <summary>For BETWEEN: the second value</summary>
        public string SecondValue { get; set; } = string.Empty;

        /// <summary>For LIKE: the pattern</summary>
        public string LikePattern { get; set; } = string.Empty;

        /// <summary>If this is part of an aggregate function (for HAVING)</summary>
        public AggregateFunction? AggregateFunc { get; set; }

        /// <summary>The full expression text for complex expressions</summary>
        public string ExpressionText { get; set; } = string.Empty;

        /// <summary>The second table alias for join-like conditions (e.g., a.col = b.col)</summary>
        public string RightTableAlias { get; set; } = string.Empty;

        /// <summary>The second column for join-like conditions</summary>
        public string RightColumnName { get; set; } = string.Empty;

        /// <summary>Whether both sides reference columns (column-to-column comparison)</summary>
        public bool IsColumnComparison { get; set; }

        /// <summary>Nesting depth for parenthesized conditions</summary>
        public int NestingDepth { get; set; }

        /// <summary>Whether this leaf predicate is driven by an outer subquery operator.</summary>
        public bool IsSubqueryPredicate =>
            HasSubquery || Operator is ComparisonOp.Exists or ComparisonOp.NotExists;

        public override string ToString()
        {
            var prefix = AggregateFunc.HasValue ? $"{AggregateFunc}(" : "";
            var suffix = AggregateFunc.HasValue ? ")" : "";
            var alias = string.IsNullOrEmpty(TableAlias) ? "" : $"{TableAlias}.";
            var neg = IsNegated ? "NOT " : "";

            return Operator switch
            {
                ComparisonOp.In => $"{neg}{alias}{ColumnName} IN ({string.Join(", ", InValues)})",
                ComparisonOp.Between => $"{neg}{alias}{ColumnName} BETWEEN {Value} AND {SecondValue}",
                ComparisonOp.Like => $"{neg}{alias}{ColumnName} LIKE '{LikePattern}'",
                ComparisonOp.IsNull => $"{alias}{ColumnName} IS {(IsNegated ? "NOT " : "")}NULL",
                ComparisonOp.Exists => $"{neg}EXISTS (subquery)",
                _ when IsColumnComparison => $"{prefix}{alias}{ColumnName}{suffix} {OperatorToString()} {RightTableAlias}.{RightColumnName}",
                _ => $"{prefix}{alias}{ColumnName}{suffix} {OperatorToString()} {Value}"
            };
        }

        private string OperatorToString() => Operator switch
        {
            ComparisonOp.Equal => "=",
            ComparisonOp.NotEqual => "<>",
            ComparisonOp.GreaterThan => ">",
            ComparisonOp.GreaterThanOrEqual => ">=",
            ComparisonOp.LessThan => "<",
            ComparisonOp.LessThanOrEqual => "<=",
            _ => Operator.ToString()
        };
    }

    public enum ComparisonOp
    {
        Equal,
        NotEqual,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        In,
        NotIn,
        Between,
        Like,
        IsNull,
        IsNotNull,
        Exists,
        NotExists,
        Any,
        All
    }

    public enum ConditionSource
    {
        Where,
        Having,
        JoinOn,
        SubqueryWhere
    }

    public enum LogicalOp
    {
        And,
        Or,
        Not
    }

    public enum AggregateFunction
    {
        Count,
        Sum,
        Avg,
        Max,
        Min,
        CountDistinct
    }
}
