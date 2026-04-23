using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Loads one deterministic sample row per table to drive realistic value synthesis.
    /// </summary>
    public class TableSampleExtractor
    {
        private readonly Func<SqlConnection> _connectionFactory;

        public TableSampleExtractor(Func<SqlConnection> connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public Dictionary<string, Dictionary<string, object?>> LoadSamples(IEnumerable<TableSchema> schemas)
        {
            var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

            using var connection = _connectionFactory();
            foreach (var schema in schemas)
            {
                var sample = LoadSample(connection, schema);
                if (sample != null && sample.Count > 0)
                {
                    result[schema.TableName] = sample;
                }
            }

            return result;
        }

        private static Dictionary<string, object?>? LoadSample(SqlConnection connection, TableSchema schema)
        {
            var tableSql = $"{QuoteIdentifier(schema.SchemaName)}.{QuoteIdentifier(schema.TableName)}";
            var orderBy = BuildOrderBy(schema);
            var sql = $"SELECT TOP (1) * FROM {tableSql}{orderBy};";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            return values;
        }

        private static string BuildOrderBy(TableSchema schema)
        {
            if (schema.PrimaryKey?.Columns.Any() == true)
            {
                var pkColumns = string.Join(", ", schema.PrimaryKey.Columns.Select(c => $"{QuoteIdentifier(c)} ASC"));
                return $" ORDER BY {pkColumns}";
            }

            var identityColumn = schema.Columns.FirstOrDefault(c => c.IsIdentity);
            if (identityColumn != null)
            {
                return $" ORDER BY {QuoteIdentifier(identityColumn.ColumnName)} ASC";
            }

            var firstColumn = schema.Columns.OrderBy(c => c.OrdinalPosition).FirstOrDefault();
            return firstColumn != null
                ? $" ORDER BY {QuoteIdentifier(firstColumn.ColumnName)} ASC"
                : string.Empty;
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"[{identifier.Replace("]", "]]")}]";
        }
    }
}
