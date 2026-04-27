using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.DataGeneration.ValueGenerators;
using SqlTestDataGenerator.Parsing;
using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Core engine that orchestrates the generation of test data for each branch scenario.
    /// Combines parsed query info + schema metadata + condition solving to produce INSERT data.
    /// </summary>
    public class DataGenerationEngine
    {
        private readonly ValueGeneratorFactory _valueFactory = new();
        private readonly DependencyOrderResolver _orderResolver = new();
        private int _fallbackSeedCounter = 1;
        private Dictionary<string, int> _tableIdMaxValues = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _generationLocalNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
        private DateTime _generationUtcNow = DateTime.UtcNow;

        /// <summary>
        /// Fallback starting seed for generated data when database-backed per-table seeds are unavailable.
        /// </summary>
        public int StartId { get; set; } = 1;

        /// <summary>
        /// Optional per-table starting IDs resolved from the connected database.
        /// Keys are table names, values are the next available numeric key for that table.
        /// </summary>
        public Dictionary<string, int>? TableSeedStarts { get; set; }

        /// <summary>
        /// Optional sample rows loaded from the connected database.
        /// Keys are table names, values are sample row dictionaries keyed by column name.
        /// </summary>
        public Dictionary<string, Dictionary<string, object?>>? SampleRowsByTable { get; set; }

        /// <summary>
        /// When enabled, unconstrained strings are expanded to max length and numerics are pushed toward max value.
        /// When disabled, unconstrained values are synthesized from database sample rows when available.
        /// </summary>
        public bool UseMaxLengthMaxValueMode { get; set; }

        /// <summary>
        /// Target number of rows generated per table for each selected scenario.
        /// </summary>
        public int RowsPerTable { get; set; } = 1;

        /// <summary>
        /// Generate test data for all scenarios.
        /// </summary>
        public GeneratedDataSet Generate(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            List<BranchScenario> scenarios)
        {
            _fallbackSeedCounter = StartId;
            _tableIdMaxValues = BuildTableIdMaxValues(schemas);
            _generationLocalNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
            _generationUtcNow = DateTime.UtcNow;
            var dataSet = new GeneratedDataSet
            {
                OriginalSql = query.OriginalSql
            };

            // Build one unified generation scope up front:
            // main query tables + subquery tables + all FK ancestors.
            var generationScope = CollectGenerationScope(query, schemas);
            var tableNames = generationScope
                .Where(t => schemas.TryGetValue(t, out var s) && s.Columns.Any())
                .ToList();
            List<string> insertOrder;

            try
            {
                insertOrder = _orderResolver.ResolveInsertOrder(tableNames, schemas);
            }
            catch
            {
                insertOrder = _orderResolver.ResolveFromJoins(query);
            }

            var nextTableIds = InitializeNextTableIds(tableNames, schemas);

            foreach (var tableName in tableNames)
            {
                if (!insertOrder.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                {
                    insertOrder.Add(tableName);
                }
            }

            foreach (var scenario in scenarios)
            {
                var workingScenario = CloneScenarioDescriptor(scenario);
                workingScenario.InsertOrder = new List<string>(insertOrder);
                GenerateScenarioData(workingScenario, query, schemas, insertOrder, nextTableIds);
                EnforceScenarioForeignKeyClosure(workingScenario, schemas);
                ValidateScenarioLocalForeignKeys(workingScenario, schemas);
                dataSet.Scenarios.Add(workingScenario);
            }

            return dataSet;
        }

        /// <summary>
        /// Generate data for test scenarios without database schema (offline mode).
        /// Uses column types inferred from the SQL conditions.
        /// </summary>
        public GeneratedDataSet GenerateWithoutSchema(ParsedQuery query, List<BranchScenario> scenarios)
        {
            // Create minimal schemas from query analysis
            var schemas = InferSchemasFromQuery(query);
            return Generate(query, schemas, scenarios);
        }

        // ═════════════════════════════════════════════════════════════════
        // Scenario generation
        // ═════════════════════════════════════════════════════════════════

        private void GenerateScenarioData(
            BranchScenario scenario,
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            List<string> insertOrder,
            Dictionary<string, int> nextTableIds)
        {
            var selfReferencePlans = BuildSelfReferencePlans(query, schemas);

            switch (scenario.Type)
            {
                case ScenarioType.Positive:
                    GeneratePositiveData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;

                case ScenarioType.WhereNegative:
                    GenerateWhereNegativeData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;

                case ScenarioType.HavingNegative:
                    GenerateHavingNegativeData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;

                case ScenarioType.JoinMiss:
                    GenerateJoinMissData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;

                case ScenarioType.SubqueryMiss:
                    GenerateSubqueryMissData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;

                case ScenarioType.Boundary:
                    GenerateBoundaryData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans);
                    break;
            }
        }

        private int GetRequestedRowCount() => Math.Max(1, RowsPerTable);

        private Dictionary<string, int> InitializeNextTableIds(
            IEnumerable<string> tableNames,
            Dictionary<string, TableSchema> schemas)
        {
            var nextIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in tableNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (TableSeedStarts != null &&
                    TableSeedStarts.TryGetValue(tableName, out var resolvedSeed) &&
                    resolvedSeed > 0 &&
                    IsSeedWithinKeyRange(schema, resolvedSeed))
                {
                    nextIds[tableName] = resolvedSeed;
                    continue;
                }

                nextIds[tableName] = ResolveFallbackSeedForTable(schema);
            }

            return nextIds;
        }

        private int AllocateTableIdBlock(
            Dictionary<string, int> nextTableIds,
            string tableName,
            int rowCount)
        {
            if (rowCount <= 0)
                return 0;

            if (!nextTableIds.TryGetValue(tableName, out var nextId) || nextId <= 0)
            {
                nextId = 1;
                nextTableIds[tableName] = nextId;
            }

            if (_tableIdMaxValues.TryGetValue(tableName, out var maxValue) &&
                maxValue > 0 &&
                nextId + rowCount - 1 > maxValue)
            {
                throw new InvalidOperationException(
                    $"Cannot allocate {rowCount} key value(s) for table [{tableName}] within the supported range. " +
                    $"Next value {nextId} would exceed max key {maxValue}.");
            }

            nextTableIds[tableName] = nextId + rowCount + 1;
            return nextId;
        }

        private static int ResolveFallbackSeedForTable(TableSchema? schema)
        {
            var keyColumn = ResolveNumericKeyColumn(schema);
            if (keyColumn == null)
                return 1;

            return keyColumn.DataType.Equals("tinyint", StringComparison.OrdinalIgnoreCase) ? 1 : 1;
        }

        private static bool IsSeedWithinKeyRange(TableSchema? schema, int seed)
        {
            var keyColumn = ResolveNumericKeyColumn(schema);
            if (keyColumn == null)
                return seed > 0;

            return keyColumn.DataType.ToLowerInvariant() switch
            {
                "tinyint" => seed >= byte.MinValue && seed <= byte.MaxValue,
                "smallint" => seed >= short.MinValue && seed <= short.MaxValue,
                "int" => true,
                "bigint" => true,
                _ => seed > 0
            };
        }

        private static Dictionary<string, int> BuildTableIdMaxValues(Dictionary<string, TableSchema> schemas)
        {
            var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var schema in schemas.Values)
            {
                var keyColumn = ResolveNumericKeyColumn(schema);
                if (keyColumn == null)
                    continue;

                var max = GetIntegerTypeMaxValue(keyColumn);

                foreach (var dependentSchema in schemas.Values)
                {
                    foreach (var fk in dependentSchema.ForeignKeys.Where(f =>
                                 f.ReferencedTable.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var fkColumn = dependentSchema.GetColumn(fk.ColumnName);
                        if (fkColumn?.TypeCategory != DataTypeCategory.Integer)
                            continue;

                        max = Math.Min(max, GetIntegerTypeMaxValue(fkColumn));
                    }
                }

                limits[schema.TableName] = max;
            }

            return limits;
        }

        private static int GetIntegerTypeMaxValue(ColumnSchema column)
        {
            return column.DataType.ToLowerInvariant() switch
            {
                "tinyint" => byte.MaxValue,
                "smallint" => short.MaxValue,
                "int" => int.MaxValue,
                _ => int.MaxValue
            };
        }

        private static ColumnSchema? ResolveNumericKeyColumn(TableSchema? schema)
        {
            if (schema == null)
                return null;

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

        // ─── Positive scenario: all conditions satisfied ────────────────
        private void GeneratePositiveData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // Determine how many rows we need for HAVING conditions
            int rowMultiplier = DetermineRowMultiplier(query);
            var specialMinimumRows = BuildSpecialMinimumRowRequirements(schemas, query);

            // Generate rows for each table in dependency order
            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                // For tables that contribute to aggregates, create multiple rows
                bool isAggregateSource = IsAggregateSourceTable(tableName, alias, query);
                int rowCount = DetermineScenarioRowCount(
                    tableName,
                    alias,
                    schema,
                    query,
                    isAggregateSource,
                    rowMultiplier,
                    GetRequestedRowCount());
                if (specialMinimumRows.TryGetValue(tableName, out var specialMinimum))
                {
                    rowCount = Math.Max(rowCount, specialMinimum);
                }
                rowCount = ApplySelfReferenceMinimumRowCount(tableName, rowCount, selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    ApplySemanticRowAdjustments(query, scenario, schema, row, rowIdx);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── WHERE negative: one condition violated ─────────────────────
        private void GenerateWhereNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = ApplySelfReferenceMinimumRowCount(
                    tableName,
                    GetRequestedRowCount(),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── HAVING negative: aggregate condition fails ─────────────────
        private void GenerateHavingNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            var testedConditions = query.EnumerateScopeConditions(ConditionSource.Having)
                .Where(c => !GetDesiredTruthForCondition(scenario, c, true))
                .ToList();

            var countRowOverrides = BuildCountNegativeRowOverrides(query, schemas, testedConditions);

            // For HAVING failures, we create fewer rows or rows with small values
            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                bool isAggregateSource = IsAggregateSourceTable(tableName, alias, query);

                // For COUNT fail: create only 1 row (instead of the required minimum)
                // For SUM fail: create rows with very small values
                int rowCount = isAggregateSource && testedConditions.Any(c => c.AggregateFunc == AggregateFunction.Count)
                    ? 1
                    : GetRequestedRowCount();

                if (countRowOverrides.TryGetValue(tableName, out var overriddenCount))
                {
                    rowCount = overriddenCount;
                }

                rowCount = ApplySelfReferenceMinimumRowCount(tableName, rowCount, selfReferencePlans);
                if (rowCount <= 0)
                {
                    continue;
                }
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        // For SUM/AVG failures, use tiny values for the aggregate column
                        bool useSmallValue = isAggregateSource &&
                            testedConditions.Any(c =>
                                c.AggregateFunc is AggregateFunction.Sum or AggregateFunction.Avg &&
                                IsConditionTargetingColumn(query, c, alias, col.ColumnName));

                        object? value;
                        if (useSmallValue && col.TypeCategory is DataTypeCategory.Decimal or DataTypeCategory.Integer or DataTypeCategory.Float)
                        {
                            value = 1; // Tiny value to make SUM fail
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── JOIN miss: LEFT/RIGHT join with no match ───────────────────
        private void GenerateJoinMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // Find which join is being tested
            var testedJoin = query.Joins
                .FirstOrDefault(j => BuildJoinScenarioKey(j).Equals(scenario.JoinKey, StringComparison.OrdinalIgnoreCase));

            string? missTableAlias = testedJoin?.RightTableAlias;
            string? missTableName = missTableAlias != null ? query.ResolveAlias(missTableAlias) : null;

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                // Skip the table that should have no match (don't insert any row for it)
                if (tableName.Equals(missTableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = ApplySelfReferenceMinimumRowCount(
                    tableName,
                    GetRequestedRowCount(),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        // For the FK column that references the missing table, use a non-existent ID
                        bool isMissingFK = testedJoin != null &&
                            col.ColumnName.Equals(testedJoin.LeftColumn, StringComparison.OrdinalIgnoreCase) &&
                            alias.Equals(testedJoin.LeftTableAlias, StringComparison.OrdinalIgnoreCase);

                        object? value;
                        if (isMissingFK)
                        {
                            value = col.TypeCategory == DataTypeCategory.Integer
                                ? SqlServerValueNormalizer.NormalizeValue(col, GetIntegerTypeMaxValue(col)) ?? GetIntegerTypeMaxValue(col)
                                : GenerateDefaultColumnValue(col, _valueFactory.GetGenerator(col.TypeCategory), rowIdx, query, alias);
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }
        }

        // ─── Subquery miss: value not in subquery result ────────────────
        private void GenerateSubqueryMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = ApplySelfReferenceMinimumRowCount(
                    tableName,
                    GetRequestedRowCount(),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── Boundary: values at exact boundary ────────────────────────
        private void GenerateBoundaryData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            // Similar to positive, but range conditions use exact boundary values
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            var testedCondition = scenario.BoundaryConditionKey == null
                ? null
                : FindConditionByKey(query, scenario.BoundaryConditionKey);

            int rowMultiplier = DetermineRowMultiplier(query);
            var specialMinimumRows = BuildSpecialMinimumRowRequirements(schemas, query);
            int? countBoundaryRows = testedCondition != null &&
                                     testedCondition.AggregateFunc is AggregateFunction.Count or AggregateFunction.CountDistinct &&
                                     TryDetermineRequiredCountRows(testedCondition, out var requiredBoundaryRows)
                ? requiredBoundaryRows
                : null;
            var countBoundaryTargetTable = testedCondition != null &&
                                           testedCondition.AggregateFunc is AggregateFunction.Count or AggregateFunction.CountDistinct
                ? query.ResolveAlias(testedCondition.TableAlias)
                : null;

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                bool isAggSource = IsAggregateSourceTable(tableName, alias, query);
                int rowCount = DetermineScenarioRowCount(
                    tableName,
                    alias,
                    schema,
                    query,
                    isAggSource,
                    rowMultiplier,
                    GetRequestedRowCount());
                if (specialMinimumRows.TryGetValue(tableName, out var specialMinimum))
                {
                    rowCount = Math.Max(rowCount, specialMinimum);
                }
                if (countBoundaryRows.HasValue &&
                    !string.IsNullOrWhiteSpace(countBoundaryTargetTable) &&
                    tableName.Equals(countBoundaryTargetTable, StringComparison.OrdinalIgnoreCase))
                {
                    rowCount = countBoundaryRows.Value;
                }
                rowCount = ApplySelfReferenceMinimumRowCount(tableName, rowCount, selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        bool isBoundaryColumn = testedCondition != null &&
                            testedCondition.AggregateFunc is not AggregateFunction.Count and not AggregateFunction.CountDistinct &&
                            IsConditionTargetingColumn(query, testedCondition, alias, col.ColumnName);

                        object? value;
                        if (isBoundaryColumn)
                        {
                            value = GenerateBoundaryValue(col, testedCondition!);
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    ApplySemanticRowAdjustments(query, scenario, schema, row, rowIdx);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, currentId, rowCount, selfReferencePlans);
            }

        }

        // ═════════════════════════════════════════════════════════════════
        // Column value generation
        // ═════════════════════════════════════════════════════════════════

        private object? GenerateColumnValue(
            BranchScenario scenario,
            ColumnSchema col, string tableAlias, ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, List<int>> referenceableTableIds,
            Dictionary<string, List<int>> referencedTableIdPools,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            int rowId,
            bool satisfy, int rowIndex,
            bool includeSubqueryConditions,
            GeneratedRow? currentRow = null)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);
            var currentTableName = col.TableName;

            // Always generate explicit values for IDENTITY columns so scripts can include full rows.
            if (col.IsIdentity)
            {
                return rowId;
            }

            // 1. Check if this column has a FK → use the parent table's ID
            var schema = schemas.Values.FirstOrDefault(s =>
                s.Columns.Any(c => c.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase)) &&
                s.TableName != col.ColumnName);

            var parentSchema = schemas.Values.FirstOrDefault(s =>
                s.TableName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase));

            // Check FK references
            var currentTableSchema = schemas.Values.FirstOrDefault(s =>
                s.Columns.Contains(col));

            if (currentTableSchema != null)
            {
                var fk = currentTableSchema.ForeignKeys
                    .FirstOrDefault(f => f.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase));

                if (fk != null &&
                    fk.ReferencedTable.Equals(currentTableSchema.TableName, StringComparison.OrdinalIgnoreCase) &&
                    TryGenerateSelfReferenceValue(currentTableSchema, col, rowId, rowIndex, selfReferencePlans, out var selfReferenceValue))
                {
                    return selfReferenceValue;
                }

                if (fk != null &&
                    TryResolvePairPatternRelatedRowId(
                        currentTableSchema,
                        fk,
                        rowIndex,
                        query,
                        referenceableTableIds,
                        tableRowIds,
                        out var pairPatternRelatedId))
                {
                    return pairPatternRelatedId;
                }

                if (fk != null && TryResolveRelatedRowId(referenceableTableIds, fk.ReferencedTable, rowIndex, out var fkId))
                {
                    return fkId;
                }

                if (fk != null && TryResolveRelatedRowId(tableRowIds, fk.ReferencedTable, rowIndex, out fkId))
                {
                    return fkId;
                }

                if (fk != null)
                {
                    var referencedSchema = schemas.GetValueOrDefault(fk.ReferencedTable);
                    var referencedColumn = referencedSchema?.GetColumn(fk.ReferencedColumn);
                    if (ShouldResolveForeignKeyByValue(col, referencedColumn) &&
                        TryResolveRelatedColumnValue(
                            scenario,
                            fk.ReferencedTable,
                            fk.ReferencedColumn,
                            rowIndex,
                            out var fkColumnValue,
                            currentRow,
                            currentTableName))
                    {
                        return fkColumnValue;
                    }
                }

                if (fk != null &&
                    !fk.ReferencedTable.Equals(currentTableSchema.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    return GetOrCreateReferencedRowId(referencedTableIdPools, fk.ReferencedTable, rowIndex, rowId);
                }

                if (fk != null &&
                    col.IsNullable &&
                    fk.ReferencedTable.Equals(currentTableSchema.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            // 2. Check if this is a PK column → use the rowId
            // 2. Resolve all direct column predicates together (instead of first match only).
            var conditionTargets = GetApplicableConditionTargets(
                scenario,
                query,
                query.EnumerateScopeConditions(ConditionSource.Where),
                currentTableName,
                tableAlias,
                col.ColumnName,
                excludeHasSubquery: true);

            conditionTargets.AddRange(GetApplicableConditionTargets(
                scenario,
                query,
                query.EnumerateScopeConditions(ConditionSource.JoinOn),
                currentTableName,
                tableAlias,
                col.ColumnName,
                excludeHasSubquery: false));

            if (includeSubqueryConditions)
            {
                conditionTargets.AddRange(FindApplicableSubqueryConditionTargets(
                    scenario,
                    query,
                    currentTableName,
                    tableAlias,
                    col.ColumnName));
            }

            conditionTargets.AddRange(GetApplicableAggregateTargets(
                scenario,
                query,
                tableAlias,
                col.ColumnName));

            if (conditionTargets.Count > 0)
            {
                var resolvedValue = ResolveColumnValueFromTargets(
                    scenario,
                    query,
                    col,
                    generator,
                    conditionTargets,
                    tableRowIds,
                    tableAlias,
                    rowIndex,
                    currentRow);

                if (resolvedValue.Resolved)
                {
                    return resolvedValue.Value;
                }
            }

            // 4. Check JOIN conditions
            var joinCondition = query.Joins
                .Where(j =>
                    (j.RightColumn.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                     j.RightTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)) ||
                    (j.LeftColumn.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                     j.LeftTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();

            if (joinCondition != null)
            {
                var otherAlias = joinCondition.LeftTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)
                    ? joinCondition.RightTableAlias
                    : joinCondition.LeftTableAlias;
                var otherColumn = joinCondition.LeftTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)
                    ? joinCondition.RightColumn
                    : joinCondition.LeftColumn;

                var otherTable = query.ResolveAlias(otherAlias);
                if (TryResolveJoinedColumnValue(scenario, otherTable, otherColumn, rowIndex, out var joinedValue))
                {
                    return joinedValue;
                }

                if (TryResolveRelatedRowId(referenceableTableIds, otherTable, rowIndex, out var joinId) ||
                    TryResolveRelatedRowId(tableRowIds, otherTable, rowIndex, out joinId))
                {
                    return joinId;
                }
            }

            // 5. Default value generation
            return GenerateDefaultColumnValue(col, generator, rowIndex, query, tableAlias);
        }

        private static bool TryResolveJoinedColumnValue(
            BranchScenario scenario,
            string tableName,
            string columnName,
            int rowIndex,
            out object? value)
        {
            return TryResolveRelatedColumnValue(scenario, tableName, columnName, rowIndex, out value);
        }

        private static bool TryResolveRelatedColumnValue(
            BranchScenario scenario,
            string tableName,
            string columnName,
            int rowIndex,
            out object? value,
            GeneratedRow? currentRow = null,
            string? currentTableName = null)
        {
            value = null;
            if (currentRow != null &&
                !string.IsNullOrWhiteSpace(currentTableName) &&
                tableName.Equals(currentTableName, StringComparison.OrdinalIgnoreCase))
            {
                var currentValue = currentRow.GetValue(columnName);
                if (currentValue != null && currentValue != DBNull.Value)
                {
                    value = currentValue;
                    return true;
                }
            }

            if (!scenario.TableRows.TryGetValue(tableName, out var rows) || rows.Count == 0)
                return false;

            var resolvedIndex = Math.Min(Math.Max(0, rowIndex), rows.Count - 1);
            var candidate = rows[resolvedIndex].GetValue(columnName);
            if (candidate == null || candidate == DBNull.Value)
                return false;

            value = candidate;
            return true;
        }

        private static bool ShouldResolveForeignKeyByValue(ColumnSchema foreignKeyColumn, ColumnSchema? referencedColumn)
        {
            if (referencedColumn == null)
                return foreignKeyColumn.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float);

            return foreignKeyColumn.TypeCategory switch
            {
                DataTypeCategory.String => true,
                DataTypeCategory.Guid => true,
                DataTypeCategory.DateTime => true,
                DataTypeCategory.DateTimeOffset => true,
                DataTypeCategory.Time => true,
                DataTypeCategory.Boolean => true,
                DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float =>
                    referencedColumn.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float),
                _ => true
            };
        }

        private object GenerateDefaultColumnValue(
            ColumnSchema col,
            IValueGenerator generator,
            int rowIndex,
            ParsedQuery query,
            string tableAlias)
        {
            if (col.TypeCategory == DataTypeCategory.String &&
                TryBuildQueryFunctionHintedString(col, rowIndex, query, out var hintedString))
            {
                return hintedString;
            }

            if (UseMaxLengthMaxValueMode)
            {
                var maxModeValue = GenerateMaxLengthMaxValue(col, rowIndex, query, tableAlias);
                if (maxModeValue != null)
                {
                    return maxModeValue;
                }
            }

            if (TryGetSampleValue(col, out var sampleValue))
            {
                var sampleModeValue = GenerateSampleBasedValue(col, sampleValue, rowIndex);
                if (sampleModeValue != null)
                {
                    return sampleModeValue;
                }
            }

            return generator.GenerateDefault(col);
        }

        private bool TryBuildQueryFunctionHintedString(
            ColumnSchema column,
            int rowIndex,
            ParsedQuery query,
            out object value)
        {
            value = string.Empty;
            var targetLength = UseMaxLengthMaxValueMode
                ? ResolveTargetStringLength(column)
                : Math.Min(ResolveTargetStringLength(column), 96);
            if (targetLength <= 0)
                return false;

            if (!TryCollectQueryFunctionStringHints(column, query, out var hints, out var needsAsciiInitial))
                return false;

            var rowToken = (rowIndex + 1).ToString("D3");
            var seed = hints.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "A";
            var baseText = Convert.ToString(BuildSemanticString(column, rowIndex, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var candidate = needsAsciiInitial
                ? $"A{seed}{baseText}Z"
                : $"{seed}{baseText}Z";

            candidate = UseMaxLengthMaxValueMode
                ? RepeatPhraseToExactLength(candidate, rowToken, targetLength)
                : FitSemanticString(candidate, rowToken, targetLength);

            value = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
            return true;
        }

        private static bool TryCollectQueryFunctionStringHints(
            ColumnSchema column,
            ParsedQuery query,
            out List<string> hints,
            out bool needsAsciiInitial)
        {
            hints = new List<string>();
            needsAsciiInitial = false;
            if (string.IsNullOrWhiteSpace(query.OriginalSql) ||
                string.IsNullOrWhiteSpace(column.ColumnName))
            {
                return false;
            }

            var escapedColumn = System.Text.RegularExpressions.Regex.Escape(column.ColumnName);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         query.OriginalSql,
                         @"CHARINDEX\s*\(\s*N?'(?<needle>[^']+)'\s*,(?:(?!\)).)*\b" + escapedColumn + @"\b",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var needle = match.Groups["needle"].Value;
                if (!string.IsNullOrWhiteSpace(needle))
                    hints.Add(needle);
            }

            needsAsciiInitial = System.Text.RegularExpressions.Regex.IsMatch(
                query.OriginalSql,
                @"(?:UPPER\s*\()?\s*LEFT\s*\(\s*TRIM\s*\([^)]*\b" + escapedColumn + @"\b[^)]*\)\s*,\s*1\s*\)\s*\)?\s+BETWEEN\s+N?'A'\s+AND\s+N?'Z'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            return hints.Count > 0 || needsAsciiInitial;
        }

        private bool TryGetSampleValue(ColumnSchema column, out object? sampleValue)
        {
            sampleValue = null;
            if (SampleRowsByTable == null)
                return false;

            if (!SampleRowsByTable.TryGetValue(column.TableName, out var row))
                return false;

            return row.TryGetValue(column.ColumnName, out sampleValue);
        }

        private object? GenerateMaxLengthMaxValue(ColumnSchema column, int rowIndex, ParsedQuery query, string tableAlias)
        {
            return column.TypeCategory switch
            {
                DataTypeCategory.String => BuildMaxLengthString(column, rowIndex),
                DataTypeCategory.Integer => BuildMaxNumericValue(column, rowIndex, query, tableAlias),
                DataTypeCategory.Decimal => BuildMaxNumericValue(column, rowIndex, query, tableAlias),
                DataTypeCategory.Float => BuildMaxNumericValue(column, rowIndex, query, tableAlias),
                _ => null
            };
        }

        private object? GenerateSampleBasedValue(ColumnSchema column, object? sampleValue, int rowIndex)
        {
            if (sampleValue == null || sampleValue == DBNull.Value)
                return null;

            return column.TypeCategory switch
            {
                DataTypeCategory.String => MutateSampleString(column, sampleValue, rowIndex),
                DataTypeCategory.Integer => MutateSampleNumeric(column, sampleValue, rowIndex),
                DataTypeCategory.Decimal => MutateSampleNumeric(column, sampleValue, rowIndex),
                DataTypeCategory.Float => MutateSampleNumeric(column, sampleValue, rowIndex),
                DataTypeCategory.DateTime => MutateSampleDateTime(column, sampleValue, rowIndex),
                DataTypeCategory.DateTimeOffset => MutateSampleDateTimeOffset(column, sampleValue, rowIndex),
                DataTypeCategory.Time => MutateSampleTime(column, sampleValue, rowIndex),
                DataTypeCategory.Boolean => MutateSampleBoolean(column, sampleValue, rowIndex),
                DataTypeCategory.Guid => Guid.NewGuid(),
                _ => SqlServerValueNormalizer.NormalizeValue(column, sampleValue)
            };
        }

        private object? GenerateConditionValue(
            BranchScenario scenario,
            ParsedQuery query,
            ColumnSchema col,
            IValueGenerator generator,
            ConditionInfo condition,
            bool satisfy,
            object? comparisonValue = null,
            string? tableAlias = null,
            int rowIndex = 0)
        {
            if (comparisonValue != null)
            {
                var comparisonLiteral = FormatComparisonLiteral(comparisonValue);
                if (satisfy)
                    return generator.GenerateSatisfying(col, ConditionOpToString(condition.Operator), comparisonLiteral);

                return generator.GenerateViolating(
                    col,
                    ConditionOpToString(condition.Operator),
                    comparisonLiteral);
            }

            var opStr = ConditionOpToString(condition.Operator);

            if (condition.Operator == ComparisonOp.Between)
            {
                var inside = condition.IsNegated ? !satisfy : satisfy;
                return GenerateBetweenValue(col, condition.Value, condition.SecondValue, inside);
            }

            if (condition.Operator == ComparisonOp.In && condition.InValues.Any())
            {
                return satisfy
                    ? generator.GenerateFromLiteral(condition.InValues[0], col)
                    : generator.GenerateViolating(col, "=", condition.InValues[0]);
            }

            if (condition.Operator == ComparisonOp.IsNull)
            {
                return satisfy ? null : generator.GenerateDefault(col);
            }

            if (condition.Operator == ComparisonOp.IsNotNull)
            {
                return satisfy ? generator.GenerateDefault(col) : null;
            }

            if (TryGenerateExpressionConditionValue(condition, col, tableAlias, satisfy, out var expressionValue))
            {
                return expressionValue;
            }

            if (condition.Operator == ComparisonOp.Like)
            {
                var shouldMatchLike = condition.IsNegated ? !satisfy : satisfy;
                return shouldMatchLike
                    ? generator.GenerateSatisfying(col, "LIKE", condition.LikePattern)
                    : generator.GenerateViolating(col, "LIKE", condition.LikePattern);
            }

            if (satisfy &&
                UseMaxLengthMaxValueMode &&
                col.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                tableAlias != null)
            {
                var maxCandidate = GenerateMaxLengthMaxValue(col, rowIndex, query, tableAlias);
                if (EvaluateCondition(maxCandidate, condition, comparisonValue, col, generator))
                {
                    return maxCandidate;
                }
            }

            if (satisfy && !UseMaxLengthMaxValueMode && IsRangeCondition(condition))
            {
                return GenerateBoundaryValue(col, condition);
            }

            return satisfy
                ? generator.GenerateSatisfying(col, opStr, condition.Value)
                : generator.GenerateViolating(col, opStr, condition.Value);
        }

        private bool TryGenerateExpressionConditionValue(
            ConditionInfo condition,
            ColumnSchema column,
            string? tableAlias,
            bool satisfy,
            out object? value)
        {
            value = null;
            if (!satisfy ||
                string.IsNullOrWhiteSpace(tableAlias) ||
                column.TypeCategory != DataTypeCategory.String ||
                condition.Operator != ComparisonOp.Equal ||
                string.IsNullOrEmpty(condition.Value))
            {
                return false;
            }

            var leftRefs = condition.ReferencedColumns
                .Where(r => !r.IsRightSide)
                .ToList();
            if (leftRefs.Count == 0)
                return false;

            var matchIndex = leftRefs.FindIndex(r =>
                r.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(r.TableAlias) ||
                 r.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) ||
                 r.TableAlias.Equals(column.TableName, StringComparison.OrdinalIgnoreCase)));
            if (matchIndex < 0)
                return false;

            value = matchIndex == 0 ? condition.Value : string.Empty;
            return true;
        }

        private object BuildMaxLengthString(ColumnSchema column, int rowIndex)
        {
            var targetLength = ResolveTargetStringLength(column);
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D4");
            object value = IsPhoneColumn(column)
                ? BuildPhoneLikeString(column, rowIndex, targetLength)
                : IsEmailColumn(column)
                    ? BuildExactMaxEmailString(column, rowToken, targetLength)
                    : IsUrlColumn(column)
                        ? BuildExactMaxUrlString(column, rowToken, targetLength)
                        : IsCodeLikeColumn(column)
                            ? BuildExactCompactToken(null, $"{GetAsciiTableLabel(column)}{GetAsciiColumnLabel(column)}", rowToken, targetLength)
                            : RepeatPhraseToExactLength(BuildLocalizedPhrase(column, rowIndex, null), rowToken, targetLength);

            return SqlServerValueNormalizer.NormalizeValue(column, value) ?? string.Empty;
        }

        private object BuildMaxNumericValue(ColumnSchema column, int rowIndex)
        {
            return BuildMaxNumericValue(column, rowIndex, query: null, tableAlias: column.TableName);
        }

        private object BuildMaxNumericValue(ColumnSchema column, int rowIndex, ParsedQuery? query, string tableAlias)
        {
            var offset = rowIndex + GetColumnVariantOffset(column);

            return column.TypeCategory switch
            {
                DataTypeCategory.Integer => SqlServerValueNormalizer.NormalizeValue(column, GetPracticalMaxIntegerValue(column, query, tableAlias) - offset) ?? 0,
                DataTypeCategory.Decimal => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    GetPracticalMaxDecimalValue(column, query, tableAlias) - (offset * GetNumericStep(column))) ?? 0m,
                DataTypeCategory.Float => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    GetPracticalMaxFloatValue(column, query, tableAlias) - offset) ?? 0d,
                _ => 0
            };
        }

        private object MutateSampleString(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var source = Convert.ToString(sampleValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrEmpty(source) || IsSyntheticSampleString(source))
            {
                return BuildSemanticString(column, rowIndex, null);
            }

            return BuildSourceDerivedString(column, source, rowIndex);
        }

        private object BuildFallbackSampleString(ColumnSchema column, int rowIndex)
        {
            return BuildSemanticString(column, rowIndex, null);
        }

        private object BuildSemanticString(ColumnSchema column, int rowIndex, string? source)
        {
            var targetLength = Math.Min(ResolveTargetStringLength(column), 96);
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D3");
            var semanticSource = ExtractSemanticSourceFragment(source);
            var asciiSeed = $"{GetAsciiTableLabel(column)}{GetAsciiColumnLabel(column)}";
            var useJapanese = SupportsJapaneseText(column);

            string candidate;
            if (IsEmailColumn(column))
            {
                var localPart = BuildCompactToken(semanticSource, asciiSeed, rowToken, Math.Max(1, targetLength - "@sample.jp".Length));
                candidate = $"{localPart.ToLowerInvariant()}@sample.jp";
            }
            else if (LooksLikeUrl(source ?? string.Empty) || IsUrlColumn(column))
            {
                var host = BuildCompactToken(semanticSource, asciiSeed, rowToken, Math.Max(1, targetLength - "https://.sample.jp".Length));
                candidate = $"https://{host.ToLowerInvariant()}.sample.jp";
            }
            else if (IsPhoneColumn(column))
            {
                candidate = BuildPhoneLikeString(column, rowIndex, targetLength);
            }
            else if (IsCodeLikeColumn(column))
            {
                candidate = BuildCompactToken(semanticSource, asciiSeed, rowToken, targetLength);
            }
            else if (IsTierLikeColumn(column))
            {
                var tiers = useJapanese
                    ? new[] { "標準", "優先", "上位", "特別", "最上" }
                    : new[] { "Standard", "Priority", "Premium", "Special", "Highest" };
                var tier = tiers[(rowIndex + GetColumnVariantOffset(column)) % tiers.Length];
                candidate = ComposeSemanticLabel(tier, GetLocalizedTableLabel(column.TableName, useJapanese), rowToken, targetLength);
            }
            else if (IsStatusLikeColumn(column))
            {
                var states = useJapanese
                    ? new[] { "有効", "準備済", "主要", "通常", "継続" }
                    : new[] { "Active", "Ready", "Primary", "Enabled", "Current" };
                var state = states[(rowIndex + GetColumnVariantOffset(column)) % states.Length];
                candidate = ComposeSemanticLabel(state, GetLocalizedTableLabel(column.TableName, useJapanese), rowToken, targetLength);
            }
            else
            {
                candidate = BuildLocalizedPhrase(column, rowIndex, semanticSource);
            }

            candidate = FitSemanticString(candidate, rowToken, targetLength);
            return SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
        }

        private object BuildSourceDerivedString(ColumnSchema column, string source, int rowIndex)
        {
            var targetLength = Math.Min(ResolveTargetStringLength(column), 96);
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D3");
            var tableToken = Abbreviate(column.TableName, 2);
            var columnToken = Abbreviate(column.ColumnName, 3);
            var token = $"{tableToken}{columnToken}{rowToken}";
            string mutated;

            if (source.Contains('@'))
            {
                var atIndex = source.IndexOf('@');
                var local = source[..atIndex];
                var domain = source[atIndex..];
                mutated = $"{local}+{token.ToLowerInvariant()}{domain}";
            }
            else if (LooksLikeUrl(source))
            {
                mutated = source.TrimEnd('/') + "/" + token.ToLowerInvariant();
            }
            else if (source.All(char.IsDigit))
            {
                mutated = ReplaceTrailingDigits(source, token);
            }
            else if (IsPhoneColumn(column))
            {
                mutated = ReplaceTrailingDigits(new string(source.Where(char.IsDigit).ToArray()), token);
                if (string.IsNullOrEmpty(mutated))
                {
                    mutated = BuildPhoneLikeString(column, rowIndex, targetLength);
                }
            }
            else if (IsCodeLikeColumn(column) || source.Any(char.IsDigit) || !source.Contains(' '))
            {
                mutated = $"{source}_{token}";
            }
            else
            {
                mutated = $"{source} ({token})";
            }

            mutated = FitSemanticString(mutated, rowToken, targetLength);
            return SqlServerValueNormalizer.NormalizeValue(column, mutated) ?? BuildSemanticString(column, rowIndex, source);
        }

        private static bool IsSyntheticSampleString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var trimmed = value.Trim();
            if (trimmed.Contains("TestData", StringComparison.OrdinalIgnoreCase))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Z]{2}_[A-Z0-9]{2,6}_\d{2,3}$"))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^TD[0-9A-Z_]+$"))
                return true;

            return false;
        }

        private static string? ExtractSemanticSourceFragment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsSyntheticSampleString(value))
                return null;

            var cleaned = HumanizeToken(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts.Take(2));
        }

        private static string HumanizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var spaced = System.Text.RegularExpressions.Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2");
            spaced = System.Text.RegularExpressions.Regex.Replace(spaced, @"[^\p{L}\p{N}]+", " ").Trim();
            if (string.IsNullOrWhiteSpace(spaced))
                return string.Empty;

            var words = spaced
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(TitleCaseAsciiWord);
            return string.Join(" ", words);
        }

        private static string BuildCompactToken(string? semanticSource, string prefix, string rowToken, int maxLength)
        {
            if (maxLength <= 0)
                return string.Empty;

            var sourceToken = new string((semanticSource ?? string.Empty)
                .Where(c => c < 128 && char.IsLetterOrDigit(c))
                .Take(6)
                .Select(char.ToUpperInvariant)
                .ToArray());

            var core = $"{prefix}{sourceToken}{rowToken}";
            if (core.Length <= maxLength)
                return core;

            if (maxLength <= rowToken.Length)
                return rowToken[^maxLength..];

            var prefixBudget = maxLength - rowToken.Length;
            var clippedPrefix = core[..prefixBudget];
            return clippedPrefix + rowToken;
        }

        private static string BuildExactCompactToken(string? semanticSource, string prefix, string rowToken, int targetLength)
        {
            if (targetLength <= 0)
                return string.Empty;

            var baseToken = BuildCompactToken(semanticSource, prefix, rowToken, targetLength);
            if (baseToken.Length >= targetLength)
                return baseToken[..targetLength];

            var fillerSeed = string.IsNullOrWhiteSpace(semanticSource)
                ? prefix
                : prefix + new string(semanticSource
                    .Where(c => c < 128 && char.IsLetterOrDigit(c))
                    .Select(char.ToUpperInvariant)
                    .ToArray());

            if (string.IsNullOrWhiteSpace(fillerSeed))
                fillerSeed = rowToken;

            var builder = new System.Text.StringBuilder(baseToken);
            var fillerIndex = 0;
            while (builder.Length < targetLength)
            {
                var ch = fillerSeed[fillerIndex % fillerSeed.Length];
                builder.Append(ch);
                fillerIndex++;
            }

            return builder.ToString(0, targetLength);
        }

        private static string BuildExactMaxEmailString(ColumnSchema column, string rowToken, int targetLength)
        {
            const string domain = "@sample.jp";
            if (targetLength <= domain.Length)
                return BuildExactCompactToken(null, "MAIL", rowToken, targetLength);

            var localLength = targetLength - domain.Length;
            var local = BuildExactCompactToken(null, $"{GetAsciiTableLabel(column)}{GetAsciiColumnLabel(column)}", rowToken, localLength)
                .ToLowerInvariant();
            return local + domain;
        }

        private static string BuildExactMaxUrlString(ColumnSchema column, string rowToken, int targetLength)
        {
            const string prefix = "https://";
            const string suffix = ".sample.jp";
            if (targetLength <= prefix.Length + suffix.Length)
                return BuildExactCompactToken(null, "URL", rowToken, targetLength);

            var hostLength = targetLength - prefix.Length - suffix.Length;
            var host = BuildExactCompactToken(null, $"{GetAsciiTableLabel(column)}{GetAsciiColumnLabel(column)}", rowToken, hostLength)
                .ToLowerInvariant();
            return prefix + host + suffix;
        }

        private static string BuildPhoneLikeString(ColumnSchema column, int rowIndex, int targetLength)
        {
            var digits = $"{Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(column.ColumnKey)) % 10000:D4}{rowIndex + 1:D4}";
            if (targetLength <= 0)
                return string.Empty;

            if (targetLength <= digits.Length)
                return digits[^targetLength..];

            return ("0" + digits).PadRight(targetLength, '0')[..targetLength];
        }

        private static string ComposeSemanticLabel(string seed, string tableToken, string rowToken, int targetLength)
        {
            var candidate = $"{seed} {tableToken} {rowToken}";
            return FitSemanticString(candidate, rowToken, targetLength);
        }

        private static string BuildLocalizedPhrase(ColumnSchema column, int rowIndex, string? semanticSource)
        {
            var useJapanese = SupportsJapaneseText(column);
            var tableLabel = GetLocalizedTableLabel(column.TableName, useJapanese);
            var columnLabel = GetLocalizedColumnLabel(column.ColumnName, useJapanese);
            var rowToken = (rowIndex + 1).ToString("D3");

            return !string.IsNullOrWhiteSpace(semanticSource)
                ? $"{tableLabel} {semanticSource} {rowToken}"
                : $"{tableLabel} {columnLabel} {rowToken}";
        }

        private static string RepeatPhraseToLength(string phrase, string rowToken, int targetLength)
        {
            if (targetLength <= 0)
                return string.Empty;

            if (string.IsNullOrWhiteSpace(phrase))
                phrase = rowToken;

            var builder = new System.Text.StringBuilder(phrase);
            while (builder.Length < targetLength + phrase.Length)
            {
                builder.Append(' ');
                builder.Append(phrase);
            }

            return FitSemanticString(builder.ToString(), rowToken, targetLength);
        }

        private static string RepeatPhraseToExactLength(string phrase, string rowToken, int targetLength)
        {
            if (targetLength <= 0)
                return string.Empty;

            if (string.IsNullOrWhiteSpace(phrase))
                phrase = rowToken;

            var normalizedPhrase = phrase.Trim();
            if (normalizedPhrase.Length >= targetLength)
                return FitSemanticString(normalizedPhrase, rowToken, targetLength);

            var builder = new System.Text.StringBuilder(normalizedPhrase);
            while (builder.Length < targetLength)
            {
                builder.Append(' ');
                builder.Append(normalizedPhrase);
            }

            if (builder.Length < targetLength)
            {
                builder.Append(' ');
                builder.Append(rowToken);
            }

            return builder.ToString(0, targetLength);
        }

        private static bool SupportsJapaneseText(ColumnSchema column)
        {
            return column.DataType.StartsWith("n", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLocalizedTableLabel(string tableName, bool useJapanese)
        {
            var lower = tableName.ToLowerInvariant();
            return lower switch
            {
                var name when name.Contains("customer") => useJapanese ? "顧客" : "Customer",
                var name when name.Contains("supplier") => useJapanese ? "仕入先" : "Supplier",
                var name when name.Contains("store") => useJapanese ? "店舗" : "Store",
                var name when name.Contains("employee") => useJapanese ? "社員" : "Employee",
                var name when name.Contains("productvariant") => useJapanese ? "商品規格" : "Variant",
                var name when name.Contains("product") => useJapanese ? "商品" : "Product",
                var name when name.Contains("category") => useJapanese ? "分類" : "Category",
                var name when name.Contains("brand") => useJapanese ? "ブランド" : "Brand",
                var name when name.Contains("address") => useJapanese ? "住所" : "Address",
                var name when name.Contains("country") => useJapanese ? "国" : "Country",
                var name when name.Contains("state") || name.Contains("province") => useJapanese ? "都道府県" : "State",
                var name when name.Contains("city") => useJapanese ? "市区町村" : "City",
                var name when name.Contains("department") => useJapanese ? "部門" : "Department",
                var name when name.Contains("jobtitle") || name.Contains("title") => useJapanese ? "職位" : "JobTitle",
                var name when name.Contains("payment") => useJapanese ? "支払" : "Payment",
                var name when name.Contains("salesorderline") => useJapanese ? "受注明細" : "OrderLine",
                var name when name.Contains("salesorder") || name.Contains("order") => useJapanese ? "受注" : "Order",
                _ => useJapanese ? "項目" : HumanizeToken(tableName)
            };
        }

        private static string GetLocalizedColumnLabel(string columnName, bool useJapanese)
        {
            var lower = columnName.ToLowerInvariant();
            return lower switch
            {
                var name when name.Contains("fullname") => useJapanese ? "氏名" : "FullName",
                var name when name.Contains("firstname") => useJapanese ? "名" : "FirstName",
                var name when name.Contains("lastname") => useJapanese ? "姓" : "LastName",
                var name when name.Contains("name") || name.Contains("title") => useJapanese ? "名称" : "Name",
                var name when name.Contains("code") || name.EndsWith("number") || name.EndsWith("no") || name.Contains("sku") || name.Contains("barcode") => useJapanese ? "コード" : "Code",
                var name when name.Contains("tier") || name.Contains("level") => useJapanese ? "ランク" : "Tier",
                var name when name.Contains("status") || name.Contains("state") => useJapanese ? "状態" : "Status",
                var name when name.Contains("type") || name.Contains("format") => useJapanese ? "種別" : "Type",
                var name when name.Contains("description") => useJapanese ? "説明" : "Description",
                var name when name.Contains("note") => useJapanese ? "備考" : "Notes",
                var name when name.Contains("email") => useJapanese ? "メール" : "Email",
                var name when name.Contains("phone") || name.Contains("mobile") || name.Contains("fax") => useJapanese ? "電話" : "Phone",
                var name when name.Contains("unitofmeasure") => useJapanese ? "単位" : "Unit",
                var name when name.Contains("address") => useJapanese ? "住所" : "Address",
                var name when name.Contains("color") => useJapanese ? "色名" : "Color",
                var name when name.Contains("size") => useJapanese ? "サイズ" : "Size",
                var name when name.Contains("url") || name.Contains("website") => useJapanese ? "URL" : "Url",
                _ => useJapanese ? "項目" : HumanizeToken(columnName)
            };
        }

        private static string GetAsciiTableLabel(ColumnSchema column)
        {
            var lower = column.TableName.ToLowerInvariant();
            return lower switch
            {
                var name when name.Contains("customer") => "CUST",
                var name when name.Contains("supplier") => "SUP",
                var name when name.Contains("store") => "STORE",
                var name when name.Contains("employee") => "EMP",
                var name when name.Contains("productvariant") => "VAR",
                var name when name.Contains("product") => "PROD",
                var name when name.Contains("category") => "CAT",
                var name when name.Contains("brand") => "BRAND",
                var name when name.Contains("address") => "ADDR",
                var name when name.Contains("country") => "COUNTRY",
                var name when name.Contains("state") || name.Contains("province") => "STATE",
                var name when name.Contains("city") => "CITY",
                var name when name.Contains("department") => "DEPT",
                var name when name.Contains("payment") => "PAY",
                var name when name.Contains("salesorderline") => "LINE",
                var name when name.Contains("salesorder") || name.Contains("order") => "ORDER",
                _ => Abbreviate(column.TableName, 6)
            };
        }

        private static string GetAsciiColumnLabel(ColumnSchema column)
        {
            var lower = column.ColumnName.ToLowerInvariant();
            return lower switch
            {
                var name when name.Contains("fullname") => "FULLNAME",
                var name when name.Contains("firstname") => "FIRST",
                var name when name.Contains("lastname") => "LAST",
                var name when name.Contains("name") => "NAME",
                var name when name.Contains("code") || name.EndsWith("number") || name.EndsWith("no") || name.Contains("sku") || name.Contains("barcode") => "CODE",
                var name when name.Contains("tier") || name.Contains("level") => "TIER",
                var name when name.Contains("status") || name.Contains("state") => "STATUS",
                var name when name.Contains("type") || name.Contains("format") => "TYPE",
                var name when name.Contains("email") => "MAIL",
                var name when name.Contains("phone") || name.Contains("mobile") || name.Contains("fax") => "PHONE",
                var name when name.Contains("url") || name.Contains("website") => "URL",
                _ => Abbreviate(column.ColumnName, 6)
            };
        }

        private static string TitleCaseAsciiWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return string.Empty;

            return word.All(c => c < 128)
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                : word;
        }

        private static string FitSemanticString(string value, string rowToken, int targetLength)
        {
            if (targetLength <= 0)
                return string.Empty;

            if (value.Length <= targetLength)
                return value;

            if (targetLength <= rowToken.Length)
                return rowToken[^targetLength..];

            var prefixLength = Math.Max(0, targetLength - rowToken.Length);
            var trimmed = value.Length >= prefixLength
                ? value[..prefixLength].TrimEnd(' ', '-', '_', '(', ')')
                : value.TrimEnd(' ', '-', '_', '(', ')');
            var recombined = trimmed + rowToken;
            if (recombined.Length < targetLength)
            {
                recombined = recombined.PadRight(targetLength, rowToken[^1]);
            }

            return recombined[..targetLength];
        }

        private static bool IsCodeLikeColumn(ColumnSchema column)
        {
            return column.ColumnName.Contains("Code", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.EndsWith("Number", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.EndsWith("No", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Sku", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Barcode", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNameLikeColumn(ColumnSchema column)
        {
            return column.ColumnName.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Title", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTierLikeColumn(ColumnSchema column)
        {
            return column.ColumnName.Contains("Tier", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Level", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStatusLikeColumn(ColumnSchema column)
        {
            return column.ColumnName.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Type", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Format", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEmailColumn(ColumnSchema column) =>
            column.ColumnName.Contains("Email", StringComparison.OrdinalIgnoreCase);

        private static bool IsUrlColumn(ColumnSchema column) =>
            column.ColumnName.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
            column.ColumnName.Contains("Website", StringComparison.OrdinalIgnoreCase);

        private static bool IsPhoneColumn(ColumnSchema column) =>
            column.ColumnName.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
            column.ColumnName.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
            column.ColumnName.Contains("Fax", StringComparison.OrdinalIgnoreCase);

        private object MutateSampleNumeric(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var offset = rowIndex + GetColumnVariantOffset(column);

            return column.TypeCategory switch
            {
                DataTypeCategory.Integer => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    Convert.ToInt64(sampleValue, System.Globalization.CultureInfo.InvariantCulture) + offset) ?? 0,
                DataTypeCategory.Decimal => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    Convert.ToDecimal(sampleValue, System.Globalization.CultureInfo.InvariantCulture) + (offset * GetNumericStep(column))) ?? 0m,
                DataTypeCategory.Float => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    Convert.ToDouble(sampleValue, System.Globalization.CultureInfo.InvariantCulture) + offset) ?? 0d,
                _ => sampleValue
            };
        }

        private object MutateSampleDateTime(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var normalized = SqlServerValueNormalizer.NormalizeValue(column, sampleValue);
            var source = normalized is DateTime dt ? dt : DateTime.Now;
            var offset = rowIndex + GetColumnVariantOffset(column);
            var mutated = column.DataType.Equals("date", StringComparison.OrdinalIgnoreCase)
                ? source.AddDays(offset)
                : source.AddMinutes(offset * 7);
            return SqlServerValueNormalizer.NormalizeValue(column, mutated) ?? mutated;
        }

        private object MutateSampleDateTimeOffset(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var normalized = SqlServerValueNormalizer.NormalizeValue(column, sampleValue);
            var source = normalized is DateTimeOffset dto ? dto : DateTimeOffset.Now;
            var offset = rowIndex + GetColumnVariantOffset(column);
            var mutated = source.AddMinutes(offset * 7);
            return SqlServerValueNormalizer.NormalizeValue(column, mutated) ?? mutated;
        }

        private object MutateSampleTime(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var normalized = SqlServerValueNormalizer.NormalizeValue(column, sampleValue);
            var source = normalized is TimeSpan ts ? ts : TimeSpan.Zero;
            var offset = rowIndex + GetColumnVariantOffset(column);
            return SqlServerValueNormalizer.NormalizeValue(column, source.Add(TimeSpan.FromMinutes(offset * 3))) ?? source;
        }

        private object MutateSampleBoolean(ColumnSchema column, object sampleValue, int rowIndex)
        {
            var normalized = SqlServerValueNormalizer.NormalizeValue(column, sampleValue);
            var source = normalized is bool b && b;
            return ((rowIndex + GetColumnVariantOffset(column)) % 2 == 0) ? source : !source;
        }

        private static bool LooksLikeUrl(string value)
        {
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReplaceTrailingDigits(string source, string token)
        {
            if (string.IsNullOrEmpty(source))
                return token;

            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits))
                digits = "01";

            var chars = source.ToCharArray();
            var replacementIndex = digits.Length - 1;
            for (int i = chars.Length - 1; i >= 0; i--)
            {
                if (!char.IsDigit(chars[i]))
                    continue;

                chars[i] = digits[replacementIndex >= 0 ? replacementIndex : 0];
                replacementIndex--;
            }

            return new string(chars);
        }

        private int ResolveTargetStringLength(ColumnSchema column)
        {
            if (column.MaxLength.HasValue && column.MaxLength.Value > 0)
                return column.MaxLength.Value;

            if (TryGetSampleValue(column, out var sampleValue) &&
                sampleValue != null)
            {
                var sampleText = Convert.ToString(sampleValue, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(sampleText))
                    return Math.Max(sampleText.Length, 12);
            }

            return 64;
        }

        private int GetColumnVariantOffset(ColumnSchema column)
        {
            var hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(column.ColumnKey));
            return (hash % 17) + 1;
        }

        private static string Abbreviate(string value, int maxLength)
        {
            var filtered = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (string.IsNullOrEmpty(filtered))
                filtered = "COL";

            return filtered.Length <= maxLength
                ? filtered
                : filtered[..maxLength];
        }

        private static decimal GetNumericStep(ColumnSchema column)
        {
            if (column.NumericScale.HasValue && column.NumericScale.Value > 0)
            {
                decimal step = 1m;
                for (int i = 0; i < column.NumericScale.Value; i++)
                {
                    step /= 10m;
                }

                return step;
            }

            return 1m;
        }

        private static long GetMaxIntegerValue(ColumnSchema column)
        {
            return column.DataType.ToLowerInvariant() switch
            {
                "tinyint" => byte.MaxValue,
                "smallint" => short.MaxValue,
                "int" => int.MaxValue,
                _ => long.MaxValue / 1024
            };
        }

        private long GetPracticalMaxIntegerValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            if (IsRatingLikeNumericColumn(column))
                return Math.Min(GetMaxIntegerValue(column), 5L);

            var absoluteMax = GetMaxIntegerValue(column);
            var safeDigits = DeterminePracticalNumericDigits(column, query, tableAlias);
            long practicalMax = 1;
            for (int i = 0; i < safeDigits; i++)
            {
                practicalMax *= 10;
            }

            practicalMax -= 1;
            return Math.Min(absoluteMax, practicalMax);
        }

        private static decimal GetMaxDecimalValue(ColumnSchema column)
        {
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var precision = Math.Max(1, column.NumericPrecision ?? 18);
            var integerDigits = Math.Max(0, precision - scale);
            decimal wholePart = 1m;
            for (int i = 0; i < integerDigits; i++)
            {
                wholePart *= 10m;
            }

            var step = GetNumericStep(column);
            var max = wholePart - step;
            return max > 0m ? max : step;
        }

        private decimal GetPracticalMaxDecimalValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var step = GetNumericStep(column);

            if (IsRatingLikeNumericColumn(column))
            {
                var ratingMax = Math.Max(step, 5m);
                return SqlServerValueNormalizer.NormalizeValue(column, ratingMax) is decimal normalizedRating
                    ? normalizedRating
                    : ratingMax;
            }

            if (IsRateLikeNumericColumn(column))
            {
                var unitMax = 1m - step;
                return unitMax > 0m ? unitMax : step;
            }

            var availableIntegerDigits = column.NumericPrecision.HasValue
                ? Math.Max(1, column.NumericPrecision.Value - scale)
                : 5;
            var safeDigits = Math.Min(availableIntegerDigits, DeterminePracticalNumericDigits(column, query, tableAlias));

            decimal wholePart = 1m;
            for (int i = 0; i < safeDigits; i++)
            {
                wholePart *= 10m;
            }

            var practicalMax = wholePart - step;
            return practicalMax > 0m ? practicalMax : step;
        }

        private static double GetMaxFloatValue(ColumnSchema column)
        {
            var digits = column.DataType.Equals("real", StringComparison.OrdinalIgnoreCase) ? 7 : 15;
            return Math.Pow(10d, digits) - 1d;
        }

        private double GetPracticalMaxFloatValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            var safeDigits = DeterminePracticalNumericDigits(column, query, tableAlias);
            return Math.Min(GetMaxFloatValue(column), Math.Pow(10d, safeDigits) - 1d);
        }

        private int DeterminePracticalNumericDigits(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            var typeDigits = column.DataType.ToLowerInvariant() switch
            {
                "tinyint" => 3,
                "smallint" => 5,
                "int" => 9,
                "bigint" => 15,
                "real" => 6,
                "float" => 8,
                _ => 5
            };

            var safeDigits = Math.Min(typeDigits, 5);

            if (IsRatingLikeNumericColumn(column))
                return 1;

            if (IsRateLikeNumericColumn(column))
                return 1;

            if (IsCountLikeNumericColumn(column))
                safeDigits = Math.Min(safeDigits, 3);

            if (IsInventoryLikeNumericColumn(column))
                safeDigits = Math.Min(safeDigits, 3);

            if (IsMeasureLikeNumericColumn(column))
                safeDigits = Math.Min(safeDigits, 4);

            if (query != null && HasMultiplicativeArithmeticRisk(column, query, tableAlias))
            {
                safeDigits = column.TypeCategory switch
                {
                    DataTypeCategory.Integer => Math.Min(safeDigits, 2),
                    DataTypeCategory.Decimal => Math.Min(safeDigits, 2),
                    DataTypeCategory.Float => Math.Min(safeDigits, 2),
                    _ => safeDigits
                };
            }

            if (query != null && IsArithmeticRiskColumn(column, query, tableAlias))
            {
                safeDigits = column.TypeCategory switch
                {
                    DataTypeCategory.Integer => Math.Min(safeDigits, 3),
                    DataTypeCategory.Decimal => Math.Min(safeDigits, 3),
                    DataTypeCategory.Float => Math.Min(safeDigits, 3),
                    _ => Math.Min(safeDigits, 4)
                };
            }

            return Math.Max(1, safeDigits);
        }

        private bool IsArithmeticRiskColumn(ColumnSchema column, ParsedQuery query, string tableAlias)
        {
            var alias = string.IsNullOrWhiteSpace(tableAlias) ? column.TableName : tableAlias;
            return IsPartOfAggregateExpression(column.ColumnName, alias, query) ||
                   query.SelectColumns.Any(c =>
                       !string.IsNullOrWhiteSpace(c.Expression) &&
                       ExpressionMentionsColumn(c.Expression, alias, column.ColumnName)) ||
                   query.HavingConditions.Any(c =>
                       !string.IsNullOrWhiteSpace(c.ExpressionText) &&
                       ExpressionMentionsColumn(c.ExpressionText, alias, column.ColumnName));
        }

        private bool HasMultiplicativeArithmeticRisk(ColumnSchema column, ParsedQuery query, string tableAlias)
        {
            var alias = string.IsNullOrWhiteSpace(tableAlias) ? column.TableName : tableAlias;
            return query.Aggregates.Any(a =>
                       !string.IsNullOrWhiteSpace(a.Expression) &&
                       ExpressionMentionsColumn(a.Expression, alias, column.ColumnName) &&
                       (a.Expression.Contains('*') || a.Expression.Contains('/'))) ||
                   query.SelectColumns.Any(c =>
                       !string.IsNullOrWhiteSpace(c.Expression) &&
                       ExpressionMentionsColumn(c.Expression, alias, column.ColumnName) &&
                       (c.Expression.Contains('*') || c.Expression.Contains('/'))) ||
                   query.HavingConditions.Any(c =>
                       !string.IsNullOrWhiteSpace(c.ExpressionText) &&
                       ExpressionMentionsColumn(c.ExpressionText, alias, column.ColumnName) &&
                       (c.ExpressionText.Contains('*') || c.ExpressionText.Contains('/')));
        }

        private static bool ExpressionMentionsColumn(string expression, string alias, string columnName)
        {
            if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(columnName))
                return false;

            static string EscapePattern(string value) => System.Text.RegularExpressions.Regex.Escape(value);

            if (!string.IsNullOrWhiteSpace(alias))
            {
                var qualifiedPattern = $@"(?i)(?:\b{EscapePattern(alias)}\b|\[{EscapePattern(alias)}\])\s*\.\s*(?:\b{EscapePattern(columnName)}\b|\[{EscapePattern(columnName)}\])";
                if (System.Text.RegularExpressions.Regex.IsMatch(expression, qualifiedPattern))
                    return true;
            }

            var unqualifiedPattern = $@"(?i)(?<![\w\].])(?:\b{EscapePattern(columnName)}\b|\[{EscapePattern(columnName)}\])(?!\s*\.)";
            return System.Text.RegularExpressions.Regex.IsMatch(expression, unqualifiedPattern);
        }

        private static bool IsMeasureLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Salary", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Revenue", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Fee", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRateLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Rate", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Percent", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Tax", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Discount", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Commission", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRatingLikeNumericColumn(ColumnSchema column)
        {
            var name = column.ColumnName;
            return name.Contains("Rating", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Stars", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Star", StringComparison.OrdinalIgnoreCase);
        }

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
                   name.Contains("Quantity", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Points", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Score", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Rank", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Capacity", StringComparison.OrdinalIgnoreCase) ||
                   IsInventoryLikeNumericColumn(column);
        }

        private List<ColumnConditionTarget> GetApplicableConditionTargets(
            BranchScenario scenario,
            ParsedQuery query,
            IEnumerable<ConditionInfo> conditions,
            string tableName,
            string tableAlias,
            string columnName,
            bool excludeHasSubquery)
        {
            var targets = new List<ColumnConditionTarget>();

            foreach (var condition in conditions)
            {
                if (!ConditionTargetsColumn(query, condition, tableName, tableAlias, columnName))
                    continue;
                if (excludeHasSubquery && condition.HasSubquery)
                    continue;

                targets.Add(new ColumnConditionTarget(
                    condition,
                    GetDesiredTruthForCondition(scenario, condition, defaultTruth: true)));
            }

            return targets;
        }

        private List<ColumnConditionTarget> GetApplicableAggregateTargets(
            BranchScenario scenario,
            ParsedQuery query,
            string tableAlias,
            string columnName)
        {
            return query.EnumerateScopeConditions(ConditionSource.Having)
                .Where(c =>
                    c.AggregateFunc.HasValue &&
                    c.AggregateFunc is not AggregateFunction.Count and not AggregateFunction.CountDistinct &&
                    IsConditionTargetingColumn(query, c, tableAlias, columnName))
                .Select(c => new ColumnConditionTarget(
                    c,
                    GetDesiredTruthForCondition(scenario, c, defaultTruth: true)))
                .ToList();
        }

        private List<ColumnConditionTarget> FindApplicableSubqueryConditionTargets(
            BranchScenario scenario,
            ParsedQuery query,
            string tableName,
            string tableAlias,
            string columnName)
        {
            var targets = new List<ColumnConditionTarget>();
            CollectSubqueryConditionTargets(
                query.Subqueries,
                scenario,
                query,
                tableName,
                tableAlias,
                columnName,
                query.AliasToTableMap,
                parentTruthMap: null,
                targets);

            return targets;
        }

        private void CollectSubqueryConditionTargets(
            IEnumerable<SubqueryInfo> subqueries,
            BranchScenario scenario,
            ParsedQuery query,
            string tableName,
            string tableAlias,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap,
            IReadOnlyDictionary<string, bool>? parentTruthMap,
            List<ColumnConditionTarget> targets)
        {
            foreach (var subquery in subqueries)
            {
                var predicateTruth = ResolveSubqueryPredicateTruth(scenario, subquery, parentTruthMap);
                var internalTruthMap = BuildSubqueryInternalTruthMap(subquery, predicateTruth);
                var localAliasMap = ExtendAliasMap(
                    new Dictionary<string, string>(aliasMap, StringComparer.OrdinalIgnoreCase),
                    subquery.Tables);

                foreach (var condition in subquery.Conditions.Where(c =>
                    ConditionTargetsColumn(localAliasMap, c, tableName, tableAlias, columnName)))
                {
                    var desiredTruth = internalTruthMap.TryGetValue(condition.Key, out var mappedTruth)
                        ? mappedTruth
                        : true;
                    object? comparisonValue = null;
                    if (condition.IsColumnComparison)
                    {
                        TryResolveSubqueryComparisonValue(
                            scenario,
                            query,
                            condition,
                            new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase),
                            localAliasMap,
                            out comparisonValue);
                    }

                    targets.Add(new ColumnConditionTarget(condition, desiredTruth, comparisonValue));
                }

                CollectSubqueryConditionTargets(
                    subquery.NestedSubqueries,
                    scenario,
                    query,
                    tableName,
                    tableAlias,
                    columnName,
                    localAliasMap,
                    internalTruthMap,
                    targets);
            }
        }

        private static bool IsNegativeSubqueryOperator(SubqueryOperator op) =>
            op is SubqueryOperator.NotExists or SubqueryOperator.NotIn;

        private static bool MatchesConditionTarget(
            ParsedQuery query,
            string? conditionTableAlias,
            string tableName,
            string tableAlias)
        {
            if (string.IsNullOrWhiteSpace(conditionTableAlias))
                return true;

            if (conditionTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase))
                return true;

            var resolved = query.ResolveAlias(conditionTableAlias);
            return resolved.Equals(tableName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesConditionTarget(
            IReadOnlyDictionary<string, string> aliasMap,
            string? conditionTableAlias,
            string tableName,
            string tableAlias)
        {
            if (string.IsNullOrWhiteSpace(conditionTableAlias))
                return true;

            if (conditionTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase))
                return true;

            if (aliasMap.TryGetValue(conditionTableAlias, out var resolved))
            {
                return resolved.Equals(tableName, StringComparison.OrdinalIgnoreCase);
            }

            return conditionTableAlias.Equals(tableName, StringComparison.OrdinalIgnoreCase);
        }

        private ResolvedColumnValue ResolveColumnValueFromTargets(
            BranchScenario scenario,
            ParsedQuery query,
            ColumnSchema col,
            IValueGenerator generator,
            IReadOnlyCollection<ColumnConditionTarget> targets,
            Dictionary<string, List<int>> tableRowIds,
            string tableAlias,
            int rowIndex,
            GeneratedRow? currentRow)
        {
            if (col.TypeCategory == DataTypeCategory.String &&
                TryResolveFunctionAwareStringValue(scenario, query, currentRow, col, tableAlias, rowIndex, targets, generator, out var stringValue))
            {
                return new ResolvedColumnValue(true, stringValue);
            }

            if (IsTemporalColumn(col) &&
                TryResolveFunctionAwareTemporalValue(scenario, query, currentRow, col, tableAlias, targets, generator, out var temporalValue))
            {
                return new ResolvedColumnValue(true, temporalValue);
            }

            var candidates = new List<object?>();

            foreach (var target in targets)
            {
                var comparisonValue = target.ComparisonValue;
                if (target.Condition.IsColumnComparison && comparisonValue == null)
                {
                    TryResolveSubqueryComparisonValue(
                        scenario,
                        query,
                        target.Condition,
                        tableRowIds,
                        query.AliasToTableMap,
                        out comparisonValue);
                }
                else if (comparisonValue == null)
                {
                    TryResolveScalarSubqueryComparisonValue(query, target.Condition, col, out comparisonValue);
                }

                candidates.Add(GenerateConditionValue(
                    scenario,
                    query,
                    col,
                    generator,
                    target.Condition,
                    target.DesiredTruth,
                    comparisonValue,
                    tableAlias,
                    rowIndex));

                if (target.Condition.Operator == ComparisonOp.Between)
                {
                    candidates.Add(GenerateBetweenValue(col, target.Condition.Value, target.Condition.SecondValue, inside: target.DesiredTruth));
                }
            }

            candidates.Add(generator.GenerateDefault(col));
            if (col.IsNullable)
            {
                candidates.Add(null);
            }

            foreach (var candidate in DeduplicateCandidates(candidates))
            {
                if (targets.All(target => EvaluateConditionTarget(candidate, target, scenario, query, col, generator, tableAlias, currentRow)))
                {
                    return new ResolvedColumnValue(true, candidate);
                }
            }

            return new ResolvedColumnValue(false, null);
        }

        private bool TryResolveFunctionAwareStringValue(
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            int rowIndex,
            IReadOnlyCollection<ColumnConditionTarget> targets,
            IValueGenerator generator,
            out object? value)
        {
            value = null;
            if (column.TypeCategory != DataTypeCategory.String || targets.Count == 0)
                return false;

            var rowToken = (rowIndex + 1).ToString("D3");
            var targetLength = UseMaxLengthMaxValueMode
                ? ResolveTargetStringLength(column)
                : Math.Min(ResolveTargetStringLength(column), 96);

            var baseCandidate = Convert.ToString(
                UseMaxLengthMaxValueMode
                    ? BuildMaxLengthString(column, rowIndex)
                    : BuildSemanticString(column, rowIndex, null),
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            var candidates = new List<string>();
            void AddCandidate(string candidate)
            {
                var trimmed = candidate.Trim();
                var normalized = UseMaxLengthMaxValueMode
                    ? RepeatPhraseToExactLength(trimmed, rowToken, targetLength)
                    : FitSemanticString(trimmed, rowToken, targetLength);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    candidates.Add(normalized);
                }
            }

            AddCandidate(baseCandidate);
            AddCandidate(baseCandidate.Replace(" ", string.Empty, StringComparison.Ordinal));
            AddCandidate($"A{baseCandidate}Z");

            var hints = ExtractStringHints(targets);
            foreach (var hint in hints)
            {
                var trimmedHint = hint.Trim();
                if (string.IsNullOrEmpty(trimmedHint))
                    continue;

                AddCandidate(trimmedHint);
                AddCandidate($"{trimmedHint}demoZ");
                AddCandidate($"{trimmedHint}sampleZ");
                AddCandidate($"{trimmedHint}商品Z");
                AddCandidate($"{trimmedHint}{baseCandidate}Z");
            }

            var runtimeHints = ExtractRuntimeStringHints(targets, scenario, query, currentRow, column, tableAlias);
            foreach (var hint in runtimeHints)
            {
                var trimmedHint = hint.Trim();
                if (string.IsNullOrEmpty(trimmedHint))
                    continue;

                AddCandidate(trimmedHint);
                AddCandidate($"{trimmedHint} {baseCandidate}");
                AddCandidate($"{trimmedHint}-{baseCandidate}");
                AddCandidate($"{trimmedHint}{baseCandidate}Z");
            }

            foreach (var candidate in DeduplicateCandidates(candidates).OfType<string>())
            {
                if (targets.All(target => EvaluateConditionTarget(candidate, target, scenario, query, column, generator, tableAlias, currentRow)))
                {
                    value = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveFunctionAwareTemporalValue(
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyCollection<ColumnConditionTarget> targets,
            IValueGenerator generator,
            out object? value)
        {
            value = null;
            if (!IsTemporalColumn(column) || targets.Count == 0)
                return false;

            var candidates = new List<object?>();

            void AddTemporal(object? candidate)
            {
                if (candidate == null)
                    return;

                try
                {
                    if (candidate is DateTimeOffset offset)
                    {
                        candidates.Add(SqlServerValueNormalizer.NormalizeValue(column, offset) ?? offset);
                        candidates.Add(SqlServerValueNormalizer.NormalizeValue(column, offset.DateTime) ?? offset.DateTime);
                        return;
                    }

                    if (candidate is TimeSpan time)
                    {
                        candidates.Add(SqlServerValueNormalizer.NormalizeValue(column, time) ?? time);
                        return;
                    }

                    if (TryConvertToDateTimeValue(candidate, out var dateTime))
                    {
                        candidates.Add(SqlServerValueNormalizer.NormalizeValue(column, dateTime) ?? dateTime);
                    }
                }
                catch
                {
                    candidates.Add(candidate);
                }
            }

            void AddDateTimeWithNeighbors(DateTime dateTime)
            {
                AddTemporal(dateTime);
                AddTemporal(dateTime.AddDays(-1));
                AddTemporal(dateTime.AddDays(1));
                AddTemporal(dateTime.AddHours(-1));
                AddTemporal(dateTime.AddHours(1));
                AddTemporal(dateTime.AddMinutes(-1));
                AddTemporal(dateTime.AddMinutes(1));
            }

            if (TryBuildCompositeDatePartCandidate(targets, scenario, query, currentRow, column, tableAlias, out var compositeDate))
            {
                AddTemporal(compositeDate);
            }

            foreach (var target in targets)
            {
                AddTemporalExpressionCandidates(target, scenario, query, currentRow, column, tableAlias, AddDateTimeWithNeighbors, AddTemporal);
                AddDateDiffTemporalCandidates(target, scenario, query, currentRow, column, tableAlias, AddDateTimeWithNeighbors);
            }

            var now = _generationLocalNow;
            var utcNow = DateTime.SpecifyKind(_generationUtcNow, DateTimeKind.Unspecified);
            AddTemporal(now);
            AddTemporal(now.Date);
            AddTemporal(now.Date.AddHours(12));
            AddTemporal(now.AddDays(-1));
            AddTemporal(now.AddDays(1));
            AddTemporal(now.AddDays(-7));
            AddTemporal(now.AddDays(7));
            AddTemporal(now.AddDays(-30));
            AddTemporal(now.AddDays(30));
            AddTemporal(new DateTime(now.Year, 1, 1, 12, 0, 0, DateTimeKind.Unspecified));
            AddTemporal(new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Unspecified));
            AddTemporal(EndOfMonth(now).AddHours(12));
            AddTemporal(utcNow);
            AddTemporal(utcNow.Date.AddHours(12));
            AddTemporal(utcNow.AddDays(-1));
            AddTemporal(utcNow.AddDays(1));

            if (column.TypeCategory == DataTypeCategory.Time)
            {
                AddTemporal(now.TimeOfDay);
                AddTemporal(now.AddHours(-1).TimeOfDay);
                AddTemporal(now.AddHours(1).TimeOfDay);
            }

            foreach (var candidate in DeduplicateCandidates(candidates))
            {
                if (targets.All(target => EvaluateConditionTarget(candidate, target, scenario, query, column, generator, tableAlias, currentRow)))
                {
                    value = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool IsTemporalColumn(ColumnSchema column) =>
            column.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset or DataTypeCategory.Time;

        private bool TryBuildCompositeDatePartCandidate(
            IReadOnlyCollection<ColumnConditionTarget> targets,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            out DateTime candidate)
        {
            var now = _generationLocalNow;
            int? year = null;
            int? month = null;
            int? day = null;
            int? hour = null;
            int? minute = null;
            int? second = null;

            foreach (var target in targets.Where(t => t.DesiredTruth))
            {
                if (!TryGetDatePartConstraintValue(target.Condition, scenario, query, currentRow, column, tableAlias, out var datePart, out var partValue))
                    continue;

                switch (NormalizeDatePart(datePart))
                {
                    case "year":
                        year = Clamp(partValue, 1, 9999);
                        break;
                    case "month":
                        month = Clamp(partValue, 1, 12);
                        break;
                    case "day":
                        day = Clamp(partValue, 1, 31);
                        break;
                    case "hour":
                        hour = Clamp(partValue, 0, 23);
                        break;
                    case "minute":
                        minute = Clamp(partValue, 0, 59);
                        break;
                    case "second":
                        second = Clamp(partValue, 0, 59);
                        break;
                }
            }

            if (year == null && month == null && day == null && hour == null && minute == null && second == null)
            {
                candidate = default;
                return false;
            }

            var resolvedYear = year ?? now.Year;
            var resolvedMonth = month ?? now.Month;
            var maxDay = DateTime.DaysInMonth(resolvedYear, resolvedMonth);
            var resolvedDay = Math.Min(day ?? now.Day, maxDay);
            candidate = new DateTime(
                resolvedYear,
                resolvedMonth,
                resolvedDay,
                hour ?? now.Hour,
                minute ?? now.Minute,
                second ?? now.Second,
                DateTimeKind.Unspecified);
            return true;
        }

        private bool TryGetDatePartConstraintValue(
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            out string datePart,
            out int value)
        {
            datePart = string.Empty;
            value = 0;

            if (!TryExtractDatePartExpression(condition.LeftExpression, column, tableAlias, out datePart))
                return false;

            if (!TryChooseIntegerForCondition(condition, scenario, query, currentRow, column, tableAlias, out value))
                return false;

            return true;
        }

        private bool TryChooseIntegerForCondition(
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            out int value)
        {
            value = 0;
            object? rawValue = null;

            if (condition.Operator == ComparisonOp.Between)
            {
                rawValue = condition.RightExpression != null
                    ? EvaluateScalarExpression(condition.RightExpression, null, scenario, query, currentRow, column, tableAlias, null)
                    : condition.Value;
            }
            else if (condition.RightExpression != null)
            {
                rawValue = EvaluateScalarExpression(condition.RightExpression, null, scenario, query, currentRow, column, tableAlias, null);
            }
            else if (!string.IsNullOrWhiteSpace(condition.Value))
            {
                rawValue = condition.Value;
            }

            if (!TryConvertInt(rawValue, out var boundary))
                return false;

            value = condition.Operator switch
            {
                ComparisonOp.GreaterThan => boundary + 1,
                ComparisonOp.LessThan => boundary - 1,
                ComparisonOp.Between => boundary,
                _ => boundary
            };
            return true;
        }

        private static bool TryExtractDatePartExpression(
            ScalarExpressionInfo? expression,
            ColumnSchema column,
            string tableAlias,
            out string datePart)
        {
            datePart = string.Empty;
            if (expression is not FunctionScalarExpressionInfo function ||
                !ExpressionReferencesTargetColumn(function, column, tableAlias))
            {
                return false;
            }

            var name = function.Name.ToUpperInvariant();
            if (name is "YEAR" or "MONTH" or "DAY")
            {
                datePart = name.ToLowerInvariant();
                return function.Arguments.Count > 0 &&
                       ExpressionReferencesTargetColumn(function.Arguments[0], column, tableAlias);
            }

            if (name == "DATEPART" && function.Arguments.Count >= 2 &&
                ExpressionReferencesTargetColumn(function.Arguments[1], column, tableAlias))
            {
                datePart = ExtractDatePartToken(function.Arguments[0], null);
                return !string.IsNullOrWhiteSpace(datePart);
            }

            return false;
        }

        private void AddTemporalExpressionCandidates(
            ColumnConditionTarget target,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            Action<DateTime> addDateTimeWithNeighbors,
            Action<object?> addTemporal)
        {
            foreach (var expression in EnumerateHintExpressions(target.Condition.LeftExpression, column, tableAlias)
                         .Concat(EnumerateHintExpressions(target.Condition.RightExpression, column, tableAlias)))
            {
                var evaluated = EvaluateScalarExpression(expression, null, scenario, query, currentRow, column, tableAlias, null);
                if (TryConvertToDateTimeValue(evaluated, out var dateTime))
                {
                    addDateTimeWithNeighbors(dateTime);
                }
                else if (evaluated is DateTimeOffset offset)
                {
                    addTemporal(offset);
                }
                else if (evaluated is TimeSpan time)
                {
                    addTemporal(time);
                }
            }

            if (TryConvertToDateTimeValue(target.Condition.Value, out var lower))
            {
                addDateTimeWithNeighbors(lower);
            }

            if (TryConvertToDateTimeValue(target.Condition.SecondValue, out var upper))
            {
                addDateTimeWithNeighbors(upper);
            }
        }

        private void AddDateDiffTemporalCandidates(
            ColumnConditionTarget target,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            Action<DateTime> addDateTimeWithNeighbors)
        {
            if (!TryExtractDateDiffExpression(target.Condition.LeftExpression, column, tableAlias, out var datePart, out var targetIsStart, out var otherExpression))
                return;

            var otherValue = EvaluateScalarExpression(otherExpression, null, scenario, query, currentRow, column, tableAlias, null);
            if (!TryConvertToDateTimeValue(otherValue, out var anchor))
                return;

            foreach (var diff in ExtractCandidateDiffValues(target.Condition, scenario, query, currentRow, column, tableAlias))
            {
                var signedDiff = targetIsStart ? -diff : diff;
                var candidate = AddSqlDatePart(anchor, datePart, signedDiff);
                if (candidate.HasValue)
                {
                    addDateTimeWithNeighbors(candidate.Value);
                }
            }
        }

        private IEnumerable<int> ExtractCandidateDiffValues(
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias)
        {
            var values = new List<int> { 0, 1, -1, 7, -7, 30, -30 };

            void AddInt(object? raw)
            {
                if (TryConvertInt(raw, out var parsed))
                {
                    values.Add(parsed);
                    values.Add(parsed - 1);
                    values.Add(parsed + 1);
                }
            }

            AddInt(condition.Value);
            AddInt(condition.SecondValue);
            if (condition.RightExpression != null)
            {
                AddInt(EvaluateScalarExpression(condition.RightExpression, null, scenario, query, currentRow, column, tableAlias, null));
            }

            return values.Distinct();
        }

        private static bool TryExtractDateDiffExpression(
            ScalarExpressionInfo? expression,
            ColumnSchema column,
            string tableAlias,
            out string datePart,
            out bool targetIsStart,
            out ScalarExpressionInfo? otherExpression)
        {
            datePart = string.Empty;
            targetIsStart = false;
            otherExpression = null;

            if (expression is not FunctionScalarExpressionInfo function ||
                !function.Name.Equals("DATEDIFF", StringComparison.OrdinalIgnoreCase) ||
                function.Arguments.Count < 3)
            {
                return false;
            }

            var startExpression = function.Arguments[1];
            var endExpression = function.Arguments[2];
            var targetInStart = ExpressionReferencesTargetColumn(startExpression, column, tableAlias);
            var targetInEnd = ExpressionReferencesTargetColumn(endExpression, column, tableAlias);
            if (targetInStart == targetInEnd)
                return false;

            datePart = ExtractDatePartToken(function.Arguments[0], null);
            targetIsStart = targetInStart;
            otherExpression = targetInStart ? endExpression : startExpression;
            return !string.IsNullOrWhiteSpace(datePart);
        }

        private static List<string> ExtractStringHints(IReadOnlyCollection<ColumnConditionTarget> targets)
        {
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                foreach (var hint in EnumerateStringHints(target.Condition.LeftExpression))
                {
                    if (!string.IsNullOrWhiteSpace(hint))
                        hints.Add(hint);
                }

                foreach (var hint in EnumerateStringHints(target.Condition.RightExpression))
                {
                    if (!string.IsNullOrWhiteSpace(hint))
                        hints.Add(hint);
                }

                if (!string.IsNullOrWhiteSpace(target.Condition.Value))
                    hints.Add(target.Condition.Value);

                foreach (var dynamicValue in target.Condition.DynamicStringValues)
                {
                    if (!string.IsNullOrWhiteSpace(dynamicValue))
                        hints.Add(dynamicValue);
                }
            }

            hints.Remove(string.Empty);
            hints.Remove(" ");
            return hints.ToList();
        }

        private List<string> ExtractRuntimeStringHints(
            IReadOnlyCollection<ColumnConditionTarget> targets,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias)
        {
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                foreach (var expression in EnumerateHintExpressions(target.Condition.LeftExpression, targetColumn, tableAlias))
                {
                    var evaluated = EvaluateScalarExpression(expression, null, scenario, query, currentRow, targetColumn, tableAlias, null);
                    var text = Convert.ToString(evaluated, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        hints.Add(text);
                    }
                }

                foreach (var expression in EnumerateHintExpressions(target.Condition.RightExpression, targetColumn, tableAlias))
                {
                    var evaluated = EvaluateScalarExpression(expression, null, scenario, query, currentRow, targetColumn, tableAlias, null);
                    var text = Convert.ToString(evaluated, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        hints.Add(text);
                    }
                }
            }

            return hints.ToList();
        }

        private IEnumerable<ScalarExpressionInfo> EnumerateHintExpressions(
            ScalarExpressionInfo? expression,
            ColumnSchema targetColumn,
            string tableAlias)
        {
            if (expression == null)
                yield break;

            if (!ExpressionReferencesTargetColumn(expression, targetColumn, tableAlias))
            {
                switch (expression)
                {
                    case FunctionScalarExpressionInfo:
                    case BinaryScalarExpressionInfo:
                    case LiteralScalarExpressionInfo:
                        yield return expression;
                        break;
                }
            }

            switch (expression)
            {
                case FunctionScalarExpressionInfo func:
                    foreach (var argument in func.Arguments)
                    {
                        foreach (var nested in EnumerateHintExpressions(argument, targetColumn, tableAlias))
                        {
                            yield return nested;
                        }
                    }
                    break;

                case BinaryScalarExpressionInfo binary:
                    if (binary.Left != null)
                    {
                        foreach (var nested in EnumerateHintExpressions(binary.Left, targetColumn, tableAlias))
                        {
                            yield return nested;
                        }
                    }

                    if (binary.Right != null)
                    {
                        foreach (var nested in EnumerateHintExpressions(binary.Right, targetColumn, tableAlias))
                        {
                            yield return nested;
                        }
                    }
                    break;

                case UnaryScalarExpressionInfo unary when unary.Operand != null:
                    foreach (var nested in EnumerateHintExpressions(unary.Operand, targetColumn, tableAlias))
                    {
                        yield return nested;
                    }
                    break;
            }
        }

        private static IEnumerable<string> EnumerateStringHints(ScalarExpressionInfo? expression)
        {
            switch (expression)
            {
                case null:
                    yield break;

                case LiteralScalarExpressionInfo literal when literal.Kind == ScalarLiteralKind.String:
                    yield return literal.Value;
                    yield break;

                case FunctionScalarExpressionInfo func:
                    foreach (var argument in func.Arguments)
                    {
                        foreach (var hint in EnumerateStringHints(argument))
                        {
                            yield return hint;
                        }
                    }
                    yield break;

                case BinaryScalarExpressionInfo binary:
                    if (binary.Left != null)
                    {
                        foreach (var hint in EnumerateStringHints(binary.Left))
                            yield return hint;
                    }

                    if (binary.Right != null)
                    {
                        foreach (var hint in EnumerateStringHints(binary.Right))
                            yield return hint;
                    }
                    yield break;

                case UnaryScalarExpressionInfo unary when unary.Operand != null:
                    foreach (var hint in EnumerateStringHints(unary.Operand))
                        yield return hint;
                    yield break;
            }
        }

        private static IEnumerable<object?> DeduplicateCandidates(IEnumerable<object?> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var key = value switch
                {
                    null => "<NULL>",
                    DateTime dt => dt.ToString("O"),
                    DateTimeOffset dto => dto.ToString("O"),
                    TimeSpan ts => ts.ToString("c"),
                    _ => value.ToString() ?? string.Empty
                };

                if (seen.Add(key))
                    yield return value;
            }
        }

        private bool EvaluateConditionTarget(
            object? candidate,
            ColumnConditionTarget target,
            BranchScenario scenario,
            ParsedQuery query,
            ColumnSchema col,
            IValueGenerator generator,
            string tableAlias,
            GeneratedRow? currentRow)
        {
            if (RequiresExpressionEvaluation(target.Condition) &&
                TryEvaluateExpressionConditionTarget(candidate, target.Condition, scenario, query, currentRow, col, tableAlias, out var expressionTruth))
                return expressionTruth == target.DesiredTruth;

            var actualTruth = EvaluateCondition(candidate, target.Condition, target.ComparisonValue, col, generator);
            return actualTruth == target.DesiredTruth;
        }

        private static bool RequiresExpressionEvaluation(ConditionInfo condition)
        {
            if (condition.AggregateFunc.HasValue)
                return false;

            if (condition.DynamicStringValues.Count > 0)
                return true;

            return ContainsComplexExpression(condition.LeftExpression) ||
                   ContainsComplexExpression(condition.RightExpression);
        }

        private static bool ContainsComplexExpression(ScalarExpressionInfo? expression)
        {
            return expression switch
            {
                null => false,
                ColumnScalarExpressionInfo => false,
                LiteralScalarExpressionInfo => false,
                FunctionScalarExpressionInfo => true,
                BinaryScalarExpressionInfo => true,
                UnaryScalarExpressionInfo => true,
                _ => false
            };
        }

        private static bool ExpressionReferencesTargetColumn(
            ScalarExpressionInfo? expression,
            ColumnSchema targetColumn,
            string tableAlias)
        {
            return expression switch
            {
                null => false,
                ColumnScalarExpressionInfo columnExpr =>
                    columnExpr.ColumnName.Equals(targetColumn.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(columnExpr.TableAlias) ||
                     columnExpr.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) ||
                     columnExpr.TableAlias.Equals(targetColumn.TableName, StringComparison.OrdinalIgnoreCase)),
                FunctionScalarExpressionInfo func => GetColumnBearingFunctionArguments(func).Any(arg => ExpressionReferencesTargetColumn(arg, targetColumn, tableAlias)),
                BinaryScalarExpressionInfo binary =>
                    ExpressionReferencesTargetColumn(binary.Left, targetColumn, tableAlias) ||
                    ExpressionReferencesTargetColumn(binary.Right, targetColumn, tableAlias),
                UnaryScalarExpressionInfo unary => ExpressionReferencesTargetColumn(unary.Operand, targetColumn, tableAlias),
                _ => false
            };
        }

        private static IEnumerable<ScalarExpressionInfo> GetColumnBearingFunctionArguments(FunctionScalarExpressionInfo function)
        {
            var startIndex = IsDatePartFunctionName(function.Name) ? 1 : 0;
            return function.Arguments.Skip(startIndex);
        }

        private static bool IsDatePartFunctionName(string? name) =>
            name?.ToUpperInvariant() is "DATEADD" or "DATEDIFF" or "DATEPART" or "DATENAME";

        private bool TryEvaluateExpressionConditionTarget(
            object? candidate,
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            out bool truth)
        {
            truth = false;
            if (condition.IsColumnComparison ||
                condition.LeftExpression == null ||
                !ConditionReferencesTargetColumn(condition, column, tableAlias))
            {
                return false;
            }

            if (condition.DynamicStringValues.Count > 0 &&
                condition.Operator == ComparisonOp.Like &&
                condition.RightExpression != null)
            {
                foreach (var dynamicValue in condition.DynamicStringValues)
                {
                    if (TryEvaluateExpressionConditionTarget(candidate, condition, scenario, query, currentRow, column, tableAlias, dynamicValue, out truth) &&
                        truth)
                    {
                        return true;
                    }
                }

                truth = false;
                return true;
            }

            return TryEvaluateExpressionConditionTarget(candidate, condition, scenario, query, currentRow, column, tableAlias, null, out truth);
        }

        private bool TryEvaluateExpressionConditionTarget(
            object? candidate,
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            string? dynamicValue,
            out bool truth)
        {
            truth = false;
            if (condition.LeftExpression == null)
                return false;

            var leftValue = EvaluateScalarExpression(condition.LeftExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue);

            switch (condition.Operator)
            {
                case ComparisonOp.IsNull:
                    truth = leftValue == null;
                    return true;

                case ComparisonOp.IsNotNull:
                    truth = leftValue != null;
                    return true;

                case ComparisonOp.Like:
                {
                    var patternValue = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue)
                        : condition.LikePattern;
                    truth = EvaluateLike(leftValue?.ToString() ?? string.Empty, patternValue?.ToString() ?? string.Empty);
                    if (condition.IsNegated)
                    {
                        truth = !truth;
                    }
                    return true;
                }

                case ComparisonOp.In:
                case ComparisonOp.NotIn:
                {
                    var match = condition.InValues.Any(v => CompareExpressionValues(leftValue, v) == 0);
                    truth = condition.Operator == ComparisonOp.In ? match : !match;
                    return true;
                }

                case ComparisonOp.Between:
                {
                    var lower = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue)
                        : (string.IsNullOrEmpty(condition.Value) ? null : condition.Value);
                    var upper = string.IsNullOrEmpty(condition.SecondValue) ? null : condition.SecondValue;
                    truth = CompareExpressionValues(leftValue, lower) >= 0 &&
                            CompareExpressionValues(leftValue, upper) <= 0;
                    if (condition.IsNegated)
                    {
                        truth = !truth;
                    }
                    return true;
                }

                case ComparisonOp.Equal:
                case ComparisonOp.NotEqual:
                case ComparisonOp.GreaterThan:
                case ComparisonOp.GreaterThanOrEqual:
                case ComparisonOp.LessThan:
                case ComparisonOp.LessThanOrEqual:
                {
                    var rightValue = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue)
                        : (string.IsNullOrEmpty(condition.Value) ? null : condition.Value);
                    if (IsScalarSubqueryPlaceholder(rightValue) &&
                        TryResolveScalarSubqueryComparisonValue(query, condition, column, out var scalarSubqueryValue))
                    {
                        rightValue = scalarSubqueryValue;
                    }

                    var comparison = CompareExpressionValues(leftValue, rightValue);
                    truth = condition.Operator switch
                    {
                        ComparisonOp.Equal => comparison == 0,
                        ComparisonOp.NotEqual => comparison != 0,
                        ComparisonOp.GreaterThan => comparison > 0,
                        ComparisonOp.GreaterThanOrEqual => comparison >= 0,
                        ComparisonOp.LessThan => comparison < 0,
                        ComparisonOp.LessThanOrEqual => comparison <= 0,
                        _ => false
                    };
                    return true;
                }
            }

            return false;
        }

        private static bool ConditionReferencesTargetColumn(
            ConditionInfo condition,
            ColumnSchema column,
            string tableAlias)
        {
            return condition.ReferencedColumns.Any(r =>
                r.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(r.TableAlias) ||
                 r.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) ||
                 r.TableAlias.Equals(column.TableName, StringComparison.OrdinalIgnoreCase)));
        }

        private object? EvaluateScalarExpression(
            ScalarExpressionInfo? expression,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue)
        {
            switch (expression)
            {
                case null:
                    return null;

                case ColumnScalarExpressionInfo columnExpr:
                    if (IsCurrentTimestampExpression(columnExpr))
                    {
                        return _generationLocalNow;
                    }

                    if (IsCurrentDateExpression(columnExpr))
                    {
                        return _generationLocalNow.Date;
                    }

                    if (columnExpr.ColumnName.Equals(targetColumn.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(columnExpr.TableAlias) ||
                         columnExpr.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) ||
                         columnExpr.TableAlias.Equals(targetColumn.TableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return candidate;
                    }

                    if (!string.IsNullOrWhiteSpace(dynamicValue) &&
                        columnExpr.ColumnName.Equals("value", StringComparison.OrdinalIgnoreCase))
                    {
                        return dynamicValue;
                    }

                    if (TryResolveExpressionReferenceValue(
                            scenario,
                            query,
                            currentRow,
                            targetColumn.TableName,
                            columnExpr,
                            out var resolvedValue))
                    {
                        return resolvedValue;
                    }

                    return ResolveNonTargetPlaceholder(columnExpr);

                case LiteralScalarExpressionInfo literal:
                    return EvaluateLiteralExpression(literal);

                case UnaryScalarExpressionInfo unary:
                {
                    var operand = EvaluateScalarExpression(unary.Operand, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue);
                    return unary.Operator switch
                    {
                        "Negative" when TryConvertDecimal(operand, out var decimalValue) => -decimalValue,
                        _ => operand
                    };
                }

                case BinaryScalarExpressionInfo binary:
                {
                    var left = EvaluateScalarExpression(binary.Left, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue);
                    var right = EvaluateScalarExpression(binary.Right, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue);
                    return EvaluateBinaryExpression(binary.Operator, left, right);
                }

                case FunctionScalarExpressionInfo func:
                    return EvaluateFunctionExpression(func, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue);
            }

            return null;
        }

        private object? EvaluateLiteralExpression(LiteralScalarExpressionInfo literal)
        {
            if (literal.Kind == ScalarLiteralKind.Other)
            {
                if (IsCurrentTimestampText(literal.Value) || IsCurrentTimestampText(literal.Text))
                    return _generationLocalNow;

                if (IsCurrentDateText(literal.Value) || IsCurrentDateText(literal.Text))
                    return _generationLocalNow.Date;
            }

            return literal.Kind switch
            {
                ScalarLiteralKind.String => literal.Value,
                ScalarLiteralKind.Integer => int.TryParse(literal.Value, out var intValue) ? intValue : literal.Value,
                ScalarLiteralKind.Numeric or ScalarLiteralKind.Real or ScalarLiteralKind.Money =>
                    decimal.TryParse(literal.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var decimalValue)
                        ? decimalValue
                        : literal.Value,
                ScalarLiteralKind.Null => null,
                _ => literal.Value
            };
        }

        private static object? EvaluateBinaryExpression(ScalarBinaryOperator op, object? left, object? right)
        {
            if (op == ScalarBinaryOperator.Add &&
                (left is string || right is string))
            {
                return (left?.ToString() ?? string.Empty) + (right?.ToString() ?? string.Empty);
            }

            if (!TryConvertDecimal(left, out var leftDecimal) ||
                !TryConvertDecimal(right, out var rightDecimal))
            {
                return null;
            }

            return op switch
            {
                ScalarBinaryOperator.Add => leftDecimal + rightDecimal,
                ScalarBinaryOperator.Subtract => leftDecimal - rightDecimal,
                ScalarBinaryOperator.Multiply => leftDecimal * rightDecimal,
                ScalarBinaryOperator.Divide => rightDecimal == 0 ? null : leftDecimal / rightDecimal,
                ScalarBinaryOperator.Modulo => rightDecimal == 0 ? null : leftDecimal % rightDecimal,
                _ => null
            };
        }

        private object? EvaluateFunctionExpression(
            FunctionScalarExpressionInfo function,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue)
        {
            var args = function.Arguments
                .Select(a => EvaluateScalarExpression(a, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue))
                .ToList();

            return function.Name.ToUpperInvariant() switch
            {
                "LOWER" => args[0]?.ToString()?.ToLowerInvariant(),
                "UPPER" => args[0]?.ToString()?.ToUpperInvariant(),
                "LTRIM" => args[0]?.ToString()?.TrimStart(),
                "RTRIM" => args[0]?.ToString()?.TrimEnd(),
                "TRIM" => args[0]?.ToString()?.Trim(),
                "LEN" => args[0]?.ToString()?.Length ?? 0,
                "LEFT" => EvaluateLeft(args),
                "RIGHT" => EvaluateRight(args),
                "SUBSTRING" => EvaluateSubstring(args),
                "CHARINDEX" => EvaluateCharIndex(args),
                "REPLACE" => EvaluateReplace(args),
                "CONCAT" => string.Concat(args.Select(a => a?.ToString() ?? string.Empty)),
                "FORMAT" => args[0] == null ? null : Convert.ToString(args[0], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                "ISNULL" => args[0] ?? args.ElementAtOrDefault(1),
                "COALESCE" => args.FirstOrDefault(a => a != null),
                "NULLIF" => EvaluateNullIf(args),
                "CAST" => EvaluateConversionFunction(args, tryMode: false),
                "CONVERT" => EvaluateConversionFunction(args, tryMode: false),
                "TRY_CAST" => EvaluateConversionFunction(args, tryMode: true),
                "TRY_CONVERT" => EvaluateConversionFunction(args, tryMode: true),
                "PARSE" => EvaluateConversionFunction(args, tryMode: false),
                "TRY_PARSE" => EvaluateConversionFunction(args, tryMode: true),
                "GETDATE" => _generationLocalNow,
                "SYSDATETIME" => _generationLocalNow,
                "CURRENT_TIMESTAMP" or "CURRENTTIMESTAMP" => _generationLocalNow,
                "CURRENT_DATE" or "CURRENTDATE" => _generationLocalNow.Date,
                "GETUTCDATE" => _generationUtcNow,
                "SYSUTCDATETIME" => _generationUtcNow,
                "DATEADD" => EvaluateDateAdd(function, args),
                "DATEDIFF" => EvaluateDateDiff(function, args),
                "DATEPART" => EvaluateDatePart(function, args),
                "YEAR" => EvaluateDatePartValue("year", args.ElementAtOrDefault(0)),
                "MONTH" => EvaluateDatePartValue("month", args.ElementAtOrDefault(0)),
                "DAY" => EvaluateDatePartValue("day", args.ElementAtOrDefault(0)),
                "EOMONTH" => EvaluateEomonth(args),
                _ => null
            };
        }

        private object? EvaluateDateAdd(FunctionScalarExpressionInfo function, IReadOnlyList<object?> args)
        {
            var datePart = ExtractDatePartToken(function.Arguments.ElementAtOrDefault(0), args.ElementAtOrDefault(0));
            if (!TryConvertInt(args.ElementAtOrDefault(1), out var amount))
                return null;

            var dateValue = args.ElementAtOrDefault(2);
            if (dateValue is DateTimeOffset offset)
            {
                return AddSqlDatePart(offset, datePart, amount);
            }

            if (!TryConvertToDateTimeValue(dateValue, out var dateTime))
                return null;

            return AddSqlDatePart(dateTime, datePart, amount);
        }

        private object? EvaluateDateDiff(FunctionScalarExpressionInfo function, IReadOnlyList<object?> args)
        {
            var datePart = ExtractDatePartToken(function.Arguments.ElementAtOrDefault(0), args.ElementAtOrDefault(0));
            if (!TryConvertToDateTimeValue(args.ElementAtOrDefault(1), out var start) ||
                !TryConvertToDateTimeValue(args.ElementAtOrDefault(2), out var end))
            {
                return null;
            }

            return EvaluateDateDiffValue(datePart, start, end);
        }

        private object? EvaluateDatePart(FunctionScalarExpressionInfo function, IReadOnlyList<object?> args)
        {
            var datePart = ExtractDatePartToken(function.Arguments.ElementAtOrDefault(0), args.ElementAtOrDefault(0));
            return EvaluateDatePartValue(datePart, args.ElementAtOrDefault(1));
        }

        private static object? EvaluateDatePartValue(string datePart, object? value)
        {
            var normalized = NormalizeDatePart(datePart);
            if (normalized == "tzoffset" && value is DateTimeOffset offset)
            {
                return (int)offset.Offset.TotalMinutes;
            }

            if (!TryConvertToDateTimeValue(value, out var dateTime))
                return null;

            return normalized switch
            {
                "year" => dateTime.Year,
                "quarter" => ((dateTime.Month - 1) / 3) + 1,
                "month" => dateTime.Month,
                "dayofyear" => dateTime.DayOfYear,
                "day" => dateTime.Day,
                "week" => System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                    dateTime,
                    System.Globalization.CalendarWeekRule.FirstDay,
                    DayOfWeek.Sunday),
                "weekday" => ((int)dateTime.DayOfWeek) + 1,
                "hour" => dateTime.Hour,
                "minute" => dateTime.Minute,
                "second" => dateTime.Second,
                "millisecond" => dateTime.Millisecond,
                "microsecond" => (dateTime.Ticks % TimeSpan.TicksPerSecond) / 10,
                "nanosecond" => (dateTime.Ticks % TimeSpan.TicksPerSecond) * 100,
                _ => null
            };
        }

        private static object? EvaluateEomonth(IReadOnlyList<object?> args)
        {
            if (!TryConvertToDateTimeValue(args.ElementAtOrDefault(0), out var startDate))
                return null;

            var monthToAdd = TryConvertInt(args.ElementAtOrDefault(1), out var parsedMonthToAdd)
                ? parsedMonthToAdd
                : 0;
            return EndOfMonth(startDate.AddMonths(monthToAdd));
        }

        private static object? EvaluateNullIf(IReadOnlyList<object?> args)
        {
            var left = args.ElementAtOrDefault(0);
            var right = args.ElementAtOrDefault(1);
            return CompareExpressionValues(left, right) == 0 ? null : left;
        }

        private static object? EvaluateConversionFunction(IReadOnlyList<object?> args, bool tryMode)
        {
            var typeName = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var value = args.ElementAtOrDefault(1);
            return ConvertUsingSqlType(value, typeName, tryMode);
        }

        private static object? EvaluateLeft(IReadOnlyList<object?> args)
        {
            var source = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var length = TryConvertInt(args.ElementAtOrDefault(1), out var value) ? value : 0;
            return length <= 0 ? string.Empty : source[..Math.Min(length, source.Length)];
        }

        private static object? EvaluateRight(IReadOnlyList<object?> args)
        {
            var source = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var length = TryConvertInt(args.ElementAtOrDefault(1), out var value) ? value : 0;
            return length <= 0 ? string.Empty : source[^Math.Min(length, source.Length)..];
        }

        private static object? EvaluateSubstring(IReadOnlyList<object?> args)
        {
            var source = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var start = TryConvertInt(args.ElementAtOrDefault(1), out var startValue) ? startValue : 1;
            var length = TryConvertInt(args.ElementAtOrDefault(2), out var lengthValue) ? lengthValue : 0;

            if (length <= 0 || start > source.Length)
                return string.Empty;

            var zeroBased = Math.Max(start - 1, 0);
            if (zeroBased >= source.Length)
                return string.Empty;

            return source.Substring(zeroBased, Math.Min(length, source.Length - zeroBased));
        }

        private static object? EvaluateCharIndex(IReadOnlyList<object?> args)
        {
            var needle = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var haystack = args.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
            var start = TryConvertInt(args.ElementAtOrDefault(2), out var startValue) ? Math.Max(startValue - 1, 0) : 0;
            if (string.IsNullOrEmpty(needle) || start >= haystack.Length)
                return 0;

            var index = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? index + 1 : 0;
        }

        private static object? EvaluateReplace(IReadOnlyList<object?> args)
        {
            var source = args.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            var oldValue = args.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
            var newValue = args.ElementAtOrDefault(2)?.ToString() ?? string.Empty;
            return source.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        private static object? ConvertUsingSqlType(object? value, string sqlType, bool tryMode)
        {
            if (value == null)
                return null;

            var normalizedType = NormalizeSqlTypeName(sqlType);

            try
            {
                return normalizedType switch
                {
                    "INT" => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
                    "BIGINT" => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
                    "SMALLINT" => Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture),
                    "TINYINT" => Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture),
                    "BIT" => Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture),
                    "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" =>
                        Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
                    "FLOAT" or "REAL" => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                    "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" =>
                        value is DateTimeOffset dateTimeOffset
                            ? dateTimeOffset.DateTime
                            : Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture),
                    "DATETIMEOFFSET" => value is DateTimeOffset dto
                        ? dto
                        : DateTimeOffset.Parse(
                            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            System.Globalization.CultureInfo.InvariantCulture),
                    "TIME" => value is TimeSpan ts
                        ? ts
                        : TimeSpan.Parse(
                            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            System.Globalization.CultureInfo.InvariantCulture),
                    "CHAR" or "NCHAR" or "VARCHAR" or "NVARCHAR" or "TEXT" or "NTEXT" =>
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                };
            }
            catch
            {
                return tryMode ? null : null;
            }
        }

        private static string NormalizeSqlTypeName(string sqlType)
        {
            if (string.IsNullOrWhiteSpace(sqlType))
                return string.Empty;

            var trimmed = sqlType.Trim().Trim('[', ']');
            var parenIndex = trimmed.IndexOf('(');
            if (parenIndex >= 0)
            {
                trimmed = trimmed[..parenIndex];
            }

            return trimmed.Trim().ToUpperInvariant();
        }

        private static object? ResolveNonTargetPlaceholder(ColumnScalarExpressionInfo expression)
        {
            if (expression.ColumnName.EndsWith("ID", StringComparison.OrdinalIgnoreCase) ||
                expression.ColumnName.EndsWith("NUM", StringComparison.OrdinalIgnoreCase) ||
                expression.ColumnName.Contains("COUNT", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return string.Empty;
        }

        private static bool TryResolveExpressionReferenceValue(
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            string currentTableName,
            ColumnScalarExpressionInfo expression,
            out object? value)
        {
            value = null;

            var referencedTable = !string.IsNullOrWhiteSpace(expression.TableAlias)
                ? query.ResolveAlias(expression.TableAlias)
                : currentTableName;

            if (currentRow != null &&
                referencedTable.Equals(currentTableName, StringComparison.OrdinalIgnoreCase))
            {
                var currentValue = currentRow.GetValue(expression.ColumnName);
                if (currentValue != null && currentValue != DBNull.Value)
                {
                    value = currentValue;
                    return true;
                }
            }

            return TryResolveScenarioValue(scenario, referencedTable, expression.ColumnName, out value);
        }

        private static int CompareExpressionValues(object? left, object? right)
        {
            if (left == null && right == null)
                return 0;
            if (left == null)
                return -1;
            if (right == null)
                return 1;

            if (TryConvertDecimal(left, out var leftDecimal) &&
                TryConvertDecimal(right, out var rightDecimal))
            {
                return leftDecimal.CompareTo(rightDecimal);
            }

            if (left is DateTime leftDateTime && right is DateTime rightDateTime)
                return leftDateTime.CompareTo(rightDateTime);

            if (left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset)
                return leftOffset.CompareTo(rightOffset);

            if (left is TimeSpan leftTime && right is TimeSpan rightTime)
                return leftTime.CompareTo(rightTime);

            if ((IsDateLikeValue(left) || IsDateLikeValue(right)) &&
                TryConvertToDateTimeValue(left, out var leftDate) &&
                TryConvertToDateTimeValue(right, out var rightDate))
            {
                return leftDate.CompareTo(rightDate);
            }

            if ((left is TimeSpan || right is TimeSpan) &&
                TryConvertToTimeSpanValue(left, out var leftTimeValue) &&
                TryConvertToTimeSpanValue(right, out var rightTimeValue))
            {
                return leftTimeValue.CompareTo(rightTimeValue);
            }

            return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryConvertDecimal(object? value, out decimal result)
        {
            switch (value)
            {
                case null:
                    result = 0;
                    return false;
                case decimal decimalValue:
                    result = decimalValue;
                    return true;
                default:
                    return decimal.TryParse(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result);
            }
        }

        private static bool TryConvertInt(object? value, out int result)
        {
            switch (value)
            {
                case int intValue:
                    result = intValue;
                    return true;
                default:
                    return int.TryParse(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result);
            }
        }

        private static bool TryConvertToDateTimeValue(object? value, out DateTime result)
        {
            switch (value)
            {
                case DateTime dateTime:
                    result = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
                    return true;
                case DateTimeOffset offset:
                    result = DateTime.SpecifyKind(offset.DateTime, DateTimeKind.Unspecified);
                    return true;
                case string text when !string.IsNullOrWhiteSpace(text):
                    var trimmed = text.Trim();
                    if (TryParseOdbcDateLiteral(trimmed, out result))
                        return true;
                    if (DateTimeOffset.TryParse(
                            trimmed,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var parsedOffset))
                    {
                        result = DateTime.SpecifyKind(parsedOffset.DateTime, DateTimeKind.Unspecified);
                        return true;
                    }
                    if (DateTime.TryParse(
                            trimmed,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out var parsedDateTime))
                    {
                        result = DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Unspecified);
                        return true;
                    }
                    break;
            }

            result = default;
            return false;
        }

        private static bool TryConvertToTimeSpanValue(object? value, out TimeSpan result)
        {
            switch (value)
            {
                case TimeSpan time:
                    result = time;
                    return true;
                case DateTime dateTime:
                    result = dateTime.TimeOfDay;
                    return true;
                case DateTimeOffset offset:
                    result = offset.TimeOfDay;
                    return true;
                case string text when TimeSpan.TryParse(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed):
                    result = parsed;
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        private static bool IsDateLikeValue(object? value)
        {
            return value switch
            {
                DateTime or DateTimeOffset => true,
                string text => LooksLikeDateTimeText(text),
                _ => false
            };
        }

        private static bool LooksLikeDateTimeText(string text)
        {
            var trimmed = text.Trim();
            return trimmed.Contains('-', StringComparison.Ordinal) ||
                   trimmed.Contains('/', StringComparison.Ordinal) ||
                   trimmed.Contains(':', StringComparison.Ordinal) ||
                   trimmed.StartsWith("{d", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("{ts", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseOdbcDateLiteral(string text, out DateTime value)
        {
            value = default;
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) ||
                !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            var firstQuote = trimmed.IndexOf('\'');
            var lastQuote = trimmed.LastIndexOf('\'');
            if (firstQuote < 0 || lastQuote <= firstQuote)
                return false;

            var inner = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
            if (!DateTime.TryParse(
                    inner,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                return false;
            }

            value = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            return true;
        }

        private static string ExtractDatePartToken(ScalarExpressionInfo? expression, object? evaluatedValue)
        {
            var raw = expression switch
            {
                ColumnScalarExpressionInfo column when !string.IsNullOrWhiteSpace(column.ColumnName) => column.ColumnName,
                LiteralScalarExpressionInfo literal when !string.IsNullOrWhiteSpace(literal.Value) => literal.Value,
                { Text.Length: > 0 } => expression.Text,
                _ => Convert.ToString(evaluatedValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            };

            return NormalizeDatePart(raw);
        }

        private static string NormalizeDatePart(string value)
        {
            var token = value
                .Trim()
                .Trim('[', ']')
                .Trim('\'', '"')
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            return token switch
            {
                "yy" or "yyyy" or "year" => "year",
                "qq" or "q" or "quarter" => "quarter",
                "mm" or "m" or "month" => "month",
                "dy" or "y" or "dayofyear" => "dayofyear",
                "dd" or "d" or "day" => "day",
                "wk" or "ww" or "week" => "week",
                "dw" or "w" or "weekday" => "weekday",
                "hh" or "hour" => "hour",
                "mi" or "n" or "minute" => "minute",
                "ss" or "s" or "second" => "second",
                "ms" or "millisecond" => "millisecond",
                "mcs" or "microsecond" => "microsecond",
                "ns" or "nanosecond" => "nanosecond",
                "tz" or "tzoffset" => "tzoffset",
                _ => token
            };
        }

        private static DateTime? AddSqlDatePart(DateTime value, string datePart, int amount)
        {
            try
            {
                return NormalizeDatePart(datePart) switch
                {
                    "year" => value.AddYears(amount),
                    "quarter" => value.AddMonths(amount * 3),
                    "month" => value.AddMonths(amount),
                    "dayofyear" or "day" or "weekday" => value.AddDays(amount),
                    "week" => value.AddDays(amount * 7),
                    "hour" => value.AddHours(amount),
                    "minute" => value.AddMinutes(amount),
                    "second" => value.AddSeconds(amount),
                    "millisecond" => value.AddMilliseconds(amount),
                    "microsecond" => value.AddTicks(amount * 10L),
                    "nanosecond" => value.AddTicks(amount / 100L),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static DateTimeOffset? AddSqlDatePart(DateTimeOffset value, string datePart, int amount)
        {
            try
            {
                return NormalizeDatePart(datePart) switch
                {
                    "year" => value.AddYears(amount),
                    "quarter" => value.AddMonths(amount * 3),
                    "month" => value.AddMonths(amount),
                    "dayofyear" or "day" or "weekday" => value.AddDays(amount),
                    "week" => value.AddDays(amount * 7),
                    "hour" => value.AddHours(amount),
                    "minute" => value.AddMinutes(amount),
                    "second" => value.AddSeconds(amount),
                    "millisecond" => value.AddMilliseconds(amount),
                    "microsecond" => value.AddTicks(amount * 10L),
                    "nanosecond" => value.AddTicks(amount / 100L),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static int EvaluateDateDiffValue(string datePart, DateTime start, DateTime end)
        {
            return NormalizeDatePart(datePart) switch
            {
                "year" => end.Year - start.Year,
                "quarter" => ((end.Year * 4) + ((end.Month - 1) / 3)) -
                             ((start.Year * 4) + ((start.Month - 1) / 3)),
                "month" => ((end.Year - start.Year) * 12) + end.Month - start.Month,
                "dayofyear" or "day" => (end.Date - start.Date).Days,
                "week" => (StartOfSqlWeek(end) - StartOfSqlWeek(start)).Days / 7,
                "hour" => (int)Math.Truncate((end - start).TotalHours),
                "minute" => (int)Math.Truncate((end - start).TotalMinutes),
                "second" => (int)Math.Truncate((end - start).TotalSeconds),
                "millisecond" => (int)Math.Truncate((end - start).TotalMilliseconds),
                "microsecond" => (int)Math.Truncate((end - start).Ticks / 10d),
                "nanosecond" => (int)Math.Truncate((end - start).Ticks * 100d),
                _ => 0
            };
        }

        private static DateTime EndOfMonth(DateTime value)
        {
            var days = DateTime.DaysInMonth(value.Year, value.Month);
            return new DateTime(value.Year, value.Month, days, 0, 0, 0, DateTimeKind.Unspecified);
        }

        private static DateTime StartOfSqlWeek(DateTime value) =>
            value.Date.AddDays(-(int)value.DayOfWeek);

        private static int Clamp(int value, int min, int max) =>
            Math.Min(Math.Max(value, min), max);

        private static bool IsCurrentTimestampExpression(ColumnScalarExpressionInfo expression) =>
            string.IsNullOrWhiteSpace(expression.TableAlias) &&
            IsCurrentTimestampText(expression.ColumnName);

        private static bool IsCurrentDateExpression(ColumnScalarExpressionInfo expression) =>
            string.IsNullOrWhiteSpace(expression.TableAlias) &&
            IsCurrentDateText(expression.ColumnName);

        private static bool IsCurrentTimestampText(string? text)
        {
            var normalized = NormalizeKeywordText(text);
            return normalized is "CURRENT_TIMESTAMP" or "CURRENTTIMESTAMP";
        }

        private static bool IsCurrentDateText(string? text)
        {
            var normalized = NormalizeKeywordText(text);
            return normalized is "CURRENT_DATE" or "CURRENTDATE";
        }

        private static string NormalizeKeywordText(string? text) =>
            (text ?? string.Empty).Trim().Trim('[', ']').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        private bool EvaluateCondition(
            object? candidate,
            ConditionInfo condition,
            object? comparisonValue,
            ColumnSchema col,
            IValueGenerator generator)
        {
            switch (condition.Operator)
            {
                case ComparisonOp.IsNull:
                    return candidate == null;
                case ComparisonOp.IsNotNull:
                    return candidate != null;
                case ComparisonOp.In:
                    return condition.InValues
                        .Select(v => generator.GenerateFromLiteral(v, col))
                        .Any(v => CompareScalarValues(candidate, v, col) == 0);
                case ComparisonOp.NotIn:
                    return condition.InValues
                        .Select(v => generator.GenerateFromLiteral(v, col))
                        .All(v => CompareScalarValues(candidate, v, col) != 0);
                case ComparisonOp.Between:
                    var lower = generator.GenerateFromLiteral(condition.Value, col);
                    var upper = generator.GenerateFromLiteral(condition.SecondValue, col);
                    var between = CompareScalarValues(candidate, lower, col) >= 0 &&
                                  CompareScalarValues(candidate, upper, col) <= 0;
                    return condition.IsNegated ? !between : between;
                case ComparisonOp.Like:
                    var like = EvaluateLike(candidate?.ToString() ?? string.Empty, condition.LikePattern);
                    return condition.IsNegated ? !like : like;
                default:
                    var rightValue = condition.IsColumnComparison || comparisonValue != null
                        ? comparisonValue
                        : generator.GenerateFromLiteral(condition.Value, col);
                    var comparison = CompareScalarValues(candidate, rightValue, col);
                    return condition.Operator switch
                    {
                        ComparisonOp.Equal => comparison == 0,
                        ComparisonOp.NotEqual => comparison != 0,
                        ComparisonOp.GreaterThan => comparison > 0,
                        ComparisonOp.GreaterThanOrEqual => comparison >= 0,
                        ComparisonOp.LessThan => comparison < 0,
                        ComparisonOp.LessThanOrEqual => comparison <= 0,
                        _ => false
                    };
            }
        }

        private static bool EvaluateLike(string input, string pattern)
        {
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("%", ".*")
                .Replace("_", ".") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(
                input,
                regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static int CompareScalarValues(object? left, object? right, ColumnSchema col)
        {
            if (left == null && right == null)
                return 0;
            if (left == null)
                return -1;
            if (right == null)
                return 1;

            return col.TypeCategory switch
            {
                DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float =>
                    Convert.ToDecimal(left).CompareTo(Convert.ToDecimal(right)),
                DataTypeCategory.Boolean =>
                    Convert.ToBoolean(left).CompareTo(Convert.ToBoolean(right)),
                DataTypeCategory.DateTime =>
                    ConvertToDateTimeForCompare(left).CompareTo(ConvertToDateTimeForCompare(right)),
                DataTypeCategory.Time =>
                    ((TimeSpan)left).CompareTo((TimeSpan)right),
                DataTypeCategory.DateTimeOffset =>
                    ConvertToDateTimeOffsetForCompare(left).CompareTo(ConvertToDateTimeOffsetForCompare(right)),
                _ =>
                    string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase)
            };
        }

        private static DateTime ConvertToDateTimeForCompare(object value)
        {
            return value switch
            {
                DateTime dt => dt,
                DateTimeOffset dto => dto.DateTime,
                string s when DateTimeOffset.TryParse(
                    s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedDto) => parsedDto.DateTime,
                _ => Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        private static DateTimeOffset ConvertToDateTimeOffsetForCompare(object value)
        {
            return value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
                string s when DateTimeOffset.TryParse(
                    s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedDto) => parsedDto,
                string s when DateTime.TryParse(
                    s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsedDt) => new DateTimeOffset(DateTime.SpecifyKind(parsedDt, DateTimeKind.Unspecified), TimeSpan.Zero),
                _ => throw new InvalidOperationException($"Cannot compare value '{value}' as datetimeoffset.")
            };
        }

        private static string FormatComparisonLiteral(object value)
        {
            return value switch
            {
                DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
        }

        private static bool TryResolveScalarSubqueryComparisonValue(
            ParsedQuery query,
            ConditionInfo condition,
            ColumnSchema column,
            out object? value)
        {
            value = null;
            if (!IsScalarSubqueryPlaceholder(condition.RightExpression?.Text) &&
                !IsScalarSubqueryPlaceholder(condition.Value))
            {
                return false;
            }

            var subquery = query.Subqueries.FirstOrDefault(s =>
                s.Operator == SubqueryOperator.ScalarComparison &&
                (string.IsNullOrWhiteSpace(condition.RightExpression?.Text) ||
                 NormalizeSqlSnippet(s.SubquerySql).Equals(
                     NormalizeSqlSnippet(condition.RightExpression.Text),
                     StringComparison.OrdinalIgnoreCase) ||
                 NormalizeSqlSnippet($"({s.SubquerySql})").Equals(
                     NormalizeSqlSnippet(condition.RightExpression.Text),
                     StringComparison.OrdinalIgnoreCase)));

            if (subquery == null)
                return false;

            value = column.TypeCategory switch
            {
                DataTypeCategory.Integer => 10,
                DataTypeCategory.Decimal => 10m,
                DataTypeCategory.Float => 10d,
                DataTypeCategory.DateTime => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                DataTypeCategory.DateTimeOffset => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                _ => null
            };

            return value != null;
        }

        private static bool IsScalarSubqueryPlaceholder(object? value) =>
            IsScalarSubqueryPlaceholder(value?.ToString());

        private static bool IsScalarSubqueryPlaceholder(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains("SELECT", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSqlSnippet(string value) =>
            new(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

        private static bool IsRangeCondition(ConditionInfo condition)
        {
            return condition.Operator is
                ComparisonOp.GreaterThan or
                ComparisonOp.GreaterThanOrEqual or
                ComparisonOp.LessThan or
                ComparisonOp.LessThanOrEqual or
                ComparisonOp.Between;
        }

        private static bool GetDesiredTruthForCondition(
            BranchScenario scenario,
            ConditionInfo condition,
            bool defaultTruth)
        {
            return scenario.PredicateTruthMap.TryGetValue(condition.Key, out var desiredTruth)
                ? desiredTruth
                : defaultTruth;
        }

        private static ConditionInfo? FindConditionByKey(ParsedQuery query, string key)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsConditionTargetingColumn(
            ParsedQuery query,
            ConditionInfo condition,
            string tableAlias,
            string columnName)
        {
            return ConditionTargetsColumn(
                query,
                condition,
                query.ResolveAlias(tableAlias),
                tableAlias,
                columnName);
        }

        private static bool ConditionTargetsColumn(
            ParsedQuery query,
            ConditionInfo condition,
            string tableName,
            string tableAlias,
            string columnName)
        {
            if (!string.IsNullOrWhiteSpace(condition.ColumnName) &&
                condition.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                MatchesConditionTarget(
                    query,
                    condition.TableAlias,
                    tableName,
                    tableAlias))
            {
                return true;
            }

            if (condition.ReferencedColumns.Any(r =>
                    MatchesColumnReference(query, r, tableName, tableAlias, columnName)))
            {
                return true;
            }

            return !condition.IsSubqueryPredicate &&
                   condition.ReferencedColumns.Count == 0 &&
                   !string.IsNullOrWhiteSpace(condition.ExpressionText) &&
                   ExpressionMentionsColumn(condition.ExpressionText, tableAlias, columnName);
        }

        private static bool ConditionTargetsColumn(
            IReadOnlyDictionary<string, string> aliasMap,
            ConditionInfo condition,
            string tableName,
            string tableAlias,
            string columnName)
        {
            if (!string.IsNullOrWhiteSpace(condition.ColumnName) &&
                condition.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                MatchesConditionTarget(aliasMap, condition.TableAlias, tableName, tableAlias))
            {
                return true;
            }

            if (condition.ReferencedColumns.Any(r =>
                    !r.IsRightSide &&
                    MatchesColumnReference(aliasMap, r, tableName, tableAlias, columnName)))
            {
                return true;
            }

            return !condition.IsSubqueryPredicate &&
                   condition.ReferencedColumns.Count == 0 &&
                   !string.IsNullOrWhiteSpace(condition.ExpressionText) &&
                   ExpressionMentionsColumn(condition.ExpressionText, tableAlias, columnName);
        }

        private static bool MatchesColumnReference(
            ParsedQuery query,
            ConditionColumnReference reference,
            string tableName,
            string tableAlias,
            string columnName)
        {
            return reference.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                   MatchesConditionTarget(query, reference.TableAlias, tableName, tableAlias);
        }

        private static bool MatchesColumnReference(
            IReadOnlyDictionary<string, string> aliasMap,
            ConditionColumnReference reference,
            string tableName,
            string tableAlias,
            string columnName)
        {
            return reference.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                   MatchesConditionTarget(aliasMap, reference.TableAlias, tableName, tableAlias);
        }

        private bool IsPredicatePinnedColumn(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Any(condition =>
                    GetDesiredTruthForCondition(scenario, condition, defaultTruth: true) &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName));
        }

        private bool HasExactOrUpperBoundPinnedPredicate(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Any(condition =>
                    GetDesiredTruthForCondition(scenario, condition, defaultTruth: true) &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName) &&
                    condition.Operator is
                        ComparisonOp.Equal or
                        ComparisonOp.In or
                        ComparisonOp.Between or
                        ComparisonOp.LessThan or
                        ComparisonOp.LessThanOrEqual or
                        ComparisonOp.Like);
        }

        private static string BuildJoinScenarioKey(JoinInfo join)
        {
            return string.Join("|",
                join.Type,
                join.LeftTableAlias,
                join.LeftColumn,
                join.RightTableAlias,
                join.RightColumn);
        }

        private static bool ResolveSubqueryPredicateTruth(
            BranchScenario scenario,
            SubqueryInfo subquery,
            IReadOnlyDictionary<string, bool>? parentTruthMap)
        {
            if (!string.IsNullOrWhiteSpace(subquery.PredicateConditionKey))
            {
                if (parentTruthMap != null &&
                    parentTruthMap.TryGetValue(subquery.PredicateConditionKey, out var nestedTruth))
                {
                    return nestedTruth;
                }

                if (scenario.PredicateTruthMap.TryGetValue(subquery.PredicateConditionKey, out var scenarioTruth))
                {
                    return scenarioTruth;
                }
            }

            return true;
        }

        private static Dictionary<string, bool> BuildSubqueryInternalTruthMap(
            SubqueryInfo subquery,
            bool predicateTruth)
        {
            if (subquery.WherePredicateScope?.Root == null)
            {
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            var subqueryShouldReturnRows = predicateTruth ^ IsNegativeSubqueryOperator(subquery.Operator);
            var assignments = PredicateTruthPlanner.GetMinimalAssignments(
                subquery.WherePredicateScope.Root,
                desiredTruth: subqueryShouldReturnRows);

            if (assignments.Count == 0)
            {
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            if (subqueryShouldReturnRows)
            {
                return new Dictionary<string, bool>(assignments[0], StringComparer.OrdinalIgnoreCase);
            }

            var conditionsByKey = subquery.WherePredicateScope.Conditions
                .ToDictionary(c => c.Key, c => c, StringComparer.OrdinalIgnoreCase);

            var best = assignments
                .OrderBy(a => a.Count(kvp =>
                    conditionsByKey.TryGetValue(kvp.Key, out var condition) &&
                    condition.IsColumnComparison))
                .ThenBy(a => a.Count)
                .ThenBy(a => string.Join("|", a.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => $"{k.Key}:{k.Value}")), StringComparer.OrdinalIgnoreCase)
                .First();

            return new Dictionary<string, bool>(best, StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ColumnConditionTarget
        {
            public ColumnConditionTarget(
                ConditionInfo condition,
                bool desiredTruth,
                object? comparisonValue = null)
            {
                Condition = condition;
                DesiredTruth = desiredTruth;
                ComparisonValue = comparisonValue;
            }

            public ConditionInfo Condition { get; }
            public bool DesiredTruth { get; }
            public object? ComparisonValue { get; }
        }

        private readonly struct ResolvedColumnValue
        {
            public ResolvedColumnValue(bool resolved, object? value)
            {
                Resolved = resolved;
                Value = value;
            }

            public bool Resolved { get; }
            public object? Value { get; }
        }

        private sealed class SelfReferencePlan
        {
            public SelfReferencePlan(int chainLength, int referenceDepth)
            {
                ChainLength = Math.Max(1, chainLength);
                ReferenceDepth = Math.Max(0, Math.Min(referenceDepth, ChainLength - 1));
            }

            public int ChainLength { get; }
            public int ReferenceDepth { get; }
        }

        private object? GenerateBoundaryValue(ColumnSchema col, ConditionInfo condition)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);

            // For boundary tests, use the exact boundary value
            return condition.Operator switch
            {
                ComparisonOp.GreaterThanOrEqual => generator.GenerateFromLiteral(condition.Value, col),
                ComparisonOp.LessThanOrEqual => generator.GenerateFromLiteral(condition.Value, col),
                ComparisonOp.GreaterThan => generator.GenerateSatisfying(col, ">", condition.Value),
                ComparisonOp.LessThan => generator.GenerateSatisfying(col, "<", condition.Value),
                ComparisonOp.Between => generator.GenerateFromLiteral(condition.Value, col), // Lower boundary
                _ => generator.GenerateFromLiteral(condition.Value, col)
            };
        }

        private object? GenerateBetweenValue(ColumnSchema col, string lower, string upper, bool inside)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);

            if (inside)
            {
                // Generate a value between lower and upper
                if (col.TypeCategory == DataTypeCategory.DateTime)
                {
                    if (DateTime.TryParse(lower, out var lo) && DateTime.TryParse(upper, out var hi))
                    {
                        var mid = lo.AddDays((hi - lo).TotalDays / 2);
                        return mid;
                    }
                }
                if (col.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal)
                {
                    if (decimal.TryParse(lower, out var lo) && decimal.TryParse(upper, out var hi))
                    {
                        return lo + (hi - lo) / 2;
                    }
                }
                return generator.GenerateFromLiteral(lower, col);
            }
            else
            {
                // Generate a value outside the range
                return generator.GenerateViolating(col, ">=", lower);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Subquery support data
        // ═════════════════════════════════════════════════════════════════

        private void GenerateSubquerySupportData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, ref int nextId, bool satisfy)
        {
            var tableRowIds = InitializeGeneratedIdMapFromScenario(scenario, schemas);
            var aliasToTableMap = new Dictionary<string, string>(query.AliasToTableMap, StringComparer.OrdinalIgnoreCase);

            foreach (var subquery in query.Subqueries)
            {
                if (!satisfy)
                    continue;

                var predicateTruth = ResolveSubqueryPredicateTruth(scenario, subquery, parentTruthMap: null);
                var shouldReturnRows = predicateTruth ^ IsNegativeSubqueryOperator(subquery.Operator);
                if (shouldReturnRows)
                {
                    GenerateSubqueryMatchData(
                        scenario,
                        subquery,
                        query,
                        schemas,
                        ref nextId,
                        tableRowIds,
                        aliasToTableMap);
                }
            }
        }

        private void GenerateSubqueryMatchData(
            BranchScenario scenario, SubqueryInfo subquery,
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            ref int nextId,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, string> aliasToTableMap)
        {
            var localAliasMap = ExtendAliasMap(aliasToTableMap, subquery.Tables);
            var directTableNames = subquery.Tables
                .Select(t => t.TableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> insertOrder;
            try
            {
                insertOrder = _orderResolver.ResolveInsertOrder(directTableNames, schemas);
            }
            catch
            {
                insertOrder = directTableNames.ToList();
            }

            foreach (var tableName in directTableNames)
            {
                if (!insertOrder.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                {
                    insertOrder.Add(tableName);
                }
            }

            foreach (var tableName in insertOrder)
            {
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                var subTable = subquery.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
                var tableAlias = subTable?.EffectiveName ?? tableName;
                var row = new GeneratedRow { TableName = tableName };
                int rowId = nextId++;

                foreach (var col in schema.Columns)
                {
                    if (col.IsComputed) continue;

                    var value = GenerateSubqueryColumnValue(
                        scenario,
                        query,
                        subquery,
                        schema,
                        tableAlias,
                        col,
                        tableRowIds,
                        localAliasMap,
                        rowId);
                    row.SetValue(col.ColumnName, value);
                }

                scenario.AddRow(tableName, row);
                RegisterGeneratedIds(tableRowIds, tableName, rowId, 1);
            }

            // Handle nested subqueries recursively
            foreach (var nested in subquery.NestedSubqueries)
            {
                GenerateSubqueryMatchData(
                    scenario,
                    nested,
                    query,
                    schemas,
                    ref nextId,
                    tableRowIds,
                    localAliasMap);
            }
        }

        private object? GenerateSubqueryColumnValue(
            BranchScenario scenario,
            ParsedQuery query,
            SubqueryInfo subquery,
            TableSchema schema,
            string tableAlias,
            ColumnSchema column,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, string> aliasToTableMap,
            int rowId)
        {
            var generator = _valueFactory.GetGenerator(column.TypeCategory);
            var internalTruthMap = BuildSubqueryInternalTruthMap(subquery, predicateTruth: true);

            if (column.IsIdentity || column.IsPrimaryKey)
                return rowId;

            var fk = schema.ForeignKeys
                .FirstOrDefault(f => f.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (fk != null)
            {
                if (TryResolveRelatedRowId(tableRowIds, fk.ReferencedTable, 0, out var fkId))
                    return fkId;

                if (column.IsNullable)
                    return null;
            }

            var conditionTargets = subquery.Conditions
                .Where(c => ConditionTargetsColumn(aliasToTableMap, c, schema.TableName, tableAlias, column.ColumnName))
                .Select(c =>
                {
                    object? comparisonValue = null;
                    if (c.IsColumnComparison)
                    {
                        TryResolveSubqueryComparisonValue(
                            scenario,
                            query,
                            c,
                            tableRowIds,
                            aliasToTableMap,
                            out comparisonValue);
                    }

                    var desiredTruth = internalTruthMap.TryGetValue(c.Key, out var mappedTruth)
                        ? mappedTruth
                        : true;
                    return new ColumnConditionTarget(c, desiredTruth, comparisonValue);
                })
                .ToList();

            var globalSubqueryTargets = FindApplicableSubqueryConditionTargets(
                scenario,
                query,
                schema.TableName,
                tableAlias,
                column.ColumnName);

            foreach (var extraTarget in globalSubqueryTargets)
            {
                if (conditionTargets.All(t => !t.Condition.Key.Equals(extraTarget.Condition.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    conditionTargets.Add(extraTarget);
                }
            }

            if (conditionTargets.Count > 0)
            {
                var resolved = ResolveColumnValueFromTargets(
                    scenario,
                    query,
                    column,
                    generator,
                    conditionTargets,
                    tableRowIds,
                    tableAlias,
                    0,
                    currentRow: null);

                if (resolved.Resolved)
                    return resolved.Value;
            }

            if (!string.IsNullOrWhiteSpace(subquery.SelectColumn) &&
                column.ColumnName.Equals(subquery.SelectColumn, StringComparison.OrdinalIgnoreCase) &&
                TryResolveScenarioValue(
                    scenario,
                    aliasToTableMap,
                    subquery.ParentTableAlias,
                    subquery.ParentColumnName,
                    out var parentValue))
            {
                return parentValue;
            }

            return generator.GenerateDefault(column);
        }
        private static Dictionary<string, List<int>> InitializeGeneratedIdMapFromScenario(
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in scenario.TableRows)
            {
                if (!schemas.TryGetValue(kvp.Key, out var schema))
                    continue;
                if (schema.PrimaryKey?.Columns.Count != 1)
                    continue;

                var pkColumn = schema.PrimaryKey.Columns[0];
                var ids = kvp.Value
                    .Select(r => r.GetValue(pkColumn))
                    .Where(v => v != null && int.TryParse(v!.ToString(), out _))
                    .Select(v => Convert.ToInt32(v))
                    .Distinct()
                    .ToList();

                if (ids.Any())
                {
                    map[kvp.Key] = ids;
                }
            }

            return map;
        }

        private static Dictionary<string, string> ExtendAliasMap(
            Dictionary<string, string> source,
            IEnumerable<TableInfo> tables)
        {
            var map = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                if (!string.IsNullOrWhiteSpace(table.Alias))
                    map[table.Alias] = table.TableName;
                map[table.TableName] = table.TableName;
            }

            return map;
        }

        private static HashSet<string> CollectGenerationScope(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas)
        {
            var scope = new HashSet<string>(
                query.Tables
                    .Select(t => t.TableName)
                    .Where(t => !string.IsNullOrWhiteSpace(t)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var subquery in query.Subqueries)
            {
                CollectSubqueryTables(subquery, scope);
            }

            var queue = new Queue<string>(scope);
            while (queue.Count > 0)
            {
                var tableName = queue.Dequeue();
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                foreach (var fk in schema.ForeignKeys)
                {
                    if (string.IsNullOrWhiteSpace(fk.ReferencedTable))
                        continue;

                    if (scope.Add(fk.ReferencedTable))
                    {
                        queue.Enqueue(fk.ReferencedTable);
                    }
                }
            }

            return scope;
        }

        private static void CollectSubqueryTables(
            SubqueryInfo subquery,
            HashSet<string> scope)
        {
            foreach (var table in subquery.Tables)
            {
                if (!string.IsNullOrWhiteSpace(table.TableName))
                {
                    scope.Add(table.TableName);
                }
            }

            foreach (var nested in subquery.NestedSubqueries)
            {
                CollectSubqueryTables(nested, scope);
            }
        }

        private static bool TryResolveSubqueryComparisonValue(
            BranchScenario scenario,
            ParsedQuery query,
            ConditionInfo condition,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, string> aliasToTableMap,
            out object? value)
        {
            value = null;

            if (string.IsNullOrWhiteSpace(condition.RightColumnName))
                return false;

            if (!string.IsNullOrWhiteSpace(condition.RightTableAlias))
            {
                var tableName = aliasToTableMap.TryGetValue(condition.RightTableAlias, out var resolvedTable)
                    ? resolvedTable
                    : query.ResolveAlias(condition.RightTableAlias);

                if (TryResolveScenarioValue(scenario, tableName, condition.RightColumnName, out value))
                    return true;

                if (TryResolveRelatedRowId(tableRowIds, tableName, 0, out var relatedId))
                {
                    value = relatedId;
                    return true;
                }
            }

            return TryResolveScenarioValue(scenario, null, condition.RightColumnName, out value);
        }

        private static bool TryResolveScenarioValue(
            BranchScenario scenario,
            Dictionary<string, string> aliasToTableMap,
            string? tableAlias,
            string? columnName,
            out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(columnName))
                return false;

            string? tableName = null;
            if (!string.IsNullOrWhiteSpace(tableAlias))
            {
                tableName = aliasToTableMap.TryGetValue(tableAlias, out var resolved)
                    ? resolved
                    : tableAlias;
            }

            return TryResolveScenarioValue(scenario, tableName, columnName, out value);
        }

        private static bool TryResolveScenarioValue(
            BranchScenario scenario,
            string? tableName,
            string columnName,
            out object? value)
        {
            value = null;

            if (!string.IsNullOrWhiteSpace(tableName) &&
                scenario.TableRows.TryGetValue(tableName, out var scopedRows))
            {
                foreach (var row in scopedRows)
                {
                    var rowValue = row.GetValue(columnName);
                    if (rowValue != null)
                    {
                        value = rowValue;
                        return true;
                    }
                }
            }

            foreach (var rows in scenario.TableRows.Values)
            {
                foreach (var row in rows)
                {
                    var rowValue = row.GetValue(columnName);
                    if (rowValue != null)
                    {
                        value = rowValue;
                        return true;
                    }
                }
            }

            return false;
        }

        // ═════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════

        private void ApplySemanticRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            ApplyQueryFunctionHintedStringRowAdjustments(query, schema, row, rowIndex);
            ApplyScalarAvgSubqueryRowAdjustments(query, schema, row, rowIndex);

            if (!NeedsAggregateDiversitySupport(query))
                return;

            if (IsInventoryLikeTable(schema.TableName, schema))
            {
                ApplyInventoryRowAdjustments(query, scenario, schema, row, rowIndex);
            }
            else if (IsReviewLikeTable(schema.TableName, schema))
            {
                ApplyReviewRowAdjustments(query, scenario, schema, row, rowIndex);
            }
            else if (IsProductCostLikeTable(schema.TableName, schema))
            {
                ApplyProductRowAdjustments(query, scenario, schema, row, rowIndex);
            }
            else if (IsLineLikeTable(schema.TableName, schema))
            {
                ApplyLineRowAdjustments(query, scenario, schema, row, rowIndex);
            }
            else if (IsPaymentLikeTable(schema.TableName, schema))
            {
                ApplyPaymentRowAdjustments(query, scenario, schema, row, rowIndex);
            }
        }

        private void ApplyQueryFunctionHintedStringRowAdjustments(
            ParsedQuery query,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            foreach (var column in schema.Columns.Where(c => c.TypeCategory == DataTypeCategory.String))
            {
                if (!TryCollectQueryFunctionStringHints(column, query, out var hints, out var needsAsciiInitial))
                    continue;

                var existing = Convert.ToString(row.GetValue(column.ColumnName), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (hints.All(h => existing.Contains(h, StringComparison.OrdinalIgnoreCase)) &&
                    (!needsAsciiInitial || (existing.Trim().Length > 0 && char.ToUpperInvariant(existing.Trim()[0]) is >= 'A' and <= 'Z')))
                {
                    continue;
                }

                var rowToken = (rowIndex + 1).ToString("D3");
                var targetLength = UseMaxLengthMaxValueMode
                    ? ResolveTargetStringLength(column)
                    : Math.Min(ResolveTargetStringLength(column), 96);
                var prefix = needsAsciiInitial &&
                             (existing.Trim().Length == 0 || char.ToUpperInvariant(existing.Trim()[0]) is < 'A' or > 'Z')
                    ? "A"
                    : string.Empty;
                var suffix = string.Concat(hints.Where(h => !existing.Contains(h, StringComparison.OrdinalIgnoreCase)));
                var candidate = $"{prefix}{existing}{suffix}Z";
                candidate = UseMaxLengthMaxValueMode
                    ? RepeatPhraseToExactLength(candidate, rowToken, targetLength)
                    : FitSemanticString(candidate, rowToken, targetLength);
                row.SetValue(column.ColumnName, SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate);
            }
        }

        private static void ApplyScalarAvgSubqueryRowAdjustments(
            ParsedQuery query,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var scalarAggregateSubquery = query.Subqueries.FirstOrDefault(s =>
                s.Operator == SubqueryOperator.ScalarComparison &&
                !string.IsNullOrWhiteSpace(s.SelectColumn) &&
                s.Tables.Any(t => t.TableName.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase)) &&
                s.SubquerySql.Contains("AVG", StringComparison.OrdinalIgnoreCase));
            if (scalarAggregateSubquery == null)
                return;

            var column = schema.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(scalarAggregateSubquery.SelectColumn, StringComparison.OrdinalIgnoreCase) &&
                c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float);
            if (column == null)
                return;

            var rawValue = rowIndex == 0 ? 20m : 1m;
            var normalized = SqlServerValueNormalizer.NormalizeValue(column, rawValue);
            if (normalized != null)
            {
                row.SetValue(column.ColumnName, normalized);
            }
        }

        private void ApplyLineRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var quantity = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityOrdered", rowIndex, 0, 2, 2 + rowIndex)
                : 2 + rowIndex;
            var unitPrice = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "UnitPrice", rowIndex, 0, 2, 11.25m + (rowIndex * 2.15m))
                : 11.25m + (rowIndex * 2.15m);
            var unitCost = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "UnitCost", rowIndex, 3, 2, Math.Max(0.01m, unitPrice - 1.35m))
                : unitPrice - 1.35m;
            var lineTotal = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "LineTotal", rowIndex, 7, 3, (quantity * unitPrice) + (rowIndex * 0.5m))
                : (quantity * unitPrice) + (rowIndex * 0.5m);

            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityOrdered", quantity);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "Quantity", quantity);
            var quantityShipped = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityShipped", rowIndex, 2, 2, Math.Max(1, quantity - 1))
                : Math.Max(1, quantity - 1);
            var quantityReturned = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityReturned", rowIndex, 4, 2, rowIndex % 2)
                : rowIndex % 2;
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityShipped", quantityShipped);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityReturned", quantityReturned);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitPrice", unitPrice);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitCost", unitCost);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "LineTotal", lineTotal);
            var discountPercent = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "DiscountPercent", rowIndex, 11, 1, 0.01m + (rowIndex * 0.01m))
                : 0.01m + (rowIndex * 0.01m);
            var taxRate = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "TaxRate", rowIndex, 13, 1, 0.05m + (rowIndex * 0.01m))
                : 0.05m + (rowIndex * 0.01m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "DiscountPercent", discountPercent);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "TaxRate", taxRate);
        }

        private void ApplyPaymentRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var amountPaid = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "AmountPaid", rowIndex, 0, 3, 35.00m + (rowIndex * 9.75m))
                : 35.00m + (rowIndex * 9.75m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "AmountPaid", amountPaid);
            var gatewayFee = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "GatewayFee", rowIndex, 4, 2, 0.50m + (rowIndex * 0.10m))
                : 0.50m + (rowIndex * 0.10m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "GatewayFee", gatewayFee);
        }

        private void ApplyInventoryRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var totalStock = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityOnHand", rowIndex, 0, 2, 90 - (rowIndex * 3))
                : 90 - (rowIndex * 3);
            var reorderLevel = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "ReorderLevel", rowIndex, 2, 2, Math.Max(1, totalStock / 3))
                : Math.Max(1, totalStock / 3);

            if (reorderLevel >= totalStock)
                reorderLevel = Math.Max(1, totalStock - 1);

            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityOnHand", totalStock);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "TotalStock", totalStock);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "ReorderLevel", reorderLevel);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "TotalReorderLevel", reorderLevel);
        }

        private void ApplyReviewRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var fallback = Math.Max(1m, 5m - (rowIndex * 0.5m));
            var ratingValue = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "RatingValue", rowIndex, 0, 1, fallback)
                : fallback;
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "RatingValue", ratingValue);
        }

        private void ApplyProductRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var costPrice = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "CostPrice", rowIndex, 0, 1, 8.50m + (rowIndex * 1.10m))
                : 8.50m + (rowIndex * 1.10m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "CostPrice", costPrice);
        }

        private int BuildSafeHighInteger(
            ParsedQuery query,
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            int maxDigits,
            int fallback)
        {
            var raw = BuildAdjustedMaxInteger(query, schema, columnName, rowIndex, extraOffset, fallback);
            var safeCap = (int)Math.Min(raw, Math.Pow(10, Math.Max(1, maxDigits)) - 1);
            var candidate = safeCap - rowIndex;
            return candidate > 0 ? candidate : Math.Max(1, fallback);
        }

        private decimal BuildSafeHighDecimal(
            ParsedQuery query,
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            int maxIntegerDigits,
            decimal fallback)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return fallback;

            var queryAwareMax = BuildAdjustedMaxDecimal(query, schema, columnName, rowIndex, extraOffset, fallback);
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var integerDigits = column.NumericPrecision.HasValue
                ? Math.Max(1, column.NumericPrecision.Value - scale)
                : Math.Max(1, maxIntegerDigits);
            var cappedIntegerDigits = Math.Min(integerDigits, Math.Max(1, maxIntegerDigits));

            decimal wholePart = 1m;
            for (int i = 0; i < cappedIntegerDigits; i++)
            {
                wholePart *= 10m;
            }

            var step = GetNumericStep(column);
            var safeMax = wholePart - step;
            safeMax = Math.Min(safeMax, queryAwareMax);
            var candidate = safeMax - ((rowIndex + extraOffset) * step);
            if (candidate <= 0m)
                candidate = step;

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate);
            return normalized is decimal d ? d : fallback;
        }

        private int BuildAdjustedMaxInteger(
            ParsedQuery query,
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            int fallback)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return fallback;

            var raw = BuildMaxNumericValue(column, rowIndex + extraOffset, query, schema.TableName);
            try
            {
                return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private decimal BuildAdjustedMaxDecimal(
            ParsedQuery query,
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            decimal fallback)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return fallback;

            var raw = BuildMaxNumericValue(column, rowIndex + extraOffset, query, schema.TableName);
            try
            {
                return Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private void SetNormalizedRowValueIfSafe(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            string columnName,
            object rawValue)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return;

            if ((!UseMaxLengthMaxValueMode && IsPredicatePinnedColumn(query, scenario, schema.TableName, columnName)) ||
                (UseMaxLengthMaxValueMode && HasExactOrUpperBoundPinnedPredicate(query, scenario, schema.TableName, columnName)))
            {
                return;
            }

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, rawValue);
            if (normalized != null)
            {
                row.SetValue(columnName, normalized);
            }
        }

        private int DetermineScenarioRowCount(
            string tableName,
            string tableAlias,
            TableSchema schema,
            ParsedQuery query,
            bool isAggregateSource,
            int rowMultiplier,
            int requestedRows)
        {
            var rowCount = isAggregateSource
                ? Math.Max(rowMultiplier, requestedRows)
                : requestedRows;

            rowCount = Math.Max(rowCount, GetPairPatternMinimumRows(tableName, schema, query));

            if (!NeedsAggregateDiversitySupport(query))
                return rowCount;

            if (requestedRows == 1 && IsResultCardinalityAnchorTable(tableName, tableAlias, query))
                return rowCount;

            var diversityFloor = GetAggregateDiversityMinimumRows(tableName, schema);
            return Math.Max(rowCount, diversityFloor);
        }

        private bool IsResultCardinalityAnchorTable(string tableName, string tableAlias, ParsedQuery query)
        {
            bool MatchesTable(string aliasOrName)
            {
                if (string.IsNullOrWhiteSpace(aliasOrName))
                    return false;

                var resolved = query.ResolveAlias(aliasOrName);
                return resolved.Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                       aliasOrName.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) ||
                       aliasOrName.Equals(tableName, StringComparison.OrdinalIgnoreCase);
            }

            if (query.GroupByColumns.Any(g => MatchesTable(g.TableAlias)))
                return true;

            if (query.SelectColumns.Any(s =>
                    !s.IsAggregate &&
                    !string.IsNullOrWhiteSpace(s.ColumnName) &&
                    MatchesTable(s.TableAlias)))
            {
                return true;
            }

            return query.HasDistinct &&
                   query.SelectColumns.Any(s =>
                       !s.IsAggregate &&
                       !string.IsNullOrWhiteSpace(s.ColumnName) &&
                       MatchesTable(s.TableAlias));
        }

        private int GetPairPatternMinimumRows(string tableName, TableSchema schema, ParsedQuery query)
        {
            if (!TryGetLineSelfJoinPairPattern(schema, query, out var pairPattern))
                return 1;

            if (tableName.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                return pairPattern.DistinctProductCount * pairPattern.DistinctOrderCount;

            if (tableName.Equals(pairPattern.OrderTableName, StringComparison.OrdinalIgnoreCase))
                return pairPattern.DistinctOrderCount;

            if (tableName.Equals(pairPattern.ProductTableName, StringComparison.OrdinalIgnoreCase))
                return pairPattern.DistinctProductCount;

            return 1;
        }

        private Dictionary<string, int> BuildSpecialMinimumRowRequirements(
            Dictionary<string, TableSchema> schemas,
            ParsedQuery query)
        {
            var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var schema in schemas.Values)
            {
                if (!TryGetLineSelfJoinPairPattern(schema, query, out var pairPattern))
                {
                    if (query.Subqueries.Any(s =>
                        s.Operator == SubqueryOperator.ScalarComparison &&
                        !string.IsNullOrWhiteSpace(s.SelectColumn) &&
                        s.Tables.Any(t => t.TableName.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase)) &&
                        s.SubquerySql.Contains("AVG", StringComparison.OrdinalIgnoreCase)))
                    {
                        requirements[schema.TableName] = Math.Max(
                            requirements.GetValueOrDefault(schema.TableName),
                            2);
                    }

                    continue;
                }

                requirements[schema.TableName] = Math.Max(
                    requirements.GetValueOrDefault(schema.TableName),
                    pairPattern.DistinctProductCount * pairPattern.DistinctOrderCount);

                requirements[pairPattern.OrderTableName] = Math.Max(
                    requirements.GetValueOrDefault(pairPattern.OrderTableName),
                    pairPattern.DistinctOrderCount);

                requirements[pairPattern.ProductTableName] = Math.Max(
                    requirements.GetValueOrDefault(pairPattern.ProductTableName),
                    pairPattern.DistinctProductCount);
            }

            return requirements;
        }

        private int DetermineRowMultiplier(ParsedQuery query)
        {
            int multiplier = 1;

            foreach (var having in query.HavingConditions)
            {
                if (having.AggregateFunc == AggregateFunction.Count &&
                    TryDetermineRequiredCountRows(having, out var requiredRows))
                {
                    // COUNT(x) >= N → need N rows
                    multiplier = Math.Max(multiplier, requiredRows);
                }
            }

            return multiplier;
        }

        private bool NeedsAggregateDiversitySupport(ParsedQuery query)
        {
            if (!query.GroupByColumns.Any() || !query.Aggregates.Any())
                return false;

            var aggregateKinds = query.Aggregates
                .Select(a => a.Function)
                .Distinct()
                .Count();

            var hasMixedCountAndValueAggregates =
                query.Aggregates.Any(a => a.Function is AggregateFunction.Count or AggregateFunction.CountDistinct) &&
                query.Aggregates.Any(a => a.Function is AggregateFunction.Sum or AggregateFunction.Avg);

            var hasArithmeticProjection = query.SelectColumns.Any(c =>
                !string.IsNullOrWhiteSpace(c.Expression) &&
                c.Expression.IndexOfAny(new[] { '/', '*', '+', '-' }) >= 0);

            return aggregateKinds >= 2 || hasMixedCountAndValueAggregates || hasArithmeticProjection;
        }

        private static int GetAggregateDiversityMinimumRows(string tableName, TableSchema schema)
        {
            if (IsLineLikeTable(tableName, schema))
                return 3;

            if (IsOrderLikeTable(tableName, schema) || IsPaymentLikeTable(tableName, schema))
                return 2;

            return 1;
        }

        private static bool IsLineLikeTable(string tableName, TableSchema schema)
        {
            if (IsInventoryLikeTable(tableName, schema) || IsReviewLikeTable(tableName, schema))
                return false;

            return tableName.Contains("Line", StringComparison.OrdinalIgnoreCase) ||
                   tableName.Contains("Detail", StringComparison.OrdinalIgnoreCase) ||
                   tableName.Contains("Item", StringComparison.OrdinalIgnoreCase) ||
                   schema.Columns.Any(c =>
                       c.ColumnName.Contains("Quantity", StringComparison.OrdinalIgnoreCase) ||
                       c.ColumnName.Contains("UnitPrice", StringComparison.OrdinalIgnoreCase) ||
                       c.ColumnName.Contains("LineTotal", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOrderLikeTable(string tableName, TableSchema schema)
        {
            return tableName.Contains("Order", StringComparison.OrdinalIgnoreCase) &&
                   !IsLineLikeTable(tableName, schema);
        }

        private static bool IsPaymentLikeTable(string tableName, TableSchema schema)
        {
            return tableName.Contains("Payment", StringComparison.OrdinalIgnoreCase) ||
                   schema.Columns.Any(c =>
                       c.ColumnName.Contains("AmountPaid", StringComparison.OrdinalIgnoreCase) ||
                       c.ColumnName.Contains("PaymentDate", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsInventoryLikeTable(string tableName, TableSchema schema)
        {
            return tableName.Contains("Inventory", StringComparison.OrdinalIgnoreCase) ||
                   schema.Columns.Any(c =>
                       c.ColumnName.Contains("QuantityOnHand", StringComparison.OrdinalIgnoreCase) ||
                       c.ColumnName.Contains("ReorderLevel", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsReviewLikeTable(string tableName, TableSchema schema)
        {
            return tableName.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
                   schema.Columns.Any(c =>
                       c.ColumnName.Contains("RatingValue", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProductCostLikeTable(string tableName, TableSchema schema)
        {
            return tableName.Contains("Product", StringComparison.OrdinalIgnoreCase) &&
                   schema.Columns.Any(c =>
                       c.ColumnName.Contains("CostPrice", StringComparison.OrdinalIgnoreCase));
        }

        private bool TryGetLineSelfJoinPairPattern(TableSchema schema, ParsedQuery query, out LineSelfJoinPairPattern pairPattern)
        {
            pairPattern = default;

            if (!IsLineLikeTable(schema.TableName, schema))
                return false;

            var aliases = query.Tables
                .Where(t => query.ResolveAlias(t.TableName).Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                .Select(t => string.IsNullOrWhiteSpace(t.Alias) ? t.TableName : t.Alias)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count < 2)
                return false;

            var hasSelfJoin = query.Joins.Any(j =>
                query.ResolveAlias(j.LeftTableAlias).Equals(schema.TableName, StringComparison.OrdinalIgnoreCase) &&
                query.ResolveAlias(j.RightTableAlias).Equals(schema.TableName, StringComparison.OrdinalIgnoreCase) &&
                !j.LeftTableAlias.Equals(j.RightTableAlias, StringComparison.OrdinalIgnoreCase));

            if (!hasSelfJoin)
                return false;

            var orderFk = schema.ForeignKeys.FirstOrDefault(f =>
                f.ReferencedTable.Contains("Order", StringComparison.OrdinalIgnoreCase));
            var productFk = schema.ForeignKeys.FirstOrDefault(f =>
                f.ReferencedTable.Contains("Product", StringComparison.OrdinalIgnoreCase));

            if (orderFk == null || productFk == null)
                return false;

            pairPattern = new LineSelfJoinPairPattern(
                schema.TableName,
                orderFk.ReferencedTable,
                productFk.ReferencedTable,
                DistinctOrderCount: 2,
                DistinctProductCount: Math.Max(2, aliases.Count));
            return true;
        }

        private readonly record struct LineSelfJoinPairPattern(
            string LineTableName,
            string OrderTableName,
            string ProductTableName,
            int DistinctOrderCount,
            int DistinctProductCount);

        private static bool TryDetermineRequiredCountRows(ConditionInfo condition, out int requiredRows)
        {
            requiredRows = 1;

            if (condition.Operator == ComparisonOp.Between)
            {
                if (int.TryParse(condition.Value, out var lowerBound))
                {
                    requiredRows = Math.Max(1, lowerBound);
                    return true;
                }

                return false;
            }

            if (!int.TryParse(condition.Value, out var n))
                return false;

            requiredRows = condition.Operator switch
            {
                ComparisonOp.GreaterThan => Math.Max(1, n + 1),
                ComparisonOp.GreaterThanOrEqual => Math.Max(1, n),
                ComparisonOp.Equal => Math.Max(1, n),
                ComparisonOp.NotEqual => n == 1 ? 2 : 1,
                ComparisonOp.LessThan => 1,
                ComparisonOp.LessThanOrEqual => 1,
                _ => Math.Max(1, n)
            };

            return true;
        }

        private static bool TryDetermineViolatingCountRows(ConditionInfo condition, out int violatingRows)
        {
            violatingRows = 0;

            if (condition.Operator == ComparisonOp.Between)
            {
                if (int.TryParse(condition.Value, out var lowerBound))
                {
                    violatingRows = Math.Max(0, lowerBound - 1);
                    return true;
                }

                return false;
            }

            if (!int.TryParse(condition.Value, out var n))
                return false;

            violatingRows = condition.Operator switch
            {
                ComparisonOp.GreaterThan => Math.Max(0, n),
                ComparisonOp.GreaterThanOrEqual => Math.Max(0, n - 1),
                ComparisonOp.Equal => Math.Max(0, n - 1),
                ComparisonOp.NotEqual => Math.Max(0, n),
                ComparisonOp.LessThan => Math.Max(0, n),
                ComparisonOp.LessThanOrEqual => Math.Max(0, n + 1),
                _ => Math.Max(0, n - 1)
            };

            return true;
        }

        private Dictionary<string, int> BuildCountNegativeRowOverrides(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            List<ConditionInfo> testedConditions)
        {
            var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var condition in testedConditions.Where(c => c.AggregateFunc is AggregateFunction.Count or AggregateFunction.CountDistinct))
            {
                var targetTable = query.ResolveAlias(condition.TableAlias);
                if (string.IsNullOrWhiteSpace(targetTable))
                    continue;

                if (!TryDetermineViolatingCountRows(condition, out var violatingRows))
                    continue;

                overrides[targetTable] = violatingRows;

                if (violatingRows == 0)
                {
                    foreach (var descendant in CollectDescendantTables(targetTable, schemas))
                    {
                        overrides[descendant] = 0;
                    }
                }
            }

            return overrides;
        }

        private static HashSet<string> CollectDescendantTables(
            string rootTable,
            Dictionary<string, TableSchema> schemas)
        {
            var descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(rootTable);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var schema in schemas.Values)
                {
                    if (!schema.ForeignKeys.Any(fk => fk.ReferencedTable.Equals(current, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (descendants.Add(schema.TableName))
                    {
                        queue.Enqueue(schema.TableName);
                    }
                }
            }

            descendants.Remove(rootTable);
            return descendants;
        }

        private static int ApplySelfReferenceMinimumRowCount(
            string tableName,
            int rowCount,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            return selfReferencePlans.TryGetValue(tableName, out var plan)
                ? Math.Max(rowCount, plan.ChainLength)
                : rowCount;
        }

        private static Dictionary<string, SelfReferencePlan> BuildSelfReferencePlans(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas)
        {
            var plans = new Dictionary<string, SelfReferencePlan>(StringComparer.OrdinalIgnoreCase);

            foreach (var schema in schemas.Values)
            {
                var selfReferenceColumns = schema.ForeignKeys
                    .Where(fk => fk.ReferencedTable.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                    .Select(fk => fk.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (selfReferenceColumns.Count == 0)
                    continue;

                var aliases = query.Tables
                    .Where(t => t.TableName.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                    .Select(GetEffectiveTableAlias)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (aliases.Count <= 1)
                    continue;

                var pkColumns = (schema.PrimaryKey?.Columns ?? new List<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var childToParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var join in query.Joins)
                {
                    var leftTable = query.ResolveAlias(join.LeftTableAlias);
                    var rightTable = query.ResolveAlias(join.RightTableAlias);

                    if (!leftTable.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase) ||
                        !rightTable.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pkColumns.Contains(join.LeftColumn) &&
                        selfReferenceColumns.Contains(join.RightColumn))
                    {
                        childToParent[NormalizeAlias(join.RightTableAlias, schema.TableName)] =
                            NormalizeAlias(join.LeftTableAlias, schema.TableName);
                    }
                    else if (pkColumns.Contains(join.RightColumn) &&
                             selfReferenceColumns.Contains(join.LeftColumn))
                    {
                        childToParent[NormalizeAlias(join.LeftTableAlias, schema.TableName)] =
                            NormalizeAlias(join.RightTableAlias, schema.TableName);
                    }
                }

                int maxDepth = 0;
                foreach (var alias in aliases)
                {
                    maxDepth = Math.Max(
                        maxDepth,
                        ComputeSelfReferenceDepth(alias, childToParent, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                }

                var chainLength = Math.Max(maxDepth + 1, aliases.Count);
                var referenceDepth = maxDepth > 0 ? maxDepth : chainLength - 1;
                if (chainLength > 1)
                {
                    plans[schema.TableName] = new SelfReferencePlan(chainLength, referenceDepth);
                }
            }

            return plans;
        }

        private static int ComputeSelfReferenceDepth(
            string alias,
            Dictionary<string, string> childToParent,
            HashSet<string> visiting)
        {
            if (!childToParent.TryGetValue(alias, out var parentAlias))
                return 0;

            if (!visiting.Add(alias))
                return 0;

            var depth = 1 + ComputeSelfReferenceDepth(parentAlias, childToParent, visiting);
            visiting.Remove(alias);
            return depth;
        }

        private static string GetEffectiveTableAlias(TableInfo table) =>
            NormalizeAlias(table.Alias, table.TableName);

        private static string NormalizeAlias(string? alias, string tableName) =>
            string.IsNullOrWhiteSpace(alias) ? tableName : alias;

        private static bool TryGenerateSelfReferenceValue(
            TableSchema currentTableSchema,
            ColumnSchema column,
            int rowId,
            int rowIndex,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            out object? value)
        {
            value = null;

            if (!selfReferencePlans.TryGetValue(currentTableSchema.TableName, out var plan) ||
                plan.ChainLength <= 1)
            {
                return false;
            }

            var depth = Math.Abs(rowIndex) % plan.ChainLength;
            if (depth == 0)
            {
                if (column.IsNullable)
                {
                    value = null;
                    return true;
                }

                if (currentTableSchema.PrimaryKey?.Columns.Count == 1)
                {
                    value = rowId;
                    return true;
                }

                return false;
            }

            value = rowId - 1;
            return true;
        }

        private static void RegisterGeneratedIds(
            Dictionary<string, List<int>> tableRowIds,
            string tableName,
            int startId,
            int rowCount)
        {
            tableRowIds[tableName] = Enumerable
                .Range(startId, Math.Max(0, rowCount))
                .ToList();
        }

        private static void RegisterReferenceableIds(
            Dictionary<string, List<int>> tableRowIds,
            string tableName,
            int startId,
            int rowCount,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var generatedIds = Enumerable
                .Range(startId, Math.Max(0, rowCount))
                .ToList();

            if (!selfReferencePlans.TryGetValue(tableName, out var plan) ||
                plan.ChainLength <= 1)
            {
                tableRowIds[tableName] = generatedIds;
                return;
            }

            var referenceableIds = generatedIds
                .Where((_, index) => (index % plan.ChainLength) == plan.ReferenceDepth)
                .ToList();

            if (referenceableIds.Count == 0)
            {
                referenceableIds = generatedIds
                    .Where((_, index) => (index % plan.ChainLength) == (plan.ChainLength - 1))
                    .ToList();
            }

            tableRowIds[tableName] = referenceableIds.Count > 0
                ? referenceableIds
                : generatedIds;
        }

        private static bool TryResolveRelatedRowId(
            Dictionary<string, List<int>> tableRowIds,
            string tableName,
            int rowIndex,
            out int resolvedId)
        {
            resolvedId = default;
            if (!tableRowIds.TryGetValue(tableName, out var ids) || ids.Count == 0)
                return false;

            var safeIndex = Math.Abs(rowIndex) % ids.Count;
            resolvedId = ids[safeIndex];
            return true;
        }

        private bool TryResolvePairPatternRelatedRowId(
            TableSchema currentTableSchema,
            ForeignKeyInfo fk,
            int rowIndex,
            ParsedQuery query,
            Dictionary<string, List<int>> referenceableTableIds,
            Dictionary<string, List<int>> tableRowIds,
            out int resolvedId)
        {
            resolvedId = default;

            if (!TryGetLineSelfJoinPairPattern(currentTableSchema, query, out var pairPattern))
                return false;

            int targetIndex;
            if (fk.ReferencedTable.Equals(pairPattern.OrderTableName, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = Math.Abs(rowIndex) / pairPattern.DistinctProductCount;
            }
            else if (fk.ReferencedTable.Equals(pairPattern.ProductTableName, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = Math.Abs(rowIndex) % pairPattern.DistinctProductCount;
            }
            else
            {
                return false;
            }

            return TryResolveRelatedRowIdAtIndex(referenceableTableIds, fk.ReferencedTable, targetIndex, out resolvedId) ||
                   TryResolveRelatedRowIdAtIndex(tableRowIds, fk.ReferencedTable, targetIndex, out resolvedId);
        }

        private static bool TryResolveRelatedRowIdAtIndex(
            Dictionary<string, List<int>> tableRowIds,
            string tableName,
            int requestedIndex,
            out int resolvedId)
        {
            resolvedId = default;
            if (!tableRowIds.TryGetValue(tableName, out var ids) || ids.Count == 0)
                return false;

            var safeIndex = Math.Abs(requestedIndex) % ids.Count;
            resolvedId = ids[safeIndex];
            return true;
        }

        private static void EnforceScenarioForeignKeyClosure(
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            var orderedTables = scenario.InsertOrder
                .Concat(scenario.TableRows.Keys.Where(t => !scenario.InsertOrder.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var tableName in orderedTables)
            {
                if (!scenario.TableRows.TryGetValue(tableName, out var rows) ||
                    !schemas.TryGetValue(tableName, out var schema) ||
                    rows.Count == 0)
                {
                    continue;
                }

                foreach (var (row, rowIndex) in rows.Select((r, i) => (r, i)))
                {
                    foreach (var fk in schema.ForeignKeys)
                    {
                        if (!scenario.TableRows.TryGetValue(fk.ReferencedTable, out var referencedRows) ||
                            referencedRows.Count == 0)
                        {
                            continue;
                        }

                        var currentValue = row.GetValue(fk.ColumnName);
                        if (LocalReferencedValueExists(referencedRows, fk.ReferencedColumn, currentValue))
                        {
                            continue;
                        }

                        var alignedValue = ResolveAlignedReferencedValue(referencedRows, fk.ReferencedColumn, rowIndex);
                        if (alignedValue == null || alignedValue == DBNull.Value)
                        {
                            continue;
                        }

                        row.SetValue(fk.ColumnName, alignedValue);
                    }
                }
            }
        }

        private static void ValidateScenarioLocalForeignKeys(
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            foreach (var (tableName, rows) in scenario.TableRows)
            {
                if (!schemas.TryGetValue(tableName, out var schema))
                {
                    continue;
                }

                foreach (var row in rows)
                {
                    foreach (var fk in schema.ForeignKeys)
                    {
                        if (!scenario.TableRows.TryGetValue(fk.ReferencedTable, out var referencedRows) ||
                            referencedRows.Count == 0)
                        {
                            continue;
                        }

                        var fkValue = row.GetValue(fk.ColumnName);
                        if (fkValue == null || fkValue == DBNull.Value)
                        {
                            continue;
                        }

                        if (LocalReferencedValueExists(referencedRows, fk.ReferencedColumn, fkValue))
                        {
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Generated scenario contains an unresolved local FK [{tableName}.{fk.ColumnName}] -> " +
                            $"[{fk.ReferencedTable}.{fk.ReferencedColumn}] with value [{fkValue}].");
                    }
                }
            }
        }

        private static bool LocalReferencedValueExists(
            List<GeneratedRow> referencedRows,
            string referencedColumn,
            object? candidateValue)
        {
            if (candidateValue == null || candidateValue == DBNull.Value)
            {
                return false;
            }

            return referencedRows.Any(r => ValuesEqual(r.GetValue(referencedColumn), candidateValue));
        }

        private static object? ResolveAlignedReferencedValue(
            List<GeneratedRow> referencedRows,
            string referencedColumn,
            int rowIndex)
        {
            if (referencedRows.Count == 0)
            {
                return null;
            }

            var safeIndex = Math.Abs(rowIndex) % referencedRows.Count;
            var aligned = referencedRows[safeIndex].GetValue(referencedColumn);
            if (aligned != null && aligned != DBNull.Value)
            {
                return aligned;
            }

            return referencedRows
                .Select(r => r.GetValue(referencedColumn))
                .FirstOrDefault(v => v != null && v != DBNull.Value);
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            if (left == null || left == DBNull.Value || right == null || right == DBNull.Value)
            {
                return false;
            }

            if (left is byte[] leftBytes && right is byte[] rightBytes)
            {
                return leftBytes.SequenceEqual(rightBytes);
            }

            return string.Equals(
                Convert.ToString(left, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(right, System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        private static int GetOrCreateReferencedRowId(
            Dictionary<string, List<int>> referencedTableIdPools,
            string tableName,
            int rowIndex,
            int seedValue)
        {
            if (!referencedTableIdPools.TryGetValue(tableName, out var ids))
            {
                ids = new List<int>();
                referencedTableIdPools[tableName] = ids;
            }

            while (ids.Count <= rowIndex)
            {
                ids.Add(seedValue + ids.Count);
            }

            return ids[rowIndex];
        }

        private bool IsAggregateSourceTable(string tableName, string alias, ParsedQuery query)
        {
            // A table is an aggregate source if it's referenced in GROUP BY aggregates
            // or if it contributes to ORDER BY... typically the detail/child tables
            foreach (var agg in query.Aggregates)
            {
                if (agg.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Check expression-based aggregates
                if (!string.IsNullOrEmpty(agg.Expression) &&
                    agg.Expression.Contains(alias + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool IsPartOfAggregateExpression(string columnName, string alias, ParsedQuery query)
        {
            foreach (var agg in query.Aggregates)
            {
                if (agg.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                    agg.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(agg.Expression) &&
                    agg.Expression.Contains(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private Dictionary<string, TableSchema> InferSchemasFromQuery(ParsedQuery query)
        {
            // Create minimal schemas based on what we can infer from the SQL
            var schemas = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in query.Tables)
            {
                var schema = new TableSchema { TableName = table.TableName };
                var alias = table.EffectiveName;
                var addedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Add columns from SELECT
                foreach (var sel in query.SelectColumns)
                {
                    if ((sel.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrEmpty(sel.TableAlias)) &&
                        !string.IsNullOrEmpty(sel.ColumnName) && sel.ColumnName != "*")
                    {
                        if (addedColumns.Add(sel.ColumnName))
                        {
                            schema.Columns.Add(new ColumnSchema
                            {
                                TableName = schema.TableName,
                                SchemaName = schema.SchemaName,
                                ColumnName = sel.ColumnName,
                                DataType = "varchar",
                                MaxLength = 100,
                                IsNullable = true
                            });
                        }
                    }
                }

                // Add columns from conditions
                foreach (var cond in query.WhereConditions.Concat(query.HavingConditions))
                {
                    if (cond.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(cond.ColumnName))
                    {
                        if (addedColumns.Add(cond.ColumnName))
                        {
                            schema.Columns.Add(new ColumnSchema
                            {
                                TableName = schema.TableName,
                                SchemaName = schema.SchemaName,
                                ColumnName = cond.ColumnName,
                                DataType = InferDataType(cond),
                                IsNullable = cond.Operator is ComparisonOp.IsNull or ComparisonOp.IsNotNull
                            });
                        }
                    }
                }

                // Add columns from JOINs
                foreach (var join in query.Joins)
                {
                    var col = join.LeftTableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) ? join.LeftColumn :
                              join.RightTableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) ? join.RightColumn : null;

                    if (col != null && addedColumns.Add(col))
                    {
                        schema.Columns.Add(new ColumnSchema
                        {
                            TableName = schema.TableName,
                            SchemaName = schema.SchemaName,
                            ColumnName = col,
                            DataType = "int",
                            IsPrimaryKey = join.LeftTableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }

                schemas[table.TableName] = schema;
            }

            return schemas;
        }

        private string InferDataType(ConditionInfo condition)
        {
            if (string.IsNullOrEmpty(condition.Value)) return "varchar";

            // Try to detect type from value
            if (int.TryParse(condition.Value, out _)) return "int";
            if (decimal.TryParse(condition.Value, out _)) return "decimal";
            if (DateTime.TryParse(condition.Value, out _)) return "datetime";
            if (condition.Value is "0" or "1" &&
                condition.Operator == ComparisonOp.Equal) return "bit";

            return "varchar";
        }

        private string ConditionOpToString(ComparisonOp op) => op switch
        {
            ComparisonOp.Equal => "=",
            ComparisonOp.NotEqual => "<>",
            ComparisonOp.GreaterThan => ">",
            ComparisonOp.GreaterThanOrEqual => ">=",
            ComparisonOp.LessThan => "<",
            ComparisonOp.LessThanOrEqual => "<=",
            ComparisonOp.Like => "LIKE",
            _ => "="
        };

        private static BranchScenario CloneScenarioDescriptor(BranchScenario source)
        {
            return new BranchScenario
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                Type = source.Type,
                ExpectedToReturnRows = source.ExpectedToReturnRows,
                TestedCondition = source.TestedCondition,
                ScopeLabel = source.ScopeLabel,
                BoundaryConditionKey = source.BoundaryConditionKey,
                JoinKey = source.JoinKey,
                PredicateTruthMap = new Dictionary<string, bool>(source.PredicateTruthMap, StringComparer.OrdinalIgnoreCase),
                TestedConditions = new List<string>(source.TestedConditions)
            };
        }
    }
}
