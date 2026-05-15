using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;
using System.Globalization;
using System.Text;

namespace SqlTestDataGenerator.Output
{
    /// <summary>
    /// Generates INSERT SQL statements from generated data sets.
    /// Handles proper value formatting, IDENTITY_INSERT, and transaction wrapping.
    /// </summary>
    public class InsertScriptGenerator
    {
        /// <summary>Whether to wrap each scenario in a transaction</summary>
        public bool WrapInTransaction { get; set; } = true;

        /// <summary>Whether to add SET IDENTITY_INSERT ON/OFF for identity columns</summary>
        public bool HandleIdentityInsert { get; set; } = true;

        /// <summary>Whether to include comments with scenario descriptions</summary>
        public bool IncludeComments { get; set; } = true;

        /// <summary>Schema name prefix for table references</summary>
        public string SchemaName { get; set; } = "dbo";

        /// <summary>Schemas for identity column detection</summary>
        public Dictionary<string, TableSchema>? Schemas { get; set; }

        /// <summary>
        /// Generate the complete INSERT script for all scenarios.
        /// </summary>
        public string GenerateScript(GeneratedDataSet dataSet)
        {
            var sb = new StringBuilder();

            // Header
            if (IncludeComments)
            {
                sb.AppendLine("-- ═══════════════════════════════════════════════════════════");
                sb.AppendLine($"-- SQL Test Data Generator");
                sb.AppendLine($"-- Generated: {dataSet.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"-- Scenarios: {dataSet.Scenarios.Count}");
                sb.AppendLine("-- ═══════════════════════════════════════════════════════════");
                sb.AppendLine();
            }

            foreach (var scenario in dataSet.Scenarios)
            {
                sb.AppendLine(GenerateScenarioScript(scenario));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate INSERT script for a single scenario.
        /// </summary>
        public string GenerateScenarioScript(BranchScenario scenario)
        {
            var sb = new StringBuilder();
            var identityTablesInScenario = GetIdentityTablesWithGeneratedValues(scenario);

            // Scenario header
            if (IncludeComments)
            {
                sb.AppendLine($"-- ══════════════════════════════════════════════════════════");
                sb.AppendLine($"-- Scenario {scenario.Id}: {scenario.Name}");
                sb.AppendLine($"-- {scenario.Description}");
                sb.AppendLine($"-- Expected to return rows: {(scenario.ExpectedToReturnRows ? "YES" : "NO")}");
                if (!string.IsNullOrEmpty(scenario.TestedCondition))
                    sb.AppendLine($"-- Tested condition: {scenario.TestedCondition}");
                sb.AppendLine($"-- ══════════════════════════════════════════════════════════");
                sb.AppendLine();
            }

            if (WrapInTransaction)
            {
                sb.AppendLine("BEGIN TRANSACTION;");
                sb.AppendLine("BEGIN TRY");
                sb.AppendLine();
            }

            // Generate INSERTs in dependency order
            var rowsByTable = CollectScenarioRowsForInsert(scenario);

            foreach (var tableName in scenario.InsertOrder)
            {
                if (!rowsByTable.TryGetValue(tableName, out var rows) || !rows.Any())
                    continue;

                var validRows = FilterInsertableRows(tableName, rows);
                if (!validRows.Any())
                    continue;

                // Check for identity columns
                bool hasIdentity = false;
                if (HandleIdentityInsert && Schemas != null &&
                    Schemas.TryGetValue(tableName, out var schema))
                {
                    hasIdentity = HasIdentityValues(schema, validRows);
                    if (hasIdentity)
                    {
                        sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{tableName}] ON;");
                    }
                }

                foreach (var insertStmt in GenerateInsertStatements(tableName, validRows))
                {
                    sb.AppendLine(insertStmt);
                }

                if (hasIdentity)
                {
                    sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{tableName}] OFF;");
                }

                sb.AppendLine();
            }

            // Also handle tables not in insertOrder (subquery support data)
            foreach (var kvp in rowsByTable)
            {
                if (scenario.InsertOrder.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                    continue;

                var validRows = FilterInsertableRows(kvp.Key, kvp.Value);
                if (!validRows.Any())
                    continue;

                bool hasIdentity = false;
                if (HandleIdentityInsert && Schemas != null &&
                    Schemas.TryGetValue(kvp.Key, out var schema))
                {
                    hasIdentity = HasIdentityValues(schema, validRows);
                    if (hasIdentity)
                    {
                        sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{kvp.Key}] ON;");
                    }
                }

                foreach (var insertStmt in GenerateInsertStatements(kvp.Key, validRows))
                {
                    sb.AppendLine(insertStmt);
                }

                if (hasIdentity)
                {
                    sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{kvp.Key}] OFF;");
                }
                sb.AppendLine();
            }

            if (WrapInTransaction)
            {
                sb.AppendLine("    COMMIT TRANSACTION;");
                sb.AppendLine("    PRINT 'Scenario " + scenario.Id + " data inserted successfully.';");
                sb.AppendLine("END TRY");
                sb.AppendLine("BEGIN CATCH");

                if (HandleIdentityInsert && identityTablesInScenario.Any())
                {
                    foreach (var table in identityTablesInScenario)
                    {
                        sb.AppendLine("    BEGIN TRY");
                        sb.AppendLine($"        SET IDENTITY_INSERT [{SchemaName}].[{table}] OFF;");
                        sb.AppendLine("    END TRY");
                        sb.AppendLine("    BEGIN CATCH");
                        sb.AppendLine("    END CATCH");
                    }
                }

                sb.AppendLine("    ROLLBACK TRANSACTION;");
                sb.AppendLine("    PRINT 'Error in Scenario " + scenario.Id + ": ' + ERROR_MESSAGE();");
                sb.AppendLine("END CATCH");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate INSERT statements grouped by identical column sets.
        /// </summary>
        private List<string> GenerateInsertStatements(string tableName, List<GeneratedRow> rows)
        {
            var statements = new List<string>();
            var groupedRows = rows.GroupBy(r =>
                string.Join("|", r.ColumnValues.Keys.Select(c => c.ToLowerInvariant())));

            foreach (var group in groupedRows)
            {
                var first = group.First();
                var columns = first.ColumnValues.Keys.ToList();
                var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
                var valueLines = new List<string>();

                foreach (var row in group)
                {
                    var values = columns.Select(col => FormatValue(row.ColumnValues[col]));
                    valueLines.Add($"({string.Join(", ", values)})");
                }

                var valuesSql = string.Join(",\r\n", valueLines);
                statements.Add($"INSERT INTO [{SchemaName}].[{tableName}] ({colList}) VALUES\r\n{valuesSql};");
            }

            return statements;
        }

        private static Dictionary<string, List<GeneratedRow>> CollectScenarioRowsForInsert(BranchScenario scenario)
        {
            var result = new Dictionary<string, List<GeneratedRow>>(StringComparer.OrdinalIgnoreCase);

            void AddRows(Dictionary<string, List<GeneratedRow>> source)
            {
                foreach (var (tableName, rows) in source)
                {
                    if (!result.TryGetValue(tableName, out var targetRows))
                    {
                        targetRows = new List<GeneratedRow>();
                        result[tableName] = targetRows;
                    }

                    targetRows.AddRange(rows);
                }
            }

            AddRows(scenario.TableRows);
            AddRows(scenario.AntiMatchRows);
            return result;
        }

        private static bool HasIdentityValues(TableSchema schema, List<GeneratedRow> rows)
        {
            var identityColumns = schema.Columns
                .Where(c => c.IsIdentity)
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rows.Any(r => r.ColumnValues.Keys.Any(identityColumns.Contains));
        }

        private List<GeneratedRow> FilterInsertableRows(string tableName, IEnumerable<GeneratedRow> rows)
        {
            var result = new List<GeneratedRow>();
            TableSchema? schema = null;
            Schemas?.TryGetValue(tableName, out schema);

            foreach (var row in rows)
            {
                var filtered = new GeneratedRow { TableName = row.TableName, Role = row.Role };

                if (schema == null)
                {
                    foreach (var kvp in row.ColumnValues)
                    {
                        filtered.SetValue(kvp.Key, kvp.Value);
                    }
                }
                else
                {
                    foreach (var column in schema.Columns
                                 .Where(c => !c.IsComputed && !c.IsStoreGenerated)
                                 .OrderBy(c => c.OrdinalPosition))
                    {
                        if (row.ColumnValues.TryGetValue(column.ColumnName, out var value))
                        {
                            filtered.SetValue(column.ColumnName, value);
                        }
                    }
                }

                if (filtered.ColumnValues.Any())
                {
                    result.Add(filtered);
                }
            }

            return result;
        }

        private List<string> GetIdentityTablesWithGeneratedValues(BranchScenario scenario)
        {
            var tables = new List<string>();
            if (!HandleIdentityInsert || Schemas == null)
                return tables;

            foreach (var kvp in CollectScenarioRowsForInsert(scenario))
            {
                var validRows = kvp.Value.Where(r => r.ColumnValues.Any()).ToList();
                if (!validRows.Any())
                    continue;

                if (Schemas.TryGetValue(kvp.Key, out var schema) && HasIdentityValues(schema, validRows))
                {
                    tables.Add(kvp.Key);
                }
            }

            return tables
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Format a value for use in a SQL INSERT statement.
        /// </summary>
        private string FormatValue(object? value)
        {
            if (value == null || value == DBNull.Value)
                return "NULL";

            return value switch
            {
                SqlExpressionValue expressionValue => expressionValue.Expression,
                bool b => b ? "1" : "0",
                int i => i.ToString(),
                long l => l.ToString(),
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                double dbl => dbl.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'",
                TimeSpan ts => $"'{ts:hh\\:mm\\:ss}'",
                Guid g => $"'{g}'",
                string s => $"N'{EscapeSqlString(s)}'",
                byte[] bytes => $"0x{BitConverter.ToString(bytes).Replace("-", "")}",
                _ => $"N'{EscapeSqlString(value.ToString() ?? "")}'",
            };
        }

        /// <summary>
        /// Escape single quotes in SQL strings.
        /// </summary>
        private string EscapeSqlString(string value)
        {
            return value.Replace("'", "''");
        }
    }

    /// <summary>
    /// Generates DELETE/cleanup scripts to remove test data.
    /// </summary>
    public class CleanupScriptGenerator
    {
        public string SchemaName { get; set; } = "dbo";

        public string GenerateResetScript(
            GeneratedDataSet dataSet,
            bool includeComments = true)
        {
            var sb = new StringBuilder();

            if (includeComments)
            {
                sb.AppendLine("-- Reset existing rows from generated tables before re-insert");
                sb.AppendLine($"-- Generated: {dataSet.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
            }

            foreach (var tableName in CollectDeleteOrder(dataSet))
            {
                sb.AppendLine($"DELETE FROM [{SchemaName}].[{tableName}];");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate DELETE script for all scenarios (reverse dependency order).
        /// </summary>
        public string GenerateCleanupScript(GeneratedDataSet dataSet)
        {
            var sb = new StringBuilder();

            sb.AppendLine("-- ═══════════════════════════════════════════════════════════");
            sb.AppendLine("-- CLEANUP SCRIPT — Delete all generated test data");
            sb.AppendLine($"-- Generated: {dataSet.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("-- ═══════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine();

            // Collect all IDs per table across all scenarios
            var tableIds = new Dictionary<string, HashSet<object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var scenario in dataSet.Scenarios)
            {
                // Reverse order for DELETE
                var deleteOrder = scenario.InsertOrder.AsEnumerable().Reverse().ToList();
                var rowsByTable = CollectScenarioRowsForCleanup(scenario);

                foreach (var tableName in deleteOrder)
                {
                    if (!rowsByTable.TryGetValue(tableName, out var rows))
                        continue;

                    if (!tableIds.ContainsKey(tableName))
                        tableIds[tableName] = new HashSet<object>();

                    foreach (var row in rows)
                    {
                        // Try to find PK value
                        var firstCol = row.ColumnValues.FirstOrDefault();
                        if (firstCol.Value != null)
                        {
                            tableIds[tableName].Add(firstCol.Value);
                        }
                    }
                }
            }

            // Generate DELETE statements (reverse dependency order)
            var allTables = dataSet.Scenarios
                .SelectMany(s => s.InsertOrder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Reverse()
                .ToList();

            foreach (var tableName in allTables)
            {
                if (!tableIds.TryGetValue(tableName, out var ids) || !ids.Any())
                    continue;

                var firstRow = dataSet.Scenarios
                    .SelectMany(s => CollectScenarioRowsForCleanup(s).GetValueOrDefault(tableName) ?? new List<GeneratedRow>())
                    .FirstOrDefault();

                var pkColumn = firstRow?.ColumnValues.Keys.FirstOrDefault() ?? "id";

                var idList = string.Join(", ", ids.Select(FormatDeleteValue));
                sb.AppendLine($"    DELETE FROM [{SchemaName}].[{tableName}] WHERE [{pkColumn}] IN ({idList});");
            }

            sb.AppendLine();
            sb.AppendLine("    COMMIT TRANSACTION;");
            sb.AppendLine("    PRINT 'Cleanup completed successfully.';");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine("    ROLLBACK TRANSACTION;");
            sb.AppendLine("    PRINT 'Cleanup error: ' + ERROR_MESSAGE();");
            sb.AppendLine("END CATCH");

            return sb.ToString();
        }

        private static Dictionary<string, List<GeneratedRow>> CollectScenarioRowsForCleanup(BranchScenario scenario)
        {
            var result = new Dictionary<string, List<GeneratedRow>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in new[] { scenario.TableRows, scenario.AntiMatchRows })
            {
                foreach (var (tableName, rows) in source)
                {
                    if (!result.TryGetValue(tableName, out var targetRows))
                    {
                        targetRows = new List<GeneratedRow>();
                        result[tableName] = targetRows;
                    }

                    targetRows.AddRange(rows);
                }
            }

            return result;
        }

        private string FormatDeleteValue(object value)
        {
            return value switch
            {
                string s => $"N'{s.Replace("'", "''")}'",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                _ => value.ToString() ?? "NULL"
            };
        }

        private static List<string> CollectDeleteOrder(GeneratedDataSet dataSet)
        {
            var insertionOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var scenario in dataSet.Scenarios)
            {
                var orderedTables = scenario.InsertOrder
                    .Concat(scenario.TableRows.Keys.Where(t => !scenario.InsertOrder.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    .Concat(scenario.AntiMatchRows.Keys.Where(t => !scenario.InsertOrder.Contains(t, StringComparer.OrdinalIgnoreCase)));

                foreach (var tableName in orderedTables)
                {
                    if (seen.Add(tableName))
                    {
                        insertionOrder.Add(tableName);
                    }
                }
            }

            insertionOrder.Reverse();
            return insertionOrder;
        }
    }
}
