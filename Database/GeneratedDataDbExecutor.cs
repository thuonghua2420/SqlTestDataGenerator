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
                foreach (var plannedTable in plannedTables)
                {
                    fkGraph.EnsureTable(plannedTable.SchemaName, plannedTable.TableName);
                }

                var clearTableKeys = ExpandWithDependentTables(plannedTableKeys, fkGraph.ParentToChildren);
                var deleteOrder = BuildDeleteOrder(plannedTableKeys, clearTableKeys, fkGraph.ParentToChildren);
                var insertOrder = ResolveInsertOrder(plannedRows.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), schemas);

                await EnsurePlannedRowsAreInsertableAsync(
                    connection,
                    tx,
                    plannedRows,
                    schemas,
                    clearTableKeys,
                    insertOrder,
                    cancellationToken);

                var result = new DirectInsertResult
                {
                    GeneratedTables = generatedTableKeys.Count,
                    PlannedTables = plannedTableKeys.Count,
                    SynthesizedAncestorTables = Math.Max(0, plannedTableKeys.Count - generatedTableKeys.Count),
                    TablesCleared = clearTableKeys.Count,
                    DependentTablesCleared = Math.Max(0, clearTableKeys.Count - plannedTableKeys.Count),
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
                                PrepareRowForInsert(schema, row);

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
                                    catch (SqlException ex) when (IsArithmeticOverflow(ex) && TryApplyLastChanceNumericInsertSafety(schema, row))
                                    {
                                        // Retry once with the mutated row. If SQL Server still rejects it,
                                        // the next loop will throw with the post-safety values in diagnostics.
                                    }
                                    catch (InvalidOperationException ex) when (IsArithmeticOverflow(ex) && TryApplyLastChanceNumericInsertSafety(schema, row))
                                    {
                                        // InsertRowAsync wraps SQL arithmetic overflow with a richer diagnostic.
                                        // Retry with the healed row before surfacing that diagnostic.
                                    }
                                }
                            }

                            result.TablesInserted++;
                            result.InsertedTables.Add(new DirectInsertTableInfo
                            {
                                Key = BuildTableKey(schema.SchemaName, schema.TableName),
                                SchemaName = NormalizeSchema(schema.SchemaName),
                                TableName = schema.TableName,
                                DisplayName = $"{NormalizeSchema(schema.SchemaName)}.{schema.TableName}",
                                InsertedRowCount = rows.Count
                            });
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
                            validateExistingRows: true,
                            cancellationToken);
                    }
                }

                await ValidateForeignKeysInDatabaseAsync(
                    connection,
                    tx,
                    plannedRows,
                    schemas,
                    cancellationToken);

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
                    if (fkColumn == null || fkColumn.IsComputed || fkColumn.IsStoreGenerated)
                        continue;

                    var fkValue = row.GetValue(fk.ColumnName);
                    if (IsNullValue(fkValue))
                    {
                        if (fkColumn.IsNullable || HasDefaultValue(fkColumn))
                            continue;

                        fkValue = GenerateSyntheticValue(fkColumn);
                        row.SetValue(fk.ColumnName, fkValue);
                    }
                    else
                    {
                        fkValue = SqlServerValueNormalizer.NormalizeValue(fkColumn, fkValue) ?? DBNull.Value;
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

                    var referencedColumn = referencedSchema.GetColumn(fk.ReferencedColumn);
                    var existsInDb = await ReferencedValueExistsAsync(
                        connection,
                        transaction,
                        referencedSchemaName,
                        fk.ReferencedTable,
                        fk.ReferencedColumn,
                        referencedColumn,
                        fkValue!,
                        refExistsCache,
                        cancellationToken);

                    if (existsInDb)
                        continue;

                    var syntheticParent = CreateSyntheticParentRow(
                        referencedSchema,
                        fk.ReferencedColumn,
                        fkValue!);

                    AddPlannedRow(plannedRows, referencedSchema.TableName, syntheticParent, referencedSchema);
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
                if (fkColumn == null || fkColumn.IsComputed || fkColumn.IsStoreGenerated)
                    continue;

                var fkValue = row.GetValue(fk.ColumnName);
                if (IsNullValue(fkValue))
                {
                    if (fkColumn.IsNullable || HasDefaultValue(fkColumn))
                        continue;

                    throw new InvalidOperationException(
                        $"Required FK column [{schema.SchemaName}.{schema.TableName}.{fk.ColumnName}] has no value.");
                }

                fkValue = SqlServerValueNormalizer.NormalizeValue(fkColumn, fkValue) ?? DBNull.Value;
                row.SetValue(fk.ColumnName, fkValue);

                var referencedSchemaName = NormalizeSchema(fk.ReferencedSchema);
                var referencedTableKey = BuildTableKey(referencedSchemaName, fk.ReferencedTable);

                if (plannedTableKeys.Contains(referencedTableKey) &&
                    FindPlannedRowByColumn(plannedRows, fk.ReferencedTable, fk.ReferencedColumn, fkValue) != null)
                {
                    continue;
                }

                var referencedSchema = schemas.TryGetValue(fk.ReferencedTable, out var resolvedReferencedSchema)
                    ? resolvedReferencedSchema
                    : null;
                var referencedColumn = referencedSchema?.GetColumn(fk.ReferencedColumn);

                var exists = await ReferencedValueExistsAsync(
                    connection,
                    transaction,
                    referencedSchemaName,
                    fk.ReferencedTable,
                    fk.ReferencedColumn,
                    referencedColumn,
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
            var matchColumnSchema = schema.GetColumn(matchColumn);
            row.SetValue(
                matchColumn,
                matchColumnSchema == null
                    ? matchValue
                    : SqlServerValueNormalizer.NormalizeValue(matchColumnSchema, matchValue) ?? DBNull.Value);

            foreach (var pkColumnName in schema.PrimaryKey?.Columns ?? Enumerable.Empty<string>())
            {
                var pkColumn = schema.GetColumn(pkColumnName);
                if (pkColumn == null || pkColumn.IsComputed || pkColumn.IsStoreGenerated)
                    continue;
                if (row.ColumnValues.ContainsKey(pkColumnName))
                    continue;

                row.SetValue(pkColumnName, GenerateSyntheticValue(pkColumn));
            }

            foreach (var column in schema.Columns.OrderBy(c => c.OrdinalPosition))
            {
                if (column.IsComputed || column.IsStoreGenerated || row.ColumnValues.ContainsKey(column.ColumnName))
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
            GeneratedRow row,
            TableSchema? schema = null)
        {
            if (!plannedRows.ContainsKey(tableName))
            {
                plannedRows[tableName] = new List<GeneratedRow>();
            }

            if (schema != null &&
                TryMergeWithExistingPrimaryKeyRow(plannedRows[tableName], schema, row))
            {
                return;
            }

            plannedRows[tableName].Add(row);
        }

        private static bool TryMergeWithExistingPrimaryKeyRow(
            List<GeneratedRow> rows,
            TableSchema schema,
            GeneratedRow candidate)
        {
            var pkColumns = schema.PrimaryKey?.Columns
                .Select(schema.GetColumn)
                .Where(c => c != null && !c.IsComputed && !c.IsStoreGenerated)
                .Select(c => c!)
                .ToList();
            if (pkColumns == null || pkColumns.Count == 0)
                return false;

            var candidateKey = BuildConstraintKey(pkColumns, candidate);
            if (candidateKey == null)
                return false;

            foreach (var existing in rows)
            {
                var existingKey = BuildConstraintKey(pkColumns, existing);
                if (existingKey == null ||
                    !existingKey.Equals(candidateKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var kvp in candidate.ColumnValues)
                {
                    var existingValue = existing.GetValue(kvp.Key);
                    if (IsNullValue(existingValue) && !IsNullValue(kvp.Value))
                    {
                        existing.SetValue(kvp.Key, kvp.Value);
                    }
                }

                return true;
            }

            return false;
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
            List<string> insertOrder,
            CancellationToken cancellationToken)
        {
            var uniqueExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in plannedRows)
            {
                if (!schemas.TryGetValue(kvp.Key, out var schema))
                    continue;

                EnsureRequiredColumns(schema, kvp.Value);
                NormalizeRowValues(schema, kvp.Value);
                ApplyComputedNumericConversionInsertSafety(schema, kvp.Value);
                ApplyComputedProductInsertSafety(schema, kvp.Value);
                ApplyComputedNumericConversionInsertSafety(schema, kvp.Value);
                NormalizeRowValues(schema, kvp.Value);
            }

            await EnsurePrimaryKeysAsync(
                connection,
                transaction,
                plannedRows,
                schemas,
                clearTableKeys,
                insertOrder,
                uniqueExistsCache,
                cancellationToken);

            foreach (var tableName in insertOrder)
            {
                if (!plannedRows.TryGetValue(tableName, out var rows))
                    continue;
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                await EnsureUniqueConstraintsAsync(
                    connection,
                    transaction,
                    schema,
                    rows,
                    clearTableKeys,
                    uniqueExistsCache,
                    cancellationToken);

                NormalizeRowValues(schema, rows);
            }
        }

        private async Task EnsurePrimaryKeysAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            Dictionary<string, TableSchema> schemas,
            HashSet<string> clearTableKeys,
            List<string> insertOrder,
            Dictionary<string, bool> uniqueExistsCache,
            CancellationToken cancellationToken)
        {
            var childFkMap = BuildChildForeignKeyMap(schemas);

            foreach (var tableName in insertOrder)
            {
                if (!plannedRows.TryGetValue(tableName, out var rows) || rows.Count == 0)
                    continue;
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;
                if (schema.PrimaryKey?.Columns.Any() != true)
                    continue;

                var pkColumns = schema.PrimaryKey.Columns
                    .Select(schema.GetColumn)
                    .Where(c => c != null && !c.IsComputed && !c.IsStoreGenerated)
                    .Select(c => c!)
                    .ToList();
                if (!pkColumns.Any())
                    continue;

                var pkSpec = new UniqueConstraintSpec("PRIMARY_KEY", pkColumns);
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool tableWillBeCleared = clearTableKeys.Contains(BuildTableKey(schema.SchemaName, schema.TableName));

                foreach (var row in rows)
                {
                    int attempts = 0;
                    while (true)
                    {
                        if (++attempts > MaxUniqueMutationAttempts)
                        {
                            throw new InvalidOperationException(
                                $"Exceeded {MaxUniqueMutationAttempts} attempts while canonicalizing primary key for " +
                                $"[{schema.SchemaName}.{schema.TableName}].");
                        }

                        var key = BuildConstraintKey(pkColumns, row);
                        if (key != null &&
                            !seenKeys.Contains(key) &&
                            (tableWillBeCleared || !await UniqueConstraintExistsInDbAsync(
                                connection, transaction, schema, pkSpec, row, uniqueExistsCache, cancellationToken)))
                        {
                            seenKeys.Add(key);
                            break;
                        }

                        if (!TryRemapPrimaryKeyRow(schema, pkColumns, row, plannedRows, childFkMap))
                        {
                            throw new InvalidOperationException(
                                $"Cannot satisfy unique constraint [{schema.SchemaName}.{schema.TableName}.PRIMARY_KEY] " +
                                $"for generated data.");
                        }
                    }
                }
            }
        }

        private void EnsureRequiredColumns(TableSchema schema, List<GeneratedRow> rows)
        {
            foreach (var row in rows)
            {
                foreach (var column in schema.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    if (column.IsComputed || column.IsStoreGenerated)
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

        private async Task ValidateForeignKeysInDatabaseAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            Dictionary<string, TableSchema> schemas,
            CancellationToken cancellationToken)
        {
            var existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var (tableName, rows) in plannedRows)
            {
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                foreach (var row in rows)
                {
                    foreach (var fk in schema.ForeignKeys)
                    {
                        var value = row.GetValue(fk.ColumnName);
                        if (IsNullValue(value))
                            continue;

                        var fkColumn = schema.GetColumn(fk.ColumnName);
                        if (fkColumn != null)
                        {
                            value = SqlServerValueNormalizer.NormalizeValue(fkColumn, value) ?? DBNull.Value;
                            row.SetValue(fk.ColumnName, value);
                        }

                        var referencedColumn = schemas.TryGetValue(fk.ReferencedTable, out var referencedSchema)
                            ? referencedSchema.GetColumn(fk.ReferencedColumn)
                            : null;

                        var exists = await ReferencedValueExistsAsync(
                            connection,
                            transaction,
                            NormalizeSchema(fk.ReferencedSchema),
                            fk.ReferencedTable,
                            fk.ReferencedColumn,
                            referencedColumn,
                            value!,
                            existsCache,
                            cancellationToken);

                        if (!exists)
                        {
                            throw new InvalidOperationException(
                                $"Post-insert FK validation failed for " +
                                $"[{schema.SchemaName}.{schema.TableName}.{fk.ColumnName}] -> " +
                                $"[{NormalizeSchema(fk.ReferencedSchema)}.{fk.ReferencedTable}.{fk.ReferencedColumn}] " +
                                $"with value [{value}].");
                        }
                    }
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
                    AddTypedParameter(cmd, paramName, col, value!);
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

                if (TryGenerateDistinctMutationValue(column, row.GetValue(column.ColumnName), out var mutatedValue))
                {
                    row.SetValue(column.ColumnName, mutatedValue);
                    return true;
                }
            }

            return false;
        }

        private bool TryGenerateDistinctMutationValue(
            ColumnSchema column,
            object? currentValue,
            out object mutatedValue)
        {
            var currentKey = IsNullValue(currentValue) ? null : BuildValueKey(currentValue!);

            if (column.TypeCategory == DataTypeCategory.Boolean && currentValue is bool boolValue)
            {
                mutatedValue = !boolValue;
                return true;
            }

            for (int attempt = 0; attempt < 16; attempt++)
            {
                var candidate = GenerateSyntheticValue(column);
                if (candidate == DBNull.Value)
                    continue;

                var candidateKey = BuildValueKey(candidate);
                if (!string.Equals(candidateKey, currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    mutatedValue = candidate;
                    return true;
                }
            }

            mutatedValue = currentValue ?? DBNull.Value;
            return false;
        }

        private bool TryRemapPrimaryKeyRow(
            TableSchema schema,
            IReadOnlyList<ColumnSchema> pkColumns,
            GeneratedRow row,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            Dictionary<string, List<ChildForeignKeyRef>> childFkMap)
        {
            var oldValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var newValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in pkColumns)
            {
                var oldValue = row.GetValue(column.ColumnName);
                oldValues[column.ColumnName] = oldValue;

                if (!TryGenerateDistinctMutationValue(column, oldValue, out var mutatedValue))
                    return false;

                row.SetValue(column.ColumnName, mutatedValue);
                newValues[column.ColumnName] = mutatedValue;
            }

            PropagatePrimaryKeyChanges(schema, oldValues, newValues, plannedRows, childFkMap);
            return true;
        }

        private static Dictionary<string, List<ChildForeignKeyRef>> BuildChildForeignKeyMap(
            Dictionary<string, TableSchema> schemas)
        {
            var map = new Dictionary<string, List<ChildForeignKeyRef>>(StringComparer.OrdinalIgnoreCase);

            foreach (var childSchema in schemas.Values)
            {
                foreach (var fk in childSchema.ForeignKeys)
                {
                    var parentKey = BuildTableKey(fk.ReferencedSchema, fk.ReferencedTable);
                    if (!map.TryGetValue(parentKey, out var refs))
                    {
                        refs = new List<ChildForeignKeyRef>();
                        map[parentKey] = refs;
                    }

                    refs.Add(new ChildForeignKeyRef(
                        childSchema.TableName,
                        NormalizeSchema(childSchema.SchemaName),
                        fk.ColumnName,
                        fk.ReferencedColumn));
                }
            }

            return map;
        }

        private static void PropagatePrimaryKeyChanges(
            TableSchema parentSchema,
            Dictionary<string, object?> oldValues,
            Dictionary<string, object?> newValues,
            Dictionary<string, List<GeneratedRow>> plannedRows,
            Dictionary<string, List<ChildForeignKeyRef>> childFkMap)
        {
            var parentKey = BuildTableKey(parentSchema.SchemaName, parentSchema.TableName);
            if (!childFkMap.TryGetValue(parentKey, out var childRefs))
                return;

            foreach (var childRef in childRefs)
            {
                if (!plannedRows.TryGetValue(childRef.ChildTableName, out var childRows))
                    continue;
                if (!oldValues.TryGetValue(childRef.ReferencedColumnName, out var oldValue))
                    continue;
                if (!newValues.TryGetValue(childRef.ReferencedColumnName, out var newValue))
                    continue;

                foreach (var childRow in childRows)
                {
                    if (ValuesEqual(childRow.GetValue(childRef.ChildColumnName), oldValue))
                    {
                        childRow.SetValue(childRef.ChildColumnName, newValue);
                    }
                }
            }
        }

        private static bool CanMutateConstraintColumn(TableSchema schema, ColumnSchema column)
        {
            if (column.IsComputed || column.IsStoreGenerated)
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
                foreach (var kvp in EnumerateScenarioRowsForInsert(scenario))
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

                        AddPlannedRow(result, kvp.Key, cloned, schema);
                    }
                }
            }

            return result;
        }

        private static IEnumerable<KeyValuePair<string, List<GeneratedRow>>> EnumerateScenarioRowsForInsert(BranchScenario scenario)
        {
            foreach (var tableRows in scenario.TableRows)
                yield return tableRows;

            foreach (var tableRows in scenario.AntiMatchRows)
                yield return tableRows;
        }

        private Dictionary<string, object?> FilterRowColumns(GeneratedRow row, TableSchema schema)
        {
            var allowedColumns = schema.Columns
                .Where(c => !c.IsComputed && !c.IsStoreGenerated)
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
                    if (column == null || column.IsComputed || column.IsStoreGenerated)
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

        private static void PrepareRowForInsert(TableSchema schema, GeneratedRow row)
        {
            NormalizeRowValues(schema, new[] { row });
            ApplyComputedNumericConversionInsertSafety(schema, new[] { row });
            ApplyComputedProductInsertSafety(schema, new[] { row });
            ApplyHeuristicProductPairInsertSafety(schema, row);
            ApplyComputedNumericConversionInsertSafety(schema, new[] { row });
            NormalizeRowValues(schema, new[] { row });
        }

        private static bool TryApplyLastChanceNumericInsertSafety(TableSchema schema, GeneratedRow row)
        {
            var before = BuildRowNumericValueSnapshot(schema, row);
            PrepareRowForInsert(schema, row);
            var after = BuildRowNumericValueSnapshot(schema, row);
            return !before.Equals(after, StringComparison.Ordinal);
        }

        private static string BuildRowNumericValueSnapshot(TableSchema schema, GeneratedRow row)
        {
            return string.Join("|",
                schema.Columns
                    .Where(c => c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
                    .OrderBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .Select(c => $"{c.ColumnName}={FormatDiagnosticValue(row.GetValue(c.ColumnName))}"));
        }

        private static void ApplyComputedNumericConversionInsertSafety(TableSchema schema, IEnumerable<GeneratedRow> rows)
        {
            var plans = ComputedExpressionSafety.BuildNumericConversionPlans(schema);
            if (plans.Count == 0)
                return;

            var indexedRows = rows.Select((row, index) => new { Row = row, Index = index });
            foreach (var item in indexedRows)
            {
                foreach (var plan in plans)
                {
                    if (!TryConvertDecimal(item.Row.GetValue(plan.SourceColumn.ColumnName), out var currentValue) ||
                        currentValue >= plan.MinValue && currentValue <= plan.MaxValue)
                    {
                        continue;
                    }

                    var safeDecimal = BuildSafeComputedConversionSourceDecimal(plan, currentValue, item.Index);
                    item.Row.SetValue(
                        plan.SourceColumn.ColumnName,
                        SqlServerValueNormalizer.NormalizeValue(plan.SourceColumn, safeDecimal));
                }
            }
        }

        private static decimal BuildSafeComputedConversionSourceDecimal(
            ComputedNumericConversionPlan plan,
            decimal currentValue,
            int rowIndex)
        {
            var step = ComputedExpressionSafety.GetNumericStep(plan.TargetColumn);
            if (step <= 0m)
                step = 1m;

            var offset = Math.Max(0, rowIndex) * step;
            if (currentValue > plan.MaxValue)
            {
                var candidate = plan.MaxValue - offset;
                return candidate >= plan.MinValue ? candidate : plan.MaxValue;
            }

            if (currentValue < plan.MinValue)
            {
                var candidate = plan.MinValue + offset;
                return candidate <= plan.MaxValue ? candidate : plan.MinValue;
            }

            return currentValue;
        }

        private static void ApplyComputedProductInsertSafety(TableSchema schema, IEnumerable<GeneratedRow> rows)
        {
            var plans = schema.Columns
                .Where(c => c.IsComputed)
                .Select(c => TryBuildComputedProductInsertPlan(schema, c, out var plan) ? plan : null)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
            plans.AddRange(BuildImplicitProductInsertPlans(schema, plans));

            if (plans.Count == 0)
                return;

            foreach (var row in rows)
            {
                foreach (var plan in plans)
                {
                    if (!TryGetComputedProductResultMax(plan, out var resultMax) ||
                        !TryConvertDecimal(row.GetValue(plan.LeftColumn.ColumnName), out var left) ||
                        !TryConvertDecimal(row.GetValue(plan.RightColumn.ColumnName), out var right) ||
                        IsProductWithinMax(left, right, resultMax))
                    {
                        continue;
                    }

                    var keepLeft = ShouldKeepComputedProductFactor(plan.LeftColumn, plan.RightColumn);
                    var keepColumn = keepLeft ? plan.LeftColumn : plan.RightColumn;
                    var reduceColumn = keepLeft ? plan.RightColumn : plan.LeftColumn;
                    var keepValue = keepLeft ? left : right;

                    if (keepValue == 0m)
                    {
                        keepValue = 1m;
                        row.SetValue(keepColumn.ColumnName, SqlServerValueNormalizer.NormalizeValue(keepColumn, keepValue));
                    }

                    var reduced = BuildSafeFactorForComputedProduct(reduceColumn, resultMax, keepValue);
                    row.SetValue(reduceColumn.ColumnName, reduced);

                    if (!TryConvertDecimal(reduced, out var reducedDecimal))
                        continue;

                    var finalLeft = keepLeft ? keepValue : reducedDecimal;
                    var finalRight = keepLeft ? reducedDecimal : keepValue;
                    if (!IsProductWithinMax(finalLeft, finalRight, resultMax))
                    {
                        row.SetValue(plan.LeftColumn.ColumnName, SqlServerValueNormalizer.NormalizeValue(plan.LeftColumn, 1m));
                        row.SetValue(plan.RightColumn.ColumnName, BuildSafeFactorForComputedProduct(plan.RightColumn, resultMax, 1m));
                    }
                }
            }
        }

        private static void ApplyHeuristicProductPairInsertSafety(TableSchema schema, GeneratedRow row)
        {
            foreach (var plan in BuildHeuristicProductInsertPlans(schema))
            {
                if (!TryGetHeuristicProductResultMax(plan, out var resultMax) ||
                    !TryConvertDecimal(row.GetValue(plan.LeftColumn.ColumnName), out var left) ||
                    !TryConvertDecimal(row.GetValue(plan.RightColumn.ColumnName), out var right) ||
                    IsProductWithinMax(left, right, resultMax))
                {
                    continue;
                }

                // Last-mile heuristic has no predicate model, so preserve money/rate-like
                // values first and lower count/quantity/stock where possible.
                var leftIsCount = IsComputedProductCountFactorColumn(plan.LeftColumn);
                var rightIsCount = IsComputedProductCountFactorColumn(plan.RightColumn);
                if (leftIsCount && TryReduceComputedProductFactor(row, plan.LeftColumn, plan.RightColumn, right, resultMax))
                    continue;
                if (rightIsCount && TryReduceComputedProductFactor(row, plan.RightColumn, plan.LeftColumn, left, resultMax))
                    continue;

                var keepLeft = ShouldKeepComputedProductFactor(plan.LeftColumn, plan.RightColumn);
                var reduceColumn = keepLeft ? plan.RightColumn : plan.LeftColumn;
                var otherColumn = keepLeft ? plan.LeftColumn : plan.RightColumn;
                var otherValue = keepLeft ? left : right;
                if (TryReduceComputedProductFactor(row, reduceColumn, otherColumn, otherValue, resultMax))
                    continue;

                row.SetValue(plan.LeftColumn.ColumnName, SqlServerValueNormalizer.NormalizeValue(plan.LeftColumn, 1m));
                row.SetValue(plan.RightColumn.ColumnName, BuildSafeFactorForComputedProduct(plan.RightColumn, resultMax, 1m));
            }
        }

        private static bool TryReduceComputedProductFactor(
            GeneratedRow row,
            ColumnSchema reduceColumn,
            ColumnSchema otherColumn,
            decimal otherValue,
            decimal resultMax)
        {
            var reduced = BuildSafeFactorForComputedProduct(reduceColumn, resultMax, otherValue);
            if (!TryConvertDecimal(reduced, out var reducedDecimal) ||
                !IsProductWithinMax(reducedDecimal, otherValue, resultMax))
            {
                return false;
            }

            row.SetValue(reduceColumn.ColumnName, reduced);
            return true;
        }

        private static IEnumerable<ComputedProductInsertPlan> BuildHeuristicProductInsertPlans(TableSchema schema)
        {
            var countColumns = schema.Columns
                .Where(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    IsComputedProductCountFactorColumn(c))
                .ToList();
            if (countColumns.Count == 0)
                yield break;

            var measureColumns = schema.Columns
                .Where(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    IsProductFactorMeasureColumn(c))
                .ToList();
            if (measureColumns.Count == 0)
                yield break;

            foreach (var count in countColumns)
            {
                foreach (var measure in measureColumns)
                {
                    if (count.ColumnName.Equals(measure.ColumnName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    yield return new ComputedProductInsertPlan(measure, count, measure);
                }
            }
        }

        private static object? BuildSafeFactorForComputedProduct(ColumnSchema column, decimal resultMax, decimal otherFactor)
        {
            var limit = otherFactor == 0m ? resultMax : resultMax / Math.Abs(otherFactor);
            if (limit <= 0m)
                limit = 1m;

            var sign = otherFactor < 0m ? -1m : 1m;
            var candidate = RoundTowardZeroForColumn(column, limit) * sign;
            if (candidate == 0m && resultMax > 0m)
                candidate = column.TypeCategory == DataTypeCategory.Integer ? sign : GetStepForColumn(column) * sign;

            return SqlServerValueNormalizer.NormalizeValue(column, candidate);
        }

        private static bool ShouldKeepComputedProductFactor(ColumnSchema leftColumn, ColumnSchema rightColumn)
        {
            var leftIsCountFactor = IsComputedProductCountFactorColumn(leftColumn);
            var rightIsCountFactor = IsComputedProductCountFactorColumn(rightColumn);
            var leftIsMeasure = IsMeasureLikeNumericColumn(leftColumn);
            var rightIsMeasure = IsMeasureLikeNumericColumn(rightColumn);

            if (leftIsCountFactor && rightIsMeasure)
                return true;
            if (rightIsCountFactor && leftIsMeasure)
                return false;
            if (leftIsMeasure && !rightIsMeasure)
                return false;
            if (rightIsMeasure && !leftIsMeasure)
                return true;

            return true;
        }

        private static IEnumerable<ComputedProductInsertPlan> BuildImplicitProductInsertPlans(
            TableSchema schema,
            IReadOnlyCollection<ComputedProductInsertPlan> explicitPlans)
        {
            var quantityColumns = schema.Columns
                .Where(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    IsComputedProductCountFactorColumn(c))
                .ToList();
            if (quantityColumns.Count == 0)
                yield break;

            var measureColumns = schema.Columns
                .Where(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    IsProductFactorMeasureColumn(c))
                .ToList();
            if (measureColumns.Count == 0)
                yield break;

            foreach (var quantity in quantityColumns)
            {
                foreach (var measure in measureColumns)
                {
                    if (quantity.ColumnName.Equals(measure.ColumnName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (explicitPlans.Any(plan => SameProductFactorPair(plan, quantity, measure)))
                        continue;

                    yield return new ComputedProductInsertPlan(measure, quantity, measure);
                }
            }
        }

        private static bool SameProductFactorPair(
            ComputedProductInsertPlan plan,
            ColumnSchema first,
            ColumnSchema second) =>
            (plan.LeftColumn.ColumnName.Equals(first.ColumnName, StringComparison.OrdinalIgnoreCase) &&
             plan.RightColumn.ColumnName.Equals(second.ColumnName, StringComparison.OrdinalIgnoreCase)) ||
            (plan.LeftColumn.ColumnName.Equals(second.ColumnName, StringComparison.OrdinalIgnoreCase) &&
             plan.RightColumn.ColumnName.Equals(first.ColumnName, StringComparison.OrdinalIgnoreCase));

        private static decimal RoundTowardZeroForColumn(ColumnSchema column, decimal value)
        {
            value = Math.Abs(value);
            if (column.TypeCategory == DataTypeCategory.Integer)
                return decimal.Floor(value);

            var scale = Math.Min(28, Math.Max(0, column.NumericScale ?? 0));
            if (scale == 0)
                return decimal.Floor(value);

            var factor = 1m;
            for (var i = 0; i < scale; i++)
                factor *= 10m;

            return decimal.Floor(value * factor) / factor;
        }

        private static decimal GetStepForColumn(ColumnSchema column)
        {
            var scale = Math.Min(28, Math.Max(0, column.NumericScale ?? 0));
            decimal step = 1m;
            for (var i = 0; i < scale; i++)
                step /= 10m;
            return step;
        }

        private static bool TryBuildComputedProductInsertPlan(
            TableSchema schema,
            ColumnSchema computedColumn,
            out ComputedProductInsertPlan plan)
        {
            plan = null!;
            if (!IsComputedNumericProductResultColumn(computedColumn))
            {
                return false;
            }

            var referencedColumns = new List<ColumnSchema>();
            if (!string.IsNullOrWhiteSpace(computedColumn.ComputedExpression) &&
                computedColumn.ComputedExpression.Contains('*', StringComparison.Ordinal))
            {
                referencedColumns = ExtractComputedExpressionColumnNames(computedColumn.ComputedExpression, schema)
                    .Where(c => !c.Equals(computedColumn.ColumnName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(schema.GetColumn)
                    .Where(c => c != null &&
                                !c.IsComputed &&
                                !c.IsStoreGenerated &&
                                c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
                    .Cast<ColumnSchema>()
                    .ToList();
            }

            if (referencedColumns.Count == 0)
            {
                var quantity = schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    c.ColumnName.Contains("Quantity", StringComparison.OrdinalIgnoreCase));
                var measure = schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    IsMeasureLikeNumericColumn(c));

                if (quantity != null && measure != null)
                {
                    referencedColumns.Add(quantity);
                    referencedColumns.Add(measure);
                }
            }

            if (referencedColumns.Count != 2)
                return false;

            plan = new ComputedProductInsertPlan(computedColumn, referencedColumns[0], referencedColumns[1]);
            return true;
        }

        private static bool IsComputedNumericProductResultColumn(ColumnSchema column)
        {
            if (!column.IsComputed)
                return false;

            return column.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float ||
                   column.NumericPrecision.HasValue ||
                   column.NumericScale.HasValue ||
                   column.ColumnName.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Computed", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ExtractComputedExpressionColumnNames(string expression, TableSchema schema)
        {
            var knownColumns = schema.Columns
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var regex = new System.Text.RegularExpressions.Regex(
                @"\[(?<bracket>[^\]]+)\]|\b(?<bare>[A-Za-z_][A-Za-z0-9_]*)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in regex.Matches(expression))
            {
                var name = match.Groups["bracket"].Success
                    ? match.Groups["bracket"].Value
                    : match.Groups["bare"].Value;
                if (knownColumns.Contains(name))
                    yield return name;
            }
        }

        private static bool TryGetColumnPositiveMax(ColumnSchema column, out decimal max)
        {
            max = 0m;
            object? normalized;
            try
            {
                normalized = column.TypeCategory switch
                {
                    DataTypeCategory.Integer => SqlServerValueNormalizer.NormalizeValue(column, long.MaxValue),
                    DataTypeCategory.Decimal => SqlServerValueNormalizer.NormalizeValue(column, decimal.MaxValue),
                    DataTypeCategory.Float => SqlServerValueNormalizer.NormalizeValue(column, 999999999999999d),
                    DataTypeCategory.String when column.NumericPrecision.HasValue || column.NumericScale.HasValue =>
                        SqlServerValueNormalizer.NormalizeValue(
                            new ColumnSchema
                            {
                                TableName = column.TableName,
                                SchemaName = column.SchemaName,
                                ColumnName = column.ColumnName,
                                DataType = "decimal",
                                NumericPrecision = column.NumericPrecision,
                                NumericScale = column.NumericScale
                            },
                            decimal.MaxValue),
                    _ => null
                };
            }
            catch
            {
                return false;
            }

            return TryConvertDecimal(normalized, out max) && max > 0m;
        }

        private static bool TryGetComputedProductResultMax(
            ComputedProductInsertPlan plan,
            out decimal max)
        {
            if (!TryGetColumnPositiveMax(plan.ResultColumn, out max))
                return false;

            if (plan.ResultColumn.IsComputedTypeInferred &&
                TryGetInferredComputedProductResultMax(plan, out var inferredMax))
            {
                max = Math.Min(max, inferredMax);
            }

            return max > 0m;
        }

        private static bool TryGetHeuristicProductResultMax(
            ComputedProductInsertPlan plan,
            out decimal max)
        {
            if (plan.ResultColumn.IsComputed)
                return TryGetComputedProductResultMax(plan, out max);

            max = 0m;
            var measureBounds = new[] { plan.LeftColumn, plan.RightColumn }
                .Where(IsProductFactorMeasureColumn)
                .Select(factor => TryGetColumnPositiveMax(factor, out var factorMax) ? factorMax : 0m)
                .Where(factorMax => factorMax > 0m)
                .ToList();

            if (measureBounds.Count > 0)
            {
                max = measureBounds.Min();
                return max > 0m;
            }

            var sourceBounds = new[] { plan.LeftColumn, plan.RightColumn }
                .Select(factor => TryGetColumnPositiveMax(factor, out var factorMax) ? factorMax : 0m)
                .Where(factorMax => factorMax > 0m)
                .ToList();

            if (sourceBounds.Count == 0)
                return false;

            max = sourceBounds.Min();
            return max > 0m;
        }

        private static bool TryGetInferredComputedProductResultMax(
            ComputedProductInsertPlan plan,
            out decimal max)
        {
            max = 0m;
            var sourceBounds = new List<decimal>();

            foreach (var factor in new[] { plan.LeftColumn, plan.RightColumn })
            {
                if (factor.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float))
                    continue;

                if (TryGetColumnPositiveMax(factor, out var factorMax) && factorMax > 0m)
                    sourceBounds.Add(factorMax);
            }

            if (sourceBounds.Count == 0)
                return false;

            var measureBounds = new[] { plan.LeftColumn, plan.RightColumn }
                .Where(IsMeasureLikeNumericColumn)
                .Select(factor => TryGetColumnPositiveMax(factor, out var factorMax) ? factorMax : 0m)
                .Where(factorMax => factorMax > 0m)
                .ToList();

            max = measureBounds.Count > 0
                ? measureBounds.Min()
                : sourceBounds.Min();
            return max > 0m;
        }

        private static bool IsProductWithinMax(decimal left, decimal right, decimal max)
        {
            try
            {
                return Math.Abs(left * right) <= max;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool TryConvertDecimal(object? value, out decimal result)
        {
            result = 0m;
            if (value == null || value == DBNull.Value)
                return false;

            try
            {
                result = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMeasureLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Revenue", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Fee", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProductFactorMeasureColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
                   (name.Contains("Amount", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Total", StringComparison.OrdinalIgnoreCase)) ||
                   name.Contains("Fee", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsQuantityLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Quantity", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Qty", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("Qty", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsComputedProductCountFactorColumn(ColumnSchema column) =>
            IsQuantityLikeNumericColumn(column) ||
            IsInventoryLikeNumericColumn(column) ||
            IsCountLikeNumericColumn(column);

        private static bool IsInventoryLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Stock", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("OnHand", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Reorder", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Backorder", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCountLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Count", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("Cnt", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Cnt", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ComputedProductInsertPlan
        {
            public ComputedProductInsertPlan(ColumnSchema resultColumn, ColumnSchema leftColumn, ColumnSchema rightColumn)
            {
                ResultColumn = resultColumn;
                LeftColumn = leftColumn;
                RightColumn = rightColumn;
            }

            public ColumnSchema ResultColumn { get; }
            public ColumnSchema LeftColumn { get; }
            public ColumnSchema RightColumn { get; }
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
            ColumnSchema? column,
            object value,
            Dictionary<string, bool> cache,
            CancellationToken cancellationToken)
        {
            var normalizedValue = column == null
                ? value
                : SqlServerValueNormalizer.NormalizeValue(column, value) ?? DBNull.Value;
            var key = $"{schemaName}.{tableName}.{columnName}|{BuildValueKey(normalizedValue)}";
            if (cache.TryGetValue(key, out var cached))
                return cached;

            var sql = $"SELECT TOP (1) 1 FROM [{schemaName}].[{tableName}] WHERE [{columnName}] = @v;";
            using var cmd = CreateCommand(connection, transaction, sql);
            if (column == null)
                cmd.Parameters.AddWithValue("@v", normalizedValue);
            else
                AddTypedParameter(cmd, "@v", column, normalizedValue);

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
                .Where(c => c != null && !c.IsComputed && !c.IsStoreGenerated)
                .OrderBy(c => c!.OrdinalPosition)
                .Select(c => c!)
                .ToList();

            if (!columns.Any())
                return 0;

            var colList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
            var valueTokens = new List<string>();
            var parameters = new List<(string Name, ColumnSchema Column, object Value)>();

            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var value = row.GetValue(col.ColumnName) ?? DBNull.Value;
                var normalized = SqlServerValueNormalizer.NormalizeValue(col, value) ?? DBNull.Value;
                if (normalized is SqlExpressionValue expressionValue)
                {
                    valueTokens.Add(expressionValue.Expression);
                    continue;
                }

                var parameterName = $"@p{parameters.Count}";
                valueTokens.Add(parameterName);
                parameters.Add((parameterName, col, normalized));
            }

            var sql = $"INSERT INTO {GetQualifiedTableName(schema)} ({colList}) VALUES ({string.Join(", ", valueTokens)});";

            using var cmd = CreateCommand(connection, transaction, sql);
            foreach (var parameter in parameters)
            {
                AddTypedParameter(cmd, parameter.Name, parameter.Column, parameter.Value);
            }

            try
            {
                return await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (IsArithmeticOverflow(ex))
            {
                throw new InvalidOperationException(
                    BuildArithmeticOverflowDiagnostic(schema, row, columns, ex),
                    ex);
            }
        }

        private static bool IsArithmeticOverflow(SqlException ex) =>
            ex.Number == 8115 ||
            ex.Message.Contains("Arithmetic overflow", StringComparison.OrdinalIgnoreCase);

        private static bool IsArithmeticOverflow(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is SqlException sqlException && IsArithmeticOverflow(sqlException))
                    return true;

                if (current.Message.Contains("Arithmetic overflow", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string BuildArithmeticOverflowDiagnostic(
            TableSchema schema,
            GeneratedRow row,
            IReadOnlyList<ColumnSchema> columns,
            SqlException ex)
        {
            var numericValues = columns
                .Where(c => c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
                .Select(c => $"{c.ColumnName}={FormatDiagnosticValue(row.GetValue(c.ColumnName))} ({c.EffectiveDataType}{FormatNumericShape(c)})")
                .ToList();

            var detail = numericValues.Count == 0
                ? "No numeric insert parameters were present."
                : string.Join(", ", numericValues);

            return $"Arithmetic overflow while inserting [{schema.SchemaName}.{schema.TableName}]. " +
                   $"Numeric values: {detail}. SQL Server said: {ex.Message}";
        }

        private static string FormatNumericShape(ColumnSchema column)
        {
            if (column.TypeCategory != DataTypeCategory.Decimal)
                return string.Empty;

            return column.NumericPrecision.HasValue || column.NumericScale.HasValue
                ? $"({column.NumericPrecision?.ToString() ?? "?"},{column.NumericScale?.ToString() ?? "?"})"
                : string.Empty;
        }

        private static string FormatDiagnosticValue(object? value)
        {
            if (value == null || value == DBNull.Value)
                return "NULL";

            if (value is byte[] bytes)
                return $"0x{Convert.ToHexString(bytes.Take(16).ToArray())}{(bytes.Length > 16 ? "..." : string.Empty)}";

            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length <= 80 ? text : text[..80] + "...";
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

            switch (column.EffectiveDataType.ToLowerInvariant())
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
            return column.EffectiveDataType.ToLowerInvariant() switch
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
                "rowversion" => SqlDbType.Binary,
                "timestamp" => SqlDbType.Binary,
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

        private sealed class ChildForeignKeyRef
        {
            public string ChildTableName { get; }
            public string ChildSchemaName { get; }
            public string ChildColumnName { get; }
            public string ReferencedColumnName { get; }

            public ChildForeignKeyRef(
                string childTableName,
                string childSchemaName,
                string childColumnName,
                string referencedColumnName)
            {
                ChildTableName = childTableName;
                ChildSchemaName = childSchemaName;
                ChildColumnName = childColumnName;
                ReferencedColumnName = referencedColumnName;
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
        public List<DirectInsertTableInfo> InsertedTables { get; set; } = new();
    }

    public class DirectInsertTableInfo
    {
        public string Key { get; set; } = string.Empty;
        public string SchemaName { get; set; } = "dbo";
        public string TableName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int InsertedRowCount { get; set; }
    }
}
