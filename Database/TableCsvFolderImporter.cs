using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;
using System.Globalization;
using System.Text;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Imports CSV files produced by <see cref="TableCsvExporter"/> back into generated-row structures.
    /// The parser preserves the semantic difference between:
    /// - null  => empty unquoted field
    /// - empty => quoted empty string ("")
    /// </summary>
    public class TableCsvFolderImporter
    {
        public IReadOnlyList<CsvTableFileReference> DiscoverTableFiles(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new InvalidOperationException("CSV folder path is required.");

            var fullPath = Path.GetFullPath(folderPath.Trim());
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"CSV folder does not exist: {fullPath}");

            var files = Directory.GetFiles(fullPath, "*.csv", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(ParseTableReference)
                .ToList();

            if (files.Count == 0)
                throw new InvalidOperationException("No CSV files were found in the selected folder.");

            var duplicateTableNames = files
                .GroupBy(f => f.TableName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateTableNames.Count > 0)
            {
                throw new InvalidOperationException(
                    $"CSV folder contains duplicate table names across files: {string.Join(", ", duplicateTableNames)}. " +
                    "Current import flow requires unique table names.");
            }

            return files;
        }

        public async Task<CsvFolderImportResult> LoadFolderAsync(
            string folderPath,
            Dictionary<string, TableSchema> schemas,
            CancellationToken cancellationToken = default)
        {
            var files = DiscoverTableFiles(folderPath);
            var scenario = new BranchScenario
            {
                Id = 1,
                Name = "CSV Import",
                Description = $"Imported from folder: {Path.GetFullPath(folderPath)}",
                Type = ScenarioType.Positive,
                ExpectedToReturnRows = true
            };

            int parsedRows = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!schemas.TryGetValue(file.TableName, out var schema))
                {
                    throw new InvalidOperationException(
                        $"Missing schema metadata for imported CSV file [{file.DisplayName}].");
                }

                var rows = await LoadTableRowsAsync(file.FilePath, schema, cancellationToken);
                foreach (var row in rows)
                {
                    scenario.AddRow(schema.TableName, row);
                }

                parsedRows += rows.Count;
            }

            var dataSet = new GeneratedDataSet
            {
                OriginalSql = $"CSV Import: {Path.GetFullPath(folderPath)}",
                GeneratedAt = DateTime.Now,
                Scenarios = new List<BranchScenario> { scenario },
                Notes = files.Select(f => f.DisplayName).ToList()
            };

            return new CsvFolderImportResult
            {
                FolderPath = Path.GetFullPath(folderPath),
                CsvFilesRead = files.Count,
                ParsedRows = parsedRows,
                DataSet = dataSet,
                Files = files.ToList()
            };
        }

        private static CsvTableFileReference ParseTableReference(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException($"Invalid CSV file name: {filePath}");

            var dotIndex = fileName.IndexOf('.');
            string schemaName;
            string tableName;

            if (dotIndex <= 0 || dotIndex == fileName.Length - 1)
            {
                schemaName = "dbo";
                tableName = fileName;
            }
            else
            {
                schemaName = fileName[..dotIndex];
                tableName = fileName[(dotIndex + 1)..];
            }

            return new CsvTableFileReference
            {
                FilePath = filePath,
                SchemaName = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName,
                TableName = tableName,
                DisplayName = $"{(string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName)}.{tableName}"
            };
        }

        private static async Task<List<GeneratedRow>> LoadTableRowsAsync(
            string filePath,
            TableSchema schema,
            CancellationToken cancellationToken)
        {
            var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
            var records = ParseCsvRecords(text);
            if (records.Count == 0)
                return new List<GeneratedRow>();

            var header = records[0];
            if (header.Count == 0)
                throw new InvalidOperationException($"CSV file [{filePath}] does not contain a header row.");

            var headers = header
                .Select(f => f.Value)
                .ToArray();

            ValidateHeaders(headers, schema, filePath);

            var rows = new List<GeneratedRow>();
            for (int rowIndex = 1; rowIndex < records.Count; rowIndex++)
            {
                var record = records[rowIndex];
                if (record.Count != headers.Length)
                {
                    throw new InvalidOperationException(
                        $"CSV file [{filePath}] row {rowIndex + 1} has {record.Count} field(s), expected {headers.Length}.");
                }

                var row = new GeneratedRow { TableName = schema.TableName };
                for (int i = 0; i < headers.Length; i++)
                {
                    var column = schema.GetColumn(headers[i]);
                    if (column == null || column.IsComputed)
                        continue;

                    var value = ConvertFieldValue(record[i], column, filePath, rowIndex + 1);
                    row.SetValue(column.ColumnName, value);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static void ValidateHeaders(string[] headers, TableSchema schema, string filePath)
        {
            var unknownHeaders = headers
                .Where(h => schema.GetColumn(h) == null)
                .ToList();

            if (unknownHeaders.Count > 0)
            {
                throw new InvalidOperationException(
                    $"CSV file [{filePath}] contains unknown column(s): {string.Join(", ", unknownHeaders)}.");
            }
        }

        private static object? ConvertFieldValue(
            CsvField field,
            ColumnSchema column,
            string filePath,
            int csvRowNumber)
        {
            try
            {
                if (field.Value.Length == 0 && !field.WasQuoted)
                    return null;

                if (field.Value.Length == 0 && field.WasQuoted)
                {
                    if (column.TypeCategory is DataTypeCategory.String or DataTypeCategory.Xml)
                        return string.Empty;

                    throw new InvalidOperationException(
                        $"Quoted empty string is only valid for string-like columns. " +
                        $"Column [{column.ColumnKey}] is [{column.DataType}].");
                }

                object raw = column.DataType.ToLowerInvariant() switch
                {
                    "bigint" => long.Parse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "int" => int.Parse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "smallint" => short.Parse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "tinyint" => byte.Parse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "decimal" or "numeric" or "money" or "smallmoney" =>
                        decimal.Parse(field.Value, NumberStyles.Any, CultureInfo.InvariantCulture),
                    "float" => double.Parse(field.Value, NumberStyles.Any, CultureInfo.InvariantCulture),
                    "real" => float.Parse(field.Value, NumberStyles.Any, CultureInfo.InvariantCulture),
                    "bit" => ParseBoolean(field.Value),
                    "date" or "datetime" or "datetime2" or "smalldatetime" =>
                        DateTime.Parse(field.Value, CultureInfo.InvariantCulture, DateTimeStyles.None),
                    "datetimeoffset" =>
                        DateTimeOffset.Parse(field.Value, CultureInfo.InvariantCulture, DateTimeStyles.None),
                    "time" =>
                        TimeSpan.Parse(field.Value, CultureInfo.InvariantCulture),
                    "uniqueidentifier" =>
                        Guid.Parse(field.Value),
                    "binary" or "varbinary" or "image" =>
                        ParseBinary(field.Value),
                    _ => field.Value
                };

                return SqlServerValueNormalizer.NormalizeValue(column, raw);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse CSV value for [{column.ColumnKey}] at row {csvRowNumber} in file [{filePath}]. " +
                    $"Raw value: [{field.Value}]. {ex.Message}",
                    ex);
            }
        }

        private static bool ParseBoolean(string value)
        {
            if (value == "1")
                return true;
            if (value == "0")
                return false;

            return bool.Parse(value);
        }

        private static byte[] ParseBinary(string value)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.FromHexString(value[2..]);

            return Encoding.UTF8.GetBytes(value);
        }

        private static List<List<CsvField>> ParseCsvRecords(string text)
        {
            var records = new List<List<CsvField>>();
            var currentRecord = new List<CsvField>();
            var currentValue = new StringBuilder();
            bool inQuotes = false;
            bool fieldWasQuoted = false;
            bool atFieldStart = true;

            void CompleteField()
            {
                currentRecord.Add(new CsvField(currentValue.ToString(), fieldWasQuoted));
                currentValue.Clear();
                fieldWasQuoted = false;
                atFieldStart = true;
            }

            void CompleteRecord()
            {
                CompleteField();
                records.Add(currentRecord);
                currentRecord = new List<CsvField>();
            }

            for (int i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            currentValue.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        currentValue.Append(ch);
                    }

                    continue;
                }

                if (atFieldStart && ch == '"')
                {
                    inQuotes = true;
                    fieldWasQuoted = true;
                    atFieldStart = false;
                    continue;
                }

                if (ch == ',')
                {
                    CompleteField();
                    continue;
                }

                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    CompleteRecord();
                    continue;
                }

                currentValue.Append(ch);
                atFieldStart = false;
            }

            if (inQuotes)
                throw new InvalidOperationException("CSV parse error: unterminated quoted field.");

            if (currentValue.Length > 0 || fieldWasQuoted || currentRecord.Count > 0)
            {
                CompleteRecord();
            }

            while (records.Count > 0 && records[^1].Count == 1 && records[^1][0].Value.Length == 0 && !records[^1][0].WasQuoted)
            {
                records.RemoveAt(records.Count - 1);
            }

            return records;
        }

        private readonly record struct CsvField(string Value, bool WasQuoted);
    }

    public class CsvTableFileReference
    {
        public string FilePath { get; set; } = string.Empty;
        public string SchemaName { get; set; } = "dbo";
        public string TableName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class CsvFolderImportResult
    {
        public string FolderPath { get; set; } = string.Empty;
        public int CsvFilesRead { get; set; }
        public int ParsedRows { get; set; }
        public GeneratedDataSet DataSet { get; set; } = new();
        public List<CsvTableFileReference> Files { get; set; } = new();
    }
}
