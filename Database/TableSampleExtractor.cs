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
            var sql = $"SELECT TOP (128) * FROM {tableSql}{orderBy};";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = cmd.ExecuteReader();
            Dictionary<string, object?>? bestRow = null;
            int bestScore = int.MinValue;

            while (reader.Read())
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                var score = ScoreSampleRow(values);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = values;
                }
            }

            return bestRow;
        }

        private static int ScoreSampleRow(Dictionary<string, object?> values)
        {
            int score = 0;

            foreach (var value in values.Values)
            {
                if (value == null || value == DBNull.Value)
                    continue;

                if (value is string text)
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    score += IsGeneratedLikeString(text) ? -6 : 6;
                    continue;
                }

                score += 1;
            }

            return score;
        }

        private static bool IsGeneratedLikeString(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                return true;

            if (trimmed.Contains("TestData", StringComparison.OrdinalIgnoreCase))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^TD[0-9A-Z_]+$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Z]{2,4}(?:[A-Z]{2,4}){3,}\d*$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^TestData_\d+_[A-Z0-9]{4,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            return false;
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
