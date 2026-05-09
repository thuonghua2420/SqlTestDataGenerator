namespace SqlTestDataGenerator.DataGeneration.Models
{
    /// <summary>
    /// A single generated row of data for a table.
    /// </summary>
    public class GeneratedRow
    {
        public string TableName { get; set; } = string.Empty;
        public Dictionary<string, object?> ColumnValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public void SetValue(string column, object? value)
        {
            ColumnValues[column] = value;
        }

        public object? GetValue(string column)
        {
            return ColumnValues.TryGetValue(column, out var val) ? val : null;
        }
    }

    /// <summary>
    /// A complete test data set containing all scenarios.
    /// </summary>
    public class GeneratedDataSet
    {
        public string OriginalSql { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public List<BranchScenario> Scenarios { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Represents a single test scenario (one branch path through the SQL).
    /// </summary>
    public class BranchScenario
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ScenarioType Type { get; set; }
        public bool ExpectedToReturnRows { get; set; }

        /// <summary>Which condition is being tested (null for positive scenario)</summary>
        public string? TestedCondition { get; set; }

        /// <summary>Exact predicate truth map that this scenario requires.</summary>
        public Dictionary<string, bool> PredicateTruthMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Readable list of predicate texts that define this scenario.</summary>
        public List<string> TestedConditions { get; set; } = new();

        /// <summary>Scope label used for UI descriptions (for example "Main WHERE" or "CTE Orders HAVING").</summary>
        public string ScopeLabel { get; set; } = string.Empty;

        /// <summary>Boundary predicate key when this is a boundary scenario.</summary>
        public string? BoundaryConditionKey { get; set; }

        /// <summary>Join key when this is a join-miss scenario.</summary>
        public string? JoinKey { get; set; }

        /// <summary>The rows to insert, keyed by table name, ordered by dependency</summary>
        public Dictionary<string, List<GeneratedRow>> TableRows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tables in dependency order for INSERT</summary>
        public List<string> InsertOrder { get; set; } = new();

        /// <summary>Errors discovered during the output validation phase</summary>
        public List<string> ValidationErrors { get; set; } = new();

        public void AddRow(string tableName, GeneratedRow row)
        {
            if (!TableRows.ContainsKey(tableName))
                TableRows[tableName] = new List<GeneratedRow>();
            TableRows[tableName].Add(row);
        }
    }

    public enum ScenarioType
    {
        /// <summary>All conditions satisfied — query should return this data</summary>
        Positive,
        /// <summary>A WHERE condition is violated — row should NOT appear</summary>
        WhereNegative,
        /// <summary>A HAVING condition is not met — group should be filtered out</summary>
        HavingNegative,
        /// <summary>LEFT/RIGHT JOIN with no match — row appears with NULLs</summary>
        JoinMiss,
        /// <summary>Subquery IN/EXISTS — target not in subquery result</summary>
        SubqueryMiss,
        /// <summary>Boundary value test</summary>
        Boundary
    }
}
