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
        private int _fallbackSeedCounter = 90000;

        /// <summary>
        /// Fallback starting seed for generated data when database-backed per-table seeds are unavailable.
        /// </summary>
        public int StartId { get; set; } = 90000;

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

            var nextTableIds = InitializeNextTableIds(tableNames);

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

        private Dictionary<string, int> InitializeNextTableIds(IEnumerable<string> tableNames)
        {
            var nextIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fallbackSeed = _fallbackSeedCounter;

            foreach (var tableName in tableNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TableSeedStarts != null &&
                    TableSeedStarts.TryGetValue(tableName, out var resolvedSeed) &&
                    resolvedSeed > 0)
                {
                    nextIds[tableName] = resolvedSeed;
                    continue;
                }

                nextIds[tableName] = fallbackSeed;
                fallbackSeed += 1000;
            }

            _fallbackSeedCounter = fallbackSeed;
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
                nextId = _fallbackSeedCounter;
                nextTableIds[tableName] = nextId;
                _fallbackSeedCounter += 1000;
            }

            nextTableIds[tableName] = nextId + rowCount + 1;
            return nextId;
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
                    schema,
                    query,
                    isAggregateSource,
                    rowMultiplier,
                    GetRequestedRowCount());
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
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
                            value = 99999; // Non-existent foreign key
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
                            rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
                    schema,
                    query,
                    isAggSource,
                    rowMultiplier,
                    GetRequestedRowCount());
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
                                rowId, satisfy: true, rowIdx, includeSubqueryConditions: true);
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
            bool includeSubqueryConditions)
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

                if (fk != null && TryResolveRelatedRowId(referenceableTableIds, fk.ReferencedTable, rowIndex, out var fkId))
                {
                    return fkId;
                }

                if (fk != null && TryResolveRelatedRowId(tableRowIds, fk.ReferencedTable, rowIndex, out fkId))
                {
                    return fkId;
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
            if (col.IsPrimaryKey)
            {
                return rowId;
            }

            // 3. Resolve all direct column predicates together (instead of first match only).
            var conditionTargets = GetApplicableConditionTargets(
                scenario,
                query,
                query.EnumerateScopeConditions(ConditionSource.Where),
                currentTableName,
                tableAlias,
                col.ColumnName,
                excludeHasSubquery: true);

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
                    tableRowIds);

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
                // For join columns, use the shared ID from the other table
                var otherAlias = joinCondition.LeftTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)
                    ? joinCondition.RightTableAlias
                    : joinCondition.LeftTableAlias;

                var otherTable = query.ResolveAlias(otherAlias);
                if (TryResolveRelatedRowId(referenceableTableIds, otherTable, rowIndex, out var joinId) ||
                    TryResolveRelatedRowId(tableRowIds, otherTable, rowIndex, out joinId))
                {
                    return joinId;
                }
            }

            // 5. Default value generation
            return GenerateDefaultColumnValue(col, generator, rowIndex);
        }

        private object GenerateDefaultColumnValue(
            ColumnSchema col,
            IValueGenerator generator,
            int rowIndex)
        {
            if (UseMaxLengthMaxValueMode)
            {
                var maxModeValue = GenerateMaxLengthMaxValue(col, rowIndex);
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

        private bool TryGetSampleValue(ColumnSchema column, out object? sampleValue)
        {
            sampleValue = null;
            if (SampleRowsByTable == null)
                return false;

            if (!SampleRowsByTable.TryGetValue(column.TableName, out var row))
                return false;

            return row.TryGetValue(column.ColumnName, out sampleValue);
        }

        private object? GenerateMaxLengthMaxValue(ColumnSchema column, int rowIndex)
        {
            return column.TypeCategory switch
            {
                DataTypeCategory.String => BuildMaxLengthString(column, rowIndex),
                DataTypeCategory.Integer => BuildMaxNumericValue(column, rowIndex),
                DataTypeCategory.Decimal => BuildMaxNumericValue(column, rowIndex),
                DataTypeCategory.Float => BuildMaxNumericValue(column, rowIndex),
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
            object? comparisonValue = null)
        {
            if (condition.IsColumnComparison && comparisonValue != null)
            {
                if (satisfy)
                {
                    return comparisonValue;
                }

                return generator.GenerateViolating(
                    col,
                    ConditionOpToString(condition.Operator),
                    comparisonValue?.ToString() ?? string.Empty);
            }

            var opStr = ConditionOpToString(condition.Operator);

            if (condition.Operator == ComparisonOp.Between)
            {
                return GenerateBetweenValue(col, condition.Value, condition.SecondValue, inside: satisfy);
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

            if (condition.Operator == ComparisonOp.Like)
            {
                return satisfy
                    ? generator.GenerateSatisfying(col, "LIKE", condition.LikePattern)
                    : generator.GenerateViolating(col, "LIKE", condition.LikePattern);
            }
            return satisfy
                ? generator.GenerateSatisfying(col, opStr, condition.Value)
                : generator.GenerateViolating(col, opStr, condition.Value);
        }

        private object BuildMaxLengthString(ColumnSchema column, int rowIndex)
        {
            var targetLength = ResolveTargetStringLength(column);
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D4");
            if (targetLength <= rowToken.Length)
            {
                return rowToken[^targetLength..];
            }

            var prefixSeed = $"{Abbreviate(column.TableName, 3)}{Abbreviate(column.ColumnName, 4)}";
            var prefixLength = targetLength - rowToken.Length;
            var builder = new System.Text.StringBuilder(prefixSeed);
            while (builder.Length < prefixLength)
            {
                builder.Append(Abbreviate(column.ColumnName, 3));
            }

            var prefix = builder.ToString();
            if (prefix.Length > prefixLength)
            {
                prefix = prefix[..prefixLength];
            }

            var value = prefix + rowToken;

            return SqlServerValueNormalizer.NormalizeValue(column, value) ?? string.Empty;
        }

        private object BuildMaxNumericValue(ColumnSchema column, int rowIndex)
        {
            var offset = rowIndex + GetColumnVariantOffset(column);

            return column.TypeCategory switch
            {
                DataTypeCategory.Integer => SqlServerValueNormalizer.NormalizeValue(column, GetMaxIntegerValue(column) - offset) ?? 0,
                DataTypeCategory.Decimal => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    GetMaxDecimalValue(column) - (offset * GetNumericStep(column))) ?? 0m,
                DataTypeCategory.Float => SqlServerValueNormalizer.NormalizeValue(
                    column,
                    GetMaxFloatValue(column) - offset) ?? 0d,
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
            var tableToken = Abbreviate(column.TableName, 3);
            var columnToken = Abbreviate(column.ColumnName, 4);
            var semanticSource = ExtractSemanticSourceFragment(source);

            string candidate;
            if (IsEmailColumn(column))
            {
                var localPart = BuildCompactToken(semanticSource, $"{tableToken}{columnToken}", rowToken, Math.Max(1, targetLength - "@example.test".Length));
                candidate = $"{localPart}@example.test";
            }
            else if (LooksLikeUrl(source ?? string.Empty) || IsUrlColumn(column))
            {
                var host = BuildCompactToken(semanticSource, $"{tableToken}{columnToken}", rowToken, Math.Max(1, targetLength - "https://.example.test".Length));
                candidate = $"https://{host.ToLowerInvariant()}.example.test";
            }
            else if (IsPhoneColumn(column))
            {
                candidate = BuildPhoneLikeString(column, rowIndex, targetLength);
            }
            else if (IsCodeLikeColumn(column))
            {
                candidate = BuildCompactToken(semanticSource, $"{tableToken}{columnToken}", rowToken, targetLength);
            }
            else if (IsTierLikeColumn(column))
            {
                var tiers = new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond" };
                var tier = tiers[(rowIndex + GetColumnVariantOffset(column)) % tiers.Length];
                candidate = ComposeSemanticLabel(tier, tableToken, rowToken, targetLength);
            }
            else if (IsStatusLikeColumn(column))
            {
                var states = new[] { "Active", "Ready", "Primary", "Enabled", "Current" };
                var state = states[(rowIndex + GetColumnVariantOffset(column)) % states.Length];
                candidate = ComposeSemanticLabel(state, tableToken, rowToken, targetLength);
            }
            else if (IsNameLikeColumn(column))
            {
                var tableLabel = HumanizeToken(column.TableName);
                var columnLabel = HumanizeToken(column.ColumnName);
                candidate = $"{tableLabel} {columnLabel} {rowToken}";
                if (!string.IsNullOrWhiteSpace(semanticSource))
                {
                    candidate = $"{tableLabel} {semanticSource} {rowToken}";
                }
            }
            else
            {
                var columnLabel = HumanizeToken(column.ColumnName);
                candidate = $"{tableToken}-{columnLabel}-{rowToken}";
                if (!string.IsNullOrWhiteSpace(semanticSource))
                {
                    candidate = $"{columnLabel} {semanticSource} {rowToken}";
                }
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
            spaced = System.Text.RegularExpressions.Regex.Replace(spaced, "[^A-Za-z0-9]+", " ").Trim();
            if (string.IsNullOrWhiteSpace(spaced))
                return string.Empty;

            var words = spaced
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            return string.Join(" ", words);
        }

        private static string BuildCompactToken(string? semanticSource, string prefix, string rowToken, int maxLength)
        {
            if (maxLength <= 0)
                return string.Empty;

            var sourceToken = new string((semanticSource ?? string.Empty)
                .Where(char.IsLetterOrDigit)
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

        private static double GetMaxFloatValue(ColumnSchema column)
        {
            var digits = column.DataType.Equals("real", StringComparison.OrdinalIgnoreCase) ? 7 : 15;
            return Math.Pow(10d, digits) - 1d;
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
                if (!condition.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (excludeHasSubquery && condition.HasSubquery)
                    continue;
                if (!MatchesConditionTarget(query, condition.TableAlias, tableName, tableAlias))
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
                    c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                    MatchesConditionTarget(localAliasMap, c.TableAlias, tableName, tableAlias)))
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
            Dictionary<string, List<int>> tableRowIds)
        {
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

                candidates.Add(GenerateConditionValue(
                    scenario,
                    query,
                    col,
                    generator,
                    target.Condition,
                    target.DesiredTruth,
                    comparisonValue));

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
                if (targets.All(target => EvaluateConditionTarget(candidate, target, col, generator)))
                {
                    return new ResolvedColumnValue(true, candidate);
                }
            }

            return new ResolvedColumnValue(false, null);
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
            ColumnSchema col,
            IValueGenerator generator)
        {
            var actualTruth = EvaluateCondition(candidate, target.Condition, target.ComparisonValue, col, generator);
            return actualTruth == target.DesiredTruth;
        }

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
                    return CompareScalarValues(candidate, lower, col) >= 0 &&
                           CompareScalarValues(candidate, upper, col) <= 0;
                case ComparisonOp.Like:
                    return EvaluateLike(candidate?.ToString() ?? string.Empty, condition.LikePattern);
                default:
                    var rightValue = condition.IsColumnComparison
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
                    Convert.ToDateTime(left).CompareTo(Convert.ToDateTime(right)),
                DataTypeCategory.Time =>
                    ((TimeSpan)left).CompareTo((TimeSpan)right),
                DataTypeCategory.DateTimeOffset =>
                    ((DateTimeOffset)left).CompareTo((DateTimeOffset)right),
                _ =>
                    string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase)
            };
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
            return condition.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                   MatchesConditionTarget(
                       query,
                       condition.TableAlias,
                       query.ResolveAlias(tableAlias),
                       tableAlias);
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
                .Where(c =>
                    c.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(c.TableAlias) ||
                     c.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)))
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
                    tableRowIds);

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
            if (!NeedsAggregateDiversitySupport(query))
                return;

            if (IsLineLikeTable(schema.TableName, schema))
            {
                ApplyLineRowAdjustments(query, scenario, schema, row, rowIndex);
            }
            else if (IsPaymentLikeTable(schema.TableName, schema))
            {
                ApplyPaymentRowAdjustments(query, scenario, schema, row, rowIndex);
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
                ? BuildSafeHighInteger(schema, "QuantityOrdered", rowIndex, 0, 3, 2 + rowIndex)
                : 2 + rowIndex;
            var unitPrice = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "UnitPrice", rowIndex, 0, 4, 11.25m + (rowIndex * 2.15m))
                : 11.25m + (rowIndex * 2.15m);
            var unitCost = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "UnitCost", rowIndex, 3, 4, Math.Max(0.01m, unitPrice - 1.35m))
                : unitPrice - 1.35m;
            var lineTotal = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "LineTotal", rowIndex, 7, 5, (quantity * unitPrice) + (rowIndex * 0.5m))
                : (quantity * unitPrice) + (rowIndex * 0.5m);

            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityOrdered", quantity);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "Quantity", quantity);
            var quantityShipped = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(schema, "QuantityShipped", rowIndex, 2, 3, Math.Max(1, quantity - 1))
                : Math.Max(1, quantity - 1);
            var quantityReturned = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(schema, "QuantityReturned", rowIndex, 4, 2, rowIndex % 2)
                : rowIndex % 2;
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityShipped", quantityShipped);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityReturned", quantityReturned);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitPrice", unitPrice);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitCost", unitCost);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "LineTotal", lineTotal);
            var discountPercent = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "DiscountPercent", rowIndex, 11, 2, 0.01m + (rowIndex * 0.01m))
                : 0.01m + (rowIndex * 0.01m);
            var taxRate = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "TaxRate", rowIndex, 13, 2, 0.05m + (rowIndex * 0.01m))
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
                ? BuildSafeHighDecimal(schema, "AmountPaid", rowIndex, 0, 5, 35.00m + (rowIndex * 9.75m))
                : 35.00m + (rowIndex * 9.75m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "AmountPaid", amountPaid);
            var gatewayFee = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(schema, "GatewayFee", rowIndex, 4, 3, 0.50m + (rowIndex * 0.10m))
                : 0.50m + (rowIndex * 0.10m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "GatewayFee", gatewayFee);
        }

        private int BuildSafeHighInteger(
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            int maxDigits,
            int fallback)
        {
            var raw = BuildAdjustedMaxInteger(schema, columnName, rowIndex, extraOffset, fallback);
            var safeCap = (int)Math.Min(raw, Math.Pow(10, Math.Max(1, maxDigits)) - 1);
            var candidate = safeCap - rowIndex;
            return candidate > 0 ? candidate : Math.Max(1, fallback);
        }

        private decimal BuildSafeHighDecimal(
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
            var candidate = safeMax - ((rowIndex + extraOffset) * step);
            if (candidate <= 0m)
                candidate = step;

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate);
            return normalized is decimal d ? d : fallback;
        }

        private int BuildAdjustedMaxInteger(
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            int fallback)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return fallback;

            var raw = BuildMaxNumericValue(column, rowIndex + extraOffset);
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
            TableSchema schema,
            string columnName,
            int rowIndex,
            int extraOffset,
            decimal fallback)
        {
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return fallback;

            var raw = BuildMaxNumericValue(column, rowIndex + extraOffset);
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

            if (scenario.BoundaryConditionKey != null &&
                FindConditionByKey(query, scenario.BoundaryConditionKey) is { } boundaryCondition &&
                IsConditionTargetingColumn(query, boundaryCondition, schema.TableName, columnName))
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
            TableSchema schema,
            ParsedQuery query,
            bool isAggregateSource,
            int rowMultiplier,
            int requestedRows)
        {
            var rowCount = isAggregateSource
                ? Math.Max(rowMultiplier, requestedRows)
                : requestedRows;

            if (!NeedsAggregateDiversitySupport(query))
                return rowCount;

            var diversityFloor = GetAggregateDiversityMinimumRows(tableName, schema);
            return Math.Max(rowCount, diversityFloor);
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
