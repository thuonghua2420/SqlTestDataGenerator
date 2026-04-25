using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Resolves the next numeric key to use for each table based on existing database contents.
    /// </summary>
    public class TableKeySeedResolver
    {
        private readonly Func<SqlConnection> _connectionFactory;

        public TableKeySeedResolver(Func<SqlConnection> connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public Dictionary<string, int> ResolveNextIds(IEnumerable<TableSchema> schemas)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var connection = _connectionFactory();
            foreach (var schema in schemas)
            {
                var keyColumn = ResolveNumericKeyColumn(schema);
                if (keyColumn == null)
                    continue;

                var nextId = ResolveNextId(connection, schema, keyColumn);
                if (nextId > 0)
                {
                    result[schema.TableName] = nextId;
                }
            }

            return result;
        }

        private static ColumnSchema? ResolveNumericKeyColumn(TableSchema schema)
        {
            var identityColumn = schema.Columns.FirstOrDefault(c =>
                c.IsIdentity &&
                c.TypeCategory == DataTypeCategory.Integer);

            if (identityColumn != null)
                return identityColumn;

            if (schema.PrimaryKey?.Columns.Count == 1)
            {
                var pkColumn = schema.GetColumn(schema.PrimaryKey.Columns[0]);
                if (pkColumn != null && pkColumn.TypeCategory == DataTypeCategory.Integer)
                    return pkColumn;
            }

            return null;
        }

        private static int ResolveNextId(SqlConnection connection, TableSchema schema, ColumnSchema keyColumn)
        {
            var tableSql = $"{QuoteIdentifier(schema.SchemaName)}.{QuoteIdentifier(schema.TableName)}";
            var columnSql = QuoteIdentifier(keyColumn.ColumnName);

            long maxValue = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT ISNULL(MAX(TRY_CONVERT(bigint, {columnSql})), 0) FROM {tableSql};";
                var scalar = cmd.ExecuteScalar();
                if (scalar != null && scalar != DBNull.Value)
                {
                    maxValue = Convert.ToInt64(scalar);
                }
            }

            long identityCurrent = 0;
            if (keyColumn.IsIdentity)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT CAST(ISNULL(ic.last_value, ic.seed_value) AS bigint)
FROM sys.identity_columns ic
JOIN sys.tables t ON ic.object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @SchemaName
  AND t.name = @TableName;";
                cmd.Parameters.AddWithValue("@SchemaName", schema.SchemaName);
                cmd.Parameters.AddWithValue("@TableName", schema.TableName);

                var scalar = cmd.ExecuteScalar();
                if (scalar != null && scalar != DBNull.Value)
                {
                    identityCurrent = Convert.ToInt64(scalar);
                }
            }

            var nextValue = Math.Max(maxValue, identityCurrent) + 1;
            if (nextValue <= 0)
                nextValue = 1;

            var maxAllowed = keyColumn.DataType.ToLowerInvariant() switch
            {
                "tinyint" => byte.MaxValue,
                "smallint" => short.MaxValue,
                "int" => int.MaxValue,
                _ => long.MaxValue
            };

            if (nextValue > maxAllowed)
                return 0;

            return nextValue > int.MaxValue ? int.MaxValue : (int)nextValue;
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"[{identifier.Replace("]", "]]")}]";
        }
    }
}
