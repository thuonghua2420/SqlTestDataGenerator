namespace SqlTestDataGenerator.Parsing.Models
{
    /// <summary>
    /// Represents a subquery found in the SQL query.
    /// </summary>
    public class SubqueryInfo
    {
        /// <summary>Unique identifier for this subquery</summary>
        public int Id { get; set; }

        /// <summary>The context where this subquery appears</summary>
        public SubqueryContext Context { get; set; }

        /// <summary>The operator used with this subquery (IN, EXISTS, comparison)</summary>
        public SubqueryOperator Operator { get; set; }

        /// <summary>The parent column being compared to the subquery result</summary>
        public string ParentTableAlias { get; set; } = string.Empty;

        /// <summary>The parent column name</summary>
        public string ParentColumnName { get; set; } = string.Empty;

        /// <summary>The full subquery SQL text</summary>
        public string SubquerySql { get; set; } = string.Empty;

        /// <summary>Tables referenced inside the subquery</summary>
        public List<TableInfo> Tables { get; set; } = new();

        /// <summary>Conditions inside the subquery's WHERE clause</summary>
        public List<ConditionInfo> Conditions { get; set; } = new();

        /// <summary>Exact boolean structure of the subquery WHERE clause, if present.</summary>
        public PredicateScope? WherePredicateScope { get; set; }

        /// <summary>The SELECT column from the subquery (for IN/comparison)</summary>
        public string SelectColumn { get; set; } = string.Empty;

        /// <summary>The SELECT column's table alias</summary>
        public string SelectTableAlias { get; set; } = string.Empty;

        /// <summary>Nested subqueries inside this subquery</summary>
        public List<SubqueryInfo> NestedSubqueries { get; set; } = new();

        /// <summary>Table-valued function sources declared in this subquery (for example STRING_SPLIT aliases).</summary>
        public List<TableFunctionInfo> TableFunctions { get; set; } = new();

        /// <summary>Whether this is a correlated subquery (references parent tables)</summary>
        public bool IsCorrelated { get; set; }

        /// <summary>Nesting level (0 = top-level subquery, 1 = nested, etc.)</summary>
        public int NestingLevel { get; set; }

        /// <summary>Predicate key of the outer EXISTS / IN condition that owns this subquery.</summary>
        public string PredicateConditionKey { get; set; } = string.Empty;

        public override string ToString() =>
            $"{Operator} subquery on {ParentTableAlias}.{ParentColumnName} (Level {NestingLevel})";
    }

    public enum SubqueryContext
    {
        WhereClause,
        HavingClause,
        FromClause,
        SelectClause,
        ExistsCheck
    }

    public enum SubqueryOperator
    {
        In,
        NotIn,
        Exists,
        NotExists,
        ScalarComparison,
        Any,
        All
    }

    public sealed class TableFunctionInfo
    {
        public string Alias { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public List<string> LiteralArguments { get; set; } = new();
    }
}
