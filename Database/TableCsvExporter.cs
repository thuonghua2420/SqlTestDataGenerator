using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.Schema.Models;
using System.Data;
using System.Globalization;
using System.Text;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Exports selected SQL Server tables to CSV using a DBeaver-compatible layout:
    /// - .csv extension
    /// - comma delimiter
    /// - header row at top
    /// - header labels as-is
    /// - quote escaping with double quotes
    /// - always quote string-like values
    /// - nulls as empty fields
    /// </summary>
    public class TableCsvExporter
    {
        private const int DefaultCommandTimeoutSeconds = 60;

        public async Task<CsvExportResult> ExportAsync(
            SqlConnection connection,
            IReadOnlyCollection<DirectInsertTableInfo> tables,
            string folderPath,
            Dictionary<string, TableSchema>? schemas = null,
            CancellationToken cancellationToken = default)
        {
            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("Database connection is not open.");

            if (tables.Count == 0)
                return new CsvExportResult();

            if (string.IsNullOrWhiteSpace(folderPath))
                throw new InvalidOperationException("Export folder path is required.");

            Directory.CreateDirectory(folderPath);

            var orderedTables = tables
                .Where(t => !string.IsNullOrWhiteSpace(t.TableName))
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new CsvExportResult();
            foreach (var table in orderedTables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var schema = FindTableSchema(schemas, table.SchemaName, table.TableName);
                var filePath = Path.Combine(folderPath, BuildFileName(table));
                var rowCount = await ExportTableAsync(connection, table, schema, filePath, cancellationToken);

                result.ExportedTables++;
                result.ExportedRows += rowCount;
                result.Files.Add(filePath);
            }

            return result;
        }

        private static async Task<int> ExportTableAsync(
            SqlConnection connection,
            DirectInsertTableInfo table,
            TableSchema? schema,
            string filePath,
            CancellationToken cancellationToken)
        {
            var sql = new StringBuilder()
                .Append("SELECT * FROM ")
                .Append(GetQualifiedTableName(table))
                .Append(BuildOrderByClause(schema))
                .Append(';')
                .ToString();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = DefaultCommandTimeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            await using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var columnNames = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToArray();
            var columns = columnNames
                .Select(name => schema?.GetColumn(name))
                .ToArray();

            await writer.WriteLineAsync(BuildCsvLine(columnNames.Select((name, index) => EscapeCsv(name, shouldQuoteAlways: false)).ToArray()));

            int rowCount = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var fields = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    fields[i] = FormatCsvField(value, columns[i]);
                }

                await writer.WriteLineAsync(BuildCsvLine(fields));
                rowCount++;
            }

            await writer.FlushAsync(cancellationToken);
            return rowCount;
        }

        private static string FormatCsvField(object? value, ColumnSchema? column)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            if (value is string text && text.Length == 0)
                return "\"\"";

            var raw = FormatScalar(value, column);
            var shouldQuoteAlways = column?.TypeCategory is DataTypeCategory.String or DataTypeCategory.Xml
                || (column == null && value is string);

            return EscapeCsv(raw, shouldQuoteAlways);
        }

        private static string FormatScalar(object value, ColumnSchema? column)
        {
            if (value is byte[] bytes)
                return $"0x{Convert.ToHexString(bytes)}";

            if (value is bool boolean)
                return boolean ? "1" : "0";

            if (value is DateTimeOffset dto)
                return FormatDateTimeOffset(dto, column);

            if (value is DateTime dateTime)
                return FormatDateTime(dateTime, column);

            if (value is TimeSpan timeSpan)
                return FormatTimeSpan(timeSpan);

            return value switch
            {
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString("G17", CultureInfo.InvariantCulture),
                float f => f.ToString("G9", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private static string FormatDateTime(DateTime value, ColumnSchema? column)
        {
            var normalized = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            return column?.DataType.ToLowerInvariant() switch
            {
                "date" => normalized.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "smalldatetime" => normalized.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => TrimFractionalSeconds(normalized.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture))
            };
        }

        private static string FormatDateTimeOffset(DateTimeOffset value, ColumnSchema? column)
        {
            if (column?.DataType.Equals("date", StringComparison.OrdinalIgnoreCase) == true)
                return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var main = value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            return $"{TrimFractionalSeconds(main)} {value:zzz}";
        }

        private static string FormatTimeSpan(TimeSpan value)
        {
            var raw = value.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
            return TrimFractionalSeconds(raw);
        }

        private static string TrimFractionalSeconds(string text)
        {
            var separatorIndex = text.LastIndexOf('.');
            if (separatorIndex < 0)
                return text;

            int trimIndex = text.Length - 1;
            while (trimIndex > separatorIndex && text[trimIndex] == '0')
            {
                trimIndex--;
            }

            if (trimIndex == separatorIndex)
                return text[..separatorIndex];

            return text[..(trimIndex + 1)];
        }

        private static string EscapeCsv(string value, bool shouldQuoteAlways)
        {
            var requiresQuote = shouldQuoteAlways
                || value.Contains(',')
                || value.Contains('"')
                || value.Contains('\r')
                || value.Contains('\n');

            if (!requiresQuote)
                return value;

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string BuildCsvLine(IReadOnlyList<string> fields)
        {
            if (fields.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(fields[i]);
            }

            return builder.ToString();
        }

        private static string BuildOrderByClause(TableSchema? schema)
        {
            if (schema?.PrimaryKey?.Columns.Any() != true)
                return string.Empty;

            var columns = schema.PrimaryKey.Columns
                .Select(name => $"[{name}]")
                .ToArray();

            return $" ORDER BY {string.Join(", ", columns)}";
        }

        private static TableSchema? FindTableSchema(
            Dictionary<string, TableSchema>? schemas,
            string schemaName,
            string tableName)
        {
            if (schemas == null || schemas.Count == 0)
                return null;

            return schemas.Values.FirstOrDefault(s =>
                s.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                NormalizeSchema(s.SchemaName).Equals(NormalizeSchema(schemaName), StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildFileName(DirectInsertTableInfo table)
        {
            var rawName = $"{NormalizeSchema(table.SchemaName)}.{table.TableName}.csv";
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                rawName = rawName.Replace(invalid, '_');
            }

            return rawName;
        }

        private static string GetQualifiedTableName(DirectInsertTableInfo table) =>
            $"[{NormalizeSchema(table.SchemaName)}].[{table.TableName}]";

        private static string NormalizeSchema(string? schemaName) =>
            string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName!;
    }

    public class CsvExportResult
    {
        public int ExportedTables { get; set; }
        public int ExportedRows { get; set; }
        public List<string> Files { get; set; } = new();
    }
}
