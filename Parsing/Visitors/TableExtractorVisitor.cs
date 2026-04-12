using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts all table references (FROM clause + JOIN targets) from the SQL AST.
    /// </summary>
    public class TableExtractorVisitor : TSqlFragmentVisitor
    {
        public List<TableInfo> Tables { get; } = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var tableName = node.SchemaObject.BaseIdentifier?.Value ?? "";
            var schemaName = node.SchemaObject.SchemaIdentifier?.Value ?? "";
            var alias = node.Alias?.Value ?? "";

            var key = $"{schemaName}.{tableName}.{alias}";
            if (!_seen.Contains(key) && !string.IsNullOrEmpty(tableName))
            {
                _seen.Add(key);
                Tables.Add(new TableInfo
                {
                    TableName = tableName,
                    SchemaName = schemaName,
                    Alias = alias,
                    Role = TableRole.From // Will be refined by JoinExtractorVisitor
                });
            }

            base.Visit(node);
        }
    }
}
