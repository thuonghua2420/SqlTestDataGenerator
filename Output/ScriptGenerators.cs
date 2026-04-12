using SqlTestDataGenerator.DataGeneration.Models;
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

            // Track which tables need IDENTITY_INSERT
            var identityTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Generate INSERTs in dependency order
            foreach (var tableName in scenario.InsertOrder)
            {
                if (!scenario.TableRows.TryGetValue(tableName, out var rows) || !rows.Any())
                    continue;

                // Check for identity columns
                bool hasIdentity = false;
                if (HandleIdentityInsert && Schemas != null &&
                    Schemas.TryGetValue(tableName, out var schema))
                {
                    hasIdentity = schema.HasIdentityColumn;
                    if (hasIdentity)
                    {
                        sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{tableName}] ON;");
                        identityTables.Add(tableName);
                    }
                }

                foreach (var row in rows)
                {
                    sb.AppendLine(GenerateInsertStatement(tableName, row));
                }

                if (hasIdentity)
                {
                    sb.AppendLine($"SET IDENTITY_INSERT [{SchemaName}].[{tableName}] OFF;");
                }

                sb.AppendLine();
            }

            // Also handle tables not in insertOrder (subquery support data)
            foreach (var kvp in scenario.TableRows)
            {
                if (scenario.InsertOrder.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                    continue;

                foreach (var row in kvp.Value)
                {
                    sb.AppendLine(GenerateInsertStatement(kvp.Key, row));
                }
                sb.AppendLine();
            }

            if (WrapInTransaction)
            {
                sb.AppendLine("    COMMIT TRANSACTION;");
                sb.AppendLine("    PRINT 'Scenario " + scenario.Id + " data inserted successfully.';");
                sb.AppendLine("END TRY");
                sb.AppendLine("BEGIN CATCH");
                sb.AppendLine("    ROLLBACK TRANSACTION;");
                sb.AppendLine("    PRINT 'Error in Scenario " + scenario.Id + ": ' + ERROR_MESSAGE();");
                sb.AppendLine("END CATCH");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a single INSERT statement for a row.
        /// </summary>
        private string GenerateInsertStatement(string tableName, GeneratedRow row)
        {
            var columns = row.ColumnValues.Keys.ToList();
            var values = columns.Select(col => FormatValue(row.ColumnValues[col])).ToList();

            var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
            var valList = string.Join(", ", values);

            return $"INSERT INTO [{SchemaName}].[{tableName}] ({colList}) VALUES ({valList});";
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
                bool b => b ? "1" : "0",
                int i => i.ToString(),
                long l => l.ToString(),
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                double dbl => dbl.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'",
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

                foreach (var tableName in deleteOrder)
                {
                    if (!scenario.TableRows.TryGetValue(tableName, out var rows))
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
                    .SelectMany(s => s.TableRows.GetValueOrDefault(tableName) ?? new List<GeneratedRow>())
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

        private string FormatDeleteValue(object value)
        {
            return value switch
            {
                string s => $"N'{s.Replace("'", "''")}'",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                _ => value.ToString() ?? "NULL"
            };
        }
    }
}
