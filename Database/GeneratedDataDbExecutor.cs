using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.DataGeneration;
using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.DataGeneration.ValueGenerators;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;
using System.Data;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Executes generated rows directly against SQL Server:
    /// 1) clear existing rows from generated tables and their FK descendants
    /// 2) synthesize any missing ancestor rows required by FK chains
    /// 3) insert all rows in dependency order
    /// </summary>
    public class GeneratedDataDbExecutor
    {
        private const int DefaultCommandTimeoutSeconds = 60;
        private const int MaxUniqueMutationAttempts = 128;
        private readonly DependencyOrderResolver _orderResolver = new();
        private readonly ValueGeneratorFactory _valueFactory = new();

        public async Task<DirectInsertResult> ClearAndInsertAsync(
            SqlConnection connection,
            GeneratedDataSet dataSet,
            Dictionary<string, TableSchema> schemas,
            CancellationToken cancellationToken = default)
        {
            if (connection.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("Database connection is not open.");

            var generatedRows = CollectRows(dataSet, schemas);
            if (!generatedRows.Any())
                return new DirectInsertResult();

            using var tx = connection.BeginTransaction();
            string? identityTableOn = null;
            string? identityTableSchemaOn = null;

            try
            {
                var plannedRows = await BuildInsertPlanAsync(
                    connection, tx, generatedRows, schemas, cancellationToken);

                var generatedTables = ResolveTables(generatedRows.Keys, schemas);
                var generatedTableKeys = generatedTables
                    .Select(t => t.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var plannedTables = ResolveTables(plannedRows.Keys, schemas)
                    .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var plannedTableKeys = plannedTables
                    .Select(t => t.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var fkGraph = await LoadForeignKeyGraphAsync(connection, tx, cancellationToken);
                foreach (var generatedTable in generatedTables)
                {
                    fkGraph.EnsureTable(generatedTable.SchemaName, generatedTable.TableName);
                }

                var clearTableKeys = ExpandWithDependentTables(generatedTableKeys, fkGraph.ParentToChildren);
                var deleteOrder = BuildDeleteOrder(generatedTableKeys, clearTableKeys, fkGraph.ParentToChildren);
                var insertOrder = ResolveInsertOrder(plannedRows.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), schemas);

                await EnsurePlannedRowsAreInsertableAsync(
                    connection,
                    tx,
                    plannedRows,
                    schemas,
                    clearTableKeys,
                    cancellationToken);

                var result = new DirectInsertResult
                {
                    GeneratedTables = generatedTableKeys.Count,
                    PlannedTables = plannedTableKeys.Count,
                    SynthesizedAncestorTables = Math.Max(0, plannedTableKeys.Count - generatedTableKeys.Count),
                    TablesCleared = clearTableKeys.Count,
                    DependentTablesCleared = Math.Max(0, clearTableKeys.Count - generatedTableKeys.Count),
                    ClearedTables = deleteOrder
                        .Where(fkGraph.TableMap.ContainsKey)
                        .Select(k => fkGraph.TableMap[k].DisplayName)
                        .ToList()
                };

                try
                {
                    result.RowsDeleted += await ClearTablesOrderedAsync(
                        connection, tx, deleteOrder, fkGraph, cancellationToken);
                }
                catch (SqlException ex) when (IsConstraintConflict(ex))
                {
                    result.RowsDeleted += await ClearTablesWithConstraintsDisabledAsync(
                        connection, tx, deleteOrder, clearTableKeys, fkGraph, cancellationToken);
                    result.UsedConstraintDisableFallback = true;
                }

                bool insertConstraintBypassEnabled = false;
                try
                {
                    foreach (var tableName in insertOrder)
                    {
                        if (!plannedRows.TryGetValue(tableName, out var rows) || rows.Count == 0)
                            continue;
                        if (!schemas.TryGetValue(tableName, out var schema))
                            continue;

                        var includeIdentity = HasIdentityValues(schema, rows);
                        if (includeIdentity)
                        {
                            await SetIdentityInsertAsync(connection, tx, schema, true, cancellationToken);
                            identityTableOn = schema.TableName;
                            identityTableSchemaOn = schema.SchemaName;
                        }

                        try
                        {
                            foreach (var row in rows)
                            {
                                await HealForeignKeysAsync(
                                    connection, tx, schema, row, plannedRows, plannedTableKeys, schemas, cancellationToken);

                                bool inserted = false;
                                while (!inserted)
                                {
                                    try
                                    {
                                        result.RowsInserted += await InsertRowAsync(
                                            connection, tx, schema, row, cancellationToken);
                                        inserted = true;
                                    }
                                    catch (SqlException ex) when (IsConstraintConflict(ex) && !insertConstraintBypassEnabled)
                                    {
                                        await SetConstraintStateAsync(
                                            connection,
                                            tx,
                                            plannedTables,
                                            enable: false,
                                            validateExistingRows: false,
                                            cancellationToken);

                                        insertConstraintBypassEnabled = true;
                                        result.UsedInsertConstraintBypass = true;
                                    }
                                }
                            }

                            result.TablesInserted++;
                        }
                        finally
                        {
                            if (includeIdentity)
                            {
                                try
                                {
                                    await SetIdentityInsertAsync(connection, tx, schema, false, cancellationToken);
                                    identityTableOn = null;
                                    identityTableSchemaOn = null;
                                }
                                catch
                                {
                                    // Outer catch will perform best-effort cleanup.
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (insertConstraintBypassEnabled)
                    {
                        await SetConstraintStateAsync(
                            connection,
                            tx,
                            plannedTables.AsEnumerable().Reverse(),
                            enable: true,
                            validateExistingRows: false,
                            cancellationToken);
                    }
                }

                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex)
            {
                try
                {
                    await tx.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Ignore rollback cleanup failures.
                }

                if (!string.IsNullOrWhiteSpace(identityTableOn))
                {
                    try
                    {
                        var cleanupSchema = NormalizeSchema(identityTableSchemaOn);
                        var cleanupSql = $"SET IDENTITY_INSERT [{cleanupSchema}].[{identityTableOn}] OFF;";
                        await ExecuteNonQueryAsync(connection, null, cleanupSql, cancellationToken);
                    }
                    catch
                    {
                        // Best effort only.
                    }
                }

                throw new InvalidOperationException($"Direct insert failed: {ex.Message}", ex);
            }
        }

        private async Task<Dictionary<string, List<GeneratedRow>>> BuildInsertPlanAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            Dictionary<string, List<GeneratedRow>> generatedRows,
            Dictionary<string, TableSchema> schemas,
            CancellationToken cancellationToken)
        {
            var plannedRows = CloneRows(generatedRows);
            var queue = new Queue<(string TableName, GeneratedRow Row)>(
                plannedRows.SelectMany(kvp => kvp.Value.Select(row => (kvp.Key, row))));
            var refExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int processed = 0;

            while (queue.Count > 0)
            {
                if ((processed++ & 31) == 0)
                {
                    await Task.Yield();
                }

                var (tableName, row) = queue.Dequeue();
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                foreach (var fk in schema.ForeignKeys)
                {
                    var fkColumn = schema.GetColumn(fk.ColumnName);
                    if (fkColumn == null || fkColumn.IsComputed)
                        continue;

                    var fkValue = row.GetValue(fk.ColumnName);
                    if (IsNullValue(fkValue))
                    {
                        if (fkColumn.IsNullable || HasDefaultValue(fkColumn))
                            continue;

                        fkValue = GenerateSyntheticValue(fkColumn);
                        row.SetValue(fk.ColumnName, fkValue);
                    }

                    var referencedSchemaName = NormalizeSchema(fk.ReferencedSchema);
                    if (!schemas.TryGetValue(fk.ReferencedTable, out var referencedSchema))
                    {
                        throw new InvalidOperationException(
                            $"Missing schema for referenced table [{referencedSchemaName}.{fk.ReferencedTable}] " +
                            $"required by FK [{schema.SchemaName}.{schema.TableName}.{fk.ColumnName}].");
                    }

                    if (FindPlannedRowByColumn(plannedRows, fk.ReferencedTable, fk.ReferencedColumn, fkValue) != null)
                        continue;

                    var existsInDb = await ReferencedValueExistsAsync(
                        connection,
                        transaction,
                        referencedSchemaName,
                        fk.ReferencedTable,
                        fk.ReferencedColumn,
                        fkValue!,
                        refExistsCache,
                        cancellationToken);

                    if (existsInDb)
                        continue;

                    var syntheticParent = CreateSyntheticParentRow(
                        referencedSchema,
                        fk.ReferencedColumn,
                        fkValue!);

                    AddPlannedRow(plannedRows, referencedSchema.TableName, syntheticParent);
                    queue.Enqueue((referencedSchema.TableName, syntheticParent));
                }
            }

            return plannedRows;
        }

        private async Task HealForeignKeysAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            TableSchema schema,
            GeneratedRow row,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            HashSet<string> plannedTableKeys,
            Dictionary<string, TableSchema> schemas,
            CancellationToken cancellationToken)
        {
            var refExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var fallbackRefValueCache = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var fk in schema.ForeignKeys)
            {
                var fkColumn = schema.GetColumn(fk.ColumnName);
                if (fkColumn == null || fkColumn.IsComputed)
                    continue;

                var fkValue = row.GetValue(fk.ColumnName);
                if (IsNullValue(fkValue))
                {
                    if (fkColumn.IsNullable || HasDefaultValue(fkColumn))
                        continue;

                    throw new InvalidOperationException(
                        $"Required FK column [{schema.SchemaName}.{schema.TableName}.{fk.ColumnName}] has no value.");
                }

                var referencedSchemaName = NormalizeSchema(fk.ReferencedSchema);
                var referencedTableKey = BuildTableKey(referencedSchemaName, fk.ReferencedTable);

                if (plannedTableKeys.Contains(referencedTableKey) &&
                    FindPlannedRowByColumn(plannedRows, fk.ReferencedTable, fk.ReferencedColumn, fkValue) != null)
                {
                    continue;
                }

                var exists = await ReferencedValueExistsAsync(
                    connection,
                    transaction,
                    referencedSchemaName,
                    fk.ReferencedTable,
                    fk.ReferencedColumn,
                    fkValue!,
                    refExistsCache,
                    cancellationToken);

                if (exists)
                    continue;

                var fallbackValue = await GetFallbackReferencedValueAsync(
                    connection,
                    transaction,
                    referencedSchemaName,
                    fk.ReferencedTable,
                    fk.ReferencedColumn,
                    fallbackRefValueCache,
                    cancellationToken);

                if (!IsNullValue(fallbackValue))
                {
                    row.SetValue(fk.ColumnName, fallbackValue);
                    continue;
                }

                if (fkColumn.IsNullable)
                {
                    row.SetValue(fk.ColumnName, null);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cannot resolve FK [{schema.SchemaName}.{schema.TableName}.{fk.ColumnName}] -> " +
                    $"[{referencedSchemaName}.{fk.ReferencedTable}.{fk.ReferencedColumn}]. " +
                    $"Insert plan has no matching row and database fallback is unavailable.");
            }
        }

        private GeneratedRow CreateSyntheticParentRow(
            TableSchema schema,
            string matchColumn,
            object matchValue)
        {
            var row = new GeneratedRow { TableName = schema.TableName };
            row.SetValue(matchColumn, matchValue);

            foreach (var pkColumnName in schema.PrimaryKey?.Columns ?? Enumerable.Empty<string>())
            {
                var pkColumn = schema.GetColumn(pkColumnName);
                if (pkColumn == null || pkColumn.IsComputed)
                    continue;
                if (row.ColumnValues.ContainsKey(pkColumnName))
                    continue;

                row.SetValue(pkColumnName, GenerateSyntheticValue(pkColumn));
            }

            foreach (var column in schema.Columns.OrderBy(c => c.OrdinalPosition))
            {
                if (column.IsComputed || row.ColumnValues.ContainsKey(column.ColumnName))
                    continue;
                if (column.IsNullable || HasDefaultValue(column))
                    continue;

                if (column.IsIdentity)
                {
                    row.SetValue(column.ColumnName, GenerateSyntheticValue(column));
                    continue;
                }

                var fk = schema.ForeignKeys.FirstOrDefault(f =>
                    f.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));

                if (fk != null &&
                    fk.ReferencedTable.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    if (schema.PrimaryKey?.Columns.Count == 1)
                    {
                        var selfPkValue = row.GetValue(schema.PrimaryKey.Columns[0]);
                        if (!IsNullValue(selfPkValue))
                        {
                            row.SetValue(column.ColumnName, selfPkValue);
                            continue;
                        }
                    }

                    if (column.IsNullable)
                        continue;
                }

                row.SetValue(column.ColumnName, GenerateSyntheticValue(column));
            }

            return row;
        }

        private object GenerateSyntheticValue(ColumnSchema column)
        {
            var raw = column.TypeCategory switch
            {
                DataTypeCategory.Binary => new byte[] { 0x01, 0x02, 0x03, 0x04 },
                DataTypeCategory.Xml => "<root />",
                _ => _valueFactory.GetGenerator(column.TypeCategory).GenerateDefault(column)
            };

            return SqlServerValueNormalizer.NormalizeValue(column, raw) ?? DBNull.Value;
        }

        private static bool HasDefaultValue(ColumnSchema column) =>
            !string.IsNullOrWhiteSpace(column.DefaultValue);

        private static Dictionary<string, List<GeneratedRow>> CloneRows(
            Dictionary<string, List<GeneratedRow>> source)
        {
            var clone = new Dictionary<string, List<GeneratedRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in source)
            {
                clone[kvp.Key] = kvp.Value
                    .Select(CloneRow)
                    .ToList();
            }

            return clone;
        }

        private static GeneratedRow CloneRow(GeneratedRow source)
        {
            var clone = new GeneratedRow { TableName = source.TableName };
            foreach (var kvp in source.ColumnValues)
            {
                clone.SetValue(kvp.Key, kvp.Value);
            }

            return clone;
        }

        private static GeneratedRow? FindPlannedRowByColumn(
            Dictionary<string, List<GeneratedRow>> plannedRows,
            string tableName,
            string columnName,
            object? value)
        {
            if (!plannedRows.TryGetValue(tableName, out var rows))
                return null;

            return rows.FirstOrDefault(r => ValuesEqual(r.GetValue(columnName), value));
        }

        private static void AddPlannedRow(
            Dictionary<string, List<GeneratedRow>> plannedRows,
            string tableName,
            GeneratedRow row)
        {
            if (!plannedRows.ContainsKey(tableName))
            {
                plannedRows[tableName] = new List<GeneratedRow>();
            }

            plannedRows[tableName].Add(row);
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            if (IsNullValue(left) && IsNullValue(right))
                return true;
            if (IsNullValue(left) || IsNullValue(right))
                return false;

            return left switch
            {
                byte[] leftBytes when right is byte[] rightBytes => leftBytes.SequenceEqual(rightBytes),
                _ => Equals(left, right)
            };
        }

        private static bool IsNullValue(object? value) =>
            value == null || value == DBNull.Value;

        private async Task EnsurePlannedRowsAreInsertableAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            Dictionary<string, TableSchema> schemas,
            HashSet<string> clearTableKeys,
            CancellationToken cancellationToken)
        {
            var uniqueExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in plannedRows)
            {
                if (!schemas.TryGetValue(kvp.Key, out var schema))
                    continue;

                EnsureRequiredColumns(schema, kvp.Value);
                NormalizeRowValues(schema, kvp.Value);
                await EnsureUniqueConstraintsAsync(
                    connection,
                    transaction,
                    schema,
                    kvp.Value,
                    clearTableKeys,
                    uniqueExistsCache,
                    cancellationToken);
            }
        }

        private void EnsureRequiredColumns(TableSchema schema, List<GeneratedRow> rows)
        {
            foreach (var row in rows)
            {
                foreach (var column in schema.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    if (column.IsComputed)
                        continue;
                    if (row.ColumnValues.ContainsKey(column.ColumnName) && !IsNullValue(row.GetValue(column.ColumnName)))
                        continue;
                    if (column.IsIdentity || column.IsNullable || HasDefaultValue(column))
                        continue;

                    var fk = schema.ForeignKeys.FirstOrDefault(f =>
                        f.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
                    if (fk != null)
                        continue;

                    row.SetValue(column.ColumnName, GenerateSyntheticValue(column));
                }
            }
        }

        private async Task EnsureUniqueConstraintsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            TableSchema schema,
            List<GeneratedRow> rows,
            HashSet<string> clearTableKeys,
            Dictionary<string, bool> uniqueExistsCache,
            CancellationToken cancellationToken)
        {
            var tableKey = BuildTableKey(schema.SchemaName, schema.TableName);
            bool tableWillBeCleared = clearTableKeys.Contains(tableKey);
            var specs = BuildUniqueConstraintSpecs(schema).ToList();
            if (!specs.Any())
                return;

            var seenPerSpec = specs.ToDictionary(
                s => s.Name,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            int rowIndex = 0;

            foreach (var row in rows)
            {
                rowIndex++;
                if ((rowIndex & 31) == 0)
                {
                    await Task.Yield();
                }

                int attempts = 0;
                while (true)
                {
                    if (++attempts > MaxUniqueMutationAttempts)
                    {
                        throw new InvalidOperationException(
                            $"Exceeded {MaxUniqueMutationAttempts} attempts while satisfying unique constraints for " +
                            $"[{schema.SchemaName}.{schema.TableName}] row {rowIndex}. " +
                            $"Check short-string domains or immutable unique columns.");
                    }

                    var finalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    bool mutated = false;

                    foreach (var spec in specs)
                    {
                        var key = BuildConstraintKey(spec.Columns, row);
                        if (key == null)
                            continue;

                        if (seenPerSpec[spec.Name].Contains(key) ||
                            (!tableWillBeCleared && await UniqueConstraintExistsInDbAsync(
                                connection, transaction, schema, spec, row, uniqueExistsCache, cancellationToken)))
                        {
                            if (!TryMutateConstraintRow(schema, spec, row))
                            {
                                throw new InvalidOperationException(
                                    $"Cannot satisfy unique constraint [{schema.SchemaName}.{schema.TableName}.{spec.Name}] " +
                                    $"for generated data.");
                            }

                            mutated = true;
                            break;
                        }

                        finalKeys[spec.Name] = key;
                    }

                    if (mutated)
                    {
                        continue;
                    }

                    foreach (var entry in finalKeys)
                    {
                        seenPerSpec[entry.Key].Add(entry.Value);
                    }

                    break;
                }
            }
        }

        private IEnumerable<UniqueConstraintSpec> BuildUniqueConstraintSpecs(TableSchema schema)
        {
            if (schema.PrimaryKey?.Columns.Any() == true)
            {
                var pkColumns = schema.PrimaryKey.Columns
                    .Select(schema.GetColumn)
                    .Where(c => c != null)
                    .Select(c => c!)
                    .ToList();

                if (pkColumns.Any())
                {
                    yield return new UniqueConstraintSpec("PRIMARY_KEY", pkColumns);
                }
            }

            foreach (var unique in schema.UniqueConstraints)
            {
                var columns = unique.Columns
                    .Select(schema.GetColumn)
                    .Where(c => c != null)
                    .Select(c => c!)
                    .ToList();

                if (columns.Any())
                {
                    yield return new UniqueConstraintSpec(unique.ConstraintName, columns);
                }
            }
        }

        private static string? BuildConstraintKey(
            IReadOnlyList<ColumnSchema> columns,
            GeneratedRow row)
        {
            var parts = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var value = row.GetValue(column.ColumnName);
                if (IsNullValue(value))
                    return null;

                parts.Add(BuildValueKey(value!));
            }

            return string.Join("|", parts);
        }

        private async Task<bool> UniqueConstraintExistsInDbAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            TableSchema schema,
            UniqueConstraintSpec spec,
            GeneratedRow row,
            Dictionary<string, bool> cache,
            CancellationToken cancellationToken)
        {
            var key = BuildConstraintKey(spec.Columns, row);
            if (key == null)
                return false;

            var cacheKey = $"{BuildTableKey(schema.SchemaName, schema.TableName)}.{spec.Name}|{key}";
            if (cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var predicates = new List<string>();
            using var cmd = CreateCommand(connection, transaction);
            for (int i = 0; i < spec.Columns.Count; i++)
            {
                var col = spec.Columns[i];
                var value = row.GetValue(col.ColumnName);
                if (IsNullValue(value))
                {
                    predicates.Add($"[{col.ColumnName}] IS NULL");
                }
                else
                {
                    var paramName = $"@p{i}";
                    predicates.Add($"[{col.ColumnName}] = {paramName}");
                    cmd.Parameters.AddWithValue(paramName, value!);
                }
            }

            cmd.CommandText =
                $"SELECT TOP (1) 1 FROM {GetQualifiedTableName(schema)} WHERE {string.Join(" AND ", predicates)};";

            var exists = await cmd.ExecuteScalarAsync(cancellationToken) is not null;
            cache[cacheKey] = exists;
            return exists;
        }

        private bool TryMutateConstraintRow(
            TableSchema schema,
            UniqueConstraintSpec spec,
            GeneratedRow row)
        {
            foreach (var column in spec.Columns)
            {
                if (!CanMutateConstraintColumn(schema, column))
                    continue;

                row.SetValue(column.ColumnName, GenerateSyntheticValue(column));
                return true;
            }

            return false;
        }

        private static bool CanMutateConstraintColumn(TableSchema schema, ColumnSchema column)
        {
            if (column.IsComputed)
                return false;
            if (schema.PrimaryKey?.Columns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase) == true)
                return false;
            if (schema.ForeignKeys.Any(f => f.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        private static async Task<int> ClearTablesOrderedAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            List<string> deleteOrder,
            ForeignKeyGraph graph,
            CancellationToken cancellationToken)
        {
            int rowsDeleted = 0;

            foreach (var tableKey in deleteOrder)
            {
                if (!graph.TableMap.TryGetValue(tableKey, out var table))
                    continue;

                rowsDeleted += await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {table.QualifiedName};",
                    cancellationToken);
            }

            return rowsDeleted;
        }

        private static async Task<int> ClearTablesWithConstraintsDisabledAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            List<string> deleteOrder,
            HashSet<string> clearTableKeys,
            ForeignKeyGraph graph,
            CancellationToken cancellationToken)
        {
            var allTables = clearTableKeys
                .Where(graph.TableMap.ContainsKey)
                .Select(k => graph.TableMap[k])
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int rowsDeleted = 0;
            try
            {
                await SetConstraintStateAsync(
                    connection,
                    transaction,
                    allTables,
                    enable: false,
                    validateExistingRows: false,
                    cancellationToken);

                foreach (var tableKey in deleteOrder)
                {
                    if (!graph.TableMap.TryGetValue(tableKey, out var table))
                        continue;

                    rowsDeleted += await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"DELETE FROM {table.QualifiedName};",
                        cancellationToken);
                }
            }
            finally
            {
                await SetConstraintStateAsync(
                    connection,
                    transaction,
                    allTables.AsEnumerable().Reverse(),
                    enable: true,
                    validateExistingRows: true,
                    cancellationToken);
            }

            return rowsDeleted;
        }

        private static async Task SetConstraintStateAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            IEnumerable<DbTableRef> tables,
            bool enable,
            bool validateExistingRows,
            CancellationToken cancellationToken)
        {
            foreach (var table in tables)
            {
                string sql = !enable
                    ? $"ALTER TABLE {table.QualifiedName} NOCHECK CONSTRAINT ALL;"
                    : validateExistingRows
                        ? $"ALTER TABLE {table.QualifiedName} WITH CHECK CHECK CONSTRAINT ALL;"
                        : $"ALTER TABLE {table.QualifiedName} CHECK CONSTRAINT ALL;";

                await ExecuteNonQueryAsync(connection, transaction, sql, cancellationToken);
            }
        }

        private Dictionary<string, List<GeneratedRow>> CollectRows(
            GeneratedDataSet dataSet,
            Dictionary<string, TableSchema> schemas)
        {
            var result = new Dictionary<string, List<GeneratedRow>>(StringComparer.OrdinalIgnoreCase);

            foreach (var scenario in dataSet.Scenarios)
            {
                foreach (var kvp in scenario.TableRows)
                {
                    if (!schemas.TryGetValue(kvp.Key, out var schema))
                        continue;

                    foreach (var row in kvp.Value)
                    {
                        var filtered = FilterRowColumns(row, schema);
                        if (!filtered.Any())
                            continue;

                        var cloned = new GeneratedRow { TableName = row.TableName };
                        foreach (var col in filtered)
                        {
                            cloned.SetValue(col.Key, col.Value);
                        }

                        AddPlannedRow(result, kvp.Key, cloned);
                    }
                }
            }

            return result;
        }

        private Dictionary<string, object?> FilterRowColumns(GeneratedRow row, TableSchema schema)
        {
            var allowedColumns = schema.Columns
                .Where(c => !c.IsComputed)
                .ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);

            var filtered = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in row.ColumnValues)
            {
                if (allowedColumns.TryGetValue(kvp.Key, out var column))
                {
                    filtered[kvp.Key] = SqlServerValueNormalizer.NormalizeValue(column, kvp.Value);
                }
            }

            return filtered;
        }

        private static void NormalizeRowValues(TableSchema schema, IEnumerable<GeneratedRow> rows)
        {
            foreach (var row in rows)
            {
                foreach (var kvp in row.ColumnValues.ToList())
                {
                    var column = schema.GetColumn(kvp.Key);
                    if (column == null || column.IsComputed)
                        continue;

                    try
                    {
                        row.SetValue(kvp.Key, SqlServerValueNormalizer.NormalizeValue(column, kvp.Value));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Cannot normalize [{schema.SchemaName}.{schema.TableName}.{column.ColumnName}] " +
                            $"from runtime type [{kvp.Value?.GetType().Name ?? "null"}] with value [{kvp.Value}]. {ex.Message}",
                            ex);
                    }
                }
            }
        }

        private static bool IsConstraintConflict(SqlException ex)
        {
            return ex.Number == 547 ||
                   ex.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase);
        }

        private static List<DbTableRef> ResolveTables(
            IEnumerable<string> tableNames,
            Dictionary<string, TableSchema> schemas)
        {
            var result = new List<DbTableRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in tableNames)
            {
                if (schemas.TryGetValue(tableName, out var schema))
                {
                    var table = new DbTableRef(schema.SchemaName, schema.TableName);
                    if (seen.Add(table.Key))
                        result.Add(table);
                    continue;
                }

                var fallback = new DbTableRef("dbo", tableName);
                if (seen.Add(fallback.Key))
                    result.Add(fallback);
            }

            return result;
        }

        private static HashSet<string> ExpandWithDependentTables(
            HashSet<string> rootKeys,
            Dictionary<string, HashSet<string>> parentToChildren)
        {
            var result = new HashSet<string>(rootKeys, StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(rootKeys);

            while (queue.Count > 0)
            {
                var parentKey = queue.Dequeue();
                if (!parentToChildren.TryGetValue(parentKey, out var children))
                    continue;

                foreach (var childKey in children)
                {
                    if (result.Add(childKey))
                    {
                        queue.Enqueue(childKey);
                    }
                }
            }

            return result;
        }

        private static List<string> BuildDeleteOrder(
            HashSet<string> rootKeys,
            HashSet<string> clearKeys,
            Dictionary<string, HashSet<string>> parentToChildren)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var root in rootKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                Dfs(root, clearKeys, parentToChildren, visited, visiting, order);
            }

            foreach (var tableKey in clearKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Contains(tableKey))
                {
                    Dfs(tableKey, clearKeys, parentToChildren, visited, visiting, order);
                }
            }

            return order;
        }

        private static void Dfs(
            string current,
            HashSet<string> clearKeys,
            Dictionary<string, HashSet<string>> parentToChildren,
            HashSet<string> visited,
            HashSet<string> visiting,
            List<string> order)
        {
            if (!clearKeys.Contains(current) || visited.Contains(current) || visiting.Contains(current))
                return;

            visiting.Add(current);

            if (parentToChildren.TryGetValue(current, out var children))
            {
                foreach (var child in children.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    Dfs(child, clearKeys, parentToChildren, visited, visiting, order);
                }
            }

            visiting.Remove(current);
            visited.Add(current);
            order.Add(current);
        }

        private static async Task<ForeignKeyGraph> LoadForeignKeyGraphAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            const string sql = @"
SELECT DISTINCT
    sp.name AS ParentSchema,
    tp.name AS ParentTable,
    sc.name AS ChildSchema,
    tc.name AS ChildTable
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables tc ON fkc.parent_object_id = tc.object_id
JOIN sys.schemas sc ON tc.schema_id = sc.schema_id
JOIN sys.tables tp ON fkc.referenced_object_id = tp.object_id
JOIN sys.schemas sp ON tp.schema_id = sp.schema_id;";

            var graph = new ForeignKeyGraph();
            using var cmd = CreateCommand(connection, transaction, sql);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var parentSchema = reader.GetString(0);
                var parentTable = reader.GetString(1);
                var childSchema = reader.GetString(2);
                var childTable = reader.GetString(3);

                var parent = graph.EnsureTable(parentSchema, parentTable);
                var child = graph.EnsureTable(childSchema, childTable);
                graph.ParentToChildren[parent.Key].Add(child.Key);
            }

            return graph;
        }

        private List<string> ResolveInsertOrder(
            HashSet<string> tableNames,
            Dictionary<string, TableSchema> schemas)
        {
            List<string> order;
            try
            {
                order = _orderResolver.ResolveInsertOrder(tableNames, schemas);
            }
            catch
            {
                order = tableNames.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            }

            order = order
                .Where(tableNames.Contains)
                .ToList();

            foreach (var table in tableNames)
            {
                if (!order.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(table);
                }
            }

            return order;
        }

        private static bool HasIdentityValues(TableSchema schema, List<GeneratedRow> rows)
        {
            var identityColumns = schema.Columns
                .Where(c => c.IsIdentity)
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rows.Any(r => r.ColumnValues.Keys.Any(identityColumns.Contains));
        }

        private static string GetQualifiedTableName(TableSchema schema) =>
            $"[{NormalizeSchema(schema.SchemaName)}].[{schema.TableName}]";

        private static string BuildTableKey(string schemaName, string tableName) =>
            $"{NormalizeSchema(schemaName)}.{tableName}";

        private static string NormalizeSchema(string? schemaName) =>
            string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName!;

        private static async Task<bool> ReferencedValueExistsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string schemaName,
            string tableName,
            string columnName,
            object value,
            Dictionary<string, bool> cache,
            CancellationToken cancellationToken)
        {
            var key = $"{schemaName}.{tableName}.{columnName}|{BuildValueKey(value)}";
            if (cache.TryGetValue(key, out var cached))
                return cached;

            var sql = $"SELECT TOP (1) 1 FROM [{schemaName}].[{tableName}] WHERE [{columnName}] = @v;";
            using var cmd = CreateCommand(connection, transaction, sql);
            cmd.Parameters.AddWithValue("@v", value);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            var exists = result != null && result != DBNull.Value;
            cache[key] = exists;
            return exists;
        }

        private static async Task<object?> GetFallbackReferencedValueAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string schemaName,
            string tableName,
            string columnName,
            Dictionary<string, object?> cache,
            CancellationToken cancellationToken)
        {
            var key = $"{schemaName}.{tableName}.{columnName}";
            if (cache.TryGetValue(key, out var cached))
                return cached;

            var sql = $"SELECT TOP (1) [{columnName}] FROM [{schemaName}].[{tableName}] ORDER BY [{columnName}];";
            using var cmd = CreateCommand(connection, transaction, sql);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            cache[key] = value;
            return value;
        }

        private static string BuildValueKey(object value)
        {
            return value switch
            {
                DateTime dt => dt.ToString("O"),
                DateTimeOffset dto => dto.ToString("O"),
                TimeSpan ts => ts.ToString(),
                Guid g => g.ToString(),
                byte[] bytes => Convert.ToBase64String(bytes),
                _ => value.ToString() ?? string.Empty
            };
        }

        private static async Task SetIdentityInsertAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            TableSchema schema,
            bool on,
            CancellationToken cancellationToken)
        {
            var sql = $"SET IDENTITY_INSERT {GetQualifiedTableName(schema)} {(on ? "ON" : "OFF")};";
            await ExecuteNonQueryAsync(connection, transaction, sql, cancellationToken);
        }

        private static async Task<int> InsertRowAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            TableSchema schema,
            GeneratedRow row,
            CancellationToken cancellationToken)
        {
            var columns = row.ColumnValues.Keys
                .Select(schema.GetColumn)
                .Where(c => c != null && !c.IsComputed)
                .OrderBy(c => c!.OrdinalPosition)
                .Select(c => c!)
                .ToList();

            if (!columns.Any())
                return 0;

            var colList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
            var paramNames = columns.Select((_, i) => $"@p{i}").ToList();
            var sql = $"INSERT INTO {GetQualifiedTableName(schema)} ({colList}) VALUES ({string.Join(", ", paramNames)});";

            using var cmd = CreateCommand(connection, transaction, sql);
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var value = row.GetValue(col.ColumnName) ?? DBNull.Value;
                AddTypedParameter(cmd, paramNames[i], col, value);
            }

            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> ExecuteNonQueryAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            string sql,
            CancellationToken cancellationToken)
        {
            using var cmd = CreateCommand(connection, transaction, sql);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddTypedParameter(
            SqlCommand cmd,
            string parameterName,
            ColumnSchema column,
            object value)
        {
            var parameter = cmd.Parameters.Add(parameterName, ResolveSqlDbType(column));

            if (value == DBNull.Value)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, value) ?? DBNull.Value;
            if (normalized == DBNull.Value)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            switch (column.DataType.ToLowerInvariant())
            {
                case "decimal":
                case "numeric":
                    parameter.Precision = (byte)Math.Max(1, column.NumericPrecision ?? 18);
                    parameter.Scale = (byte)Math.Max(0, column.NumericScale ?? 0);
                    parameter.Value = normalized;
                    break;
                case "money":
                    parameter.Value = normalized;
                    break;
                case "smallmoney":
                    parameter.Value = normalized;
                    break;
                case "char":
                case "varchar":
                case "nchar":
                case "nvarchar":
                    if (column.MaxLength.HasValue)
                    {
                        parameter.Size = Math.Max(1, column.MaxLength.Value);
                    }
                    parameter.Value = normalized;
                    break;
                default:
                    parameter.Value = normalized;
                    break;
            }
        }

        private static SqlDbType ResolveSqlDbType(ColumnSchema column)
        {
            return column.DataType.ToLowerInvariant() switch
            {
                "bigint" => SqlDbType.BigInt,
                "int" => SqlDbType.Int,
                "smallint" => SqlDbType.SmallInt,
                "tinyint" => SqlDbType.TinyInt,
                "bit" => SqlDbType.Bit,
                "decimal" => SqlDbType.Decimal,
                "numeric" => SqlDbType.Decimal,
                "money" => SqlDbType.Money,
                "smallmoney" => SqlDbType.SmallMoney,
                "float" => SqlDbType.Float,
                "real" => SqlDbType.Real,
                "date" => SqlDbType.Date,
                "datetime" => SqlDbType.DateTime,
                "datetime2" => SqlDbType.DateTime2,
                "smalldatetime" => SqlDbType.SmallDateTime,
                "datetimeoffset" => SqlDbType.DateTimeOffset,
                "time" => SqlDbType.Time,
                "char" => SqlDbType.Char,
                "varchar" => SqlDbType.VarChar,
                "nchar" => SqlDbType.NChar,
                "nvarchar" => SqlDbType.NVarChar,
                "text" => SqlDbType.Text,
                "ntext" => SqlDbType.NText,
                "uniqueidentifier" => SqlDbType.UniqueIdentifier,
                "binary" => SqlDbType.Binary,
                "varbinary" => SqlDbType.VarBinary,
                "image" => SqlDbType.Image,
                "xml" => SqlDbType.Xml,
                _ => SqlDbType.Variant
            };
        }

        private static SqlCommand CreateCommand(
            SqlConnection connection,
            SqlTransaction? transaction,
            string? sql = null)
        {
            var cmd = new SqlCommand
            {
                Connection = connection,
                CommandTimeout = DefaultCommandTimeoutSeconds
            };

            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            if (!string.IsNullOrWhiteSpace(sql))
            {
                cmd.CommandText = sql;
            }

            return cmd;
        }

        private sealed class ForeignKeyGraph
        {
            public Dictionary<string, DbTableRef> TableMap { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, HashSet<string>> ParentToChildren { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public DbTableRef EnsureTable(string schemaName, string tableName)
            {
                var table = new DbTableRef(schemaName, tableName);
                if (!TableMap.ContainsKey(table.Key))
                {
                    TableMap[table.Key] = table;
                }

                if (!ParentToChildren.ContainsKey(table.Key))
                {
                    ParentToChildren[table.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return TableMap[table.Key];
            }
        }

        private sealed class DbTableRef
        {
            public string SchemaName { get; }
            public string TableName { get; }
            public string Key => $"{SchemaName}.{TableName}";
            public string DisplayName => Key;
            public string QualifiedName => $"[{SchemaName}].[{TableName}]";

            public DbTableRef(string schemaName, string tableName)
            {
                SchemaName = NormalizeSchema(schemaName);
                TableName = tableName;
            }
        }

        private sealed class UniqueConstraintSpec
        {
            public string Name { get; }
            public IReadOnlyList<ColumnSchema> Columns { get; }

            public UniqueConstraintSpec(string name, IReadOnlyList<ColumnSchema> columns)
            {
                Name = name;
                Columns = columns;
            }
        }
    }

    public class DirectInsertResult
    {
        public int GeneratedTables { get; set; }
        public int PlannedTables { get; set; }
        public int SynthesizedAncestorTables { get; set; }
        public int TablesCleared { get; set; }
        public int DependentTablesCleared { get; set; }
        public int TablesInserted { get; set; }
        public int RowsDeleted { get; set; }
        public int RowsInserted { get; set; }
        public bool UsedConstraintDisableFallback { get; set; }
        public bool UsedInsertConstraintBypass { get; set; }
        public List<string> ClearedTables { get; set; } = new();
    }
}
