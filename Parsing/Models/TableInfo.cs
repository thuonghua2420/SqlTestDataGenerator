namespace SqlTestDataGenerator.Parsing.Models
{
    /// <summary>
    /// Information about a table referenced in the SQL query.
    /// </summary>
    public class TableInfo
    {
        /// <summary>Physical table name in database</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>Alias used in the query (e.g., "e" for employees)</summary>
        public string Alias { get; set; } = string.Empty;

        /// <summary>Schema name if specified (e.g., "dbo")</summary>
        public string SchemaName { get; set; } = string.Empty;

        /// <summary>How this table is referenced (FROM, JOIN, subquery)</summary>
        public TableRole Role { get; set; }

        /// <summary>Get the effective reference name (alias if available, otherwise table name)</summary>
        public string EffectiveName => string.IsNullOrEmpty(Alias) ? TableName : Alias;

        public override string ToString() =>
            string.IsNullOrEmpty(Alias) ? TableName : $"{TableName} ({Alias})";
    }

    public enum TableRole
    {
        From,
        InnerJoin,
        LeftJoin,
        RightJoin,
        FullJoin,
        CrossJoin,
        SubqueryFrom,
        DerivedTable
    }
}
