using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.DataGeneration.ValueGenerators;
using SqlTestDataGenerator.Parsing.Models;
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
        private int _idCounter = 90000;

        /// <summary>
        /// Starting ID for generated data. Set high to avoid conflicts with existing data.
        /// </summary>
        public int StartId { get; set; } = 90000;

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
            _idCounter = StartId;
            var dataSet = new GeneratedDataSet
            {
                OriginalSql = query.OriginalSql
            };

            // Resolve INSERT order
            var tableNames = query.Tables
                .Select(t => t.TableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
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

            // Also include tables from subqueries
            foreach (var sub in query.Subqueries)
            {
                foreach (var subTable in sub.Tables)
                {
                    if (!tableNames.Contains(subTable.TableName, StringComparer.OrdinalIgnoreCase) &&
                        schemas.TryGetValue(subTable.TableName, out var subSchema) &&
                        subSchema.Columns.Any())
                    {
                        tableNames.Add(subTable.TableName);
                    }
                }
            }

            foreach (var scenario in scenarios)
            {
                var workingScenario = CloneScenarioDescriptor(scenario);
                workingScenario.InsertOrder = new List<string>(insertOrder);
                GenerateScenarioData(workingScenario, query, schemas, insertOrder);
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
            List<string> insertOrder)
        {
            // Generate a unique ID set for this scenario
            var scenarioBaseId = _idCounter;
            _idCounter += GetScenarioIdStride();

            switch (scenario.Type)
            {
                case ScenarioType.Positive:
                    GeneratePositiveData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;

                case ScenarioType.WhereNegative:
                    GenerateWhereNegativeData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;

                case ScenarioType.HavingNegative:
                    GenerateHavingNegativeData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;

                case ScenarioType.JoinMiss:
                    GenerateJoinMissData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;

                case ScenarioType.SubqueryMiss:
                    GenerateSubqueryMissData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;

                case ScenarioType.Boundary:
                    GenerateBoundaryData(scenario, query, schemas, insertOrder, scenarioBaseId);
                    break;
            }
        }

        private int GetRequestedRowCount() => Math.Max(1, RowsPerTable);

        private int GetScenarioIdStride() => Math.Max(1000, GetRequestedRowCount() * 500);

        // ─── Positive scenario: all conditions satisfied ────────────────
        private void GeneratePositiveData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

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
                int rowCount = isAggregateSource
                    ? Math.Max(rowMultiplier, GetRequestedRowCount())
                    : GetRequestedRowCount();

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        var value = GenerateColumnValue(col, alias, query, schemas,
                            tableRowIds, referencedTableIdPools, rowId, satisfy: true, rowIdx);
                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }

            // Handle subquery support data
            GenerateSubquerySupportData(scenario, query, schemas, ref currentId, satisfy: true);
        }

        // ─── WHERE negative: one condition violated ─────────────────────
        private void GenerateWhereNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

            // Find the condition being tested
            var testedCondition = query.WhereConditions
                .FirstOrDefault(c => c.ToString() == scenario.TestedCondition);

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = GetRequestedRowCount();
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        // Check if this column is the one being tested
                        bool isTestedColumn = testedCondition != null &&
                            col.ColumnName.Equals(testedCondition.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrEmpty(testedCondition.TableAlias) ||
                             alias.Equals(testedCondition.TableAlias, StringComparison.OrdinalIgnoreCase));

                        var value = GenerateColumnValue(col, alias, query, schemas,
                            tableRowIds, referencedTableIdPools, rowId, satisfy: !isTestedColumn, rowIdx);
                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }
        }

        // ─── HAVING negative: aggregate condition fails ─────────────────
        private void GenerateHavingNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

            var testedCondition = query.HavingConditions
                .FirstOrDefault(c => c.ToString() == scenario.TestedCondition);

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
                int rowCount = isAggregateSource ? 1 : GetRequestedRowCount();

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        // For SUM/AVG failures, use tiny values for the aggregate column
                        bool useSmallValue = isAggregateSource &&
                            testedCondition?.AggregateFunc is AggregateFunction.Sum or AggregateFunction.Avg &&
                            IsPartOfAggregateExpression(col.ColumnName, alias, query);

                        object? value;
                        if (useSmallValue && col.TypeCategory is DataTypeCategory.Decimal or DataTypeCategory.Integer or DataTypeCategory.Float)
                        {
                            value = 1; // Tiny value to make SUM fail
                        }
                        else
                        {
                            value = GenerateColumnValue(col, alias, query, schemas,
                                tableRowIds, referencedTableIdPools, rowId, satisfy: true, rowIdx);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }
        }

        // ─── JOIN miss: LEFT/RIGHT join with no match ───────────────────
        private void GenerateJoinMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

            // Find which join is being tested
            var testedJoin = query.Joins
                .FirstOrDefault(j => scenario.TestedCondition?.Contains(j.RightTableAlias) == true);

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

                int rowCount = GetRequestedRowCount();
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
                            value = GenerateColumnValue(col, alias, query, schemas,
                                tableRowIds, referencedTableIdPools, rowId, satisfy: true, rowIdx);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }
        }

        // ─── Subquery miss: value not in subquery result ────────────────
        private void GenerateSubqueryMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            // Similar to positive but the subquery-referenced column has a value
            // that does NOT appear in the subquery result
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = GetRequestedRowCount();
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        var value = GenerateColumnValue(col, alias, query, schemas,
                            tableRowIds, referencedTableIdPools, rowId, satisfy: true, rowIdx);
                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }

            // DO NOT generate subquery support data → the IN/EXISTS condition will fail
        }

        // ─── Boundary: values at exact boundary ────────────────────────
        private void GenerateBoundaryData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, int baseId)
        {
            // Similar to positive, but range conditions use exact boundary values
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int currentId = baseId;

            var testedCondition = query.WhereConditions
                .FirstOrDefault(c => c.ToString() == scenario.TestedCondition);

            int rowMultiplier = DetermineRowMultiplier(query);

            foreach (var tableName in insertOrder)
            {
                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                bool isAggSource = IsAggregateSourceTable(tableName, alias, query);
                int rowCount = isAggSource
                    ? Math.Max(rowMultiplier, GetRequestedRowCount())
                    : GetRequestedRowCount();

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed) continue;

                        bool isBoundaryColumn = testedCondition != null &&
                            col.ColumnName.Equals(testedCondition.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrEmpty(testedCondition.TableAlias) ||
                             alias.Equals(testedCondition.TableAlias, StringComparison.OrdinalIgnoreCase));

                        object? value;
                        if (isBoundaryColumn)
                        {
                            value = GenerateBoundaryValue(col, testedCondition!);
                        }
                        else
                        {
                            value = GenerateColumnValue(col, alias, query, schemas,
                                tableRowIds, referencedTableIdPools, rowId, satisfy: true, rowIdx);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, currentId, rowCount);
                currentId += rowCount + 1;
            }

            GenerateSubquerySupportData(scenario, query, schemas, ref currentId, satisfy: true);
        }

        // ═════════════════════════════════════════════════════════════════
        // Column value generation
        // ═════════════════════════════════════════════════════════════════

        private object? GenerateColumnValue(
            ColumnSchema col, string tableAlias, ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, List<int>> referencedTableIdPools,
            int rowId,
            bool satisfy, int rowIndex)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);

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

                if (fk != null && TryResolveRelatedRowId(tableRowIds, fk.ReferencedTable, rowIndex, out var fkId))
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

            // 3. Check if this column has a WHERE condition
            var condition = query.WhereConditions
                .FirstOrDefault(c =>
                    c.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(c.TableAlias) ||
                     c.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)) &&
                    !c.HasSubquery);

            if (condition != null)
            {
                var opStr = ConditionOpToString(condition.Operator);

                if (condition.Operator == ComparisonOp.Between)
                {
                    // For BETWEEN, generate a value within the range
                    if (satisfy)
                    {
                        return GenerateBetweenValue(col, condition.Value, condition.SecondValue, inside: true);
                    }
                    else
                    {
                        return GenerateBetweenValue(col, condition.Value, condition.SecondValue, inside: false);
                    }
                }

                if (condition.Operator == ComparisonOp.In && condition.InValues.Any())
                {
                    if (satisfy)
                        return generator.GenerateFromLiteral(condition.InValues[0], col);
                    else
                        return generator.GenerateViolating(col, "=", condition.InValues[0]);
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

            // 3.5 Check HAVING aggregate conditions for positive-path generation
            if (satisfy)
            {
                var aggregateCondition = query.HavingConditions
                    .FirstOrDefault(c =>
                        c.AggregateFunc.HasValue &&
                        c.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(c.TableAlias) ||
                         c.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)));

                if (aggregateCondition != null)
                {
                    var opStr = ConditionOpToString(aggregateCondition.Operator);

                    if (aggregateCondition.Operator == ComparisonOp.Between)
                    {
                        return GenerateBetweenValue(col, aggregateCondition.Value, aggregateCondition.SecondValue, inside: true);
                    }

                    if (!string.IsNullOrWhiteSpace(aggregateCondition.Value))
                    {
                        return generator.GenerateSatisfying(col, opStr, aggregateCondition.Value);
                    }
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
                if (TryResolveRelatedRowId(tableRowIds, otherTable, rowIndex, out var joinId))
                {
                    return joinId;
                }
            }

            // 5. Default value generation
            return generator.GenerateDefault(col);
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
                if (!satisfy) continue; // Don't create matching data if we want a miss

                // For IN subqueries: create data that makes the parent value appear in the subquery result
                if (subquery.Operator is SubqueryOperator.In or SubqueryOperator.Exists)
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

            var condition = FindSubqueryCondition(subquery, tableAlias, column.ColumnName);
            if (condition != null)
            {
                if (condition.IsColumnComparison &&
                    TryResolveSubqueryComparisonValue(
                        scenario,
                        query,
                        condition,
                        tableRowIds,
                        aliasToTableMap,
                        out var comparisonValue))
                {
                    return comparisonValue;
                }

                var opStr = ConditionOpToString(condition.Operator);
                if (condition.Operator == ComparisonOp.Between)
                    return GenerateBetweenValue(column, condition.Value, condition.SecondValue, inside: true);
                if (condition.Operator == ComparisonOp.In && condition.InValues.Any())
                    return generator.GenerateFromLiteral(condition.InValues[0], column);
                if (condition.Operator == ComparisonOp.IsNull)
                    return null;
                if (condition.Operator == ComparisonOp.IsNotNull)
                    return generator.GenerateDefault(column);
                if (condition.Operator == ComparisonOp.Like)
                    return generator.GenerateSatisfying(column, "LIKE", condition.LikePattern);
                if (!string.IsNullOrWhiteSpace(condition.Value))
                    return generator.GenerateSatisfying(column, opStr, condition.Value);
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

        private static ConditionInfo? FindSubqueryCondition(
            SubqueryInfo subquery,
            string tableAlias,
            string columnName)
        {
            return subquery.Conditions.FirstOrDefault(c =>
                c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(c.TableAlias) ||
                 c.TableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)));
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

        private int DetermineRowMultiplier(ParsedQuery query)
        {
            int multiplier = 1;

            foreach (var having in query.HavingConditions)
            {
                if (having.AggregateFunc == AggregateFunction.Count)
                {
                    // COUNT(x) >= N → need N rows
                    if (int.TryParse(having.Value, out var n))
                    {
                        multiplier = Math.Max(multiplier, n);
                    }
                }
                else if (having.AggregateFunc is AggregateFunction.Sum or AggregateFunction.Avg)
                {
                    // For SUM > N, we need multiple rows
                    multiplier = Math.Max(multiplier, 3);
                }
            }

            return multiplier;
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
                TestedCondition = source.TestedCondition
            };
        }
    }
}
