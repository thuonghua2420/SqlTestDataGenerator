namespace SqlTestDataGenerator.Parsing.Models
{
    /// <summary>
    /// The complete result of parsing a SQL query.
    /// Contains all extracted information needed for data generation.
    /// </summary>
    public class ParsedQuery
    {
        /// <summary>Original SQL text</summary>
        public string OriginalSql { get; set; } = string.Empty;

        /// <summary>All tables referenced in the query (FROM + JOINs)</summary>
        public List<TableInfo> Tables { get; set; } = new();

        /// <summary>All JOIN relationships</summary>
        public List<JoinInfo> Joins { get; set; } = new();

        /// <summary>WHERE clause conditions</summary>
        public List<ConditionInfo> WhereConditions { get; set; } = new();

        /// <summary>HAVING clause conditions</summary>
        public List<ConditionInfo> HavingConditions { get; set; } = new();

        /// <summary>Exact predicate scopes with boolean structure preserved.</summary>
        public List<PredicateScope> PredicateScopes { get; set; } = new();

        /// <summary>GROUP BY columns (alias.column format)</summary>
        public List<GroupByColumn> GroupByColumns { get; set; } = new();

        /// <summary>Subqueries found in the query</summary>
        public List<SubqueryInfo> Subqueries { get; set; } = new();

        /// <summary>Aggregate functions used (for HAVING analysis)</summary>
        public List<AggregateInfo> Aggregates { get; set; } = new();

        /// <summary>SELECT columns (for understanding output shape)</summary>
        public List<SelectColumnInfo> SelectColumns { get; set; } = new();

        /// <summary>Whether the query uses DISTINCT</summary>
        public bool HasDistinct { get; set; }

        /// <summary>TOP N value if present</summary>
        public int? TopCount { get; set; }

        /// <summary>Parse errors if any</summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>Warnings during analysis</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Map alias → table name for quick lookup</summary>
        public Dictionary<string, string> AliasToTableMap
        {
            get
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in Tables)
                {
                    if (!string.IsNullOrEmpty(t.Alias))
                        map[t.Alias] = t.TableName;
                    map[t.TableName] = t.TableName;
                }
                return map;
            }
        }

        /// <summary>Resolve alias to physical table name</summary>
        public string ResolveAlias(string aliasOrName)
        {
            var m = AliasToTableMap;
            return m.TryGetValue(aliasOrName, out var name) ? name : aliasOrName;
        }

        public IEnumerable<ConditionInfo> EnumerateScopeConditions(ConditionSource source)
        {
            return PredicateScopes
                .Where(s => s.Source == source)
                .SelectMany(s => s.Conditions);
        }
    }

    public class GroupByColumn
    {
        public string TableAlias { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;

        public override string ToString() =>
            string.IsNullOrEmpty(TableAlias) ? ColumnName : $"{TableAlias}.{ColumnName}";
    }

    public class AggregateInfo
    {
        public AggregateFunction Function { get; set; }
        public string TableAlias { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        /// <summary>Full expression if it's a computed aggregate like SUM(a * b)</summary>
        public string Expression { get; set; } = string.Empty;
        public string OutputAlias { get; set; } = string.Empty;
        public bool IsDistinct { get; set; }

        public override string ToString() =>
            $"{Function}({(IsDistinct ? "DISTINCT " : "")}{(string.IsNullOrEmpty(Expression) ? $"{TableAlias}.{ColumnName}" : Expression)})";
    }

    public class SelectColumnInfo
    {
        public string TableAlias { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        public string OutputAlias { get; set; } = string.Empty;
        public string Expression { get; set; } = string.Empty;
        public bool IsAggregate { get; set; }
    }
}
