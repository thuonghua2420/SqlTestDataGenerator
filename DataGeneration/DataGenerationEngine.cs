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
        private const string RequiredStringSeed = "あアﾀa6＠";
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
        /// Randomizes generated unconstrained values after row generation so sample data looks less index-based.
        /// Predicate-bound keys, relationships, and computed dependencies are left unchanged.
        /// </summary>
        public bool ShuffleGeneratedStringCharacters { get; set; }

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
                ApplyNegativeScenarioPinnedUniqueKeyIsolation(
                    query,
                    workingScenario,
                    schemas,
                    nextTableIds,
                    scenarios.Any(s => s.ExpectedToReturnRows));
                ApplyScenarioScalarAggregateComparisonAdjustments(query, workingScenario, schemas);
                ApplyScenarioInsertSafetyAdjustments(query, workingScenario, schemas);
                ApplyScenarioHavingAggregateComparisonAdjustments(query, workingScenario, schemas);
                ApplyScenarioInsertSafetyAdjustments(query, workingScenario, schemas);
                EnforceScenarioForeignKeyClosure(workingScenario, schemas);
                ApplyScenarioExpressionJoinDomainAdjustments(query, workingScenario, schemas);
                EnforceScenarioForeignKeyClosure(workingScenario, schemas);
                ApplyScenarioJoinColumnComparisonAdjustments(query, workingScenario, schemas);
                ApplyScenarioInsertSafetyAdjustments(query, workingScenario, schemas);
                EnforceScenarioUniqueKeyDiversity(query, workingScenario, schemas);
                EnforceScenarioForeignKeyClosure(workingScenario, schemas);
                ApplyScenarioStringShuffleAdjustments(query, workingScenario, schemas);
                ValidateScenarioLocalForeignKeys(workingScenario, schemas);
                ValidateScenarioData(workingScenario);
                ApplyScenarioRecordOrderShuffle(workingScenario);
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
            var forcedColumnValues = BuildScenarioForcedColumnValues(scenario, query, schemas);

            switch (scenario.Type)
            {
                case ScenarioType.Positive:
                    GeneratePositiveData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;

                case ScenarioType.WhereNegative:
                    GenerateWhereNegativeData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;

                case ScenarioType.HavingNegative:
                    GenerateHavingNegativeData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;

                case ScenarioType.JoinMiss:
                    GenerateJoinMissData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;

                case ScenarioType.SubqueryMiss:
                    GenerateSubqueryMissData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;

                case ScenarioType.Boundary:
                    GenerateBoundaryData(scenario, query, schemas, insertOrder, nextTableIds, selfReferencePlans, forcedColumnValues);
                    break;
            }
        }

        private void ApplyScenarioInsertSafetyAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            if (!UseMaxLengthMaxValueMode)
                return;

            foreach (var tableRows in scenario.TableRows)
            {
                if (!schemas.TryGetValue(tableRows.Key, out var schema))
                    continue;

                foreach (var row in tableRows.Value)
                {
                    ApplyComputedProductSafetyAdjustments(query, scenario, schema, row);
                }
            }
        }

        private void ApplyScenarioStringShuffleAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            if (!ShuffleGeneratedStringCharacters)
                return;

            var usedValueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in scenario.TableRows.Values.SelectMany(r => r))
            {
                foreach (var rawValue in row.ColumnValues.Values)
                {
                    var text = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(text))
                    {
                        IncrementUsedValue(usedValueCounts, text);
                    }
                }
            }

            foreach (var (tableName, rows) in scenario.TableRows)
            {
                if (!schemas.TryGetValue(tableName, out var schema))
                    continue;

                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];
                    foreach (var column in schema.Columns)
                    {
                        if (!CanShuffleGeneratedColumn(query, scenario, schema, column))
                            continue;

                        var rawValue = row.GetValue(column.ColumnName);
                        if (!TryBuildShuffledGeneratedValue(
                                tableName,
                                column,
                                rowIndex,
                                rawValue,
                                usedValueCounts,
                                out var shuffledValue,
                                out var shuffledValueKey))
                        {
                            continue;
                        }

                        var originalKey = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(originalKey))
                            continue;

                        DecrementUsedValue(usedValueCounts, originalKey);
                        IncrementUsedValue(usedValueCounts, shuffledValueKey);
                        row.SetValue(column.ColumnName, shuffledValue);
                    }
                }
            }
        }

        private void ApplyScenarioRecordOrderShuffle(BranchScenario scenario)
        {
            if (!ShuffleGeneratedStringCharacters)
                return;

            foreach (var (tableName, rows) in scenario.TableRows)
            {
                if (rows.Count < 2)
                    continue;

                ShuffleRowsInPlace(tableName, scenario.Name, rows);
            }
        }

        private static void ShuffleRowsInPlace(
            string tableName,
            string scenarioName,
            List<GeneratedRow> rows)
        {
            var original = rows.ToList();
            var signature = BuildRowOrderSignature(rows);
            var random = new Random(BuildStableShuffleSeed(
                tableName,
                "__record_order__",
                rows.Count,
                scenarioName + "|" + signature,
                attempt: 0));

            for (var i = rows.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (rows[i], rows[j]) = (rows[j], rows[i]);
            }

            if (!RowsHaveSameReferenceOrder(rows, original))
                return;

            var offsetSeed = BuildStableShuffleSeed(
                tableName,
                "__record_order_offset__",
                rows.Count,
                scenarioName + "|" + signature,
                attempt: 1);
            var offset = (offsetSeed % (rows.Count - 1)) + 1;
            var rotated = rows.Skip(offset).Concat(rows.Take(offset)).ToList();
            rows.Clear();
            rows.AddRange(rotated);
        }

        private static string BuildRowOrderSignature(IReadOnlyList<GeneratedRow> rows)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var row in rows.Take(16))
            {
                builder.Append(row.TableName);
                builder.Append(':');
                foreach (var value in row.ColumnValues
                             .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                             .Take(8))
                {
                    builder.Append(value.Key);
                    builder.Append('=');
                    builder.Append(Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture));
                    builder.Append(';');
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private static bool RowsHaveSameReferenceOrder(
            IReadOnlyList<GeneratedRow> rows,
            IReadOnlyList<GeneratedRow> original)
        {
            if (rows.Count != original.Count)
                return false;

            for (var i = 0; i < rows.Count; i++)
            {
                if (!ReferenceEquals(rows[i], original[i]))
                    return false;
            }

            return true;
        }

        private static void IncrementUsedValue(Dictionary<string, int> usedValueCounts, string value)
        {
            usedValueCounts.TryGetValue(value, out var count);
            usedValueCounts[value] = count + 1;
        }

        private static void DecrementUsedValue(Dictionary<string, int> usedValueCounts, string value)
        {
            if (!usedValueCounts.TryGetValue(value, out var count))
                return;

            if (count <= 1)
            {
                usedValueCounts.Remove(value);
                return;
            }

            usedValueCounts[value] = count - 1;
        }

        private bool CanShuffleGeneratedColumn(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            ColumnSchema column)
        {
            if (column.IsComputed ||
                column.IsStoreGenerated ||
                column.IsIdentity ||
                column.IsPrimaryKey ||
                schema.PrimaryKey?.Columns.Any(c => c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) == true ||
                schema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) ||
                IsOrderBySensitiveColumn(query, schema, column) ||
                IsPredicatePinnedComputedSourceColumn(query, scenario, schema, column) ||
                IsColumnProtected(query, scenario, schema, column))
            {
                return false;
            }

            return column.TypeCategory is DataTypeCategory.String or
                DataTypeCategory.Integer or
                DataTypeCategory.Decimal or
                DataTypeCategory.Float or
                DataTypeCategory.DateTime or
                DataTypeCategory.Time or
                DataTypeCategory.DateTimeOffset or
                DataTypeCategory.Boolean or
                DataTypeCategory.Guid or
                DataTypeCategory.Binary or
                DataTypeCategory.Xml or
                DataTypeCategory.Spatial or
                DataTypeCategory.HierarchyId or
                DataTypeCategory.SqlVariant;
        }

        private static bool IsOrderBySensitiveColumn(
            ParsedQuery query,
            TableSchema schema,
            ColumnSchema column)
        {
            if (string.IsNullOrWhiteSpace(query.OriginalSql) ||
                string.IsNullOrWhiteSpace(column.ColumnName) ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    query.OriginalSql,
                    @"\bORDER\s+BY\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }

            var escapedColumn = System.Text.RegularExpressions.Regex.Escape(column.ColumnName);
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    query.OriginalSql,
                    @"\bORDER\s+BY\b[\s\S]*\b" + escapedColumn + @"\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (column.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset or DataTypeCategory.Time)
                return true;

            return schema.PrimaryKey?.Columns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase) == true ||
                   column.ColumnName.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Rank", StringComparison.OrdinalIgnoreCase) ||
                   column.ColumnName.Contains("Sequence", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasShuffleUnsafePredicateColumn(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Any(condition =>
                    TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) &&
                    desiredTruth &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName) &&
                    IsShuffleUnsafePredicate(condition, tableAlias, columnName, query.ResolveAlias(tableAlias), query.AliasToTableMap));
        }

        private static bool IsShuffleUnsafePredicate(
            ConditionInfo condition,
            string tableAlias,
            string columnName,
            string tableName,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            if (condition.Operator == ComparisonOp.IsNotNull &&
                !condition.HasSubquery &&
                !condition.IsColumnComparison &&
                !HasScalarSubqueryPlaceholder(condition))
            {
                return false;
            }

            if (IsLengthOnlyPredicate(condition, tableAlias, tableName, columnName, aliasMap) ||
                IsSingleCharacterPresencePredicate(condition, tableAlias, tableName, columnName, aliasMap))
            {
                return false;
            }

            return true;
        }

        private static bool IsLengthOnlyPredicate(
            ConditionInfo condition,
            string tableAlias,
            string tableName,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            return condition.LeftExpression is FunctionScalarExpressionInfo function &&
                   function.Name.Equals("LEN", StringComparison.OrdinalIgnoreCase) &&
                   function.Arguments.Count >= 1 &&
                   ExpressionReferencesColumn(function.Arguments[0], tableAlias, tableName, columnName, aliasMap);
        }

        private static bool IsSingleCharacterPresencePredicate(
            ConditionInfo condition,
            string tableAlias,
            string tableName,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            if (condition.LeftExpression is not FunctionScalarExpressionInfo function ||
                !function.Name.Equals("CHARINDEX", StringComparison.OrdinalIgnoreCase) ||
                function.Arguments.Count < 2 ||
                !ExpressionReferencesColumn(function.Arguments[1], tableAlias, tableName, columnName, aliasMap) ||
                !TryGetLiteralString(function.Arguments[0], out var needle) ||
                needle.Length != 1)
            {
                return false;
            }

            if (!TryConvertDecimal(condition.Value, out var boundary))
                return false;

            return condition.Operator switch
            {
                ComparisonOp.GreaterThan => boundary <= 0m,
                ComparisonOp.GreaterThanOrEqual => boundary <= 1m,
                ComparisonOp.NotEqual => boundary == 0m,
                _ => false
            };
        }

        private static bool ExpressionReferencesColumn(
            ScalarExpressionInfo? expression,
            string tableAlias,
            string tableName,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            return expression switch
            {
                null => false,
                ColumnScalarExpressionInfo column =>
                    column.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                    TableAliasMatchesTarget(column.TableAlias, tableAlias, tableName, aliasMap),
                FunctionScalarExpressionInfo function =>
                    GetColumnBearingFunctionArguments(function).Any(arg =>
                        ExpressionReferencesColumn(arg, tableAlias, tableName, columnName, aliasMap)),
                BinaryScalarExpressionInfo binary =>
                    ExpressionReferencesColumn(binary.Left, tableAlias, tableName, columnName, aliasMap) ||
                    ExpressionReferencesColumn(binary.Right, tableAlias, tableName, columnName, aliasMap),
                UnaryScalarExpressionInfo unary =>
                    ExpressionReferencesColumn(unary.Operand, tableAlias, tableName, columnName, aliasMap),
                CaseScalarExpressionInfo caseExpression =>
                    ExpressionReferencesColumn(caseExpression.InputExpression, tableAlias, tableName, columnName, aliasMap) ||
                    ExpressionReferencesColumn(caseExpression.ElseExpression, tableAlias, tableName, columnName, aliasMap) ||
                    caseExpression.WhenClauses.Any(w =>
                        PredicateExpressionReferencesColumn(w.Predicate, tableAlias, tableName, columnName, aliasMap) ||
                        ExpressionReferencesColumn(w.WhenExpression, tableAlias, tableName, columnName, aliasMap) ||
                        ExpressionReferencesColumn(w.ThenExpression, tableAlias, tableName, columnName, aliasMap)),
                _ => false
            };
        }

        private static bool PredicateExpressionReferencesColumn(
            PredicateExpression? expression,
            string tableAlias,
            string tableName,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            return expression switch
            {
                null => false,
                PredicateLeafExpression leaf =>
                    ConditionTargetsColumn(aliasMap, leaf.Condition, tableName, tableAlias, columnName),
                PredicateBinaryExpression binary =>
                    PredicateExpressionReferencesColumn(binary.Left, tableAlias, tableName, columnName, aliasMap) ||
                    PredicateExpressionReferencesColumn(binary.Right, tableAlias, tableName, columnName, aliasMap),
                PredicateNotExpression not =>
                    PredicateExpressionReferencesColumn(not.Inner, tableAlias, tableName, columnName, aliasMap),
                _ => false
            };
        }

        private static bool TryGetLiteralString(ScalarExpressionInfo? expression, out string value)
        {
            value = string.Empty;
            if (expression is LiteralScalarExpressionInfo literal &&
                literal.Kind == ScalarLiteralKind.String)
            {
                value = literal.Value;
                return true;
            }

            return false;
        }

        private bool IsPredicatePinnedComputedSourceColumn(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            ColumnSchema column)
        {
            if (schema.Columns.All(c => !c.IsComputed || string.IsNullOrWhiteSpace(c.ComputedExpression)))
                return false;

            return schema.Columns
                .Where(c => c.IsComputed && !string.IsNullOrWhiteSpace(c.ComputedExpression))
                .Any(c =>
                    IsPredicatePinnedColumn(query, scenario, schema.TableName, c.ColumnName) &&
                    ExtractComputedExpressionColumnNames(c.ComputedExpression, schema)
                        .Any(name => name.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool TryBuildShuffledGeneratedValue(
            string tableName,
            ColumnSchema column,
            int rowIndex,
            object? rawValue,
            Dictionary<string, int> usedValueCounts,
            out object? shuffledValue,
            out string shuffledValueKey)
        {
            shuffledValue = null;
            shuffledValueKey = string.Empty;

            var text = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (column.TypeCategory == DataTypeCategory.String)
            {
                if (!TryBuildShuffledText(
                        tableName,
                        column,
                        rowIndex,
                        text,
                        usedValueCounts,
                        out shuffledValue,
                        out shuffledValueKey))
                {
                    return false;
                }

                return !ValuesEqual(rawValue, shuffledValue);
            }

            if (column.TypeCategory == DataTypeCategory.Guid)
            {
                var candidate = BuildDeterministicGuid(tableName, column.ColumnName, rowIndex, text);
                shuffledValue = candidate;
                shuffledValueKey = candidate.ToString();
                return !ValuesEqual(rawValue, shuffledValue) && !usedValueCounts.ContainsKey(shuffledValueKey);
            }

            if (column.TypeCategory == DataTypeCategory.Boolean)
            {
                var candidate = rowIndex % 2 == 0;
                shuffledValue = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
                shuffledValueKey = Convert.ToString(shuffledValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                return !ValuesEqual(rawValue, shuffledValue) &&
                       !string.IsNullOrEmpty(shuffledValueKey) &&
                       !usedValueCounts.ContainsKey(shuffledValueKey);
            }

            if (column.TypeCategory is DataTypeCategory.Binary or DataTypeCategory.Xml or
                DataTypeCategory.Spatial or DataTypeCategory.HierarchyId or DataTypeCategory.SqlVariant)
            {
                if (!TryBuildShuffledSpecialValue(
                        tableName,
                        column,
                        rowIndex,
                        rawValue,
                        text,
                        usedValueCounts,
                        out shuffledValue,
                        out shuffledValueKey))
                {
                    return false;
                }

                return !ValuesEqual(rawValue, shuffledValue);
            }

            if (column.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.Time or DataTypeCategory.DateTimeOffset)
            {
                return TryBuildShuffledTemporalValue(
                    tableName,
                    column,
                    rowIndex,
                    rawValue,
                    text,
                    usedValueCounts,
                    out shuffledValue,
                    out shuffledValueKey);
            }

            return TryBuildShuffledNumericValue(
                tableName,
                column,
                rowIndex,
                rawValue,
                text,
                usedValueCounts,
                out shuffledValue,
                out shuffledValueKey);
        }

        private static bool TryBuildShuffledText(
            string tableName,
            ColumnSchema column,
            int rowIndex,
            string value,
            Dictionary<string, int> usedValueCounts,
            out object? shuffledValue,
            out string shuffledValueKey)
        {
            shuffledValue = null;
            shuffledValueKey = string.Empty;
            var sources = BuildShuffleTextSources(value, rowIndex, column);

            foreach (var source in sources)
            {
                if (source.Length < 2 || source.Distinct().Count() < 2)
                    continue;

                for (var attempt = 0; attempt < 16; attempt++)
                {
                    var chars = source.ToCharArray();
                    var random = new Random(BuildStableShuffleSeed(tableName, column.ColumnName, rowIndex, source, attempt));
                    for (var i = chars.Length - 1; i > 0; i--)
                    {
                        var j = random.Next(i + 1);
                        (chars[i], chars[j]) = (chars[j], chars[i]);
                    }

                    var candidate = PreserveRequiredStringSeed(new string(chars), column);
                    var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
                    var key = Convert.ToString(normalized, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(key) &&
                        !ValuesEqual(value, normalized) &&
                        !usedValueCounts.ContainsKey(key))
                    {
                        shuffledValue = normalized;
                        shuffledValueKey = key;
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> BuildShuffleTextSources(string value, int rowIndex, ColumnSchema column)
        {
            yield return value;

            var rowToken = (rowIndex + 1).ToString("D4");
            yield return value + rowToken;
            yield return rowToken + value;

            var columnToken = new string(column.ColumnName.Where(char.IsLetterOrDigit).Take(4).ToArray());
            if (!string.IsNullOrEmpty(columnToken))
            {
                yield return value + columnToken + rowToken;
            }
        }

        private static bool TryBuildShuffledNumericValue(
            string tableName,
            ColumnSchema column,
            int rowIndex,
            object? rawValue,
            string text,
            Dictionary<string, int> usedValueCounts,
            out object? shuffledValue,
            out string shuffledValueKey)
        {
            shuffledValue = null;
            shuffledValueKey = string.Empty;

            var baseDigits = text.Where(char.IsDigit).ToArray();
            if (baseDigits.Length == 0)
                return false;

            var isNegative = text.TrimStart().StartsWith("-", StringComparison.Ordinal);
            var scale = column.TypeCategory == DataTypeCategory.Decimal
                ? Math.Max(0, column.NumericScale ?? CountFractionDigits(text))
                : column.TypeCategory == DataTypeCategory.Float
                    ? CountFractionDigits(text)
                    : 0;

            for (var attempt = 0; attempt < 24; attempt++)
            {
                var shuffledDigits = BuildNumericShuffleDigits(baseDigits, column, rowIndex, scale, attempt);
                if (shuffledDigits.Length < 2 || shuffledDigits.Distinct().Count() < 2)
                    continue;

                var random = new Random(BuildStableShuffleSeed(tableName, column.ColumnName, rowIndex, new string(shuffledDigits), attempt));
                for (var i = shuffledDigits.Length - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (shuffledDigits[i], shuffledDigits[j]) = (shuffledDigits[j], shuffledDigits[i]);
                }

                var candidateText = ComposeNumericText(shuffledDigits, scale, isNegative);
                if (candidateText.Equals(text, StringComparison.Ordinal))
                    continue;

                if (!decimal.TryParse(
                        candidateText,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    continue;
                }

                var normalized = SqlServerValueNormalizer.NormalizeValue(column, parsed);
                if (normalized == null || ValuesEqual(rawValue, normalized))
                    continue;

                var key = Convert.ToString(normalized, System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key) || usedValueCounts.ContainsKey(key))
                    continue;

                shuffledValue = normalized;
                shuffledValueKey = key;
                return true;
            }

            return false;
        }

        private static char[] BuildNumericShuffleDigits(
            char[] baseDigits,
            ColumnSchema column,
            int rowIndex,
            int scale,
            int attempt)
        {
            var rowToken = (rowIndex + 1 + attempt).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var source = new string(baseDigits);
            if (baseDigits.Length < 2 ||
                baseDigits.Distinct().Count() < 2 ||
                attempt >= 4)
            {
                source += rowToken;
            }

            var maxDigits = ResolveMaxShuffleNumericDigits(column, scale);
            if (maxDigits > 0 && source.Length > maxDigits)
            {
                source = source[^maxDigits..];
            }

            return source.ToCharArray();
        }

        private static int ResolveMaxShuffleNumericDigits(ColumnSchema column, int scale)
        {
            if (column.TypeCategory == DataTypeCategory.Decimal)
            {
                return Math.Max(1, column.NumericPrecision ?? 18);
            }

            if (column.TypeCategory == DataTypeCategory.Integer)
            {
                return column.EffectiveDataType.ToLowerInvariant() switch
                {
                    "tinyint" => 3,
                    "smallint" => 5,
                    "bigint" => 18,
                    _ => 10
                };
            }

            if (column.TypeCategory == DataTypeCategory.Float)
            {
                return Math.Max(4, 12 + scale);
            }

            return 0;
        }

        private static bool TryBuildShuffledTemporalValue(
            string tableName,
            ColumnSchema column,
            int rowIndex,
            object? rawValue,
            string text,
            Dictionary<string, int> usedValueCounts,
            out object? shuffledValue,
            out string shuffledValueKey)
        {
            shuffledValue = null;
            shuffledValueKey = string.Empty;

            var digits = text.Where(char.IsDigit).ToArray();
            if (digits.Length == 0)
                return false;

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var sourceDigits = (new string(digits) + (rowIndex + 1 + attempt).ToString("D4")).ToCharArray();
                var random = new Random(BuildStableShuffleSeed(tableName, column.ColumnName, rowIndex, new string(sourceDigits), attempt));
                for (var i = sourceDigits.Length - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (sourceDigits[i], sourceDigits[j]) = (sourceDigits[j], sourceDigits[i]);
                }

                var numericSeed = BuildNumberFromDigits(sourceDigits);
                var normalized = BuildTemporalValueFromSeed(column, rawValue, numericSeed);
                if (normalized == null || ValuesEqual(rawValue, normalized))
                    continue;

                var key = Convert.ToString(normalized, System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key) || usedValueCounts.ContainsKey(key))
                    continue;

                shuffledValue = normalized;
                shuffledValueKey = key;
                return true;
            }

            return false;
        }

        private static long BuildNumberFromDigits(IEnumerable<char> digits)
        {
            long value = 0;
            foreach (var digit in digits.Where(char.IsDigit).Take(15))
            {
                value = (value * 10) + (digit - '0');
            }

            return value;
        }

        private static object? BuildTemporalValueFromSeed(ColumnSchema column, object? rawValue, long seed)
        {
            try
            {
                var baseDate = rawValue switch
                {
                    DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified),
                    DateTimeOffset dto => DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified),
                    _ => DateTime.TryParse(
                        Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed)
                        ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
                        : new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                };

                var offsetMinutes = (int)(Math.Abs(seed) % (365L * 24L * 60L));
                var candidate = new DateTime(baseDate.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                    .AddMinutes(offsetMinutes)
                    .AddSeconds(Math.Abs(seed / 17) % 60);

                return column.TypeCategory switch
                {
                    DataTypeCategory.Time => candidate.TimeOfDay,
                    DataTypeCategory.DateTimeOffset => new DateTimeOffset(candidate, TimeSpan.Zero),
                    _ => SqlServerValueNormalizer.NormalizeValue(column, candidate)
                };
            }
            catch
            {
                return null;
            }
        }

        private static int CountFractionDigits(string text)
        {
            var dotIndex = text.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex < 0 || dotIndex == text.Length - 1)
                return 0;

            return text[(dotIndex + 1)..].Count(char.IsDigit);
        }

        private static string ComposeNumericText(char[] digits, int scale, bool isNegative)
        {
            var normalizedDigits = new string(digits);
            string numericText;
            if (scale > 0)
            {
                if (normalizedDigits.Length <= scale)
                {
                    normalizedDigits = normalizedDigits.PadLeft(scale + 1, '0');
                }

                var split = normalizedDigits.Length - scale;
                numericText = normalizedDigits[..split] + "." + normalizedDigits[split..];
            }
            else
            {
                numericText = normalizedDigits;
            }

            return isNegative ? "-" + numericText : numericText;
        }

        private static int BuildStableShuffleSeed(
            string tableName,
            string columnName,
            int rowIndex,
            string value,
            int attempt)
        {
            unchecked
            {
                var hash = 17;
                foreach (var ch in tableName)
                    hash = (hash * 31) + ch;
                foreach (var ch in columnName)
                    hash = (hash * 31) + ch;
                foreach (var ch in value)
                    hash = (hash * 31) + ch;
                hash = (hash * 31) + rowIndex;
                hash = (hash * 31) + attempt;
                return hash == int.MinValue ? int.MaxValue : Math.Abs(hash);
            }
        }

        private void ApplyNegativeScenarioPinnedUniqueKeyIsolation(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas,
            Dictionary<string, int> nextTableIds,
            bool selectedSetContainsReturningScenario)
        {
            if (!selectedSetContainsReturningScenario || scenario.ExpectedToReturnRows)
                return;

            foreach (var (tableName, rows) in scenario.TableRows.ToList())
            {
                if (rows.Count == 0 || !schemas.TryGetValue(tableName, out var schema))
                    continue;

                foreach (var uniqueColumn in EnumerateSingleColumnUniqueKeyColumns(schema))
                {
                    foreach (var alias in ResolveAliasesForTable(query, tableName))
                    {
                        var pinnedConditions = FindPositiveDirectUniqueKeyConditions(
                                query,
                                scenario,
                                alias,
                                uniqueColumn.ColumnName)
                            .Where(c => !IsScenarioTestingCondition(scenario, c))
                            .ToList();

                        if (pinnedConditions.Count == 0)
                            continue;

                        var oldValues = rows
                            .Select(r => r.GetValue(uniqueColumn.ColumnName))
                            .Where(v => v != null && v != DBNull.Value)
                            .Distinct(ValueObjectEqualityComparer.Instance)
                            .ToList();

                        foreach (var oldValue in oldValues)
                        {
                            var replacement = GeneratePinnedUniqueKeyIsolationValue(schema, uniqueColumn, nextTableIds);
                            if (replacement == null || replacement == DBNull.Value || ValuesEqual(oldValue, replacement))
                                continue;

                            foreach (var row in rows.Where(r => ValuesEqual(r.GetValue(uniqueColumn.ColumnName), oldValue)))
                            {
                                row.SetValue(uniqueColumn.ColumnName, replacement);
                            }

                            PropagateScenarioKeyValueChange(
                                scenario,
                                schemas,
                                tableName,
                                uniqueColumn.ColumnName,
                                oldValue,
                                replacement);
                        }

                        break;
                    }
                }
            }
        }

        private object? GeneratePinnedUniqueKeyIsolationValue(
            TableSchema schema,
            ColumnSchema column,
            Dictionary<string, int> nextTableIds)
        {
            object rawValue = column.TypeCategory switch
            {
                DataTypeCategory.Integer => AllocateTableIdBlock(nextTableIds, schema.TableName, 1),
                DataTypeCategory.Decimal => (decimal)AllocateTableIdBlock(nextTableIds, schema.TableName, 1),
                DataTypeCategory.Float => (double)AllocateTableIdBlock(nextTableIds, schema.TableName, 1),
                DataTypeCategory.Guid => Guid.NewGuid(),
                DataTypeCategory.String => BuildSemanticString(column, AllocateTableIdBlock(nextTableIds, schema.TableName, 1), null),
                _ => AllocateTableIdBlock(nextTableIds, schema.TableName, 1)
            };

            return SqlServerValueNormalizer.NormalizeValue(column, rawValue) ?? rawValue;
        }

        private static void PropagateScenarioKeyValueChange(
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas,
            string parentTableName,
            string parentColumnName,
            object? oldValue,
            object? newValue)
        {
            foreach (var childSchema in schemas.Values)
            {
                var matchingFks = childSchema.ForeignKeys
                    .Where(fk =>
                        fk.ReferencedTable.Equals(parentTableName, StringComparison.OrdinalIgnoreCase) &&
                        fk.ReferencedColumn.Equals(parentColumnName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingFks.Count == 0 ||
                    !scenario.TableRows.TryGetValue(childSchema.TableName, out var childRows))
                {
                    continue;
                }

                foreach (var fk in matchingFks)
                {
                    foreach (var childRow in childRows)
                    {
                        if (ValuesEqual(childRow.GetValue(fk.ColumnName), oldValue))
                        {
                            childRow.SetValue(fk.ColumnName, newValue);
                        }
                    }
                }
            }
        }

        private void EnforceScenarioUniqueKeyDiversity(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            foreach (var (tableName, rows) in scenario.TableRows.ToList())
            {
                if (rows.Count < 2 || !schemas.TryGetValue(tableName, out var schema))
                    continue;

                foreach (var keyColumn in EnumerateSingleColumnUniqueKeyColumns(schema))
                {
                    if (HasFiniteDirectUniqueKeyDomain(query, scenario, schema.TableName, keyColumn.ColumnName))
                        continue;

                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var duplicateOriginalKeys = rows
                        .Select(row => BuildValueKey(row.GetValue(keyColumn.ColumnName)))
                        .Where(key => !string.IsNullOrEmpty(key))
                        .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var replacementsByOldValue = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
                    var changed = false;

                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var currentValue = row.GetValue(keyColumn.ColumnName);
                        var currentKey = BuildValueKey(currentValue);
                        if (!string.IsNullOrEmpty(currentKey) &&
                            used.Add(currentKey) &&
                            SatisfiesPositiveDirectPredicates(query, scenario, schema, keyColumn, currentValue))
                        {
                            if (duplicateOriginalKeys.Contains(currentKey))
                            {
                                AddOrdinalKeyReplacement(replacementsByOldValue, currentKey, currentValue);
                            }
                            continue;
                        }

                        if (!TryBuildDistinctUniqueKeyValue(
                                query,
                                scenario,
                                schema,
                                keyColumn,
                                currentValue,
                                rowIndex,
                                used,
                                out var replacement))
                        {
                            continue;
                        }

                        AddOrdinalKeyReplacement(
                            replacementsByOldValue,
                            string.IsNullOrEmpty(currentKey) ? "<NULL>" : currentKey,
                            replacement);
                        row.SetValue(keyColumn.ColumnName, replacement);
                        changed = true;
                    }

                    if (changed)
                    {
                        PropagateOrdinalKeyReplacements(
                            scenario,
                            schemas,
                            schema.TableName,
                            keyColumn.ColumnName,
                            replacementsByOldValue);
                    }
                }
            }
        }

        private static void AddOrdinalKeyReplacement(
            Dictionary<string, List<object?>> replacementsByOldValue,
            string oldKey,
            object? replacement)
        {
            if (!replacementsByOldValue.TryGetValue(oldKey, out var replacements))
            {
                replacements = new List<object?>();
                replacementsByOldValue[oldKey] = replacements;
            }

            replacements.Add(replacement);
        }

        private static bool HasFiniteDirectUniqueKeyDomain(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return FindPositiveDirectUniqueKeyConditions(query, scenario, tableAlias, columnName)
                .Select(GetDirectUniqueKeyDomainSize)
                .Any(size => size > 0);
        }

        private bool TryBuildDistinctUniqueKeyValue(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            ColumnSchema column,
            object? currentValue,
            int rowIndex,
            HashSet<string> used,
            out object? replacement)
        {
            replacement = null;
            var generator = _valueFactory.GetGenerator(column.TypeCategory);

            for (var attempt = 1; attempt <= Math.Max(32, RowsPerTable * 2); attempt++)
            {
                object? candidate = column.TypeCategory switch
                {
                    DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float =>
                        BuildDistinctNumericKeyCandidate(column, currentValue, rowIndex, attempt),
                    DataTypeCategory.Guid => Guid.NewGuid(),
                    DataTypeCategory.String => BuildSemanticString(column, rowIndex + attempt, null),
                    DataTypeCategory.DateTime => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(rowIndex + attempt),
                    DataTypeCategory.DateTimeOffset => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(rowIndex + attempt),
                    DataTypeCategory.Time => TimeSpan.FromSeconds((rowIndex + attempt) % (24 * 60 * 60)),
                    _ => generator.GenerateDefault(column)
                };

                candidate = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
                var key = BuildValueKey(candidate);
                if (string.IsNullOrEmpty(key) ||
                    used.Contains(key) ||
                    !SatisfiesPositiveDirectPredicates(query, scenario, schema, column, candidate))
                {
                    continue;
                }

                used.Add(key);
                replacement = candidate;
                return true;
            }

            return false;
        }

        private static object BuildDistinctNumericKeyCandidate(
            ColumnSchema column,
            object? currentValue,
            int rowIndex,
            int attempt)
        {
            var step = GetNumericRangeStep(column);
            var baseValue = TryConvertDecimal(
                    Convert.ToString(currentValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    out var parsed)
                ? parsed
                : 0m;

            return baseValue + step * (rowIndex + attempt);
        }

        private bool SatisfiesPositiveDirectPredicates(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            ColumnSchema column,
            object? candidate)
        {
            var generator = _valueFactory.GetGenerator(column.TypeCategory);
            foreach (var alias in ResolveAliasesForTable(query, schema.TableName))
            {
                foreach (var scope in query.PredicateScopes.Where(s => s.Source != ConditionSource.Having))
                {
                    foreach (var condition in scope.Conditions)
                    {
                        if (condition.AggregateFunc != null ||
                            condition.HasSubquery ||
                            condition.IsColumnComparison ||
                            HasScalarSubqueryPlaceholder(condition) ||
                            !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                            !desiredTruth ||
                            !IsConditionTargetingColumn(query, condition, alias, column.ColumnName))
                        {
                            continue;
                        }

                        if (!EvaluateCondition(candidate, condition, null, column, generator))
                            return false;
                    }
                }
            }

            return true;
        }

        private static void PropagateOrdinalKeyReplacements(
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas,
            string parentTableName,
            string parentColumnName,
            Dictionary<string, List<object?>> replacementsByOldValue)
        {
            if (replacementsByOldValue.Count == 0)
                return;

            foreach (var childSchema in schemas.Values)
            {
                var matchingFks = childSchema.ForeignKeys
                    .Where(fk =>
                        fk.ReferencedTable.Equals(parentTableName, StringComparison.OrdinalIgnoreCase) &&
                        fk.ReferencedColumn.Equals(parentColumnName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchingFks.Count == 0 ||
                    !scenario.TableRows.TryGetValue(childSchema.TableName, out var childRows))
                {
                    continue;
                }

                foreach (var fk in matchingFks)
                {
                    foreach (var group in childRows
                                 .Select((row, index) => (row, index))
                                 .Where(item => replacementsByOldValue.ContainsKey(BuildValueKey(item.row.GetValue(fk.ColumnName))))
                                 .GroupBy(item => BuildValueKey(item.row.GetValue(fk.ColumnName))))
                    {
                        var replacements = replacementsByOldValue[group.Key];
                        if (replacements.Count == 0)
                            continue;

                        var ordinal = 0;
                        foreach (var (row, _) in group.OrderBy(item => item.index))
                        {
                            row.SetValue(fk.ColumnName, replacements[ordinal % replacements.Count]);
                            ordinal++;
                        }
                    }
                }
            }
        }

        private static bool IsScenarioTestingCondition(BranchScenario scenario, ConditionInfo condition)
        {
            if (!string.IsNullOrWhiteSpace(scenario.TestedCondition) &&
                scenario.TestedCondition.Equals(condition.Key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return scenario.TestedConditions.Any(t =>
                t.Equals(condition.Key, StringComparison.OrdinalIgnoreCase) ||
                t.Equals(condition.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ValueObjectEqualityComparer : IEqualityComparer<object?>
        {
            public static readonly ValueObjectEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y) => ValuesEqual(x, y);

            public int GetHashCode(object? obj)
            {
                if (obj == null || obj == DBNull.Value)
                    return 0;

                if (obj is byte[] bytes)
                {
                    unchecked
                    {
                        var hash = 17;
                        foreach (var b in bytes)
                            hash = (hash * 31) + b;
                        return hash;
                    }
                }

                return Convert
                    .ToString(obj, System.Globalization.CultureInfo.InvariantCulture)
                    ?.ToUpperInvariant()
                    .GetHashCode() ?? 0;
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
            return column.EffectiveDataType.ToLowerInvariant() switch
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
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
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
                int requestedRows = GetRequestedRowCount();
                int rowCount = DetermineScenarioRowCount(
                    tableName,
                    alias,
                    schema,
                    query,
                    isAggregateSource,
                    rowMultiplier,
                    requestedRows);
                if (specialMinimumRows.TryGetValue(tableName, out var specialMinimum))
                {
                    rowCount = Math.Max(rowCount, specialMinimum);
                }
                rowCount = ApplyPinnedUniqueKeyRowCountLimit(query, scenario, schema, alias, rowCount, selfReferencePlans);
                rowCount = ApplySelfReferenceMinimumRowCount(tableName, rowCount, selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed || col.IsStoreGenerated) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    ApplySemanticRowAdjustments(query, scenario, schema, row, rowIdx);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── WHERE negative: one condition violated ─────────────────────
        private void GenerateWhereNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var omittedTables = BuildOmittedTablesForNegativeScenario(scenario, query, schemas);

            foreach (var tableName in insertOrder)
            {
                if (omittedTables.Contains(tableName))
                    continue;

                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = ApplySelfReferenceMinimumRowCount(
                    tableName,
                    ApplyPinnedUniqueKeyRowCountLimit(
                        query,
                        scenario,
                        schema,
                        alias,
                        query.Tables.Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Any(t => IsAggregateSourceTable(t.TableName, t.Alias, query)) ? GetRequestedRowCount() : 1,
                        selfReferencePlans),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed || col.IsStoreGenerated) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── HAVING negative: aggregate condition fails ─────────────────
        private void GenerateHavingNegativeData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            var testedConditions = query.EnumerateScopeConditions(ConditionSource.Having)
                .Where(c =>
                    TryGetDesiredTruthForCondition(scenario, c, true, out var desiredTruth) &&
                    !desiredTruth)
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
                    : (query.Tables.Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Any(t => IsAggregateSourceTable(t.TableName, t.Alias, query)) ? GetRequestedRowCount() : 1);

                if (countRowOverrides.TryGetValue(tableName, out var overriddenCount))
                {
                    rowCount = overriddenCount;
                }

                rowCount = ApplyPinnedUniqueKeyRowCountLimit(query, scenario, schema, alias, rowCount, selfReferencePlans);
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
                        if (col.IsComputed || col.IsStoreGenerated) continue;

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
                                forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── JOIN miss: LEFT/RIGHT join with no match ───────────────────
        private void GenerateJoinMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
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
                    ApplyPinnedUniqueKeyRowCountLimit(
                        query,
                        scenario,
                        schema,
                        alias,
                        query.Tables.Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Any(t => IsAggregateSourceTable(t.TableName, t.Alias, query)) ? GetRequestedRowCount() : 1,
                        selfReferencePlans),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed || col.IsStoreGenerated) continue;

                        // For the FK column that references the missing table, use a non-existent ID
                        bool isMissingFK = testedJoin != null &&
                            col.ColumnName.Equals(testedJoin.LeftColumn, StringComparison.OrdinalIgnoreCase) &&
                            alias.Equals(testedJoin.LeftTableAlias, StringComparison.OrdinalIgnoreCase);

                        object? value;
                        if (isMissingFK)
                        {
                            value = col.TypeCategory == DataTypeCategory.Integer
                                ? SqlServerValueNormalizer.NormalizeValue(col, GetIntegerTypeMaxValue(col)) ?? GetIntegerTypeMaxValue(col)
                                : GenerateDefaultColumnValue(col, _valueFactory.GetGenerator(col.TypeCategory), rowIdx, query, alias, tableSchema: null);
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }
        }

        // ─── Subquery miss: value not in subquery result ────────────────
        private void GenerateSubqueryMissData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
        {
            var tableRowIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referenceableTableIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var referencedTableIdPools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var omittedTables = BuildOmittedTablesForNegativeScenario(scenario, query, schemas);

            foreach (var tableName in insertOrder)
            {
                if (omittedTables.Contains(tableName))
                    continue;

                var schema = schemas.GetValueOrDefault(tableName);
                if (schema == null) continue;

                var alias = query.Tables
                    .FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?.Alias ?? tableName;

                int rowCount = ApplySelfReferenceMinimumRowCount(
                    tableName,
                    ApplyPinnedUniqueKeyRowCountLimit(
                        query,
                        scenario,
                        schema,
                        alias,
                        query.Tables.Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Any(t => IsAggregateSourceTable(t.TableName, t.Alias, query)) ? GetRequestedRowCount() : 1,
                        selfReferencePlans),
                    selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);
                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed || col.IsStoreGenerated) continue;

                        var value = GenerateColumnValue(scenario, col, alias, query, schemas,
                            tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                            forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }

        }

        // ─── Boundary: values at exact boundary ────────────────────────
        private HashSet<string> BuildOmittedTablesForNegativeScenario(
            BranchScenario scenario,
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas)
        {
            var omitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var condition in query.EnumerateScopeConditions(ConditionSource.JoinOn))
            {
                if (!scenario.PredicateTruthMap.TryGetValue(condition.Key, out var desiredTruth) ||
                    desiredTruth)
                {
                    continue;
                }

                if (TryResolveChildTableForColumnComparison(condition, query.AliasToTableMap, schemas, out var childTable))
                {
                    AddTableAndDependentsToOmit(childTable, schemas, omitted);
                }
            }

            foreach (var subquery in query.Subqueries)
            {
                AddOmittedTablesForNegativeSubqueryScenario(subquery, scenario, query, schemas, omitted);
            }

            return omitted;
        }

        private void AddOmittedTablesForNegativeSubqueryScenario(
            SubqueryInfo subquery,
            BranchScenario scenario,
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            HashSet<string> omitted)
        {
            if (!string.IsNullOrWhiteSpace(subquery.PredicateConditionKey) &&
                scenario.PredicateTruthMap.TryGetValue(subquery.PredicateConditionKey, out var predicateTruth) &&
                !predicateTruth &&
                subquery.Operator == SubqueryOperator.Exists)
            {
                var localAliasMap = ExtendAliasMap(
                    new Dictionary<string, string>(query.AliasToTableMap, StringComparer.OrdinalIgnoreCase),
                    subquery.Tables);

                foreach (var condition in subquery.Conditions.Where(c => c.IsColumnComparison))
                {
                    if (TryResolveChildTableForColumnComparison(condition, localAliasMap, schemas, out var childTable))
                    {
                        AddTableAndDependentsToOmit(childTable, schemas, omitted);
                    }
                }
            }

            foreach (var nested in subquery.NestedSubqueries)
            {
                AddOmittedTablesForNegativeSubqueryScenario(nested, scenario, query, schemas, omitted);
            }
        }

        private static bool TryResolveChildTableForColumnComparison(
            ConditionInfo condition,
            IReadOnlyDictionary<string, string> aliasMap,
            Dictionary<string, TableSchema> schemas,
            out string childTable)
        {
            childTable = string.Empty;
            if (!condition.IsColumnComparison ||
                string.IsNullOrWhiteSpace(condition.TableAlias) ||
                string.IsNullOrWhiteSpace(condition.ColumnName) ||
                string.IsNullOrWhiteSpace(condition.RightTableAlias) ||
                string.IsNullOrWhiteSpace(condition.RightColumnName))
            {
                return false;
            }

            var leftTable = ResolveAliasFromMap(aliasMap, condition.TableAlias);
            var rightTable = ResolveAliasFromMap(aliasMap, condition.RightTableAlias);
            if (string.IsNullOrWhiteSpace(leftTable) || string.IsNullOrWhiteSpace(rightTable))
                return false;

            if (IsForeignKeyReference(schemas, leftTable, condition.ColumnName, rightTable, condition.RightColumnName))
            {
                childTable = leftTable;
                return true;
            }

            if (IsForeignKeyReference(schemas, rightTable, condition.RightColumnName, leftTable, condition.ColumnName))
            {
                childTable = rightTable;
                return true;
            }

            childTable = rightTable;
            return schemas.ContainsKey(childTable);
        }

        private static string ResolveAliasFromMap(IReadOnlyDictionary<string, string> aliasMap, string aliasOrTable)
        {
            return aliasMap.TryGetValue(aliasOrTable, out var tableName)
                ? tableName
                : aliasOrTable;
        }

        private static bool IsForeignKeyReference(
            Dictionary<string, TableSchema> schemas,
            string childTable,
            string childColumn,
            string parentTable,
            string parentColumn)
        {
            return schemas.TryGetValue(childTable, out var schema) &&
                   schema.ForeignKeys.Any(fk =>
                       fk.ColumnName.Equals(childColumn, StringComparison.OrdinalIgnoreCase) &&
                       fk.ReferencedTable.Equals(parentTable, StringComparison.OrdinalIgnoreCase) &&
                       fk.ReferencedColumn.Equals(parentColumn, StringComparison.OrdinalIgnoreCase));
        }

        private static void AddTableAndDependentsToOmit(
            string tableName,
            Dictionary<string, TableSchema> schemas,
            HashSet<string> omitted)
        {
            if (!omitted.Add(tableName))
                return;

            foreach (var dependent in schemas.Values)
            {
                if (dependent.ForeignKeys.Any(fk => fk.ReferencedTable.Equals(tableName, StringComparison.OrdinalIgnoreCase)))
                {
                    AddTableAndDependentsToOmit(dependent.TableName, schemas, omitted);
                }
            }
        }

        private void GenerateBoundaryData(
            BranchScenario scenario, ParsedQuery query,
            Dictionary<string, TableSchema> schemas, List<string> insertOrder, Dictionary<string, int> nextTableIds,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues)
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
                rowCount = ApplyPinnedUniqueKeyRowCountLimit(query, scenario, schema, alias, rowCount, selfReferencePlans);
                rowCount = ApplySelfReferenceMinimumRowCount(tableName, rowCount, selfReferencePlans);
                int currentId = AllocateTableIdBlock(nextTableIds, tableName, rowCount);

                for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
                {
                    var row = new GeneratedRow { TableName = tableName };
                    int rowId = currentId + rowIdx;

                    foreach (var col in schema.Columns)
                    {
                        if (col.IsComputed || col.IsStoreGenerated) continue;

                        bool isBoundaryColumn = testedCondition != null &&
                            testedCondition.AggregateFunc is not AggregateFunction.Count and not AggregateFunction.CountDistinct &&
                            IsConditionTargetingColumn(query, testedCondition, alias, col.ColumnName);

                        object? value;
                        if (isBoundaryColumn)
                        {
                            value = GenerateBoundaryValue(col, testedCondition!, rowIdx);
                        }
                        else
                        {
                            value = GenerateColumnValue(scenario, col, alias, query, schemas,
                                tableRowIds, referenceableTableIds, referencedTableIdPools, selfReferencePlans,
                                forcedColumnValues, rowId, satisfy: true, rowIdx, includeSubqueryConditions: true, currentRow: row);
                        }

                        row.SetValue(col.ColumnName, value);
                    }

                    ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                    ApplySemanticRowAdjustments(query, scenario, schema, row, rowIdx);
                    scenario.AddRow(tableName, row);
                }

                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, currentId, rowCount);
                RegisterReferenceableIds(referenceableTableIds, tableName, scenario, schema, currentId, rowCount, selfReferencePlans);
            }

        }

        // ═════════════════════════════════════════════════════════════════
        // Column value generation
        // ═════════════════════════════════════════════════════════════════

        private Dictionary<string, object?> BuildScenarioForcedColumnValues(
            BranchScenario scenario,
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas)
        {
            var forcedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var emptyIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var schema in schemas.Values)
            {
                foreach (var column in schema.Columns.Where(c => ShouldPrebindRelationalColumn(schema, c)))
                {
                    foreach (var alias in ResolveAliasesForTable(query, schema.TableName))
                    {
                        var targets = GetApplicableConditionTargets(
                                scenario,
                                query,
                                query.EnumerateScopeConditions(ConditionSource.Where),
                                schema.TableName,
                                alias,
                                column.ColumnName,
                                excludeHasSubquery: true)
                            .Concat(GetApplicableConditionTargets(
                                scenario,
                                query,
                                query.EnumerateScopeConditions(ConditionSource.JoinOn),
                                schema.TableName,
                                alias,
                                column.ColumnName,
                                excludeHasSubquery: false))
                            .Concat(FindApplicableSubqueryConditionTargets(
                                scenario,
                                query,
                                schema.TableName,
                                alias,
                                column.ColumnName)
                                .Where(t => IsPrebindableSubqueryRelationalCondition(t.Condition)))
                            .Where(t => IsPrebindableRelationalCondition(t.Condition))
                            .ToList();

                        if (targets.Count == 0)
                            continue;

                        var generator = _valueFactory.GetGenerator(column.TypeCategory);
                        var resolved = ResolveColumnValueFromTargets(
                            scenario,
                            query,
                            column,
                            generator,
                            targets,
                            emptyIdMap,
                            alias,
                            rowIndex: 0,
                            currentRow: null);

                        if (!resolved.Resolved)
                            continue;

                        AddForcedColumnValueAndReferencedKeys(
                            forcedValues,
                            schemas,
                            schema,
                            column,
                            resolved.Value,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        break;
                    }
                }
            }

            return forcedValues;
        }

        private static bool ShouldPrebindRelationalColumn(TableSchema schema, ColumnSchema column)
        {
            return column.IsIdentity ||
                   column.IsPrimaryKey ||
                   schema.PrimaryKey?.Columns.Any(c => c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) == true ||
                   schema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPrebindableRelationalCondition(ConditionInfo condition)
        {
            if (condition.HasSubquery ||
                condition.IsColumnComparison ||
                condition.Operator is ComparisonOp.Exists or ComparisonOp.NotExists or ComparisonOp.Any or ComparisonOp.All or ComparisonOp.IsNotNull)
            {
                return false;
            }

            return condition.Operator is
                ComparisonOp.Equal or
                ComparisonOp.In or
                ComparisonOp.IsNull;
        }

        private static bool IsPrebindableSubqueryRelationalCondition(ConditionInfo condition)
        {
            if (!IsPrebindableRelationalCondition(condition))
                return false;

            // Correlated subquery predicates such as o.CustomerId = c.CustomerId must stay row-scoped.
            // Prebinding them as one scenario-wide value collapses all child rows to the first parent key.
            if (condition.IsColumnComparison ||
                !string.IsNullOrWhiteSpace(condition.RightColumnName) ||
                condition.RightExpression is ColumnScalarExpressionInfo ||
                condition.ReferencedColumns.Select(c => c.TableAlias).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                return false;
            }

            return true;
        }

        private static IEnumerable<string> ResolveAliasesForTable(ParsedQuery query, string tableName)
        {
            var aliases = query.Tables
                .Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.EffectiveName)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                aliases.Add(tableName);

            return aliases;
        }

        private void AddForcedColumnValueAndReferencedKeys(
            Dictionary<string, object?> forcedValues,
            Dictionary<string, TableSchema> schemas,
            TableSchema schema,
            ColumnSchema column,
            object? value,
            HashSet<string> visited)
        {
            var key = BuildForcedColumnKey(schema.TableName, column.ColumnName);
            if (!visited.Add(key))
                return;

            var normalizedValue = NormalizeForcedColumnValue(column, value);
            TryAddForcedColumnValue(forcedValues, schema.TableName, column.ColumnName, normalizedValue);

            if (normalizedValue == null || normalizedValue == DBNull.Value)
                return;

            foreach (var fk in schema.ForeignKeys.Where(f =>
                         f.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                if (!schemas.TryGetValue(fk.ReferencedTable, out var referencedSchema))
                    continue;

                var referencedColumn = referencedSchema.GetColumn(fk.ReferencedColumn);
                if (referencedColumn == null)
                    continue;

                AddForcedColumnValueAndReferencedKeys(
                    forcedValues,
                    schemas,
                    referencedSchema,
                    referencedColumn,
                    normalizedValue,
                    visited);
            }
        }

        private object? NormalizeForcedColumnValue(ColumnSchema column, object? value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            var rawValue = value is string text ? UnquoteSqlLiteral(text) : value;
            try
            {
                return SqlServerValueNormalizer.NormalizeValue(column, rawValue) ?? rawValue;
            }
            catch
            {
                var generator = _valueFactory.GetGenerator(column.TypeCategory);
                var literal = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                var generated = generator.GenerateFromLiteral(literal, column);
                return SqlServerValueNormalizer.NormalizeValue(column, generated) ?? generated;
            }
        }

        private static string UnquoteSqlLiteral(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("N'", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("'", StringComparison.Ordinal))
            {
                return trimmed[2..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            if (trimmed.StartsWith("'", StringComparison.Ordinal) && trimmed.EndsWith("'", StringComparison.Ordinal))
            {
                return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            return trimmed;
        }

        private static bool TryAddForcedColumnValue(
            Dictionary<string, object?> forcedValues,
            string tableName,
            string columnName,
            object? value)
        {
            var key = BuildForcedColumnKey(tableName, columnName);
            if (!forcedValues.TryGetValue(key, out var existing))
            {
                forcedValues[key] = value;
                return true;
            }

            if ((existing == null || existing == DBNull.Value) && (value == null || value == DBNull.Value))
                return true;

            return ValuesEqual(existing, value);
        }

        private static string BuildValueKey(object? value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            return value switch
            {
                DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                byte[] bytes => Convert.ToBase64String(bytes),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private static bool TryGetForcedColumnValue(
            Dictionary<string, object?> forcedValues,
            string tableName,
            string columnName,
            out object? value)
        {
            return forcedValues.TryGetValue(BuildForcedColumnKey(tableName, columnName), out value);
        }

        private static string BuildForcedColumnKey(string tableName, string columnName) =>
            $"{tableName}\u001F{columnName}";

        private object? GenerateColumnValue(
            BranchScenario scenario,
            ColumnSchema col, string tableAlias, ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            Dictionary<string, List<int>> tableRowIds,
            Dictionary<string, List<int>> referenceableTableIds,
            Dictionary<string, List<int>> referencedTableIdPools,
            Dictionary<string, SelfReferencePlan> selfReferencePlans,
            Dictionary<string, object?> forcedColumnValues,
            int rowId,
            bool satisfy, int rowIndex,
            bool includeSubqueryConditions,
            GeneratedRow? currentRow = null)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);
            var currentTableName = string.IsNullOrWhiteSpace(col.TableName)
                ? query.ResolveAlias(tableAlias)
                : col.TableName;
            var currentTableSchema = schemas.Values.FirstOrDefault(s =>
                s.Columns.Contains(col));
            var isForeignKeyColumn = currentTableSchema?.ForeignKeys.Any(fk =>
                fk.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase)) == true;

            if (col.IsIdentity &&
                !HasDirectValuePredicate(query, scenario, tableAlias, col.ColumnName))
            {
                return rowId;
            }

            if ((!isForeignKeyColumn || HasDirectValuePredicate(query, scenario, tableAlias, col.ColumnName)) &&
                TryGetForcedColumnValue(forcedColumnValues, currentTableName, col.ColumnName, out var forcedValue))
            {
                return forcedValue;
            }

            // Always generate explicit values for IDENTITY columns so scripts can include full rows.
            if (col.IsIdentity &&
                !HasDirectValuePredicate(query, scenario, tableAlias, col.ColumnName))
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
            var defaultValue = GenerateDefaultColumnValue(col, generator, rowIndex, query, tableAlias, tableSchema: currentTableSchema);
            return SqlServerValueNormalizer.NormalizeValue(col, defaultValue);
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
            string tableAlias,
            TableSchema? tableSchema = null)
        {
            if (col.IsStoreGenerated)
            {
                return DBNull.Value;
            }

            // 1. Special Types (Spatial, Hierarchy, Variant)
            if (col.EffectiveDataType.Equals("geography", StringComparison.OrdinalIgnoreCase) ||
                col.EffectiveDataType.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                return $"POINT({rowIndex} {rowIndex})";
            }

            if (col.EffectiveDataType.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase))
            {
                return $"/{rowIndex + 1}/";
            }

            if (col.EffectiveDataType.Equals("sql_variant", StringComparison.OrdinalIgnoreCase))
            {
                if (col.ColumnName.Contains("Date", StringComparison.OrdinalIgnoreCase))
                    return new DateTime(2024, 1, 1).AddDays(rowIndex);
                return 100.00m + rowIndex;
            }

            // 2. Query Function Hints (e.g. CHARINDEX hints)
            if (col.TypeCategory == DataTypeCategory.String &&
                TryBuildQueryFunctionHintedString(col, rowIndex, query, out var hintedString))
            {
                return hintedString;
            }

            // 3. Max Length / Max Value Mode
            if (UseMaxLengthMaxValueMode)
            {
                var maxModeValue = GenerateMaxLengthMaxValue(col, rowIndex, query, tableAlias, tableSchema);
                if (maxModeValue != null)
                {
                    return maxModeValue;
                }
            }

            // 4. Sample Data
            if (TryGetSampleValue(col, out var sampleValue))
            {
                var sampleModeValue = GenerateSampleBasedValue(col, sampleValue, rowIndex);
                if (sampleModeValue != null)
                {
                    return sampleModeValue;
                }
            }

            // 5. Default Semantic Generation (Strings and XML)
            if (col.TypeCategory == DataTypeCategory.String ||
                col.EffectiveDataType.Equals("xml", StringComparison.OrdinalIgnoreCase))
            {
                return BuildSemanticString(col, rowIndex, null);
            }

            if (col.TypeCategory == DataTypeCategory.Boolean &&
                TryGenerateSemanticBooleanDefault(col, out var booleanValue))
            {
                return booleanValue;
            }

            // 6. Type-specific generator fallback
            return generator.GenerateDefault(col);
        }

        private static bool TryGenerateSemanticBooleanDefault(ColumnSchema column, out object value)
        {
            var name = column.ColumnName;
            if (name.Contains("Active", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Enabled", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Preferred", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Confirmed", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Approved", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (name.Contains("Deleted", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Discontinued", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Seasonal", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Archived", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
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

        private object? GenerateMaxLengthMaxValue(ColumnSchema column, int rowIndex, ParsedQuery query, string tableAlias, TableSchema? tableSchema = null)
        {
            if (column.TypeCategory == DataTypeCategory.String)
                return BuildMaxLengthString(column, rowIndex);

            if (column.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
            {
                decimal? computedUpperBound = null;
                if (tableSchema != null &&
                    TryGetComputedProductUpperBound(tableSchema, column, out var bound))
                {
                    computedUpperBound = bound;
                }
                return BuildMaxNumericValue(column, rowIndex, query, tableAlias, computedUpperBound);
            }

            if (column.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset or DataTypeCategory.Time)
            {
                return BuildMaxTemporalValue(column, rowIndex);
            }

            if (column.TypeCategory == DataTypeCategory.Guid)
            {
                return Guid.NewGuid();
            }

            if (column.TypeCategory == DataTypeCategory.Boolean)
            {
                return (rowIndex % 2 == 0);
            }

            return null;
        }

        private bool TryGetComputedProductUpperBound(
            TableSchema schema,
            ColumnSchema sourceColumn,
            out decimal upperBound)
        {
            upperBound = 0m;
            if (IsMeasureLikeNumericColumn(sourceColumn))
                return false;

            foreach (var computedCol in schema.Columns)
            {
                if (!computedCol.IsComputed) continue;
                if (!TryBuildComputedProductColumnPlan(schema, computedCol, out var plan)) continue;
                if (!TryGetPositiveColumnMax(plan.ResultColumn, out var resultMax)) continue;

                ColumnSchema? otherFactor = null;
                if (plan.AnchorColumn.ColumnName.Equals(sourceColumn.ColumnName, StringComparison.OrdinalIgnoreCase))
                    otherFactor = plan.AdjustableColumn;
                else if (plan.AdjustableColumn.ColumnName.Equals(sourceColumn.ColumnName, StringComparison.OrdinalIgnoreCase))
                    otherFactor = plan.AnchorColumn;

                if (otherFactor == null) continue;

                if (!TryGetPositiveColumnMax(otherFactor, out var otherMax) || otherMax <= 0m)
                    continue;

                var limit = resultMax / otherMax;
                if (limit <= 0m) continue;

                upperBound = limit;
                return true;
            }

            if (IsQuantityLikeNumericColumn(sourceColumn) &&
                schema.Columns.Any(c => c.IsComputed && IsComputedNumericProductResultColumn(c)) &&
                TryGetImplicitProductUpperBound(schema, sourceColumn, out upperBound))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetImplicitProductUpperBound(
            TableSchema schema,
            ColumnSchema quantityColumn,
            out decimal upperBound)
        {
            upperBound = 0m;
            var measureColumn = schema.Columns.FirstOrDefault(c =>
                !c.IsComputed &&
                !c.IsStoreGenerated &&
                c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                IsProductFactorMeasureColumn(c));
            if (measureColumn == null ||
                !TryGetPositiveColumnMax(measureColumn, out var measureMax) ||
                measureMax <= 0m)
            {
                return false;
            }

            var resultColumn = schema.Columns.FirstOrDefault(c =>
                c.IsComputed &&
                (c.ColumnName.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                 c.ColumnName.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
                 c.ColumnName.Contains("Computed", StringComparison.OrdinalIgnoreCase)));

            var resultMax = resultColumn != null && TryGetPositiveColumnMax(resultColumn, out var explicitResultMax)
                ? explicitResultMax
                : measureMax;

            var limit = resultMax / measureMax;
            if (limit <= 0m)
                return false;

            upperBound = Math.Max(GetNumericRangeStep(quantityColumn), limit);
            return true;
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

            if (UseMaxLengthMaxValueMode &&
                col.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                TryGenerateMaxModeConditionValue(
                    query,
                    col,
                    generator,
                    condition,
                    satisfy,
                    comparisonValue,
                    tableAlias,
                    rowIndex,
                    out var maxConditionValue))
            {
                return maxConditionValue;
            }

            if (condition.Operator == ComparisonOp.Between)
            {
                var inside = condition.IsNegated ? !satisfy : satisfy;
                return GenerateBetweenValue(col, condition.Value, condition.SecondValue, inside);
            }

            if (condition.Operator == ComparisonOp.In && condition.InValues.Any())
            {
                return satisfy
                    ? generator.GenerateFromLiteral(condition.InValues[Math.Abs(rowIndex) % condition.InValues.Count], col)
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

            if (col.TypeCategory == DataTypeCategory.String &&
                satisfy &&
                LooksLikeSqlExpressionLiteral(condition.Value))
            {
                return BuildSemanticString(col, rowIndex, null);
            }

            if (condition.Operator == ComparisonOp.Like)
            {
                var shouldMatchLike = condition.IsNegated ? !satisfy : satisfy;
                if (col.TypeCategory == DataTypeCategory.String)
                {
                    return shouldMatchLike
                        ? SqlLikePattern.GenerateMatchingValue(condition.LikePattern, col, condition.LikeEscape)
                        : SqlLikePattern.GenerateNonMatchingValue(condition.LikePattern, col, condition.LikeEscape);
                }

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
                return GenerateBoundaryValue(col, condition, rowIndex);
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

        private bool TryGenerateMaxModeConditionValue(
            ParsedQuery query,
            ColumnSchema column,
            IValueGenerator generator,
            ConditionInfo condition,
            bool satisfy,
            object? comparisonValue,
            string? tableAlias,
            int rowIndex,
            out object? value)
        {
            value = null;
            if (!satisfy)
                return false;

            object? candidate = null;
            var step = GetNumericRangeStep(column);
            switch (condition.Operator)
            {
                case ComparisonOp.In:
                    var values = condition.InValues
                        .Select(v => TryConvertDecimal(v, out var parsed) ? (decimal?)parsed : null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();
                    if (values.Count > 0)
                        candidate = values.Max();
                    break;

                case ComparisonOp.Between:
                    if (!condition.IsNegated &&
                        TryConvertDecimal(condition.SecondValue, out var upper))
                    {
                        candidate = upper;
                    }
                    break;

                case ComparisonOp.LessThan:
                    if (TryConvertDecimal(condition.Value, out var lessThan))
                        candidate = lessThan - step;
                    break;

                case ComparisonOp.LessThanOrEqual:
                    if (TryConvertDecimal(condition.Value, out var lessThanOrEqual))
                        candidate = lessThanOrEqual;
                    break;

                case ComparisonOp.Equal:
                    candidate = generator.GenerateFromLiteral(condition.Value, column);
                    break;

                case ComparisonOp.GreaterThan:
                case ComparisonOp.GreaterThanOrEqual:
                case ComparisonOp.NotIn:
                    candidate = GenerateMaxLengthMaxValue(column, rowIndex, query, tableAlias ?? column.TableName);
                    break;
            }

            if (candidate == null)
                return false;

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate);
            if (EvaluateCondition(normalized, condition, comparisonValue, column, generator) != satisfy)
                return false;

            value = normalized;
            return true;
        }

        private object BuildMaxLengthString(ColumnSchema column, int rowIndex)
        {
            var targetLength = ResolveTargetStringLength(column);
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D4");
            object value;
            
            if (IsPhoneColumn(column))
                value = BuildPhoneLikeString(column, rowIndex, targetLength);
            else if (IsEmailColumn(column))
                value = BuildExactMaxEmailString(column, rowToken, targetLength);
            else if (IsUrlColumn(column))
                value = BuildExactMaxUrlString(column, rowToken, targetLength);
            else if (IsCodeLikeColumn(column))
                value = BuildExactCompactToken(null, $"{GetAsciiTableLabel(column)}{GetAsciiColumnLabel(column)}", rowToken, targetLength);
            else
                value = BuildMaxLengthContent(column, rowToken, targetLength);

            return SqlServerValueNormalizer.NormalizeValue(column, value) ?? string.Empty;
        }

        private object BuildMaxLengthContent(ColumnSchema column, string rowToken, int targetLength)
        {
            var seed = SupportsJapaneseText(column) ? RequiredStringSeed + " " : "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 ";
            var prefix = $"{Abbreviate(column.TableName, 2)}{Abbreviate(column.ColumnName, 2)}";
            
            if (targetLength <= prefix.Length + rowToken.Length)
            {
                return BuildExactCompactToken(null, prefix, rowToken, targetLength);
            }

            var repeated = string.Concat(Enumerable.Repeat(seed, (targetLength / seed.Length) + 1));
            var baseStr = prefix + repeated.Substring(0, targetLength - rowToken.Length - prefix.Length);
            return baseStr + rowToken;
        }

        private object BuildMaxTemporalValue(ColumnSchema column, int rowIndex)
        {
            var offset = BuildNonLinearTemporalOffset(column, rowIndex);
            var type = column.EffectiveDataType.ToLowerInvariant();
            
            if (type == "time")
            {
                return new TimeSpan(23, 59, 59).Subtract(TimeSpan.FromSeconds(offset % 60));
            }

            DateTime maxDt;
            if (type == "smalldatetime")
                maxDt = new DateTime(2079, 6, 6, 23, 59, 0).AddMinutes(-offset);
            else if (type == "date")
                maxDt = new DateTime(9999, 12, 31).AddDays(-offset);
            else
                maxDt = new DateTime(9999, 12, 31, 23, 59, 59).AddSeconds(-offset);

            return SqlServerValueNormalizer.NormalizeValue(column, maxDt) ?? maxDt;
        }

        private object BuildMaxNumericValue(ColumnSchema column, int rowIndex)
        {
            return BuildMaxNumericValue(column, rowIndex, query: null, tableAlias: column.TableName, upperBound: null);
        }

        private object BuildMaxNumericValue(ColumnSchema column, int rowIndex, ParsedQuery? query, string tableAlias, decimal? upperBound = null)
        {
            var step = GetNumericStep(column);

            switch (column.TypeCategory)
            {
                case DataTypeCategory.Integer:
                {
                    decimal baseMax = upperBound ?? GetPracticalMaxIntegerValue(column, query, tableAlias);
                    var offset = BuildNonLinearNumericOffset(column, rowIndex, 1m, baseMax);
                    var raw = baseMax - offset;
                    if (raw < 0 && baseMax >= 0) raw = 0;
                    return SqlServerValueNormalizer.NormalizeValue(column, (long)raw) ?? (long)raw;
                }
                case DataTypeCategory.Decimal:
                {
                    decimal baseMax = upperBound ?? GetPracticalMaxDecimalValue(column, query, tableAlias);
                    var offset = BuildNonLinearNumericOffset(column, rowIndex, step, baseMax);
                    var raw = baseMax - (offset * step);
                    if (raw < 0 && baseMax >= 0) raw = 0;
                    return SqlServerValueNormalizer.NormalizeValue(column, raw) ?? raw;
                }
                case DataTypeCategory.Float:
                {
                    double baseMax;
                    if (upperBound.HasValue)
                    {
                        baseMax = (double)upperBound.Value;
                    }
                    else
                    {
                        baseMax = GetPracticalMaxFloatValue(column, query, tableAlias);
                    }
                    
                    var offset = (double)BuildNonLinearNumericOffset(column, rowIndex, 1m, (decimal)Math.Min(baseMax, 1_000_000d));
                    var raw = baseMax - offset;
                    return SqlServerValueNormalizer.NormalizeValue(column, raw) ?? raw;
                }
                default:
                    return 0;
            }
        }

        private decimal BuildNonLinearNumericOffset(
            ColumnSchema column,
            int rowIndex,
            decimal step,
            decimal baseMax)
        {
            if (step <= 0m || baseMax <= 0m)
                return Math.Max(0, rowIndex);

            var maxSteps = decimal.Floor(baseMax / step);
            if (maxSteps <= 0m)
                return 0m;

            var index = Math.Max(0, rowIndex);
            if (index == 0)
                return 0m;

            var requestedRows = Math.Max(1, GetRequestedRowCount());
            var effectiveRows = Math.Max(requestedRows, index + 1);
            var minimumNeeded = effectiveRows - 1m;
            if (maxSteps <= minimumNeeded)
                return Math.Min(index, maxSteps);

            var variantRoom = Math.Min(7m, Math.Max(0m, maxSteps - minimumNeeded));
            var baseOffset = variantRoom <= 0m
                ? 0m
                : GetColumnVariantOffset(column) % (int)(variantRoom + 1m);
            var remaining = maxSteps - baseOffset - minimumNeeded;
            if (remaining < 0m)
            {
                baseOffset = 0m;
                remaining = maxSteps - minimumNeeded;
            }

            var spread = Math.Min(remaining, Math.Max(8m, effectiveRows * 11m));
            var denominator = Math.Max(1m, (effectiveRows - 1m) * (effectiveRows - 1m));
            var curved = decimal.Floor(spread * index * index / denominator);
            var offset = baseOffset + index + curved;
            return offset > maxSteps ? maxSteps : offset;
        }

        private int BuildNonLinearTemporalOffset(ColumnSchema column, int rowIndex)
        {
            var index = Math.Max(0, rowIndex);
            if (index == 0)
                return 0;

            var requestedRows = Math.Max(1, GetRequestedRowCount());
            var effectiveRows = Math.Max(requestedRows, index + 1);
            var type = column.EffectiveDataType.ToLowerInvariant();
            var maxSteps = type switch
            {
                "time" => 24 * 60 * 60 - 1,
                "smalldatetime" => 365,
                "date" => 365,
                _ => 365 * 24 * 60
            };

            var minimumNeeded = effectiveRows - 1;
            if (maxSteps <= minimumNeeded)
                return Math.Min(index, maxSteps);

            var variantRoom = Math.Min(7, Math.Max(0, maxSteps - minimumNeeded));
            var baseOffset = variantRoom <= 0 ? 0 : GetColumnVariantOffset(column) % (variantRoom + 1);
            var remaining = maxSteps - baseOffset - minimumNeeded;
            if (remaining < 0)
            {
                baseOffset = 0;
                remaining = maxSteps - minimumNeeded;
            }

            var spread = Math.Min(remaining, Math.Max(8, effectiveRows * 11));
            var denominator = Math.Max(1, (effectiveRows - 1) * (effectiveRows - 1));
            var curved = spread * index * index / denominator;
            var offset = baseOffset + index + curved;
            return Math.Min(offset, maxSteps);
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
            else if (column.EffectiveDataType.Equals("xml", StringComparison.OrdinalIgnoreCase))
            {
                candidate = $"<root><item id=\"{rowToken}\">Data</item></root>";
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

            if (useJapanese &&
                !column.EffectiveDataType.Equals("xml", StringComparison.OrdinalIgnoreCase) &&
                !IsEmailColumn(column) &&
                !IsUrlColumn(column) &&
                !IsPhoneColumn(column))
            {
                candidate = $"{RequiredStringSeed} {candidate}";
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

            return digits.PadLeft(targetLength, '0')[^targetLength..];
        }

        private static Guid BuildDeterministicGuid(string tableName, string columnName, int rowIndex, string value)
        {
            var seed = BuildStableShuffleSeed(tableName, columnName, rowIndex, value, attempt: 97);
            var bytes = new byte[16];
            new Random(seed).NextBytes(bytes);
            return new Guid(bytes);
        }

        private static bool TryBuildShuffledSpecialValue(
            string tableName,
            ColumnSchema column,
            int rowIndex,
            object? rawValue,
            string text,
            Dictionary<string, int> usedValueCounts,
            out object? shuffledValue,
            out string shuffledValueKey)
        {
            shuffledValue = null;
            shuffledValueKey = string.Empty;
            object candidate = column.TypeCategory switch
            {
                DataTypeCategory.Binary => BuildDeterministicBinaryValue(tableName, column, rowIndex, text),
                DataTypeCategory.Xml => $"<root><v>{EscapeXml(BuildSeededJapaneseValue(column, rowIndex, Math.Min(ResolveDeclaredStringLength(column), 64)))}</v></root>",
                DataTypeCategory.Spatial => BuildSpatialDefaultValue(column, rowIndex),
                DataTypeCategory.HierarchyId => $"/{rowIndex + 1}/{Math.Abs(BuildStableShuffleSeed(tableName, column.ColumnName, rowIndex, text, 11)) % 100 + 1}/",
                DataTypeCategory.SqlVariant => BuildSqlVariantDefaultValue(column, rowIndex),
                _ => text
            };

            var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
            var key = Convert.ToString(normalized, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(key) || ValuesEqual(rawValue, normalized) || usedValueCounts.ContainsKey(key))
                return false;

            shuffledValue = normalized;
            shuffledValueKey = key;
            return true;
        }

        private static byte[] BuildDeterministicBinaryValue(string tableName, ColumnSchema column, int rowIndex, string value)
        {
            var length = Math.Clamp(column.MaxLength ?? 8, 1, 64);
            var bytes = new byte[length];
            new Random(BuildStableShuffleSeed(tableName, column.ColumnName, rowIndex, value, 31)).NextBytes(bytes);
            return bytes;
        }

        private static string EscapeXml(string value) =>
            value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);

        private static string PreserveRequiredStringSeed(string candidate, ColumnSchema column)
        {
            if (IsEmailColumn(column) || IsUrlColumn(column) || IsPhoneColumn(column))
                return candidate;

            var targetLength = candidate.Length;
            if (targetLength <= 0)
                return string.Empty;

            var seed = RequiredStringSeed.Length <= targetLength
                ? RequiredStringSeed
                : RequiredStringSeed[..targetLength];

            if (candidate.Contains(seed, StringComparison.Ordinal))
                return candidate;

            var withoutSeedChars = new string(candidate
                .Where(c => seed.IndexOf(c) < 0)
                .ToArray());
            var merged = seed + withoutSeedChars;
            return merged.Length <= targetLength ? merged : merged[..targetLength];
        }

        private static string BuildSeededJapaneseValue(ColumnSchema column, int rowIndex, int targetLength)
        {
            if (targetLength <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D3");
            var phrase = $"{RequiredStringSeed} {GetLocalizedTableLabel(column.TableName, true)} {GetLocalizedColumnLabel(column.ColumnName, true)} {rowToken}";
            return FitSemanticString(phrase, rowToken, targetLength);
        }

        private static object BuildSpatialDefaultValue(ColumnSchema column, int rowIndex)
        {
            var x = 139.6917m + (rowIndex * 0.001m);
            var y = 35.6895m + (rowIndex * 0.001m);
            var wkt = FormattableString.Invariant($"POINT({x} {y})");
            return SqlServerValueNormalizer.NormalizeValue(column, wkt) ?? wkt;
        }

        private static object BuildSqlVariantDefaultValue(ColumnSchema column, int rowIndex)
        {
            var lower = column.ColumnName.ToLowerInvariant();
            if (lower.Contains("date") || lower.Contains("time"))
                return new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(rowIndex);
            if (lower.Contains("amount") || lower.Contains("price") || lower.Contains("total") || lower.Contains("qty") || lower.Contains("quantity") || lower.Contains("count"))
                return 1000m + rowIndex;

            return BuildSeededJapaneseValue(column, rowIndex, Math.Min(ResolveDeclaredStringLength(column), 64));
        }

        private static int ResolveDeclaredStringLength(ColumnSchema column)
        {
            if (column.MaxLength.HasValue && column.MaxLength.Value > 0)
                return column.MaxLength.Value;

            return 64;
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

            var result = builder.ToString(0, targetLength);
            if (result.Length > 1 && result[0] == result[^1])
            {
                var replacement = rowToken.Concat(normalizedPhrase)
                    .FirstOrDefault(ch => ch != result[0] && !char.IsWhiteSpace(ch));
                if (replacement == default)
                    replacement = result[0] == 'Z' ? 'Y' : 'Z';

                result = result[..^1] + replacement;
            }

            return result;
        }

        private static bool SupportsJapaneseText(ColumnSchema column)
        {
            return column.TypeCategory == DataTypeCategory.String;
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
            var mutated = column.EffectiveDataType.Equals("date", StringComparison.OrdinalIgnoreCase)
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
                var scale = Math.Min(28, column.NumericScale.Value);
                decimal step = 1m;
                for (int i = 0; i < scale; i++)
                {
                    step /= 10m;
                }

                return step;
            }

            return 1m;
        }

        private static long GetMaxIntegerValue(ColumnSchema column)
        {
            return column.EffectiveDataType.ToLowerInvariant() switch
            {
                "tinyint" => byte.MaxValue,
                "smallint" => short.MaxValue,
                "int" => int.MaxValue,
                _ => long.MaxValue / 1024
            };
        }

        private long GetPracticalMaxIntegerValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            if (UseMaxLengthMaxValueMode)
                return GetMaxIntegerValue(column);

            if (IsRatingLikeNumericColumn(column))
                return Math.Min(GetMaxIntegerValue(column), 5L);

            var absoluteMax = GetMaxIntegerValue(column);
            var safeDigits = DeterminePracticalNumericDigits(column, query, tableAlias);
            
            // Safety: If it's a count-like column, never exceed 3 digits (999) to avoid overflow in triggers
            if (IsCountLikeNumericColumn(column))
                safeDigits = Math.Min(safeDigits, 3);

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
            var type = column.EffectiveDataType.ToLowerInvariant();
            if (type == "money")
                return 922337203685477.5807m;
            if (type == "smallmoney")
                return 214748.3647m;

            var scale = Math.Max(0, column.NumericScale ?? 0);
            var precision = Math.Max(1, column.NumericPrecision ?? 18);
            var integerDigits = Math.Max(0, precision - scale);
            var safeIntegerDigits = Math.Min(integerDigits, 28);
            decimal wholePart = 1m;
            for (int i = 0; i < safeIntegerDigits; i++)
            {
                wholePart *= 10m;
            }

            var step = GetNumericStep(column);
            var max = safeIntegerDigits + Math.Min(scale, 28) <= 28
                ? wholePart - step
                : wholePart - 1m;
            return max > 0m ? max : step;
        }

        private decimal GetPracticalMaxDecimalValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var step = GetNumericStep(column);

            if (UseMaxLengthMaxValueMode)
                return GetMaxDecimalValue(column);

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

            // Safety: If it's a measure-like column (Price, Amount), cap at 4 integer digits (9999.99)
            if (IsMeasureLikeNumericColumn(column))
                safeDigits = Math.Min(safeDigits, 4);

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
            var digits = column.EffectiveDataType.Equals("real", StringComparison.OrdinalIgnoreCase) ? 7 : 15;
            return Math.Pow(10d, digits) - 1d;
        }

        private double GetPracticalMaxFloatValue(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            if (UseMaxLengthMaxValueMode)
                return GetMaxFloatValue(column);

            var safeDigits = DeterminePracticalNumericDigits(column, query, tableAlias);
            return Math.Min(GetMaxFloatValue(column), Math.Pow(10d, safeDigits) - 1d);
        }

        private int DeterminePracticalNumericDigits(ColumnSchema column, ParsedQuery? query, string tableAlias)
        {
            var typeDigits = column.EffectiveDataType.ToLowerInvariant() switch
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

                if (!TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth))
                    continue;

                targets.Add(new ColumnConditionTarget(condition, desiredTruth));
            }

            return targets;
        }

        private List<ColumnConditionTarget> GetApplicableAggregateTargets(
            BranchScenario scenario,
            ParsedQuery query,
            string tableAlias,
            string columnName)
        {
            var targets = new List<ColumnConditionTarget>();
            foreach (var condition in query.EnumerateScopeConditions(ConditionSource.Having))
            {
                if (!condition.AggregateFunc.HasValue ||
                    condition.AggregateFunc is AggregateFunction.Count or AggregateFunction.CountDistinct ||
                    !IsConditionTargetingColumn(query, condition, tableAlias, columnName) ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth))
                {
                    continue;
                }

                targets.Add(new ColumnConditionTarget(condition, desiredTruth));
            }

            return targets;
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
                    if (!TryGetDesiredTruthFromAssignments(internalTruthMap, condition, defaultTruth: true, out var desiredTruth))
                        continue;

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

                    targets.Add(new ColumnConditionTarget(condition, desiredTruth, comparisonValue, localAliasMap));
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

            if (IsExplicitDifferentAlias(query.AliasToTableMap, conditionTableAlias, tableAlias))
                return false;

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

        private static bool IsExplicitDifferentAlias(
            IReadOnlyDictionary<string, string> aliasMap,
            string conditionTableAlias,
            string tableAlias)
        {
            if (!aliasMap.TryGetValue(conditionTableAlias, out var resolved))
                return false;

            var tableAliasIsExplicit = aliasMap.TryGetValue(tableAlias, out var tableAliasResolved) &&
                                       !tableAlias.Equals(tableAliasResolved, StringComparison.OrdinalIgnoreCase);
            if (!tableAliasIsExplicit)
                return false;

            return !conditionTableAlias.Equals(resolved, StringComparison.OrdinalIgnoreCase) &&
                   !conditionTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase);
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

            var resolvedTargets = targets
                .Select(target => ResolveColumnConditionTargetComparisonValue(
                    scenario,
                    query,
                    target,
                    tableRowIds,
                    rowIndex,
                    col))
                .ToList();

            var candidates = new List<object?>();

            AddRangeIntersectionCandidates(
                scenario,
                query,
                currentRow,
                col,
                tableAlias,
                resolvedTargets,
                candidates);

            foreach (var target in resolvedTargets)
            {
                var comparisonValue = target.ComparisonValue;

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
                if (resolvedTargets.All(target => EvaluateConditionTarget(candidate, target, scenario, query, col, generator, tableAlias, currentRow)))
                {
                    return new ResolvedColumnValue(true, candidate);
                }
            }

            var falsifyingTargets = resolvedTargets
                .Where(target => !target.DesiredTruth)
                .ToList();
            if (falsifyingTargets.Count > 0)
            {
                foreach (var candidate in DeduplicateCandidates(candidates))
                {
                    if (falsifyingTargets.All(target => EvaluateConditionTarget(candidate, target, scenario, query, col, generator, tableAlias, currentRow)))
                    {
                        return new ResolvedColumnValue(true, candidate);
                    }
                }
            }

            return new ResolvedColumnValue(false, null);
        }

        private ColumnConditionTarget ResolveColumnConditionTargetComparisonValue(
            BranchScenario scenario,
            ParsedQuery query,
            ColumnConditionTarget target,
            Dictionary<string, List<int>> tableRowIds,
            int rowIndex,
            ColumnSchema column)
        {
            if (target.ComparisonValue != null)
                return target;

            object? comparisonValue = null;
            if (target.Condition.IsColumnComparison)
            {
                TryResolveSubqueryComparisonValue(
                    scenario,
                    query,
                    target.Condition,
                    tableRowIds,
                    query.AliasToTableMap,
                    out comparisonValue,
                    rowIndex);
            }
            else
            {
                TryResolveScalarSubqueryComparisonValue(query, target.Condition, column, out comparisonValue);
            }

            return comparisonValue == null
                ? target
                : new ColumnConditionTarget(target.Condition, target.DesiredTruth, comparisonValue, target.SubqueryAliasMap);
        }

        private void AddRangeIntersectionCandidates(
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyCollection<ColumnConditionTarget> targets,
            List<object?> candidates)
        {
            if (column.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float) ||
                targets.Count < 2)
            {
                return;
            }

            decimal? lower = null;
            decimal? upper = null;
            var step = GetNumericRangeStep(column);

            foreach (var target in targets.Where(t => t.DesiredTruth))
            {
                var condition = target.Condition;
                if (condition.Operator == ComparisonOp.Between)
                {
                    if (TryResolveConditionNumericBoundary(condition, scenario, query, currentRow, column, tableAlias, useSecondValue: false, out var betweenLower))
                    {
                        lower = MaxNullable(lower, betweenLower);
                    }

                    if (TryResolveConditionNumericBoundary(condition, scenario, query, currentRow, column, tableAlias, useSecondValue: true, out var betweenUpper))
                    {
                        upper = MinNullable(upper, betweenUpper);
                    }

                    continue;
                }

                if (!TryResolveConditionNumericBoundary(condition, scenario, query, currentRow, column, tableAlias, useSecondValue: false, out var boundary))
                    continue;

                switch (condition.Operator)
                {
                    case ComparisonOp.Equal:
                        lower = MaxNullable(lower, boundary);
                        upper = MinNullable(upper, boundary);
                        break;
                    case ComparisonOp.GreaterThan:
                        lower = MaxNullable(lower, boundary + step);
                        break;
                    case ComparisonOp.GreaterThanOrEqual:
                        lower = MaxNullable(lower, boundary);
                        break;
                    case ComparisonOp.LessThan:
                        upper = MinNullable(upper, boundary - step);
                        break;
                    case ComparisonOp.LessThanOrEqual:
                        upper = MinNullable(upper, boundary);
                        break;
                    case ComparisonOp.In:
                        foreach (var value in condition.InValues)
                        {
                            if (TryConvertDecimal(value, out var inValue))
                            {
                                candidates.Add(NormalizeNumericCandidate(column, inValue));
                            }
                        }
                        break;
                }
            }

            if (lower.HasValue)
                candidates.Add(NormalizeNumericCandidate(column, lower.Value));

            if (upper.HasValue)
                candidates.Add(NormalizeNumericCandidate(column, upper.Value));

            if (lower.HasValue && upper.HasValue && lower.Value <= upper.Value)
            {
                candidates.Add(NormalizeNumericCandidate(column, (lower.Value + upper.Value) / 2m));
            }
        }

        private bool TryResolveConditionNumericBoundary(
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            bool useSecondValue,
            out decimal value)
        {
            object? raw = null;

            if (useSecondValue)
            {
                raw = condition.SecondValue;
            }
            else if (condition.RightExpression != null)
            {
                raw = EvaluateScalarExpression(condition.RightExpression, null, scenario, query, currentRow, column, tableAlias, null);
            }
            else if (!string.IsNullOrWhiteSpace(condition.Value))
            {
                raw = condition.Value;
            }

            if (TryConvertDecimal(raw, out value))
                return true;

            value = 0m;
            return false;
        }

        private static decimal GetNumericRangeStep(ColumnSchema column)
        {
            if (column.TypeCategory == DataTypeCategory.Integer)
                return 1m;

            var scale = Math.Min(28, column.NumericScale ?? 0);
            if (scale <= 0)
                return 1m;

            decimal step = 1m;
            for (var i = 0; i < scale; i++)
            {
                step /= 10m;
            }

            return step;
        }

        private static object? NormalizeNumericCandidate(ColumnSchema column, decimal value)
        {
            if (column.TypeCategory == DataTypeCategory.Integer)
            {
                value = decimal.Truncate(value);
            }

            return SqlServerValueNormalizer.NormalizeValue(column, value) ?? value;
        }

        private static decimal MaxNullable(decimal? current, decimal candidate) =>
            current.HasValue ? Math.Max(current.Value, candidate) : candidate;

        private static decimal MinNullable(decimal? current, decimal candidate) =>
            current.HasValue ? Math.Min(current.Value, candidate) : candidate;

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

            foreach (var target in targets.Where(t => t.DesiredTruth && t.Condition.Operator == ComparisonOp.Like))
            {
                if (TryBuildLikePreservingCandidate(
                        column,
                        rowIndex,
                        target.Condition.LikePattern,
                        target.Condition.LikeEscape,
                        targetLength,
                        UseMaxLengthMaxValueMode,
                        out var likeCandidate))
                {
                    candidates.Add(likeCandidate);
                }
            }

            var hints = ExtractStringHints(targets, column);
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

            foreach (var target in targets)
            {
                if (target.Condition.LeftExpression == null) continue;
                var solverValue = target.Condition.Operator == ComparisonOp.Like
                    ? target.Condition.LikePattern
                    : target.Condition.Value;
                var constraintHints = ExpressionConstraintSolver.Solve(
                    target.Condition.LeftExpression,
                    target.Condition.Operator,
                    string.IsNullOrEmpty(solverValue) ? null : solverValue,
                    string.IsNullOrEmpty(target.Condition.SecondValue) ? null : target.Condition.SecondValue,
                    column,
                    tableAlias,
                    target.SubqueryAliasMap);
                if (constraintHints?.HasAnyHint != true) continue;
                var solvedCandidate = constraintHints.BuildCandidate(baseCandidate);
                if (!string.IsNullOrEmpty(solvedCandidate))
                    AddCandidate(solvedCandidate);
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

        private static bool TryBuildLikePreservingCandidate(
            ColumnSchema column,
            int rowIndex,
            string pattern,
            string? escape,
            int targetLength,
            bool exactLength,
            out string candidate)
        {
            candidate = string.Empty;
            if (string.IsNullOrEmpty(pattern) || targetLength <= 0)
                return false;

            var minimal = SqlLikePattern.GenerateMatchingValue(pattern, column, escape);
            if (!exactLength)
            {
                candidate = minimal;
                return SqlLikePattern.IsMatch(candidate, pattern, escape);
            }

            var suffix = TryExtractSimpleLikeSuffix(pattern, escape, out var extractedSuffix)
                ? extractedSuffix
                : string.Empty;
            var prefix = TryExtractSimpleLikePrefix(pattern, escape, out var extractedPrefix)
                ? extractedPrefix
                : string.Empty;
            var contains = TryExtractSimpleLikeContains(pattern, escape, out var extractedContains)
                ? extractedContains
                : string.Empty;
            var rowToken = (rowIndex + 1).ToString("D3");

            IEnumerable<string> BuildCandidates()
            {
                if (IsEmailColumn(column) && !string.IsNullOrWhiteSpace(suffix))
                {
                    var emailSuffix = suffix.Contains('@', StringComparison.Ordinal)
                        ? suffix
                        : "@" + suffix.TrimStart('@');
                    var localLength = targetLength - emailSuffix.Length;
                    if (localLength > 0)
                    {
                        var local = BuildExactLengthAsciiToken(column, rowIndex, localLength);
                        yield return local.ToLowerInvariant() + emailSuffix.ToLowerInvariant();
                    }
                }

                if (!string.IsNullOrEmpty(prefix) && prefix.Length <= targetLength)
                {
                    yield return prefix + BuildExactLengthAsciiToken(column, rowIndex, targetLength - prefix.Length);
                }

                if (!string.IsNullOrEmpty(suffix) && suffix.Length <= targetLength)
                {
                    yield return BuildExactLengthAsciiToken(column, rowIndex, targetLength - suffix.Length) + suffix;
                }

                if (!string.IsNullOrEmpty(contains) && contains.Length <= targetLength)
                {
                    var leftLength = Math.Max(0, (targetLength - contains.Length) / 2);
                    var rightLength = targetLength - contains.Length - leftLength;
                    yield return BuildExactLengthAsciiToken(column, rowIndex, leftLength) +
                                 contains +
                                 BuildExactLengthAsciiToken(column, rowIndex + 17, rightLength);
                }

                if (minimal.Length < targetLength && PatternStartsWithPercentWildcard(pattern, escape))
                {
                    yield return BuildExactLengthAsciiToken(column, rowIndex, targetLength - minimal.Length) + minimal;
                }

                if (minimal.Length < targetLength && PatternEndsWithPercentWildcard(pattern, escape))
                {
                    yield return minimal + BuildExactLengthAsciiToken(column, rowIndex, targetLength - minimal.Length);
                }

                yield return minimal.Length <= targetLength
                    ? minimal
                    : minimal[..targetLength];

                yield return rowToken.Length >= targetLength
                    ? rowToken[^targetLength..]
                    : BuildExactLengthAsciiToken(column, rowIndex, targetLength);
            }

            foreach (var rawCandidate in BuildCandidates())
            {
                var normalizedText = rawCandidate.Length <= targetLength
                    ? rawCandidate
                    : rawCandidate[..targetLength];
                var normalized = SqlServerValueNormalizer.NormalizeValue(column, normalizedText)?.ToString() ?? normalizedText;
                if (SqlLikePattern.IsMatch(normalized, pattern, escape))
                {
                    candidate = normalized;
                    return true;
                }
            }

            return false;
        }

        private static string BuildExactLengthAsciiToken(ColumnSchema column, int rowIndex, int length)
        {
            if (length <= 0)
                return string.Empty;

            var rowToken = (rowIndex + 1).ToString("D3");
            var baseToken = new string(
                $"{column.TableName}{column.ColumnName}{rowToken}"
                    .Where(char.IsLetterOrDigit)
                    .ToArray());
            if (string.IsNullOrEmpty(baseToken))
                baseToken = "Value" + rowToken;

            var builder = new System.Text.StringBuilder(length);
            while (builder.Length < length)
            {
                if (builder.Length > 0)
                    builder.Append('_');
                builder.Append(baseToken);
            }

            return builder.ToString(0, length);
        }

        private static bool TryExtractSimpleLikeSuffix(string pattern, string? escape, out string suffix)
        {
            suffix = string.Empty;
            if (!PatternStartsWithPercentWildcard(pattern, escape))
                return false;

            var raw = pattern[1..];
            if (ContainsUnescapedLikeWildcard(raw, escape))
                return false;

            suffix = UnescapeLikeLiteral(raw, escape);
            return !string.IsNullOrEmpty(suffix);
        }

        private static bool TryExtractSimpleLikePrefix(string pattern, string? escape, out string prefix)
        {
            prefix = string.Empty;
            if (!PatternEndsWithPercentWildcard(pattern, escape))
                return false;

            var raw = pattern[..^1];
            if (ContainsUnescapedLikeWildcard(raw, escape))
                return false;

            prefix = UnescapeLikeLiteral(raw, escape);
            return !string.IsNullOrEmpty(prefix);
        }

        private static bool TryExtractSimpleLikeContains(string pattern, string? escape, out string contains)
        {
            contains = string.Empty;
            if (!PatternStartsWithPercentWildcard(pattern, escape) ||
                !PatternEndsWithPercentWildcard(pattern, escape) ||
                pattern.Length < 3)
            {
                return false;
            }

            var raw = pattern[1..^1];
            if (ContainsUnescapedLikeWildcard(raw, escape))
                return false;

            contains = UnescapeLikeLiteral(raw, escape);
            return !string.IsNullOrEmpty(contains);
        }

        private static bool PatternStartsWithPercentWildcard(string pattern, string? escape) =>
            pattern.Length > 0 &&
            pattern[0] == '%' &&
            !IsEscapedLikePosition(pattern, 0, escape);

        private static bool PatternEndsWithPercentWildcard(string pattern, string? escape) =>
            pattern.Length > 0 &&
            pattern[^1] == '%' &&
            !IsEscapedLikePosition(pattern, pattern.Length - 1, escape);

        private static bool ContainsUnescapedLikeWildcard(string value, string? escape)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if ((value[i] == '%' || value[i] == '_' || value[i] == '[') &&
                    !IsEscapedLikePosition(value, i, escape))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEscapedLikePosition(string value, int index, string? escape)
        {
            if (string.IsNullOrEmpty(escape) || index <= 0)
                return false;

            var escapeChar = escape[0];
            var count = 0;
            for (var i = index - 1; i >= 0 && value[i] == escapeChar; i--)
            {
                count++;
            }

            return count % 2 == 1;
        }

        private static string UnescapeLikeLiteral(string value, string? escape)
        {
            if (string.IsNullOrEmpty(escape))
                return value;

            var escapeChar = escape[0];
            var builder = new System.Text.StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == escapeChar && i + 1 < value.Length)
                {
                    builder.Append(value[++i]);
                    continue;
                }

                builder.Append(value[i]);
            }

            return builder.ToString();
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

        private static List<string> ExtractStringHints(
            IReadOnlyCollection<ColumnConditionTarget> targets,
            ColumnSchema column)
        {
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddHint(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !LooksLikeSqlExpressionLiteral(value))
                {
                    hints.Add(value);
                }
            }

            foreach (var target in targets)
            {
                foreach (var hint in EnumerateStringHints(target.Condition.LeftExpression))
                {
                    AddHint(hint);
                }

                if (target.Condition.Operator == ComparisonOp.Like)
                {
                    if (!string.IsNullOrWhiteSpace(target.Condition.LikePattern))
                    {
                        AddHint(SqlLikePattern.GenerateMatchingValue(
                            target.Condition.LikePattern,
                            column,
                            target.Condition.LikeEscape));
                    }
                }
                else
                {
                    foreach (var hint in EnumerateStringHints(target.Condition.RightExpression))
                    {
                        AddHint(hint);
                    }
                }

                if (target.Condition.Operator != ComparisonOp.Like)
                    AddHint(target.Condition.Value);

                AddHint(target.Condition.SecondValue);

                foreach (var dynamicValue in target.Condition.DynamicStringValues)
                {
                    AddHint(dynamicValue);
                }
            }

            hints.Remove(string.Empty);
            hints.Remove(" ");
            return hints.ToList();
        }

        private static bool LooksLikeSqlExpressionLiteral(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim();
            if (trimmed.StartsWith("N'", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("'", StringComparison.Ordinal))
            {
                return false;
            }

            if (trimmed.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed,
                    @"\b\w+\.\w+\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                @"\b(?:LEFT|RIGHT|LOWER|UPPER|TRIM|LTRIM|RTRIM|SUBSTRING|CHARINDEX|REPLACE|CONCAT|CAST|CONVERT|TRY_CAST|TRY_CONVERT|FORMAT|LEN|ISNULL|COALESCE|NULLIF|DATEADD|DATEDIFF|DATEPART|YEAR|MONTH|DAY)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
                    if (!string.IsNullOrWhiteSpace(text) &&
                        !LooksLikeSqlExpressionLiteral(text))
                    {
                        hints.Add(text);
                    }
                }

                if (target.Condition.Operator == ComparisonOp.Like)
                {
                    if (!string.IsNullOrWhiteSpace(target.Condition.LikePattern))
                    {
                        hints.Add(SqlLikePattern.GenerateMatchingValue(
                            target.Condition.LikePattern,
                            targetColumn,
                            target.Condition.LikeEscape));
                    }

                    continue;
                }

                foreach (var expression in EnumerateHintExpressions(target.Condition.RightExpression, targetColumn, tableAlias))
                {
                    var evaluated = EvaluateScalarExpression(expression, null, scenario, query, currentRow, targetColumn, tableAlias, null);
                    var text = Convert.ToString(evaluated, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text) &&
                        !LooksLikeSqlExpressionLiteral(text))
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
                TryEvaluateExpressionConditionTarget(candidate, target.Condition, scenario, query, currentRow, col, tableAlias, target.SubqueryAliasMap, out var expressionTruth))
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
                CaseScalarExpressionInfo => true,
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
                CaseScalarExpressionInfo caseExpression =>
                    ExpressionReferencesTargetColumn(caseExpression.InputExpression, targetColumn, tableAlias) ||
                    ExpressionReferencesTargetColumn(caseExpression.ElseExpression, targetColumn, tableAlias) ||
                    caseExpression.WhenClauses.Any(w =>
                        PredicateExpressionReferencesTargetColumn(w.Predicate, targetColumn, tableAlias) ||
                        ExpressionReferencesTargetColumn(w.WhenExpression, targetColumn, tableAlias) ||
                        ExpressionReferencesTargetColumn(w.ThenExpression, targetColumn, tableAlias)),
                _ => false
            };
        }

        private static bool PredicateExpressionReferencesTargetColumn(
            PredicateExpression? expression,
            ColumnSchema targetColumn,
            string tableAlias)
        {
            return expression switch
            {
                null => false,
                PredicateLeafExpression leaf => ConditionReferencesTargetColumn(leaf.Condition, targetColumn, tableAlias),
                PredicateBinaryExpression binary =>
                    PredicateExpressionReferencesTargetColumn(binary.Left, targetColumn, tableAlias) ||
                    PredicateExpressionReferencesTargetColumn(binary.Right, targetColumn, tableAlias),
                PredicateNotExpression not => PredicateExpressionReferencesTargetColumn(not.Inner, targetColumn, tableAlias),
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
            IReadOnlyDictionary<string, string>? aliasMap,
            out bool truth)
        {
            truth = false;
            if (condition.IsColumnComparison ||
                condition.LeftExpression == null ||
                !ConditionReferencesTargetColumn(condition, column, tableAlias, aliasMap))
            {
                return false;
            }

            if (condition.DynamicStringValues.Count > 0 &&
                condition.Operator == ComparisonOp.Like &&
                condition.RightExpression != null)
            {
                foreach (var dynamicValue in condition.DynamicStringValues)
                {
                    if (TryEvaluateExpressionConditionTarget(candidate, condition, scenario, query, currentRow, column, tableAlias, aliasMap, dynamicValue, out truth) &&
                        truth)
                    {
                        return true;
                    }
                }

                truth = false;
                return true;
            }

            return TryEvaluateExpressionConditionTarget(candidate, condition, scenario, query, currentRow, column, tableAlias, aliasMap, null, out truth);
        }

        private bool TryEvaluateExpressionConditionTarget(
            object? candidate,
            ConditionInfo condition,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap,
            string? dynamicValue,
            out bool truth)
        {
            truth = false;
            if (condition.LeftExpression == null)
                return false;

            var leftValue = EvaluateScalarExpression(condition.LeftExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue, aliasMap);

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
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue, aliasMap)
                        : condition.LikePattern;
                    truth = EvaluateLike(leftValue?.ToString() ?? string.Empty, patternValue?.ToString() ?? string.Empty, condition.LikeEscape);
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
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue, aliasMap)
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
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, column, tableAlias, dynamicValue, aliasMap)
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
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap = null)
        {
            return condition.ReferencedColumns.Any(r =>
                r.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                TableAliasMatchesTarget(r.TableAlias, tableAlias, column.TableName, aliasMap));
        }

        private static bool TableAliasMatchesTarget(
            string refAlias,
            string tableAlias,
            string tableName,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            if (string.IsNullOrWhiteSpace(refAlias))
                return true;
            if (refAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase))
                return true;
            if (refAlias.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (aliasMap == null)
                return false;
            // Resolve both aliases to table names and compare
            if (!aliasMap.TryGetValue(refAlias, out var refTable))
                return false;
            if (aliasMap.TryGetValue(tableAlias, out var targetTable))
                return refTable.Equals(targetTable, StringComparison.OrdinalIgnoreCase);
            return refTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                   refTable.TrimStart('[').TrimEnd(']').Equals(tableName.TrimStart('[').TrimEnd(']'), StringComparison.OrdinalIgnoreCase);
        }

        private object? EvaluateScalarExpression(
            ScalarExpressionInfo? expression,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue,
            IReadOnlyDictionary<string, string>? aliasMap = null)
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
                        TableAliasMatchesTarget(columnExpr.TableAlias, tableAlias, targetColumn.TableName, aliasMap))
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
                    var operand = EvaluateScalarExpression(unary.Operand, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
                    return unary.Operator switch
                    {
                        "Negative" when TryConvertDecimal(operand, out var decimalValue) => -decimalValue,
                        _ => operand
                    };
                }

                case BinaryScalarExpressionInfo binary:
                {
                    var left = EvaluateScalarExpression(binary.Left, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
                    var right = EvaluateScalarExpression(binary.Right, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
                    return EvaluateBinaryExpression(binary.Operator, left, right);
                }

                case FunctionScalarExpressionInfo func:
                    return EvaluateFunctionExpression(func, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);

                case CaseScalarExpressionInfo caseExpression:
                    return EvaluateCaseExpression(caseExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
            }

            return null;
        }

        private object? EvaluateCaseExpression(
            CaseScalarExpressionInfo caseExpression,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            var inputValue = caseExpression.InputExpression == null
                ? null
                : EvaluateScalarExpression(caseExpression.InputExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);

            foreach (var whenClause in caseExpression.WhenClauses)
            {
                var matched = whenClause.Predicate != null
                    ? EvaluatePredicateExpression(whenClause.Predicate, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap)
                    : CompareExpressionValues(
                        inputValue,
                        EvaluateScalarExpression(whenClause.WhenExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap)) == 0;

                if (matched)
                {
                    return EvaluateScalarExpression(whenClause.ThenExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
                }
            }

            return EvaluateScalarExpression(caseExpression.ElseExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);
        }

        private bool EvaluatePredicateExpression(
            PredicateExpression? expression,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            return expression switch
            {
                null => false,
                PredicateLeafExpression leaf => EvaluatePredicateCondition(leaf.Condition, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap),
                PredicateBinaryExpression binary when binary.Operator == LogicalOp.And =>
                    EvaluatePredicateExpression(binary.Left, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap) &&
                    EvaluatePredicateExpression(binary.Right, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap),
                PredicateBinaryExpression binary when binary.Operator == LogicalOp.Or =>
                    EvaluatePredicateExpression(binary.Left, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap) ||
                    EvaluatePredicateExpression(binary.Right, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap),
                PredicateNotExpression not =>
                    !EvaluatePredicateExpression(not.Inner, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap),
                _ => false
            };
        }

        private bool EvaluatePredicateCondition(
            ConditionInfo condition,
            object? candidate,
            BranchScenario scenario,
            ParsedQuery query,
            GeneratedRow? currentRow,
            ColumnSchema targetColumn,
            string tableAlias,
            string? dynamicValue,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            var leftValue = condition.LeftExpression == null
                ? null
                : EvaluateScalarExpression(condition.LeftExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap);

            switch (condition.Operator)
            {
                case ComparisonOp.IsNull:
                    return leftValue == null;

                case ComparisonOp.IsNotNull:
                    return leftValue != null;

                case ComparisonOp.Like:
                {
                    var patternValue = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap)
                        : condition.LikePattern;
                    var truth = EvaluateLike(leftValue?.ToString() ?? string.Empty, patternValue?.ToString() ?? string.Empty, condition.LikeEscape);
                    return condition.IsNegated ? !truth : truth;
                }

                case ComparisonOp.In:
                    return condition.InValues.Any(v => CompareExpressionValues(leftValue, v) == 0);

                case ComparisonOp.NotIn:
                    return condition.InValues.All(v => CompareExpressionValues(leftValue, v) != 0);

                case ComparisonOp.Between:
                {
                    var lower = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap)
                        : (string.IsNullOrEmpty(condition.Value) ? null : condition.Value);
                    var upper = string.IsNullOrEmpty(condition.SecondValue) ? null : condition.SecondValue;
                    var truth = CompareExpressionValues(leftValue, lower) >= 0 &&
                                CompareExpressionValues(leftValue, upper) <= 0;
                    return condition.IsNegated ? !truth : truth;
                }

                case ComparisonOp.Equal:
                case ComparisonOp.NotEqual:
                case ComparisonOp.GreaterThan:
                case ComparisonOp.GreaterThanOrEqual:
                case ComparisonOp.LessThan:
                case ComparisonOp.LessThanOrEqual:
                {
                    var rightValue = condition.RightExpression != null
                        ? EvaluateScalarExpression(condition.RightExpression, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap)
                        : (string.IsNullOrEmpty(condition.Value) ? null : condition.Value);
                    var comparison = CompareExpressionValues(leftValue, rightValue);
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

                default:
                    return false;
            }
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
            string? dynamicValue,
            IReadOnlyDictionary<string, string>? aliasMap = null)
        {
            var args = function.Arguments
                .Select(a => EvaluateScalarExpression(a, candidate, scenario, query, currentRow, targetColumn, tableAlias, dynamicValue, aliasMap))
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
            var style = args.ElementAtOrDefault(2);
            return ConvertUsingSqlType(value, typeName, tryMode, style);
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

        private static object? ConvertUsingSqlType(object? value, string sqlType, bool tryMode, object? style = null)
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
                        ConvertToSqlString(value, style),
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

        private static string ConvertToSqlString(object value, object? style)
        {
            if (value is DateTimeOffset offset)
            {
                var styleNumber = TryConvertInt(style, out var parsedStyle) ? parsedStyle : (int?)null;
                return FormatSqlDateTimeString(offset.DateTime, styleNumber, offset);
            }

            if (value is DateTime dateTime)
            {
                var styleNumber = TryConvertInt(style, out var parsedStyle) ? parsedStyle : (int?)null;
                return FormatSqlDateTimeString(dateTime, styleNumber, null);
            }

            if (value is TimeSpan timeSpan)
            {
                return timeSpan.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FormatSqlDateTimeString(DateTime value, int? style, DateTimeOffset? offset)
        {
            var dateTime = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            return style switch
            {
                23 => dateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                101 => dateTime.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture),
                102 => dateTime.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture),
                103 => dateTime.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture),
                104 => dateTime.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture),
                105 => dateTime.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture),
                110 => dateTime.ToString("MM-dd-yyyy", System.Globalization.CultureInfo.InvariantCulture),
                111 => dateTime.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture),
                112 => dateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
                120 => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                121 => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                126 => TrimFractionalSeconds(dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)),
                127 when offset.HasValue => TrimFractionalSeconds(offset.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)) + "Z",
                127 => TrimFractionalSeconds(dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)),
                _ => TrimFractionalSeconds(dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture))
            };
        }

        private static string TrimFractionalSeconds(string value)
        {
            var dotIndex = value.LastIndexOf('.');
            if (dotIndex < 0)
                return value;

            var end = value.Length - 1;
            while (end > dotIndex && value[end] == '0')
            {
                end--;
            }

            return end == dotIndex
                ? value[..dotIndex]
                : value[..(end + 1)];
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
                    var like = EvaluateLike(candidate?.ToString() ?? string.Empty, condition.LikePattern, condition.LikeEscape);
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

        private static bool EvaluateLike(string input, string pattern, string? escape)
        {
            return SqlLikePattern.IsMatch(input, pattern, escape);
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

            if (!TryFindScalarComparisonSubquery(query, condition, out _))
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

        private static bool TryFindScalarComparisonSubquery(
            ParsedQuery query,
            ConditionInfo condition,
            out SubqueryInfo subquery)
        {
            subquery = null!;
            if (!IsScalarSubqueryPlaceholder(condition.RightExpression?.Text) &&
                !IsScalarSubqueryPlaceholder(condition.Value))
            {
                return false;
            }

            var normalizedConditionSubquery = condition.RightExpression?.Text;
            subquery = query.Subqueries.FirstOrDefault(s =>
                s.Operator == SubqueryOperator.ScalarComparison &&
                (string.IsNullOrWhiteSpace(normalizedConditionSubquery) ||
                 NormalizeSqlSnippet(s.SubquerySql).Equals(
                     NormalizeSqlSnippet(normalizedConditionSubquery),
                     StringComparison.OrdinalIgnoreCase) ||
                 NormalizeSqlSnippet($"({s.SubquerySql})").Equals(
                     NormalizeSqlSnippet(normalizedConditionSubquery),
                     StringComparison.OrdinalIgnoreCase)))!;

            return subquery != null;
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

        private static bool TryGetDesiredTruthForCondition(
            BranchScenario scenario,
            ConditionInfo condition,
            bool defaultTruth,
            out bool desiredTruth)
        {
            return TryGetDesiredTruthFromAssignments(
                scenario.PredicateTruthMap,
                condition,
                defaultTruth,
                out desiredTruth);
        }

        private static bool TryGetDesiredTruthFromAssignments(
            IReadOnlyDictionary<string, bool>? truthMap,
            ConditionInfo condition,
            bool defaultTruth,
            out bool desiredTruth)
        {
            if (truthMap != null &&
                truthMap.TryGetValue(condition.Key, out desiredTruth))
            {
                return true;
            }

            desiredTruth = defaultTruth;
            return truthMap == null ||
                   truthMap.Count == 0 ||
                   string.IsNullOrWhiteSpace(condition.Key);
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
                    TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) &&
                    desiredTruth &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName));
        }

        private bool HasDirectValuePredicate(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return query.PredicateScopes
                .Where(s => s.Source != ConditionSource.Having)
                .SelectMany(s => s.Conditions)
                .Any(condition =>
                    condition.AggregateFunc == null &&
                    !condition.HasSubquery &&
                    !condition.IsColumnComparison &&
                    !HasScalarSubqueryPlaceholder(condition) &&
                    TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) &&
                    desiredTruth &&
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
                    TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) &&
                    desiredTruth &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName) &&
                    condition.Operator is
                        ComparisonOp.Equal or
                        ComparisonOp.In or
                        ComparisonOp.Between or
                        ComparisonOp.LessThan or
                        ComparisonOp.LessThanOrEqual or
                        ComparisonOp.Like);
        }

        private void ApplyScenarioHavingAggregateComparisonAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            if (!scenario.ExpectedToReturnRows)
                return;

            ApplyScenarioHavingAggregateComparisonAdjustments(query, scenario, schemas, includeComputedTargets: false);
            ApplyScenarioHavingAggregateComparisonAdjustments(query, scenario, schemas, includeComputedTargets: true);
        }

        private void ApplyScenarioHavingAggregateComparisonAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas,
            bool includeComputedTargets)
        {
            if (!scenario.ExpectedToReturnRows)
                return;

            foreach (var condition in EnumerateAggregateComparisonConditions(query))
            {
                if (!condition.AggregateFunc.HasValue ||
                    condition.AggregateFunc is AggregateFunction.Count or AggregateFunction.CountDistinct ||
                    string.IsNullOrWhiteSpace(condition.ColumnName) ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth)
                {
                    continue;
                }

                var tableAlias = ResolveConditionTargetAlias(condition);
                var columnName = condition.ColumnName;
                ResolveDerivedColumnReference(query, ref tableAlias, ref columnName);

                if (!TryResolveTableForColumn(query, schemas, tableAlias, columnName, out var tableName) ||
                    !schemas.TryGetValue(tableName, out var schema) ||
                    !scenario.TableRows.TryGetValue(tableName, out var rows) ||
                    rows.Count == 0)
                {
                    continue;
                }

                tableAlias = string.IsNullOrWhiteSpace(tableAlias) ? tableName : tableAlias;
                var column = schema.GetColumn(columnName);
                if (column == null ||
                    column.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float))
                {
                    continue;
                }

                if (TryBuildComputedProductColumnPlan(schema, column, out var computedPlan))
                {
                    if (!includeComputedTargets)
                    {
                        continue;
                    }

                    ApplyComputedProductHavingAggregateCondition(
                        query,
                        scenario,
                        tableName,
                        tableAlias,
                        schema,
                        rows,
                        condition,
                        computedPlan);
                    continue;
                }

                if (includeComputedTargets)
                {
                    continue;
                }

                ApplyDirectHavingAggregateCondition(
                    query,
                    scenario,
                    tableName,
                    tableAlias,
                    schema,
                    rows,
                    condition,
                    column);
            }
        }

        private static IEnumerable<ConditionInfo> EnumerateAggregateComparisonConditions(ParsedQuery query)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Where(c => c.AggregateFunc.HasValue);
        }

        private void ApplyDirectHavingAggregateCondition(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            TableSchema schema,
            List<GeneratedRow> rows,
            ConditionInfo condition,
            ColumnSchema column)
        {
            if (TryResolveConditionNumericBoundary(
                    condition,
                    scenario,
                    query,
                    null,
                    column,
                    tableAlias,
                    false,
                    out var boundary) &&
                ExistingHavingAggregateConditionSatisfied(condition, rows, column, computedPlan: null, boundary))
            {
                return;
            }

            if (!CanAdjustComparisonColumn(schema, column) ||
                !TryBuildDirectHavingAggregateValue(
                    query,
                    scenario,
                    tableName,
                    tableAlias,
                    rows,
                    condition,
                    column,
                    out var value))
            {
                return;
            }

            foreach (var row in rows)
            {
                row.SetValue(column.ColumnName, value);
            }
        }

        private bool TryBuildDirectHavingAggregateValue(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            List<GeneratedRow> rows,
            ConditionInfo condition,
            ColumnSchema column,
            out object? value)
        {
            value = null;
            if (!TryResolveConditionNumericBoundary(
                    condition,
                    scenario,
                    query,
                    null,
                    column,
                    tableAlias,
                    false,
                    out var boundary))
            {
                return false;
            }

            var rowCount = Math.Max(1, rows.Count);
            var candidates = new List<decimal>();
            candidates.AddRange(BuildHavingAggregateMemberCandidates(condition, column, rowCount, boundary));
            foreach (var row in rows)
            {
                if (TryConvertDecimal(row.GetValue(column.ColumnName), out var currentValue))
                {
                    candidates.Add(currentValue);
                }
            }

            candidates.AddRange(BuildPositiveNumericColumnCandidates(query, scenario, tableName, tableAlias, column));

            foreach (var candidate in DeduplicateDecimalCandidates(candidates))
            {
                if (!TryNormalizeNumericCandidate(column, candidate, out var normalized, out var normalizedDecimal) ||
                    !EvaluateHavingAggregateProjection(condition, normalizedDecimal, rowCount, boundary) ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, column, normalized, condition.Key))
                {
                    continue;
                }

                value = normalized;
                return true;
            }

            return false;
        }

        private void ApplyComputedProductHavingAggregateCondition(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            TableSchema schema,
            List<GeneratedRow> rows,
            ConditionInfo condition,
            ComputedProductColumnPlan plan)
        {
            if (!CanAdjustComparisonColumn(schema, plan.AnchorColumn) ||
                !CanAdjustComparisonColumn(schema, plan.AdjustableColumn) ||
                !TryResolveConditionNumericBoundary(
                    condition,
                    scenario,
                    query,
                    null,
                    plan.ResultColumn,
                    tableAlias,
                    false,
                    out var boundary))
            {
                return;
            }

            if (ExistingHavingAggregateConditionSatisfied(condition, rows, plan.ResultColumn, plan, boundary))
            {
                return;
            }

            var rowCount = Math.Max(1, rows.Count);
            foreach (var row in rows)
            {
                if (!TryBuildComputedProductHavingAggregateValues(
                        query,
                        scenario,
                        tableName,
                        tableAlias,
                        row,
                        rowCount,
                        condition,
                        plan,
                        boundary,
                        out var anchorValue,
                        out var adjustableValue,
                        out var resultValue))
                {
                    continue;
                }

                row.SetValue(plan.AnchorColumn.ColumnName, anchorValue);
                row.SetValue(plan.AdjustableColumn.ColumnName, adjustableValue);

                // Virtual value for preview/verification. SQL Server recomputes computed columns on insert.
                row.SetValue(plan.ResultColumn.ColumnName, resultValue);
            }
        }

        private bool TryBuildComputedProductHavingAggregateValues(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            GeneratedRow row,
            int rowCount,
            ConditionInfo condition,
            ComputedProductColumnPlan plan,
            decimal boundary,
            out object? anchorValue,
            out object? adjustableValue,
            out object? resultValue)
        {
            anchorValue = null;
            adjustableValue = null;
            resultValue = null;

            var targetMemberCandidates = BuildHavingAggregateMemberCandidates(condition, plan.ResultColumn, rowCount, boundary)
                .ToList();
            var anchorCandidates = BuildAggregateAdjustmentCandidates(
                query,
                scenario,
                tableName,
                tableAlias,
                plan.AnchorColumn,
                row.GetValue(plan.AnchorColumn.ColumnName),
                additionalCandidates: Enumerable.Empty<decimal>());
            var baseAdjustableCandidates = BuildAggregateAdjustmentCandidates(
                query,
                scenario,
                tableName,
                tableAlias,
                plan.AdjustableColumn,
                row.GetValue(plan.AdjustableColumn.ColumnName),
                additionalCandidates: targetMemberCandidates);

            foreach (var rawAnchor in anchorCandidates)
            {
                if (!TryNormalizeNumericCandidate(plan.AnchorColumn, rawAnchor, out var normalizedAnchor, out var anchorDecimal) ||
                    anchorDecimal == 0m ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, plan.AnchorColumn, normalizedAnchor))
                {
                    continue;
                }

                var adjustableCandidates = new List<decimal>(baseAdjustableCandidates);
                foreach (var targetMember in targetMemberCandidates)
                {
                    adjustableCandidates.Add(targetMember / anchorDecimal);
                    adjustableCandidates.Add((targetMember + GetNumericRangeStep(plan.ResultColumn)) / anchorDecimal);
                }

                foreach (var rawAdjustable in DeduplicateDecimalCandidates(adjustableCandidates))
                {
                    if (!TryNormalizeNumericCandidate(plan.AdjustableColumn, rawAdjustable, out var normalizedAdjustable, out var adjustableDecimal) ||
                        !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, plan.AdjustableColumn, normalizedAdjustable))
                    {
                        continue;
                    }

                    if (TryGetPositiveColumnMax(plan.ResultColumn, out var resultMax) &&
                        !IsComputedProductWithinRange(anchorDecimal, adjustableDecimal, resultMax))
                    {
                        continue;
                    }

                    var rawResult = anchorDecimal * adjustableDecimal;
                    if (!TryNormalizeNumericCandidate(plan.ResultColumn, rawResult, out var normalizedResult, out var resultDecimal) ||
                        !EvaluateHavingAggregateProjection(condition, resultDecimal, rowCount, boundary) ||
                        !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, plan.ResultColumn, normalizedResult, condition.Key))
                    {
                        continue;
                    }

                    anchorValue = normalizedAnchor;
                    adjustableValue = normalizedAdjustable;
                    resultValue = normalizedResult;
                    return true;
                }
            }

            return false;
        }

        private static bool ExistingHavingAggregateConditionSatisfied(
            ConditionInfo condition,
            IEnumerable<GeneratedRow> rows,
            ColumnSchema column,
            ComputedProductColumnPlan? computedPlan,
            decimal boundary)
        {
            var values = new List<decimal>();
            foreach (var row in rows)
            {
                object? rawValue = null;
                if (computedPlan != null &&
                    TryConvertDecimal(row.GetValue(computedPlan.AnchorColumn.ColumnName), out var anchorValue) &&
                    TryConvertDecimal(row.GetValue(computedPlan.AdjustableColumn.ColumnName), out var adjustableValue))
                {
                    rawValue = anchorValue * adjustableValue;
                }
                else
                {
                    rawValue = row.GetValue(column.ColumnName);
                }

                if (TryConvertDecimal(rawValue, out var decimalValue))
                {
                    values.Add(decimalValue);
                }
            }

            if (values.Count == 0)
                return false;

            if (condition.AggregateFunc == AggregateFunction.Sum &&
                condition.Operator is ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual)
            {
                return values.All(value => EvaluateNumericComparison(value, condition.Operator, boundary));
            }

            var aggregateValue = condition.AggregateFunc switch
            {
                AggregateFunction.Sum => values.Sum(),
                AggregateFunction.Avg => values.Average(),
                AggregateFunction.Max => values.Max(),
                AggregateFunction.Min => values.Min(),
                _ => values.First()
            };

            return EvaluateNumericComparison(aggregateValue, condition.Operator, boundary);
        }

        private IEnumerable<decimal> BuildAggregateAdjustmentCandidates(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column,
            object? currentValue,
            IEnumerable<decimal> additionalCandidates)
        {
            var candidates = new List<decimal>();
            if (TryConvertDecimal(currentValue, out var currentDecimal))
            {
                candidates.Add(currentDecimal);
            }

            candidates.AddRange(additionalCandidates);
            candidates.AddRange(BuildPositiveNumericColumnCandidates(query, scenario, tableName, tableAlias, column));
            return DeduplicateDecimalCandidates(candidates);
        }

        private static IEnumerable<decimal> BuildHavingAggregateMemberCandidates(
            ConditionInfo condition,
            ColumnSchema column,
            int rowCount,
            decimal boundary)
        {
            var count = Math.Max(1, rowCount);
            var step = GetNumericRangeStep(column);
            var candidates = new List<decimal>();

            void AddStandardComparisonCandidates(decimal baseBoundary)
            {
                switch (condition.Operator)
                {
                    case ComparisonOp.GreaterThan:
                        candidates.Add(baseBoundary + step);
                        break;
                    case ComparisonOp.GreaterThanOrEqual:
                        candidates.Add(baseBoundary);
                        candidates.Add(baseBoundary + step);
                        break;
                    case ComparisonOp.LessThan:
                        candidates.Add(baseBoundary - step);
                        break;
                    case ComparisonOp.LessThanOrEqual:
                        candidates.Add(baseBoundary);
                        candidates.Add(baseBoundary - step);
                        break;
                    case ComparisonOp.Equal:
                        candidates.Add(baseBoundary);
                        break;
                    case ComparisonOp.NotEqual:
                        candidates.Add(baseBoundary + step);
                        candidates.Add(baseBoundary - step);
                        break;
                }
            }

            if (condition.AggregateFunc == AggregateFunction.Sum)
            {
                AddStandardComparisonCandidates(boundary / count);
                AddStandardComparisonCandidates(boundary);
            }
            else
            {
                AddStandardComparisonCandidates(boundary);
            }

            candidates.AddRange(new[] { 1m, 2m, 5m, 10m, 100m, 1000m, 100000m, 500000m, 1000000m, 15050000m });
            return DeduplicateDecimalCandidates(candidates);
        }

        private static bool EvaluateHavingAggregateProjection(
            ConditionInfo condition,
            decimal memberValue,
            int rowCount,
            decimal boundary)
        {
            var aggregateValue = condition.AggregateFunc switch
            {
                AggregateFunction.Sum when condition.Operator is ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual or ComparisonOp.NotEqual => memberValue,
                AggregateFunction.Sum => memberValue * Math.Max(1, rowCount),
                AggregateFunction.Avg => memberValue,
                AggregateFunction.Max => memberValue,
                AggregateFunction.Min => memberValue,
                _ => memberValue
            };

            return EvaluateNumericComparison(aggregateValue, condition.Operator, boundary);
        }

        private static bool EvaluateNumericComparison(decimal left, ComparisonOp op, decimal right) =>
            op switch
            {
                ComparisonOp.Equal => left == right,
                ComparisonOp.NotEqual => left != right,
                ComparisonOp.GreaterThan => left > right,
                ComparisonOp.GreaterThanOrEqual => left >= right,
                ComparisonOp.LessThan => left < right,
                ComparisonOp.LessThanOrEqual => left <= right,
                _ => true
            };

        private void ApplyScenarioScalarAggregateComparisonAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            foreach (var condition in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if (!TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth))
                {
                    continue;
                }

                if (!scenario.ExpectedToReturnRows && desiredTruth)
                {
                    continue;
                }

                var buildSuccess = TryBuildScalarAverageComparisonTarget(query, condition, desiredTruth, schemas, out var target);
                if (scenario.Name.Contains("mixed", StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"[debug:loop] buildSuccess={buildSuccess} cond={condition.ColumnName} truth={desiredTruth}");
                if (!buildSuccess) continue;
                ApplyScalarAverageComparisonTarget(query, scenario, target);
            }
        }

        private bool TryBuildScalarAverageComparisonTarget(
            ParsedQuery query,
            ConditionInfo condition,
            bool desiredTruth,
            Dictionary<string, TableSchema> schemas,
            out ScalarAverageComparisonTarget target)
        {
            target = null!;

            if (condition.Operator is not (
                    ComparisonOp.GreaterThan or
                    ComparisonOp.GreaterThanOrEqual or
                    ComparisonOp.LessThan or
                    ComparisonOp.LessThanOrEqual or
                    ComparisonOp.NotEqual) ||
                string.IsNullOrWhiteSpace(condition.ColumnName) ||
                !TryFindScalarComparisonSubquery(query, condition, out var subquery) ||
                !IsSupportedScalarComparisonAggregateSubquery(subquery) ||
                string.IsNullOrWhiteSpace(subquery.SelectColumn))
            {
                return false;
            }

            var targetAlias = ResolveConditionTargetAlias(condition);
            var targetColumnName = condition.ColumnName;
            ResolveDerivedColumnReference(query, ref targetAlias, ref targetColumnName);
            if (!TryResolveTableForColumn(query, schemas, targetAlias, targetColumnName, out var targetTableName) ||
                !schemas.TryGetValue(targetTableName, out var targetSchema))
            {
                return false;
            }

            targetAlias = string.IsNullOrWhiteSpace(targetAlias) ? targetTableName : targetAlias;
            var targetColumn = targetSchema.GetColumn(targetColumnName);
            var targetComputedPlan = targetColumn != null &&
                                     TryBuildComputedProductColumnPlan(targetSchema, targetColumn, out var resolvedTargetPlan)
                ? resolvedTargetPlan
                : null;
            if (targetColumn == null ||
                (!CanAdjustScalarAggregateColumn(targetSchema, targetColumn) && targetComputedPlan == null))
            {
                return false;
            }

            if (!TryResolveSubquerySelectColumn(query, schemas, subquery, out var aggregateTableName, out var aggregateAlias, out var aggregateColumnName) ||
                !schemas.TryGetValue(aggregateTableName, out var aggregateSchema))
            {
                return false;
            }

            var aggregateColumn = aggregateSchema.GetColumn(aggregateColumnName);
            var aggregateComputedPlan = aggregateColumn != null &&
                                         TryBuildComputedProductColumnPlan(aggregateSchema, aggregateColumn, out var resolvedAggregatePlan)
                ? resolvedAggregatePlan
                : null;
            if (aggregateColumn == null ||
                (!CanAdjustScalarAggregateColumn(aggregateSchema, aggregateColumn) && aggregateComputedPlan == null))
            {
                return false;
            }

            if (!TryResolveScalarAverageTargetOrdering(condition.Operator, desiredTruth, out var targetShouldBeHigher))
            {
                return false;
            }

            target = new ScalarAverageComparisonTarget(
                condition,
                subquery,
                targetTableName,
                targetAlias,
                targetSchema,
                targetColumn,
                aggregateTableName,
                aggregateAlias,
                aggregateSchema,
                aggregateColumn,
                targetComputedPlan,
                aggregateComputedPlan,
                desiredTruth,
                targetShouldBeHigher);
            return true;
        }

        private static bool IsSupportedScalarComparisonAggregateSubquery(SubqueryInfo subquery)
        {
            return ContainsAggregateFunctionCall(subquery.SubquerySql, "AVG") ||
                   ContainsAggregateFunctionCall(subquery.SubquerySql, "MIN") ||
                   ContainsAggregateFunctionCall(subquery.SubquerySql, "MAX");
        }

        private static bool ContainsAggregateFunctionCall(string? sql, string functionName)
        {
            if (string.IsNullOrWhiteSpace(sql) ||
                string.IsNullOrWhiteSpace(functionName))
            {
                return false;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                sql,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(functionName)}\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool TryResolveScalarAverageTargetOrdering(
            ComparisonOp op,
            bool desiredTruth,
            out bool targetShouldBeHigher)
        {
            targetShouldBeHigher = true;
            switch (op)
            {
                case ComparisonOp.GreaterThan:
                case ComparisonOp.GreaterThanOrEqual:
                    targetShouldBeHigher = desiredTruth;
                    return true;

                case ComparisonOp.LessThan:
                case ComparisonOp.LessThanOrEqual:
                    targetShouldBeHigher = !desiredTruth;
                    return true;

                case ComparisonOp.NotEqual:
                    targetShouldBeHigher = true;
                    return desiredTruth;

                default:
                    return false;
            }
        }

        private void ApplyScalarAverageComparisonTarget(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target)
        {
            if (!scenario.TableRows.TryGetValue(target.TargetTableName, out var targetRows) ||
                targetRows.Count == 0 ||
                !scenario.TableRows.TryGetValue(target.AggregateTableName, out var aggregateRows) ||
                aggregateRows.Count == 0)
            {
                return;
            }

            var sameTable = target.TargetTableName.Equals(target.AggregateTableName, StringComparison.OrdinalIgnoreCase);
            var sameColumn = target.TargetColumn.ColumnName.Equals(target.AggregateColumn.ColumnName, StringComparison.OrdinalIgnoreCase);
            if (sameTable && sameColumn && aggregateRows.Count < 2)
                return;

            if (target.TargetComputedPlan != null || target.AggregateComputedPlan != null)
            {
                ApplyComputedScalarAverageComparisonTarget(query, scenario, target, targetRows, aggregateRows, sameTable, sameColumn);
                return;
            }

            var targetRow = targetRows.FirstOrDefault(r => r.GetValue(target.TargetColumn.ColumnName) != null) ??
                            targetRows[0];

            if (!TryBuildScalarAverageValuePair(
                    query,
                    scenario,
                    target,
                    targetRow,
                    out var targetValue,
                    out var targetDecimal,
                    out var supportValue,
                    out var supportDecimal))
            {
                return;
            }

            // Remove early return to ensure global extreme values are applied

            if (sameTable && sameColumn)
            {
                if (scenario.ExpectedToReturnRows &&
                    GetRequestedRowCount() > 1 &&
                    targetRows.Count >= GetRequestedRowCount())
                {
                    ApplyMultiRowScalarAverageComparisonTarget(
                        target,
                        targetRows,
                        targetValue,
                        supportValue);
                    return;
                }

                var useUniformFalseValue = ShouldUseUniformFalseScalarAverageValue(target);
                foreach (var row in aggregateRows)
                {
                    row.SetValue(
                        target.AggregateColumn.ColumnName,
                        ReferenceEquals(row, targetRow) || useUniformFalseValue ? targetValue : supportValue);
                }

                return;
            }

            targetRow.SetValue(target.TargetColumn.ColumnName, targetValue);
            foreach (var row in aggregateRows)
            {
                row.SetValue(target.AggregateColumn.ColumnName, supportValue);
            }
        }

        private void ApplyMultiRowScalarAverageComparisonTarget(
            ScalarAverageComparisonTarget target,
            List<GeneratedRow> targetRows,
            object? targetValue,
            object? supportValue)
        {
            var requestedRows = GetRequestedRowCount();
            var targetSnapshot = targetRows
                .Take(requestedRows)
                .ToList();

            if (targetSnapshot.Count == 0)
                return;

            foreach (var row in targetSnapshot)
            {
                row.SetValue(target.TargetColumn.ColumnName, targetValue);
            }

            var supportRow = CloneGeneratedRow(targetSnapshot[0]);
            AssignDistinctGeneratedKey(target.TargetSchema, supportRow, targetRows);
            supportRow.SetValue(target.AggregateColumn.ColumnName, supportValue);
            PushSupportRowBehindOrderedTargets(target.TargetSchema, supportRow, targetSnapshot);
            targetRows.Add(supportRow);
        }

        private GeneratedRow CloneGeneratedRow(GeneratedRow source)
        {
            var clone = new GeneratedRow { TableName = source.TableName };
            foreach (var kvp in source.ColumnValues)
            {
                clone.SetValue(kvp.Key, kvp.Value);
            }

            return clone;
        }

        private void AssignDistinctGeneratedKey(
            TableSchema schema,
            GeneratedRow row,
            IReadOnlyList<GeneratedRow> existingRows)
        {
            var keyColumn = ResolveNumericKeyColumn(schema);
            if (keyColumn != null)
            {
                var maxExisting = existingRows
                    .Select(r => r.GetValue(keyColumn.ColumnName))
                    .Where(v => v != null && v != DBNull.Value)
                    .Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))
                    .Where(v => decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    .Select(v => decimal.Parse(v!, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture))
                    .DefaultIfEmpty(0m)
                    .Max();

                var nextValue = maxExisting + 1m;
                row.SetValue(
                    keyColumn.ColumnName,
                    SqlServerValueNormalizer.NormalizeValue(keyColumn, nextValue) ?? nextValue);
                return;
            }

            var guidKey = schema.Columns.FirstOrDefault(c =>
                (c.IsIdentity || c.IsPrimaryKey || schema.PrimaryKey?.Columns.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase) == true) &&
                c.TypeCategory == DataTypeCategory.Guid);
            if (guidKey != null)
            {
                row.SetValue(guidKey.ColumnName, Guid.NewGuid());
                return;
            }

            var stringKey = schema.Columns.FirstOrDefault(c =>
                (c.IsIdentity || c.IsPrimaryKey || schema.PrimaryKey?.Columns.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase) == true) &&
                c.TypeCategory == DataTypeCategory.String);
            if (stringKey != null)
            {
                row.SetValue(
                    stringKey.ColumnName,
                    BuildSemanticString(stringKey, existingRows.Count + 1, null));
            }
        }

        private static void PushSupportRowBehindOrderedTargets(
            TableSchema schema,
            GeneratedRow supportRow,
            IReadOnlyList<GeneratedRow> targetRows)
        {
            var orderDateColumn = schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset &&
                    c.ColumnName.Contains("OrderDate", StringComparison.OrdinalIgnoreCase)) ??
                schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset &&
                    c.ColumnName.Contains("Date", StringComparison.OrdinalIgnoreCase));

            if (orderDateColumn == null)
                return;

            var existingDates = targetRows
                .Select(r => TryReadDateTime(r.GetValue(orderDateColumn.ColumnName), out var value) ? value : (DateTime?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (existingDates.Count == 0)
                return;

            var supportDate = existingDates.Min().AddDays(-1);
            supportRow.SetValue(
                orderDateColumn.ColumnName,
                SqlServerValueNormalizer.NormalizeValue(orderDateColumn, supportDate) ?? supportDate);
        }

        private static bool TryReadDateTime(object? rawValue, out DateTime value)
        {
            value = default;
            if (rawValue == null || rawValue == DBNull.Value)
                return false;

            switch (rawValue)
            {
                case DateTime dateTime:
                    value = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
                    return true;
                case DateTimeOffset dateTimeOffset:
                    value = DateTime.SpecifyKind(dateTimeOffset.DateTime, DateTimeKind.Unspecified);
                    return true;
                default:
                    return DateTime.TryParse(
                        Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out value);
            }
        }

        private void ApplyComputedScalarAverageComparisonTarget(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            List<GeneratedRow> targetRows,
            List<GeneratedRow> aggregateRows,
            bool sameTable,
            bool sameColumn)
        {
            if (target.TargetComputedPlan != null &&
                target.AggregateComputedPlan == null &&
                !sameTable)
            {
                ApplyComputedTargetToScalarAggregateComparison(
                    query,
                    scenario,
                    target,
                    targetRows,
                    aggregateRows);
                return;
            }

            if (target.TargetComputedPlan == null ||
                target.AggregateComputedPlan == null ||
                !sameTable ||
                !sameColumn)
            {
                if (scenario.Name.Contains("mixed", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"[debug:adjust] return1: plan={target.TargetComputedPlan != null} sameTable={sameTable} sameColumn={sameColumn}");
                return;
            }

            var targetRow = targetRows[0];
            if (!TryBuildComputedScalarAverageValuePair(
                    query,
                    scenario,
                    target,
                    target.TargetComputedPlan,
                    out var targetAnchorValue,
                    out var targetAdjustValue,
                    out var targetComputedValue,
                    out var targetComputedDecimal,
                    out var supportAnchorValue,
                    out var supportAdjustValue,
                    out var supportComputedValue,
                    out var supportComputedDecimal))
            {
                if (scenario.Name.Contains("mixed", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"[debug:adjust] return2: targetRows={targetRows.Count}");
                return;
            }

            // Remove early return to ensure global extreme values are applied

            var useUniformFalseValue = ShouldUseUniformFalseScalarAverageValue(target);
            foreach (var row in aggregateRows)
            {
                var isTargetRow = ReferenceEquals(row, targetRow);
                row.SetValue(
                    target.TargetComputedPlan.AnchorColumn.ColumnName,
                    isTargetRow || useUniformFalseValue ? targetAnchorValue : supportAnchorValue);
                row.SetValue(
                    target.TargetComputedPlan.AdjustableColumn.ColumnName,
                    isTargetRow || useUniformFalseValue ? targetAdjustValue : supportAdjustValue);

                // Virtual value for validation/preview; SQL Server recomputes it on insert.
                row.SetValue(
                    target.TargetComputedPlan.ResultColumn.ColumnName,
                    isTargetRow || useUniformFalseValue ? targetComputedValue : supportComputedValue);
            }
        }

        private void ApplyComputedTargetToScalarAggregateComparison(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            List<GeneratedRow> targetRows,
            List<GeneratedRow> aggregateRows)
        {
            if (target.TargetComputedPlan == null ||
                targetRows.Count == 0 ||
                aggregateRows.Count == 0)
            {
                return;
            }

            var targetRow = targetRows[0];
            if (!TryBuildComputedTargetToScalarAggregateValuePair(
                    query,
                    scenario,
                    target,
                    target.TargetComputedPlan,
                    out var targetAnchorValue,
                    out var targetAdjustValue,
                    out var targetComputedValue,
                    out _,
                    out var aggregateValue,
                    out _))
            {
                return;
            }

            targetRow.SetValue(target.TargetComputedPlan.AnchorColumn.ColumnName, targetAnchorValue);
            targetRow.SetValue(target.TargetComputedPlan.AdjustableColumn.ColumnName, targetAdjustValue);
            targetRow.SetValue(target.TargetComputedPlan.ResultColumn.ColumnName, targetComputedValue);

            foreach (var row in aggregateRows)
            {
                row.SetValue(target.AggregateColumn.ColumnName, aggregateValue);
            }
        }

        private bool TryBuildComputedTargetToScalarAggregateValuePair(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            ComputedProductColumnPlan plan,
            out object? targetAnchorValue,
            out object? targetAdjustValue,
            out object? targetComputedValue,
            out decimal targetComputedDecimal,
            out object? aggregateValue,
            out decimal aggregateDecimal)
        {
            targetAnchorValue = null;
            targetAdjustValue = null;
            targetComputedValue = null;
            targetComputedDecimal = 0m;
            aggregateValue = null;
            aggregateDecimal = 0m;

            var anchorCandidates = BuildPositiveNumericColumnCandidates(
                    query,
                    scenario,
                    target.TargetTableName,
                    target.TargetAlias,
                    plan.AnchorColumn)
                .Where(v => v > 0m);
            var adjustCandidates = BuildPositiveNumericColumnCandidates(
                query,
                scenario,
                target.TargetTableName,
                target.TargetAlias,
                plan.AdjustableColumn);

            anchorCandidates = target.TargetShouldBeHigher
                ? anchorCandidates.OrderByDescending(v => v)
                : anchorCandidates.OrderBy(v => v);
            adjustCandidates = target.TargetShouldBeHigher
                ? adjustCandidates.OrderByDescending(v => v)
                : adjustCandidates.OrderBy(v => v);

            foreach (var rawAnchor in anchorCandidates)
            {
                if (!TryNormalizeNumericCandidate(plan.AnchorColumn, rawAnchor, out var normalizedAnchor, out var anchorDecimal) ||
                    anchorDecimal <= 0m ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, target.TargetTableName, target.TargetAlias, plan.AnchorColumn, normalizedAnchor))
                {
                    continue;
                }

                foreach (var rawAdjust in adjustCandidates)
                {
                    if (!TryNormalizeNumericCandidate(plan.AdjustableColumn, rawAdjust, out var normalizedAdjust, out var adjustDecimal) ||
                        !SatisfiesPositiveColumnConstraints(query, scenario, target.TargetTableName, target.TargetAlias, plan.AdjustableColumn, normalizedAdjust))
                    {
                        continue;
                    }

                    var rawComputed = anchorDecimal * adjustDecimal;
                    if (!TryNormalizeNumericCandidate(plan.ResultColumn, rawComputed, out var normalizedComputed, out var normalizedComputedDecimal))
                    {
                        continue;
                    }

                    var supportCandidates = BuildScalarAverageSupportCandidates(
                            target.AggregateColumn,
                            normalizedComputedDecimal,
                            target.TargetShouldBeHigher)
                        .Concat(BuildPositiveNumericColumnCandidates(
                            query,
                            scenario,
                            target.AggregateTableName,
                            target.AggregateAlias,
                            target.AggregateColumn));
                    supportCandidates = target.TargetShouldBeHigher
                        ? supportCandidates.OrderBy(v => v)
                        : supportCandidates.OrderByDescending(v => v);

                    foreach (var rawAggregate in supportCandidates)
                    {
                        if (!TryNormalizeNumericCandidate(target.AggregateColumn, rawAggregate, out var normalizedAggregate, out var normalizedAggregateDecimal) ||
                            !SatisfiesScalarAverageSupportSideConstraints(query, scenario, target, normalizedAggregate))
                        {
                            continue;
                        }

                        var relationSatisfied = target.TargetShouldBeHigher
                            ? normalizedComputedDecimal > normalizedAggregateDecimal
                            : normalizedComputedDecimal < normalizedAggregateDecimal;
                        if (!relationSatisfied)
                            continue;

                        targetAnchorValue = normalizedAnchor;
                        targetAdjustValue = normalizedAdjust;
                        targetComputedValue = normalizedComputed;
                        targetComputedDecimal = normalizedComputedDecimal;
                        aggregateValue = normalizedAggregate;
                        aggregateDecimal = normalizedAggregateDecimal;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ShouldUseUniformFalseScalarAverageValue(ScalarAverageComparisonTarget target)
        {
            return !target.DesiredTruth &&
                   target.Condition.Operator is ComparisonOp.GreaterThan or ComparisonOp.LessThan;
        }

        private bool TryBuildComputedScalarAverageValuePair(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            ComputedProductColumnPlan plan,
            out object? targetAnchorValue,
            out object? targetAdjustValue,
            out object? targetComputedValue,
            out decimal targetComputedDecimal,
            out object? supportAnchorValue,
            out object? supportAdjustValue,
            out object? supportComputedValue,
            out decimal supportComputedDecimal)
        {
            targetAnchorValue = null;
            targetAdjustValue = null;
            targetComputedValue = null;
            targetComputedDecimal = 0m;
            supportAnchorValue = null;
            supportAdjustValue = null;
            supportComputedValue = null;
            supportComputedDecimal = 0m;

            var anchorCandidates = BuildPositiveNumericColumnCandidates(
                    query,
                    scenario,
                    target.TargetTableName,
                    target.TargetAlias,
                    plan.AnchorColumn)
                .Where(v => v > 0m)
                .ToList();
            var adjustCandidates = BuildPositiveNumericColumnCandidates(
                    query,
                    scenario,
                    target.TargetTableName,
                    target.TargetAlias,
                    plan.AdjustableColumn)
                .ToList();

            foreach (var anchorCandidate in anchorCandidates)
            {
                if (!TryNormalizeNumericCandidate(plan.AnchorColumn, anchorCandidate, out var normalizedAnchor, out var anchorDecimal) ||
                    anchorDecimal <= 0m ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, target.TargetTableName, target.TargetAlias, plan.AnchorColumn, normalizedAnchor))
                {
                    continue;
                }

                var orderedTargetAdjustCandidates = target.TargetShouldBeHigher
                    ? adjustCandidates.OrderByDescending(v => v)
                    : adjustCandidates.OrderBy(v => v);
                var orderedSupportAdjustCandidates = target.TargetShouldBeHigher
                    ? adjustCandidates.OrderBy(v => v)
                    : adjustCandidates.OrderByDescending(v => v);

                foreach (var rawTargetAdjust in orderedTargetAdjustCandidates)
                {
                    if (!TryNormalizeNumericCandidate(plan.AdjustableColumn, rawTargetAdjust, out var normalizedTargetAdjust, out var targetAdjustDecimal) ||
                        !SatisfiesPositiveColumnConstraints(query, scenario, target.TargetTableName, target.TargetAlias, plan.AdjustableColumn, normalizedTargetAdjust))
                    {
                        continue;
                    }

                    foreach (var rawSupportAdjust in orderedSupportAdjustCandidates)
                    {
                        if (!TryNormalizeNumericCandidate(plan.AdjustableColumn, rawSupportAdjust, out var normalizedSupportAdjust, out var supportAdjustDecimal) ||
                            !SatisfiesPositiveColumnConstraints(query, scenario, target.TargetTableName, target.TargetAlias, plan.AdjustableColumn, normalizedSupportAdjust))
                        {
                            continue;
                        }

                        var targetComputedRaw = anchorDecimal * targetAdjustDecimal;
                        var supportComputedRaw = anchorDecimal * supportAdjustDecimal;
                        if (!TryNormalizeNumericCandidate(plan.ResultColumn, targetComputedRaw, out var normalizedTargetComputed, out var normalizedTargetComputedDecimal) ||
                            !TryNormalizeNumericCandidate(plan.ResultColumn, supportComputedRaw, out var normalizedSupportComputed, out var normalizedSupportComputedDecimal))
                        {
                            continue;
                        }

                        var relationSatisfied = target.TargetShouldBeHigher
                            ? normalizedTargetComputedDecimal > normalizedSupportComputedDecimal
                            : normalizedTargetComputedDecimal < normalizedSupportComputedDecimal;
                        if (!relationSatisfied)
                            continue;

                        targetAnchorValue = normalizedAnchor;
                        targetAdjustValue = normalizedTargetAdjust;
                        targetComputedValue = normalizedTargetComputed;
                        targetComputedDecimal = normalizedTargetComputedDecimal;
                        supportAnchorValue = normalizedAnchor;
                        supportAdjustValue = normalizedSupportAdjust;
                        supportComputedValue = normalizedSupportComputed;
                        supportComputedDecimal = normalizedSupportComputedDecimal;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryBuildScalarAverageValuePair(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            GeneratedRow targetRow,
            out object? targetValue,
            out decimal targetDecimal,
            out object? supportValue,
            out decimal supportDecimal)
        {
            targetValue = null;
            targetDecimal = 0m;
            supportValue = null;
            supportDecimal = 0m;

            var targetCandidates = BuildScalarAverageTargetCandidates(query, scenario, target, targetRow);
            if (scenario.ExpectedToReturnRows && GetRequestedRowCount() > 1)
            {
                targetCandidates = target.TargetShouldBeHigher
                    ? targetCandidates.OrderByDescending(v => v)
                    : targetCandidates.OrderBy(v => v);
            }

            foreach (var rawTarget in targetCandidates)
            {
                if (!TryNormalizeNumericCandidate(target.TargetColumn, rawTarget, out var normalizedTarget, out var normalizedTargetDecimal) ||
                    !SatisfiesScalarAverageTargetSideConstraints(query, scenario, target, normalizedTarget))
                {
                    continue;
                }

                var supportCandidates = BuildScalarAverageSupportCandidates(target.AggregateColumn, normalizedTargetDecimal, target.TargetShouldBeHigher);
                if (scenario.ExpectedToReturnRows && GetRequestedRowCount() > 1)
                {
                    supportCandidates = target.TargetShouldBeHigher
                        ? supportCandidates.OrderBy(v => v)
                        : supportCandidates.OrderByDescending(v => v);
                }

                foreach (var rawSupport in supportCandidates)
                {
                    if (!TryNormalizeNumericCandidate(target.AggregateColumn, rawSupport, out var normalizedSupport, out var normalizedSupportDecimal) ||
                        !SatisfiesScalarAverageSupportSideConstraints(query, scenario, target, normalizedSupport))
                    {
                        continue;
                    }

                    var relationSatisfied = target.TargetShouldBeHigher
                        ? normalizedTargetDecimal > normalizedSupportDecimal
                        : normalizedTargetDecimal < normalizedSupportDecimal;
                    if (!relationSatisfied)
                        continue;

                    targetValue = normalizedTarget;
                    targetDecimal = normalizedTargetDecimal;
                    supportValue = normalizedSupport;
                    supportDecimal = normalizedSupportDecimal;
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<decimal> BuildScalarAverageTargetCandidates(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            GeneratedRow targetRow)
        {
            var candidates = new List<decimal>();
            var step = GetNumericRangeStep(target.TargetColumn);
            if (TryConvertDecimal(targetRow.GetValue(target.TargetColumn.ColumnName), out var currentValue))
            {
                candidates.Add(currentValue);
            }

            var bounds = GetPositiveNumericBounds(
                query,
                scenario,
                target.TargetTableName,
                target.TargetAlias,
                target.TargetColumn,
                target.Condition.Key);

            candidates.AddRange(bounds.DiscreteValues);

            if (target.TargetShouldBeHigher)
            {
                if (bounds.Upper.HasValue)
                    candidates.Add(bounds.Upper.Value);
                if (bounds.Lower.HasValue)
                    candidates.Add(bounds.Lower.Value + (step * 10m));
                candidates.AddRange(new[] { 20m, 100m, 1000m, 15050000m });
            }
            else
            {
                if (bounds.Lower.HasValue)
                    candidates.Add(bounds.Lower.Value);
                if (bounds.Upper.HasValue)
                    candidates.Add(bounds.Upper.Value - (step * 10m));
                candidates.AddRange(new[] { 0m, 1m, 10m, 100m });
            }

            if (TryConvertDecimal(BuildMaxNumericValue(target.TargetColumn, 0, query, target.TargetAlias), out var maxValue))
            {
                candidates.Add(maxValue);
            }

            return DeduplicateDecimalCandidates(candidates)
                .Where(c => IsWithinNumericBounds(c, bounds));
        }

        private static IEnumerable<decimal> BuildScalarAverageSupportCandidates(
            ColumnSchema aggregateColumn,
            decimal targetDecimal,
            bool targetShouldBeHigher)
        {
            var step = GetNumericRangeStep(aggregateColumn);
            var candidates = targetShouldBeHigher
                ? new[]
                {
                    0m,
                    1m,
                    Math.Max(0m, targetDecimal / 2m),
                    targetDecimal - step,
                    targetDecimal - (step * 10m)
                }
                : new[]
                {
                    targetDecimal + step,
                    targetDecimal + (step * 10m),
                    Math.Max(100m, targetDecimal + 100m),
                    Math.Max(1000m, targetDecimal + 1000m)
                };

            return DeduplicateDecimalCandidates(candidates);
        }

        private IEnumerable<decimal> BuildPositiveNumericColumnCandidates(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column)
        {
            var bounds = GetPositiveNumericBounds(query, scenario, tableName, tableAlias, column, excludedConditionKey: string.Empty);
            var step = GetNumericRangeStep(column);
            var candidates = new List<decimal>();

            candidates.AddRange(bounds.DiscreteValues);
            if (bounds.Lower.HasValue)
            {
                candidates.Add(bounds.Lower.Value);
                candidates.Add(bounds.Lower.Value + step);
                candidates.Add(bounds.Lower.Value + (step * 100m));
                candidates.Add(bounds.Lower.Value * 2m);
            }

            if (bounds.Upper.HasValue)
            {
                candidates.Add(bounds.Upper.Value);
                candidates.Add(bounds.Upper.Value - step);
                candidates.Add(bounds.Upper.Value - (step * 100m));
            }

            if (bounds.Lower.HasValue && bounds.Upper.HasValue && bounds.Lower.Value <= bounds.Upper.Value)
            {
                candidates.Add((bounds.Lower.Value + bounds.Upper.Value) / 2m);
            }

            candidates.AddRange(new[] { 1m, 2m, 5m, 10m, 100m, 1000m, 100000m, 200000m, 15050000m, 30000000m });

            return DeduplicateDecimalCandidates(candidates)
                .Where(c => IsWithinNumericBounds(c, bounds));
        }

        private NumericColumnBounds GetPositiveNumericBounds(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column,
            string excludedConditionKey)
        {
            var bounds = new NumericColumnBounds();
            foreach (var condition in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if (condition.Key.Equals(excludedConditionKey, StringComparison.OrdinalIgnoreCase) ||
                    HasScalarSubqueryPlaceholder(condition) ||
                    condition.IsNegated ||
                    !ConditionTargetsColumn(query, condition, tableName, tableAlias, column.ColumnName) ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth)
                {
                    continue;
                }

                if (condition.Operator == ComparisonOp.In)
                {
                    foreach (var value in condition.InValues)
                    {
                        if (TryConvertDecimal(value, out var inValue))
                        {
                            bounds.DiscreteValues.Add(inValue);
                        }
                    }
                    continue;
                }

                if (condition.Operator == ComparisonOp.Between)
                {
                    if (TryResolveConditionNumericBoundary(condition, scenario, query, null, column, tableAlias, useSecondValue: false, out var lower))
                    {
                        bounds.Lower = MaxNullable(bounds.Lower, lower);
                    }

                    if (TryResolveConditionNumericBoundary(condition, scenario, query, null, column, tableAlias, useSecondValue: true, out var upper))
                    {
                        bounds.Upper = MinNullable(bounds.Upper, upper);
                    }
                    continue;
                }

                if (!TryResolveConditionNumericBoundary(condition, scenario, query, null, column, tableAlias, useSecondValue: false, out var boundary))
                    continue;

                var step = GetNumericRangeStep(column);
                switch (condition.Operator)
                {
                    case ComparisonOp.Equal:
                        bounds.Lower = MaxNullable(bounds.Lower, boundary);
                        bounds.Upper = MinNullable(bounds.Upper, boundary);
                        break;
                    case ComparisonOp.GreaterThan:
                        bounds.Lower = MaxNullable(bounds.Lower, boundary + step);
                        break;
                    case ComparisonOp.GreaterThanOrEqual:
                        bounds.Lower = MaxNullable(bounds.Lower, boundary);
                        break;
                    case ComparisonOp.LessThan:
                        bounds.Upper = MinNullable(bounds.Upper, boundary - step);
                        break;
                    case ComparisonOp.LessThanOrEqual:
                        bounds.Upper = MinNullable(bounds.Upper, boundary);
                        break;
                }
            }

            return bounds;
        }

        private bool SatisfiesScalarAverageTargetSideConstraints(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            object? value)
        {
            var generator = _valueFactory.GetGenerator(target.TargetColumn.TypeCategory);
            foreach (var condition in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if (condition.Key.Equals(target.Condition.Key, StringComparison.OrdinalIgnoreCase) ||
                    HasScalarSubqueryPlaceholder(condition) ||
                    !ConditionTargetsColumn(query, condition, target.TargetTableName, target.TargetAlias, target.TargetColumn.ColumnName) ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth)
                {
                    continue;
                }

                var conditionTarget = new ColumnConditionTarget(condition, desiredTruth);
                if (!EvaluateConditionTarget(value, conditionTarget, scenario, query, target.TargetColumn, generator, target.TargetAlias, currentRow: null))
                    return false;
            }

            return true;
        }

        private bool SatisfiesScalarAverageSupportSideConstraints(
            ParsedQuery query,
            BranchScenario scenario,
            ScalarAverageComparisonTarget target,
            object? value)
        {
            var localAliasMap = ExtendAliasMap(
                new Dictionary<string, string>(query.AliasToTableMap, StringComparer.OrdinalIgnoreCase),
                target.Subquery.Tables);
            var internalTruthMap = BuildSubqueryInternalTruthMap(target.Subquery, predicateTruth: true);
            var generator = _valueFactory.GetGenerator(target.AggregateColumn.TypeCategory);

            foreach (var condition in target.Subquery.Conditions.Where(c =>
                         ConditionTargetsColumn(localAliasMap, c, target.AggregateTableName, target.AggregateAlias, target.AggregateColumn.ColumnName)))
            {
                if (!TryGetDesiredTruthFromAssignments(internalTruthMap, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth)
                {
                    continue;
                }

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

                var conditionTarget = new ColumnConditionTarget(condition, desiredTruth, comparisonValue, localAliasMap);
                if (!EvaluateConditionTarget(value, conditionTarget, scenario, query, target.AggregateColumn, generator, target.AggregateAlias, currentRow: null))
                    return false;
            }

            return true;
        }

        private bool SatisfiesPositiveColumnConstraints(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column,
            object? value,
            string excludedConditionKey = "")
        {
            var generator = _valueFactory.GetGenerator(column.TypeCategory);
            foreach (var condition in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if ((!string.IsNullOrWhiteSpace(excludedConditionKey) &&
                     condition.Key.Equals(excludedConditionKey, StringComparison.OrdinalIgnoreCase)) ||
                    HasScalarSubqueryPlaceholder(condition) ||
                    !ConditionTargetsColumn(query, condition, tableName, tableAlias, column.ColumnName) ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth)
                {
                    continue;
                }

                var conditionTarget = new ColumnConditionTarget(condition, desiredTruth);
                if (!EvaluateConditionTarget(value, conditionTarget, scenario, query, column, generator, tableAlias, currentRow: null))
                    return false;
            }

            return true;
        }

        private static bool TryNormalizeNumericCandidate(
            ColumnSchema column,
            decimal rawValue,
            out object? normalizedValue,
            out decimal normalizedDecimal)
        {
            normalizedValue = SqlServerValueNormalizer.NormalizeValue(column, rawValue) ?? rawValue;
            return TryConvertDecimal(normalizedValue, out normalizedDecimal);
        }

        private static IEnumerable<decimal> DeduplicateDecimalCandidates(IEnumerable<decimal> candidates)
        {
            var seen = new HashSet<decimal>();
            foreach (var candidate in candidates)
            {
                if (seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static bool IsWithinNumericBounds(decimal candidate, NumericColumnBounds bounds)
        {
            if (bounds.Lower.HasValue && candidate < bounds.Lower.Value)
                return false;
            if (bounds.Upper.HasValue && candidate > bounds.Upper.Value)
                return false;
            if (bounds.DiscreteValues.Count > 0 && !bounds.DiscreteValues.Any(v => v == candidate))
                return false;
            return true;
        }

        private static bool HasScalarSubqueryPlaceholder(ConditionInfo condition) =>
            IsScalarSubqueryPlaceholder(condition.LeftExpression?.Text) ||
            IsScalarSubqueryPlaceholder(condition.RightExpression?.Text) ||
            IsScalarSubqueryPlaceholder(condition.Value);

        private static bool TryBuildComputedProductColumnPlan(
            TableSchema schema,
            ColumnSchema column,
            out ComputedProductColumnPlan plan)
        {
            plan = null!;
            if (!column.IsComputed)
                return false;

            // Allow computed columns regardless of resolved TypeCategory:
            // the schema reader may not always populate TypeCategory correctly for computed columns,
            // and we can still determine if it is numeric from source column types.
            // Only skip if TypeCategory is explicitly a non-numeric, non-computed known type.
            if (column.TypeCategory is DataTypeCategory.Boolean or
                DataTypeCategory.DateTime or DataTypeCategory.Time or DataTypeCategory.DateTimeOffset or
                DataTypeCategory.Guid or DataTypeCategory.Binary or DataTypeCategory.Xml)
            {
                return false;
            }

            if (column.TypeCategory == DataTypeCategory.String &&
                !IsComputedNumericProductResultColumn(column))
            {
                return false;
            }

            var referencedColumns = ExtractComputedExpressionColumnNames(column.ComputedExpression, schema)
                .Where(c => !c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(schema.GetColumn)
                .Where(c => c != null &&
                            !c.IsComputed &&
                            !c.IsStoreGenerated &&
                            c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
                .Cast<ColumnSchema>()
                .ToList();

            if (referencedColumns.Count == 0 &&
                column.ColumnName.Contains("LineTotal", StringComparison.OrdinalIgnoreCase))
            {
                var quantity = schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    c.ColumnName.Contains("Quantity", StringComparison.OrdinalIgnoreCase));
                var price = schema.Columns.FirstOrDefault(c =>
                    !c.IsComputed &&
                    !c.IsStoreGenerated &&
                    c.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float &&
                    (c.ColumnName.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
                     c.ColumnName.Contains("Amount", StringComparison.OrdinalIgnoreCase)));

                if (quantity != null && price != null)
                {
                    referencedColumns.Add(quantity);
                    referencedColumns.Add(price);
                }
            }

            if (referencedColumns.Count == 0 &&
                IsComputedNumericProductResultColumn(column))
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

            if (referencedColumns.Count != 2 ||
                (!string.IsNullOrWhiteSpace(column.ComputedExpression) &&
                 !column.ComputedExpression.Contains('*', StringComparison.Ordinal)))
            {
                return false;
            }

            var adjustable = referencedColumns.FirstOrDefault(c => IsMeasureLikeNumericColumn(c)) ??
                             referencedColumns.FirstOrDefault(c => c.ColumnName.Contains("Price", StringComparison.OrdinalIgnoreCase)) ??
                             referencedColumns[1];
            var anchor = referencedColumns.First(c => !c.ColumnName.Equals(adjustable.ColumnName, StringComparison.OrdinalIgnoreCase));
            plan = new ComputedProductColumnPlan(column, anchor, adjustable);
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
            if (string.IsNullOrWhiteSpace(expression))
                yield break;

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
                {
                    yield return name;
                }
            }
        }

        private static bool CanAdjustScalarAggregateColumn(TableSchema schema, ColumnSchema column)
        {
            if (column.IsComputed ||
                column.IsStoreGenerated ||
                column.IsIdentity ||
                column.IsPrimaryKey ||
                schema.PrimaryKey?.Columns.Any(c => c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) == true ||
                schema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return column.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float;
        }

        private static string ResolveConditionTargetAlias(ConditionInfo condition)
        {
            if (!string.IsNullOrWhiteSpace(condition.TableAlias))
                return condition.TableAlias;

            return condition.ReferencedColumns
                .FirstOrDefault(r => !r.IsRightSide)
                ?.TableAlias ?? string.Empty;
        }

        private static bool TryResolveTableForColumn(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            string tableAlias,
            string columnName,
            out string tableName)
        {
            tableName = string.Empty;
            if (!string.IsNullOrWhiteSpace(tableAlias))
            {
                tableName = query.ResolveAlias(tableAlias);
                return schemas.ContainsKey(tableName);
            }

            var matches = schemas.Values
                .Where(s => s.GetColumn(columnName) != null)
                .Select(s => s.TableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count != 1)
                return false;

            tableName = matches[0];
            return true;
        }

        private static void ResolveDerivedColumnReference(
            ParsedQuery query,
            ref string tableAlias,
            ref string columnName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrWhiteSpace(tableAlias) &&
                   !string.IsNullOrWhiteSpace(columnName) &&
                   seen.Add($"{tableAlias}|{columnName}") &&
                   query.TryResolveDerivedColumn(tableAlias, columnName, out var binding))
            {
                tableAlias = binding.SourceAlias;
                columnName = binding.SourceColumn;
            }

            if (!string.IsNullOrWhiteSpace(tableAlias) ||
                string.IsNullOrWhiteSpace(columnName))
            {
                return;
            }

            var unresolvedColumnName = columnName;
            var matches = query.DerivedColumnMappings.Values
                .SelectMany(c => c.Values)
                .Where(binding => binding.OutputColumn.Equals(unresolvedColumnName, StringComparison.OrdinalIgnoreCase) &&
                                  !string.IsNullOrWhiteSpace(binding.SourceColumn))
                .Select(binding => new { binding.SourceAlias, binding.SourceColumn })
                .DistinctBy(binding => $"{binding.SourceAlias}\u001F{binding.SourceColumn}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matches.Count == 1)
            {
                tableAlias = matches[0].SourceAlias;
                columnName = matches[0].SourceColumn;
            }
        }

        private static bool TryResolveSubquerySelectColumn(
            ParsedQuery query,
            Dictionary<string, TableSchema> schemas,
            SubqueryInfo subquery,
            out string tableName,
            out string tableAlias,
            out string columnName)
        {
            tableName = string.Empty;
            tableAlias = string.Empty;
            columnName = subquery.SelectColumn;
            var selectAlias = subquery.SelectTableAlias;
            ResolveDerivedColumnReference(query, ref selectAlias, ref columnName);

            if (!string.IsNullOrWhiteSpace(selectAlias))
            {
                var table = subquery.Tables.FirstOrDefault(t =>
                    t.EffectiveName.Equals(selectAlias, StringComparison.OrdinalIgnoreCase) ||
                    t.TableName.Equals(selectAlias, StringComparison.OrdinalIgnoreCase));
                if (table != null)
                {
                    tableName = table.TableName;
                    tableAlias = table.EffectiveName;
                    return true;
                }

                if (TryResolveTableForColumn(query, schemas, selectAlias, columnName, out tableName))
                {
                    tableAlias = selectAlias;
                    return true;
                }
            }

            if (subquery.Tables.Count == 1)
            {
                var table = subquery.Tables[0];
                if (schemas.TryGetValue(table.TableName, out var schema) &&
                    schema.GetColumn(columnName) != null)
                {
                    tableName = table.TableName;
                    tableAlias = table.EffectiveName;
                    return true;
                }
            }

            var resolvedColumnName = columnName;
            var matches = subquery.Tables
                .Where(t => schemas.TryGetValue(t.TableName, out var schema) &&
                            schema.GetColumn(resolvedColumnName) != null)
                .DistinctBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matches.Count == 1)
            {
                tableName = matches[0].TableName;
                tableAlias = matches[0].EffectiveName;
                return true;
            }

            return false;
        }

        private sealed class ScalarAverageComparisonTarget
        {
            public ScalarAverageComparisonTarget(
                ConditionInfo condition,
                SubqueryInfo subquery,
                string targetTableName,
                string targetAlias,
                TableSchema targetSchema,
                ColumnSchema targetColumn,
                string aggregateTableName,
                string aggregateAlias,
                TableSchema aggregateSchema,
                ColumnSchema aggregateColumn,
                ComputedProductColumnPlan? targetComputedPlan,
                ComputedProductColumnPlan? aggregateComputedPlan,
                bool desiredTruth,
                bool targetShouldBeHigher)
            {
                Condition = condition;
                Subquery = subquery;
                TargetTableName = targetTableName;
                TargetAlias = targetAlias;
                TargetSchema = targetSchema;
                TargetColumn = targetColumn;
                AggregateTableName = aggregateTableName;
                AggregateAlias = aggregateAlias;
                AggregateSchema = aggregateSchema;
                AggregateColumn = aggregateColumn;
                TargetComputedPlan = targetComputedPlan;
                AggregateComputedPlan = aggregateComputedPlan;
                DesiredTruth = desiredTruth;
                TargetShouldBeHigher = targetShouldBeHigher;
            }

            public ConditionInfo Condition { get; }
            public SubqueryInfo Subquery { get; }
            public string TargetTableName { get; }
            public string TargetAlias { get; }
            public TableSchema TargetSchema { get; }
            public ColumnSchema TargetColumn { get; }
            public string AggregateTableName { get; }
            public string AggregateAlias { get; }
            public TableSchema AggregateSchema { get; }
            public ColumnSchema AggregateColumn { get; }
            public ComputedProductColumnPlan? TargetComputedPlan { get; }
            public ComputedProductColumnPlan? AggregateComputedPlan { get; }
            public bool DesiredTruth { get; }
            public bool TargetShouldBeHigher { get; }
        }

        private sealed class ComputedProductColumnPlan
        {
            public ComputedProductColumnPlan(
                ColumnSchema resultColumn,
                ColumnSchema anchorColumn,
                ColumnSchema adjustableColumn)
            {
                ResultColumn = resultColumn;
                AnchorColumn = anchorColumn;
                AdjustableColumn = adjustableColumn;
            }

            public ColumnSchema ResultColumn { get; }
            public ColumnSchema AnchorColumn { get; }
            public ColumnSchema AdjustableColumn { get; }
        }

        private sealed class NumericColumnBounds
        {
            public decimal? Lower { get; set; }
            public decimal? Upper { get; set; }
            public List<decimal> DiscreteValues { get; } = new();
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
                object? comparisonValue = null,
                IReadOnlyDictionary<string, string>? subqueryAliasMap = null)
            {
                Condition = condition;
                DesiredTruth = desiredTruth;
                ComparisonValue = comparisonValue;
                SubqueryAliasMap = subqueryAliasMap;
            }

            public ConditionInfo Condition { get; }
            public bool DesiredTruth { get; }
            public object? ComparisonValue { get; }
            public IReadOnlyDictionary<string, string>? SubqueryAliasMap { get; }
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

        private object? GenerateBoundaryValue(ColumnSchema col, ConditionInfo condition, int rowIndex = 0)
        {
            var generator = _valueFactory.GetGenerator(col.TypeCategory);

            if (TryGenerateDistinctBoundaryValue(col, condition, rowIndex, generator, out var distinctValue))
                return distinctValue;

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

        private bool TryGenerateDistinctBoundaryValue(
            ColumnSchema column,
            ConditionInfo condition,
            int rowIndex,
            IValueGenerator generator,
            out object? value)
        {
            value = null;

            if (condition.Operator == ComparisonOp.In && condition.InValues.Count > 0)
            {
                value = generator.GenerateFromLiteral(condition.InValues[Math.Abs(rowIndex) % condition.InValues.Count], column);
                return true;
            }

            if (column.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
                return TryGenerateDistinctNumericBoundaryValue(column, condition, rowIndex, generator, out value);

            if (column.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset)
                return TryGenerateDistinctDateBoundaryValue(column, condition, rowIndex, generator, out value);

            return false;
        }

        private bool TryGenerateDistinctNumericBoundaryValue(
            ColumnSchema column,
            ConditionInfo condition,
            int rowIndex,
            IValueGenerator generator,
            out object? value)
        {
            value = null;
            var offset = Math.Max(0, rowIndex);
            var step = GetNumericRangeStep(column);
            decimal candidate;

            switch (condition.Operator)
            {
                case ComparisonOp.GreaterThan:
                    if (!TryConvertDecimal(condition.Value, out var greaterThan))
                        return false;
                    candidate = greaterThan + step * (offset + 1);
                    break;

                case ComparisonOp.GreaterThanOrEqual:
                    if (!TryConvertDecimal(condition.Value, out var greaterThanOrEqual))
                        return false;
                    candidate = greaterThanOrEqual + step * offset;
                    break;

                case ComparisonOp.LessThan:
                    if (!TryConvertDecimal(condition.Value, out var lessThan))
                        return false;
                    candidate = lessThan - step * (offset + 1);
                    break;

                case ComparisonOp.LessThanOrEqual:
                    if (!TryConvertDecimal(condition.Value, out var lessThanOrEqual))
                        return false;
                    candidate = lessThanOrEqual - step * offset;
                    break;

                case ComparisonOp.Between:
                    if (!TryConvertDecimal(condition.Value, out var lower) ||
                        !TryConvertDecimal(condition.SecondValue, out var upper) ||
                        lower > upper)
                    {
                        return false;
                    }

                    if (condition.IsNegated)
                    {
                        candidate = lower - step * (offset + 1);
                    }
                    else
                    {
                        var spanSteps = decimal.Floor((upper - lower) / step);
                        var boundedOffset = spanSteps <= 0 ? 0 : offset % (spanSteps + 1);
                        candidate = lower + step * boundedOffset;
                    }
                    break;

                case ComparisonOp.NotEqual:
                    if (!TryConvertDecimal(condition.Value, out var notEqual))
                        return false;
                    candidate = notEqual + step * (offset + 1);
                    break;

                case ComparisonOp.Equal:
                    if (!TryConvertDecimal(condition.Value, out var equal))
                        return false;
                    candidate = equal;
                    break;

                default:
                    return false;
            }

            return TryFinalizeBoundaryCandidate(column, condition, candidate, generator, out value);
        }

        private bool TryGenerateDistinctDateBoundaryValue(
            ColumnSchema column,
            ConditionInfo condition,
            int rowIndex,
            IValueGenerator generator,
            out object? value)
        {
            value = null;
            var offset = Math.Max(0, rowIndex);
            var step = column.EffectiveDataType.Equals("date", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromDays(1)
                : TimeSpan.FromMinutes(7);
            DateTime candidate;

            switch (condition.Operator)
            {
                case ComparisonOp.GreaterThan:
                    if (!TryConvertToDateTimeValue(condition.Value, out var greaterThan))
                        return false;
                    candidate = greaterThan.AddTicks(step.Ticks * (offset + 1));
                    break;

                case ComparisonOp.GreaterThanOrEqual:
                    if (!TryConvertToDateTimeValue(condition.Value, out var greaterThanOrEqual))
                        return false;
                    candidate = greaterThanOrEqual.AddTicks(step.Ticks * offset);
                    break;

                case ComparisonOp.LessThan:
                    if (!TryConvertToDateTimeValue(condition.Value, out var lessThan))
                        return false;
                    candidate = lessThan.AddTicks(-step.Ticks * (offset + 1));
                    break;

                case ComparisonOp.LessThanOrEqual:
                    if (!TryConvertToDateTimeValue(condition.Value, out var lessThanOrEqual))
                        return false;
                    candidate = lessThanOrEqual.AddTicks(-step.Ticks * offset);
                    break;

                case ComparisonOp.Between:
                    if (!TryConvertToDateTimeValue(condition.Value, out var lower) ||
                        !TryConvertToDateTimeValue(condition.SecondValue, out var upper) ||
                        lower > upper)
                    {
                        return false;
                    }

                    if (condition.IsNegated)
                    {
                        candidate = lower.AddTicks(-step.Ticks * (offset + 1));
                    }
                    else
                    {
                        var spanSteps = Math.Max(0, (long)((upper - lower).Ticks / step.Ticks));
                        var boundedOffset = spanSteps == 0 ? 0 : offset % (spanSteps + 1);
                        candidate = lower.AddTicks(step.Ticks * boundedOffset);
                    }
                    break;

                default:
                    return false;
            }

            object typedCandidate = column.TypeCategory == DataTypeCategory.DateTimeOffset
                ? new DateTimeOffset(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), TimeSpan.Zero)
                : DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);

            return TryFinalizeBoundaryCandidate(column, condition, typedCandidate, generator, out value);
        }

        private bool TryFinalizeBoundaryCandidate(
            ColumnSchema column,
            ConditionInfo condition,
            object? candidate,
            IValueGenerator generator,
            out object? value)
        {
            value = null;

            try
            {
                var normalized = SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate;
                if (!EvaluateCondition(normalized, condition, null, column, generator))
                    return false;

                value = normalized;
                return true;
            }
            catch
            {
                return false;
            }
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
                    if (col.IsComputed || col.IsStoreGenerated) continue;

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

                ApplyRowLevelPredicateAdjustments(query, scenario, schema, row);
                scenario.AddRow(tableName, row);
                RegisterGeneratedIds(tableRowIds, tableName, scenario, schema, rowId, 1);
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

            var conditionTargets = new List<ColumnConditionTarget>();
            foreach (var condition in subquery.Conditions.Where(c =>
                         ConditionTargetsColumn(aliasToTableMap, c, schema.TableName, tableAlias, column.ColumnName)))
            {
                if (!TryGetDesiredTruthFromAssignments(internalTruthMap, condition, defaultTruth: true, out var desiredTruth))
                    continue;

                object? comparisonValue = null;
                if (condition.IsColumnComparison)
                {
                    TryResolveSubqueryComparisonValue(
                        scenario,
                        query,
                        condition,
                        tableRowIds,
                        aliasToTableMap,
                        out comparisonValue);
                }

                conditionTargets.Add(new ColumnConditionTarget(condition, desiredTruth, comparisonValue));
            }

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
            out object? value,
            int rowIndex = 0)
        {
            value = null;

            if (string.IsNullOrWhiteSpace(condition.RightColumnName))
                return false;

            if (!string.IsNullOrWhiteSpace(condition.RightTableAlias))
            {
                var tableName = aliasToTableMap.TryGetValue(condition.RightTableAlias, out var resolvedTable)
                    ? resolvedTable
                    : query.ResolveAlias(condition.RightTableAlias);

                if (TryResolveRelatedColumnValue(scenario, tableName, condition.RightColumnName, rowIndex, out value))
                    return true;

                if (TryResolveRelatedRowId(tableRowIds, tableName, rowIndex, out var relatedId))
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

        private void ApplyRowLevelPredicateAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row)
        {
            foreach (var target in EnumerateRowColumnComparisonTargets(query, scenario, schema.TableName))
            {
                var condition = target.Condition;
                if (string.IsNullOrWhiteSpace(condition.ColumnName) ||
                    string.IsNullOrWhiteSpace(condition.RightColumnName))
                {
                    continue;
                }

                if (!IsSameGeneratedRowComparison(condition))
                    continue;

                var leftColumn = schema.GetColumn(condition.ColumnName);
                var rightColumn = schema.GetColumn(condition.RightColumnName);
                if (leftColumn == null || rightColumn == null)
                    continue;

                var leftValue = row.GetValue(leftColumn.ColumnName);
                var rightValue = row.GetValue(rightColumn.ColumnName);
                var currentTruth = EvaluateColumnComparison(leftValue, rightValue, condition.Operator);
                if (currentTruth == target.DesiredTruth)
                    continue;

                if (CanAdjustComparisonColumn(schema, rightColumn) &&
                    TryBuildColumnComparisonAdjustment(rightColumn, leftValue, condition.Operator, adjustRightSide: true, target.DesiredTruth, out var adjustedRight))
                {
                    row.SetValue(rightColumn.ColumnName, adjustedRight);
                    continue;
                }

                if (CanAdjustComparisonColumn(schema, leftColumn) &&
                    TryBuildColumnComparisonAdjustment(leftColumn, rightValue, condition.Operator, adjustRightSide: false, target.DesiredTruth, out var adjustedLeft))
                {
                    row.SetValue(leftColumn.ColumnName, adjustedLeft);
                }
            }
        }

        private void ApplyScenarioJoinColumnComparisonAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            foreach (var condition in query.EnumerateScopeConditions(ConditionSource.JoinOn)
                         .Where(c => c.IsColumnComparison))
            {
                if (!TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth ||
                    string.IsNullOrWhiteSpace(condition.TableAlias) ||
                    string.IsNullOrWhiteSpace(condition.ColumnName) ||
                    string.IsNullOrWhiteSpace(condition.RightTableAlias) ||
                    string.IsNullOrWhiteSpace(condition.RightColumnName))
                {
                    continue;
                }

                var leftTableName = query.ResolveAlias(condition.TableAlias);
                var rightTableName = query.ResolveAlias(condition.RightTableAlias);
                if (!schemas.TryGetValue(leftTableName, out var leftSchema) ||
                    !schemas.TryGetValue(rightTableName, out var rightSchema) ||
                    !scenario.TableRows.TryGetValue(leftTableName, out var leftRows) ||
                    !scenario.TableRows.TryGetValue(rightTableName, out var rightRows) ||
                    leftRows.Count == 0 ||
                    rightRows.Count == 0)
                {
                    continue;
                }

                var leftColumn = leftSchema.GetColumn(condition.ColumnName);
                var rightColumn = rightSchema.GetColumn(condition.RightColumnName);
                if (leftColumn == null || rightColumn == null)
                {
                    continue;
                }

                var pairCount = Math.Max(leftRows.Count, rightRows.Count);
                for (var rowIndex = 0; rowIndex < pairCount; rowIndex++)
                {
                    var leftRow = leftRows[Math.Min(rowIndex, leftRows.Count - 1)];
                    var rightRow = rightRows[Math.Min(rowIndex, rightRows.Count - 1)];
                    var leftValue = leftRow.GetValue(leftColumn.ColumnName);
                    var rightValue = rightRow.GetValue(rightColumn.ColumnName);
                    if (EvaluateColumnComparison(leftValue, rightValue, condition.Operator))
                    {
                        continue;
                    }

                    if (TryAdjustJoinComparisonLeftSide(
                            leftSchema,
                            leftColumn,
                            leftRow,
                            rightValue,
                            condition.Operator))
                    {
                        continue;
                    }

                    TryAdjustJoinComparisonRightSide(
                        rightSchema,
                        rightColumn,
                        rightRow,
                        leftValue,
                        condition.Operator);
                }
            }
        }

        private void ApplyScenarioExpressionJoinDomainAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas)
        {
            foreach (var scope in query.PredicateScopes.Where(s => s.Source == ConditionSource.JoinOn))
            {
                foreach (var condition in scope.Conditions.Where(c => c.Operator == ComparisonOp.Equal))
                {
                    if (!TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                        !desiredTruth)
                    {
                        continue;
                    }

                    ApplyExpressionSourceDomainValues(
                        query,
                        scenario,
                        schemas,
                        condition,
                        condition.LeftExpression);

                    ApplyExpressionSourceDomainValues(
                        query,
                        scenario,
                        schemas,
                        condition,
                        condition.RightExpression);

                    foreach (var table in query.Tables)
                    {
                        var tableAlias = table.EffectiveName;
                        var tableName = query.ResolveAlias(tableAlias);
                        if (!schemas.TryGetValue(tableName, out var schema) ||
                            !scenario.TableRows.TryGetValue(tableName, out var rows) ||
                            rows.Count == 0)
                        {
                            continue;
                        }

                        foreach (var column in EnumerateSingleColumnUniqueKeyColumns(schema))
                        {
                            if (!TryGetNonTargetComparisonExpression(
                                    condition,
                                    tableAlias,
                                    tableName,
                                    column.ColumnName,
                                    query.AliasToTableMap,
                                    out var expression) ||
                                !TryInferFiniteNumericExpressionDomainValues(expression, out var domainValues))
                            {
                                continue;
                            }

                            var count = Math.Min(rows.Count, domainValues.Count);
                            for (var index = 0; index < count; index++)
                            {
                                var normalized = SqlServerValueNormalizer.NormalizeValue(column, domainValues[index]);
                                if (normalized != null)
                                {
                                    rows[index].SetValue(column.ColumnName, normalized);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ApplyExpressionSourceDomainValues(
            ParsedQuery query,
            BranchScenario scenario,
            Dictionary<string, TableSchema> schemas,
            ConditionInfo condition,
            ScalarExpressionInfo? expression)
        {
            if (expression == null ||
                !TryInferFiniteNumericExpressionDomainValues(expression, out var domainValues) ||
                domainValues.Count == 0)
            {
                return;
            }

            foreach (var table in query.Tables)
            {
                var tableAlias = table.EffectiveName;
                var tableName = query.ResolveAlias(tableAlias);
                if (!schemas.TryGetValue(tableName, out var schema) ||
                    !scenario.TableRows.TryGetValue(tableName, out var rows) ||
                    rows.Count == 0)
                {
                    continue;
                }

                foreach (var column in schema.Columns)
                {
                    if (column.IsComputed ||
                        column.IsStoreGenerated ||
                        column.IsIdentity ||
                        column.IsPrimaryKey ||
                        schema.PrimaryKey?.Columns.Any(c => c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) == true ||
                        schema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) ||
                        !ExpressionReferencesColumn(expression, tableAlias, tableName, column.ColumnName, query.AliasToTableMap))
                    {
                        continue;
                    }

                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var domainValue = domainValues[rowIndex % domainValues.Count];
                        var normalized = BuildExpressionSourceDomainValue(column, rows[rowIndex].GetValue(column.ColumnName), domainValue, rowIndex, expression.Text);
                        if (normalized != null)
                        {
                            rows[rowIndex].SetValue(column.ColumnName, normalized);
                        }
                    }
                }
            }
        }

        private object? BuildExpressionSourceDomainValue(
            ColumnSchema column,
            object? currentValue,
            decimal domainValue,
            int rowIndex,
            string? expressionText)
        {
            if (column.TypeCategory == DataTypeCategory.String)
            {
                var text = Convert.ToString(currentValue, System.Globalization.CultureInfo.InvariantCulture);
                var targetLength = Math.Max(1, Math.Min(ResolveTargetStringLength(column), string.IsNullOrEmpty(text) ? 96 : text.Length));
                if (string.IsNullOrEmpty(text))
                {
                    text = IsPhoneColumn(column)
                        ? BuildPhoneLikeString(column, rowIndex, targetLength)
                        : Convert.ToString(BuildSemanticString(column, rowIndex, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }

                if (ExpressionUsesRightMostCharacter(expressionText, column.ColumnName))
                {
                    var digit = Math.Abs((int)domainValue % 10).ToString(System.Globalization.CultureInfo.InvariantCulture)[0];
                    text = text.Length == 0 ? digit.ToString() : text[..^1] + digit;
                }
                else
                {
                    text = domainValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return SqlServerValueNormalizer.NormalizeValue(column, text);
            }

            if (column.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float)
            {
                return SqlServerValueNormalizer.NormalizeValue(column, domainValue);
            }

            return null;
        }

        private static bool ExpressionUsesRightMostCharacter(string? expressionText, string columnName)
        {
            if (string.IsNullOrWhiteSpace(expressionText) || string.IsNullOrWhiteSpace(columnName))
                return false;

            var escapedColumn = System.Text.RegularExpressions.Regex.Escape(columnName);
            return System.Text.RegularExpressions.Regex.IsMatch(
                expressionText,
                @"\bRIGHT\s*\([\s\S]*\b" + escapedColumn + @"\b[\s\S]*,\s*1\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool TryAdjustJoinComparisonLeftSide(
            TableSchema schema,
            ColumnSchema column,
            GeneratedRow row,
            object? referenceValue,
            ComparisonOp op)
        {
            if (!CanAdjustComparisonColumn(schema, column) ||
                !TryBuildColumnComparisonAdjustment(
                    column,
                    referenceValue,
                    op,
                    adjustRightSide: false,
                    desiredTruth: true,
                    out var adjustedValue))
            {
                return false;
            }

            row.SetValue(column.ColumnName, adjustedValue);
            return true;
        }

        private static bool TryAdjustJoinComparisonRightSide(
            TableSchema schema,
            ColumnSchema column,
            GeneratedRow row,
            object? referenceValue,
            ComparisonOp op)
        {
            if (!CanAdjustComparisonColumn(schema, column) ||
                !TryBuildColumnComparisonAdjustment(
                    column,
                    referenceValue,
                    op,
                    adjustRightSide: true,
                    desiredTruth: true,
                    out var adjustedValue))
            {
                return false;
            }

            row.SetValue(column.ColumnName, adjustedValue);
            return true;
        }

        private IEnumerable<RowColumnComparisonTarget> EnumerateRowColumnComparisonTargets(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName)
        {
            foreach (var scope in query.PredicateScopes)
            {
                foreach (var condition in scope.Conditions.Where(c => c.IsColumnComparison))
                {
                    if (ColumnComparisonTargetsTable(condition, query.AliasToTableMap, tableName))
                    {
                        if (!TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth))
                            continue;

                        yield return new RowColumnComparisonTarget(
                            condition,
                            desiredTruth);
                    }
                }
            }

            foreach (var target in EnumerateSubqueryRowColumnComparisonTargets(
                         query.Subqueries,
                         scenario,
                         query.AliasToTableMap,
                         parentTruthMap: null,
                         tableName))
            {
                yield return target;
            }
        }

        private IEnumerable<RowColumnComparisonTarget> EnumerateSubqueryRowColumnComparisonTargets(
            IEnumerable<SubqueryInfo> subqueries,
            BranchScenario scenario,
            IReadOnlyDictionary<string, string> aliasMap,
            IReadOnlyDictionary<string, bool>? parentTruthMap,
            string tableName)
        {
            foreach (var subquery in subqueries)
            {
                var predicateTruth = ResolveSubqueryPredicateTruth(scenario, subquery, parentTruthMap);
                var internalTruthMap = BuildSubqueryInternalTruthMap(subquery, predicateTruth);
                var localAliasMap = ExtendAliasMap(
                    new Dictionary<string, string>(aliasMap, StringComparer.OrdinalIgnoreCase),
                    subquery.Tables);

                foreach (var condition in subquery.Conditions.Where(c => c.IsColumnComparison))
                {
                    if (!ColumnComparisonTargetsTable(condition, localAliasMap, tableName))
                        continue;

                    if (!TryGetDesiredTruthFromAssignments(internalTruthMap, condition, defaultTruth: true, out var desiredTruth))
                        continue;

                    yield return new RowColumnComparisonTarget(condition, desiredTruth);
                }

                foreach (var nestedTarget in EnumerateSubqueryRowColumnComparisonTargets(
                             subquery.NestedSubqueries,
                             scenario,
                             localAliasMap,
                             internalTruthMap,
                             tableName))
                {
                    yield return nestedTarget;
                }
            }
        }

        private static bool ColumnComparisonTargetsTable(
            ConditionInfo condition,
            IReadOnlyDictionary<string, string> aliasMap,
            string tableName)
        {
            if (!condition.IsColumnComparison)
                return false;

            var leftTable = ResolveAliasFromMap(aliasMap, condition.TableAlias, tableName);
            var rightTable = ResolveAliasFromMap(aliasMap, condition.RightTableAlias, tableName);
            return leftTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                   rightTable.Equals(tableName, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAliasFromMap(
            IReadOnlyDictionary<string, string> aliasMap,
            string aliasOrName,
            string fallbackTableName)
        {
            if (string.IsNullOrWhiteSpace(aliasOrName))
                return fallbackTableName;

            return aliasMap.TryGetValue(aliasOrName, out var resolved)
                ? resolved
                : aliasOrName;
        }

        private static bool IsSameGeneratedRowComparison(ConditionInfo condition)
        {
            if (string.IsNullOrWhiteSpace(condition.TableAlias) ||
                string.IsNullOrWhiteSpace(condition.RightTableAlias))
            {
                return true;
            }

            return condition.TableAlias.Equals(condition.RightTableAlias, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EvaluateColumnComparison(object? leftValue, object? rightValue, ComparisonOp op)
        {
            var comparison = CompareExpressionValues(leftValue, rightValue);
            return op switch
            {
                ComparisonOp.Equal => comparison == 0,
                ComparisonOp.NotEqual => comparison != 0,
                ComparisonOp.GreaterThan => comparison > 0,
                ComparisonOp.GreaterThanOrEqual => comparison >= 0,
                ComparisonOp.LessThan => comparison < 0,
                ComparisonOp.LessThanOrEqual => comparison <= 0,
                _ => true
            };
        }

        private static bool CanAdjustComparisonColumn(TableSchema schema, ColumnSchema column)
        {
            if (column.IsComputed || column.IsStoreGenerated || column.IsIdentity || column.IsPrimaryKey)
                return false;

            if (schema.PrimaryKey?.Columns.Any(c => c.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) == true)
                return false;

            return !schema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryBuildColumnComparisonAdjustment(
            ColumnSchema targetColumn,
            object? referenceValue,
            ComparisonOp op,
            bool adjustRightSide,
            bool desiredTruth,
            out object? adjustedValue)
        {
            adjustedValue = null;
            if (referenceValue == null || referenceValue == DBNull.Value)
                return false;

            if (!desiredTruth)
            {
                op = NegateComparisonOperator(op);
            }

            adjustedValue = adjustRightSide
                ? BuildRightSideComparisonValue(targetColumn, referenceValue, op)
                : BuildLeftSideComparisonValue(targetColumn, referenceValue, op);

            if (adjustedValue == null || adjustedValue == DBNull.Value)
                return false;

            adjustedValue = SqlServerValueNormalizer.NormalizeValue(targetColumn, adjustedValue) ?? adjustedValue;
            return true;
        }

        private static object? BuildRightSideComparisonValue(
            ColumnSchema targetColumn,
            object referenceValue,
            ComparisonOp op)
        {
            return op switch
            {
                ComparisonOp.Equal => referenceValue,
                ComparisonOp.NotEqual => BuildDifferentComparisonValue(targetColumn, referenceValue, preferGreater: true),
                ComparisonOp.GreaterThan => BuildOffsetComparisonValue(targetColumn, referenceValue, -1),
                ComparisonOp.GreaterThanOrEqual => referenceValue,
                ComparisonOp.LessThan => BuildOffsetComparisonValue(targetColumn, referenceValue, 1),
                ComparisonOp.LessThanOrEqual => referenceValue,
                _ => null
            };
        }

        private static object? BuildLeftSideComparisonValue(
            ColumnSchema targetColumn,
            object referenceValue,
            ComparisonOp op)
        {
            return op switch
            {
                ComparisonOp.Equal => referenceValue,
                ComparisonOp.NotEqual => BuildDifferentComparisonValue(targetColumn, referenceValue, preferGreater: true),
                ComparisonOp.GreaterThan => BuildOffsetComparisonValue(targetColumn, referenceValue, 1),
                ComparisonOp.GreaterThanOrEqual => referenceValue,
                ComparisonOp.LessThan => BuildOffsetComparisonValue(targetColumn, referenceValue, -1),
                ComparisonOp.LessThanOrEqual => referenceValue,
                _ => null
            };
        }

        private static ComparisonOp NegateComparisonOperator(ComparisonOp op) => op switch
        {
            ComparisonOp.Equal => ComparisonOp.NotEqual,
            ComparisonOp.NotEqual => ComparisonOp.Equal,
            ComparisonOp.GreaterThan => ComparisonOp.LessThanOrEqual,
            ComparisonOp.GreaterThanOrEqual => ComparisonOp.LessThan,
            ComparisonOp.LessThan => ComparisonOp.GreaterThanOrEqual,
            ComparisonOp.LessThanOrEqual => ComparisonOp.GreaterThan,
            _ => op
        };

        private static object? BuildDifferentComparisonValue(
            ColumnSchema targetColumn,
            object referenceValue,
            bool preferGreater)
        {
            if (targetColumn.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float ||
                TryConvertDecimal(referenceValue, out _))
            {
                return BuildOffsetComparisonValue(targetColumn, referenceValue, preferGreater ? 1 : -1);
            }

            if (targetColumn.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset &&
                TryConvertToDateTimeValue(referenceValue, out var dateTime))
            {
                return dateTime.AddDays(preferGreater ? 1 : -1);
            }

            return $"{referenceValue}_alt";
        }

        private static object? BuildOffsetComparisonValue(
            ColumnSchema targetColumn,
            object referenceValue,
            int direction)
        {
            if (targetColumn.TypeCategory is DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float ||
                TryConvertDecimal(referenceValue, out _))
            {
                if (!TryConvertDecimal(referenceValue, out var numeric))
                    return null;

                return numeric + (direction * GetNumericRangeStep(targetColumn));
            }

            if (targetColumn.TypeCategory is DataTypeCategory.DateTime or DataTypeCategory.DateTimeOffset &&
                TryConvertToDateTimeValue(referenceValue, out var dateTime))
            {
                return dateTime.AddDays(direction);
            }

            return direction >= 0
                ? $"{referenceValue}_z"
                : string.Empty;
        }

        private sealed class RowColumnComparisonTarget
        {
            public RowColumnComparisonTarget(ConditionInfo condition, bool desiredTruth)
            {
                Condition = condition;
                DesiredTruth = desiredTruth;
            }

            public ConditionInfo Condition { get; }
            public bool DesiredTruth { get; }
        }

        private void ApplySemanticRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            ApplyRightDigitRangeStringRowAdjustments(query, schema, row, rowIndex);
            ApplyQueryFunctionHintedStringRowAdjustments(query, schema, row, rowIndex);
            ApplyScalarAvgSubqueryRowAdjustments(query, scenario, schema, row, rowIndex);

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

        private void ApplyRightDigitRangeStringRowAdjustments(
            ParsedQuery query,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            foreach (var column in schema.Columns.Where(c => c.TypeCategory == DataTypeCategory.String))
            {
                if (!TryFindRightDigitRangeHint(query, column, out var lower, out var upper))
                    continue;

                var span = Math.Max(1, upper - lower + 1);
                var digit = (char)('0' + lower + (rowIndex % span));
                var existing = Convert.ToString(row.GetValue(column.ColumnName), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                var candidate = BuildStringWithTrailingDigit(column, existing, rowIndex, digit);
                row.SetValue(column.ColumnName, SqlServerValueNormalizer.NormalizeValue(column, candidate) ?? candidate);
            }
        }

        private static bool TryFindRightDigitRangeHint(
            ParsedQuery query,
            ColumnSchema column,
            out int lower,
            out int upper)
        {
            lower = 0;
            upper = 0;
            if (string.IsNullOrWhiteSpace(query.OriginalSql) ||
                string.IsNullOrWhiteSpace(column.ColumnName))
            {
                return false;
            }

            var escapedColumn = System.Text.RegularExpressions.Regex.Escape(column.ColumnName);
            var rightPattern = @"RIGHT\s*\(\s*(?:(?:\[[^\]]+\])|(?:\w+))?\s*\.?\s*\[?" +
                               escapedColumn +
                               @"\]?\s*,\s*1\s*\)";
            var pattern = rightPattern +
                          @"(?:(?!\bBETWEEN\b)[\s\S]){0,240}\bBETWEEN\s+(?<lo>[0-9])\s+AND\s+(?<hi>[0-9])";
            var match = System.Text.RegularExpressions.Regex.Match(
                query.OriginalSql,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success ||
                !int.TryParse(match.Groups["lo"].Value, out lower) ||
                !int.TryParse(match.Groups["hi"].Value, out upper))
            {
                return false;
            }

            lower = Math.Clamp(lower, 0, 9);
            upper = Math.Clamp(upper, 0, 9);
            if (lower > upper)
                (lower, upper) = (upper, lower);

            return true;
        }

        private string BuildStringWithTrailingDigit(
            ColumnSchema column,
            string existing,
            int rowIndex,
            char digit)
        {
            var targetLength = !string.IsNullOrEmpty(existing)
                ? existing.Length
                : Math.Min(ResolveTargetStringLength(column), 64);
            if (targetLength <= 0)
                return string.Empty;

            var candidate = !string.IsNullOrEmpty(existing)
                ? existing
                : IsPhoneColumn(column)
                    ? BuildPhoneLikeString(column, rowIndex, targetLength)
                    : Convert.ToString(BuildSemanticString(column, rowIndex, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            if (candidate.Length < targetLength)
                candidate = candidate.PadLeft(targetLength, '0');
            if (candidate.Length > targetLength)
                candidate = candidate[^targetLength..];

            var chars = candidate.ToCharArray();
            chars[^1] = digit;
            return new string(chars);
        }

        private void ApplyScalarAvgSubqueryRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
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
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, column.ColumnName, rawValue);
        }

        private void ApplyLineRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var quantityColumnName = ResolveFirstExistingColumnName(schema, "QuantityOrdered", "Quantity");
            var unitPriceColumnName = ResolveFirstExistingColumnName(schema, "UnitPrice", "Price");
            var quantity = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, quantityColumnName, rowIndex, 0, 0, 2 + rowIndex)
                : 2 + rowIndex;
            var unitPrice = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, unitPriceColumnName, rowIndex, 0, 0, 11.25m + (rowIndex * 2.15m))
                : 11.25m + (rowIndex * 2.15m);

            // When UseMaxLengthMaxValueMode: cap both quantity and unitPrice so that
            // quantity * unitPrice stays within the computed column's (LineTotal) maximum.
            // Derive resultMax from source column types for reliability.
            if (UseMaxLengthMaxValueMode && quantity > 0)
            {
                var lineTotalColumn = schema.Columns.FirstOrDefault(c =>
                    c.IsComputed &&
                    TryBuildComputedProductColumnPlan(schema, c, out _));

                if (lineTotalColumn != null &&
                    TryBuildComputedProductColumnPlan(schema, lineTotalColumn, out var plan))
                {
                    // Get LineTotal's max from schema.
                    if (!TryGetPositiveColumnMax(plan.ResultColumn, out var resultMax))
                        resultMax = 99_999_999.99m; // Conservative fallback

                    // Also constrain resultMax by source column max products to avoid inflation.
                    var adjColMax = 0m;
                    if (TryGetPositiveColumnMax(plan.AnchorColumn, out var anchorColMax) &&
                        TryGetPositiveColumnMax(plan.AdjustableColumn, out adjColMax))
                    {
                        try
                        {
                            var sourceProductMax = anchorColMax * adjColMax;
                            if (sourceProductMax < resultMax)
                                resultMax = sourceProductMax;
                        }
                        catch (OverflowException) { }
                    }

                    // Cap quantity first
                    if (adjColMax > 0m)
                    {
                        var maxQuantity = resultMax / adjColMax;
                        if ((decimal)quantity > maxQuantity)
                            quantity = Math.Max(1, (int)Math.Floor(maxQuantity));
                    }

                    // Then cap unitPrice using the (possibly capped) quantity
                    var maxUnitPrice = resultMax / Math.Abs((decimal)quantity);
                    if (unitPrice > maxUnitPrice)
                    {
                        var unitPriceCol = schema.GetColumn(unitPriceColumnName);
                        var step = unitPriceCol != null ? GetNumericStep(unitPriceCol) : 0.01m;
                        var offset = unitPriceCol != null
                            ? BuildNonLinearNumericOffset(unitPriceCol, rowIndex, step, maxUnitPrice)
                            : rowIndex;
                        unitPrice = Math.Max(0m, maxUnitPrice - (offset * step));
                        if (unitPriceCol != null)
                        {
                            var normalized = SqlServerValueNormalizer.NormalizeValue(unitPriceCol, unitPrice);
                            if (TryConvertDecimal(normalized, out var nd)) unitPrice = nd;
                        }
                    }
                }
            }

            var unitCost = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "UnitCost", rowIndex, 3, 0, Math.Max(0.01m, unitPrice - 1.35m))
                : unitPrice - 1.35m;
            var lineTotal = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "LineTotal", rowIndex, 7, 0, (quantity * unitPrice) + (rowIndex * 0.5m))
                : (quantity * unitPrice) + (rowIndex * 0.5m);

            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityOrdered", quantity);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "Quantity", quantity);
            var quantityShipped = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityShipped", rowIndex, 2, 0, Math.Max(1, quantity - 1))
                : Math.Max(1, quantity - 1);
            var quantityReturned = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "QuantityReturned", rowIndex, 4, 0, rowIndex % 2)
                : rowIndex % 2;
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityShipped", quantityShipped);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "QuantityReturned", quantityReturned);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitPrice", unitPrice);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "Price", unitPrice);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "UnitCost", unitCost);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "LineTotal", lineTotal);
            var discountPercent = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "DiscountPercent", rowIndex, 11, 0, 0.01m + (rowIndex * 0.01m))
                : 0.01m + (rowIndex * 0.01m);
            var taxRate = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "TaxRate", rowIndex, 13, 0, 0.05m + (rowIndex * 0.01m))
                : 0.05m + (rowIndex * 0.01m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "DiscountPercent", discountPercent);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "TaxRate", taxRate);
        }

        private static string ResolveFirstExistingColumnName(TableSchema schema, params string[] preferredNames)
        {
            foreach (var preferredName in preferredNames)
            {
                var column = schema.Columns.FirstOrDefault(c =>
                    c.ColumnName.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
                if (column != null)
                    return column.ColumnName;
            }

            return preferredNames.FirstOrDefault() ?? string.Empty;
        }

        private void ApplyPaymentRowAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row,
            int rowIndex)
        {
            var amountPaid = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "AmountPaid", rowIndex, 0, 0, 35.00m + (rowIndex * 9.75m))
                : 35.00m + (rowIndex * 9.75m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "AmountPaid", amountPaid);
            var gatewayFee = UseMaxLengthMaxValueMode
                ? BuildSafeHighDecimal(query, schema, "GatewayFee", rowIndex, 4, 0, 0.50m + (rowIndex * 0.10m))
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
                ? BuildSafeHighInteger(query, schema, "QuantityOnHand", rowIndex, 0, 0, 90 - (rowIndex * 3))
                : 90 - (rowIndex * 3);
            var reorderLevel = UseMaxLengthMaxValueMode
                ? BuildSafeHighInteger(query, schema, "ReorderLevel", rowIndex, 2, 0, Math.Max(1, totalStock / 3))
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
                ? BuildSafeHighDecimal(query, schema, "RatingValue", rowIndex, 0, 0, fallback)
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
                ? BuildSafeHighDecimal(query, schema, "CostPrice", rowIndex, 0, 0, 8.50m + (rowIndex * 1.10m))
                : 8.50m + (rowIndex * 1.10m);
            SetNormalizedRowValueIfSafe(query, scenario, schema, row, "CostPrice", costPrice);
        }

        private void ApplyComputedProductSafetyAdjustments(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            GeneratedRow row)
        {
            var tableAlias = ResolveAliasesForTable(query, schema.TableName).FirstOrDefault() ?? schema.TableName;
            foreach (var computedColumn in schema.Columns.Where(c => c.IsComputed))
            {
                if (!TryBuildComputedProductColumnPlan(schema, computedColumn, out var plan) ||
                    !TryGetPositiveColumnMax(plan.ResultColumn, out var resultMax) ||
                    !TryConvertDecimal(row.GetValue(plan.AnchorColumn.ColumnName), out var anchorDecimal) ||
                    !TryConvertDecimal(row.GetValue(plan.AdjustableColumn.ColumnName), out var adjustableDecimal))
                {
                    continue;
                }

                if (IsComputedProductWithinRange(anchorDecimal, adjustableDecimal, resultMax))
                {
                    SetComputedProductPreviewValue(row, plan, anchorDecimal, adjustableDecimal);
                    continue;
                }

                if (TryBuildSafeComputedProductPair(
                        query,
                        scenario,
                        schema.TableName,
                        tableAlias,
                        plan,
                        resultMax,
                        anchorDecimal,
                        adjustableDecimal,
                        out var safeAnchorValue,
                        out var safeAnchorDecimal,
                        out var safeAdjustableValue,
                        out var safeAdjustableDecimal))
                {
                    row.SetValue(plan.AnchorColumn.ColumnName, safeAnchorValue);
                    row.SetValue(plan.AdjustableColumn.ColumnName, safeAdjustableValue);
                    SetComputedProductPreviewValue(row, plan, safeAnchorDecimal, safeAdjustableDecimal);
                }
                else
                {
                    Console.WriteLine($"[debug:max-guard-fail] Table={schema.TableName} Anchor={plan.AnchorColumn.ColumnName}({anchorDecimal}) Adjustable={plan.AdjustableColumn.ColumnName}({adjustableDecimal}) Max={resultMax}");
                }
            }
        }

        private bool TryBuildSafeComputedProductPair(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ComputedProductColumnPlan plan,
            decimal resultMax,
            decimal currentAnchor,
            decimal currentAdjustable,
            out object? safeAnchorValue,
            out decimal safeAnchorDecimal,
            out object? safeAdjustableValue,
            out decimal safeAdjustableDecimal)
        {
            safeAnchorValue = null;
            safeAnchorDecimal = 0m;
            safeAdjustableValue = null;
            safeAdjustableDecimal = 0m;

            if (IsMeasureLikeNumericColumn(plan.AdjustableColumn) &&
                currentAdjustable != 0m &&
                TryBuildBestFactorWithinLimit(query, scenario, tableName, tableAlias, plan.AnchorColumn, resultMax / Math.Abs(currentAdjustable), out safeAnchorValue, out safeAnchorDecimal) &&
                IsComputedProductWithinRange(safeAnchorDecimal, currentAdjustable, resultMax))
            {
                safeAdjustableValue = SqlServerValueNormalizer.NormalizeValue(plan.AdjustableColumn, currentAdjustable);
                safeAdjustableDecimal = currentAdjustable;
                return true;
            }

            if (currentAnchor != 0m &&
                TryBuildBestFactorWithinLimit(query, scenario, tableName, tableAlias, plan.AdjustableColumn, resultMax / Math.Abs(currentAnchor), out safeAdjustableValue, out safeAdjustableDecimal) &&
                IsComputedProductWithinRange(currentAnchor, safeAdjustableDecimal, resultMax))
            {
                safeAnchorValue = SqlServerValueNormalizer.NormalizeValue(plan.AnchorColumn, currentAnchor);
                safeAnchorDecimal = currentAnchor;
                return true;
            }

            return TryBuildSafeComputedProductPairFromCandidates(
                query,
                scenario,
                tableName,
                tableAlias,
                plan,
                resultMax,
                currentAnchor,
                currentAdjustable,
                out safeAnchorValue,
                out safeAnchorDecimal,
                out safeAdjustableValue,
                out safeAdjustableDecimal);
        }

        private bool TryBuildSafeComputedProductPairFromCandidates(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ComputedProductColumnPlan plan,
            decimal resultMax,
            decimal currentAnchor,
            decimal currentAdjustable,
            out object? safeAnchorValue,
            out decimal safeAnchorDecimal,
            out object? safeAdjustableValue,
            out decimal safeAdjustableDecimal)
        {
            safeAnchorValue = null;
            safeAnchorDecimal = 0m;
            safeAdjustableValue = null;
            safeAdjustableDecimal = 0m;

            var anchorCandidates = BuildSafeComputedProductFactorCandidates(
                    query,
                    scenario,
                    tableName,
                    tableAlias,
                    plan.AnchorColumn,
                    currentAnchor,
                    resultMax)
                .OrderByDescending(Math.Abs)
                .ToList();
            var baseAdjustableCandidates = BuildSafeComputedProductFactorCandidates(
                    query,
                    scenario,
                    tableName,
                    tableAlias,
                    plan.AdjustableColumn,
                    currentAdjustable,
                    resultMax)
                .ToList();
            var adjustableStep = GetNumericRangeStep(plan.AdjustableColumn);

            foreach (var rawAnchor in anchorCandidates)
            {
                if (!TryNormalizeNumericCandidate(plan.AnchorColumn, rawAnchor, out var normalizedAnchor, out var anchorDecimal) ||
                    anchorDecimal == 0m ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, plan.AnchorColumn, normalizedAnchor))
                {
                    continue;
                }

                var adjustableLimit = resultMax / Math.Abs(anchorDecimal);
                var adjustableCandidates = new List<decimal>(baseAdjustableCandidates)
                {
                    adjustableLimit,
                    adjustableLimit - adjustableStep,
                    -adjustableLimit,
                    -adjustableLimit + adjustableStep
                };

                foreach (var rawAdjustable in DeduplicateDecimalCandidates(adjustableCandidates).OrderByDescending(Math.Abs))
                {
                    if (!TryNormalizeNumericCandidate(plan.AdjustableColumn, rawAdjustable, out var normalizedAdjustable, out var adjustableDecimal) ||
                        !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, plan.AdjustableColumn, normalizedAdjustable) ||
                        !IsComputedProductWithinRange(anchorDecimal, adjustableDecimal, resultMax))
                    {
                        continue;
                    }

                    safeAnchorValue = normalizedAnchor;
                    safeAnchorDecimal = anchorDecimal;
                    safeAdjustableValue = normalizedAdjustable;
                    safeAdjustableDecimal = adjustableDecimal;
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<decimal> BuildSafeComputedProductFactorCandidates(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column,
            decimal currentValue,
            decimal resultMax)
        {
            var bounds = GetPositiveNumericBounds(query, scenario, tableName, tableAlias, column, excludedConditionKey: string.Empty);
            var step = GetNumericRangeStep(column);
            var candidates = new List<decimal>
            {
                currentValue,
                1m,
                -1m,
                2m,
                5m,
                10m,
                100m,
                1000m,
                10000m,
                resultMax,
                resultMax - step
            };

            candidates.AddRange(bounds.DiscreteValues);

            if (bounds.Lower.HasValue)
            {
                candidates.Add(bounds.Lower.Value);
                candidates.Add(bounds.Lower.Value + step);
            }

            if (bounds.Upper.HasValue)
            {
                candidates.Add(bounds.Upper.Value);
                candidates.Add(bounds.Upper.Value - step);
            }

            if (TryConvertDecimal(BuildMaxNumericValue(column, 0, query, tableAlias), out var columnMax))
            {
                candidates.Add(columnMax);
                candidates.Add(columnMax - step);
            }

            return DeduplicateDecimalCandidates(candidates)
                .Where(c => IsWithinNumericBounds(c, bounds));
        }

        private bool TryBuildBestFactorWithinLimit(
            ParsedQuery query,
            BranchScenario scenario,
            string tableName,
            string tableAlias,
            ColumnSchema column,
            decimal limit,
            out object? value,
            out decimal decimalValue)
        {
            value = null;
            decimalValue = 0m;
            if (limit <= 0m)
                return false;

            var bounds = GetPositiveNumericBounds(query, scenario, tableName, tableAlias, column, excludedConditionKey: string.Empty);
            var step = GetNumericRangeStep(column);
            var upper = bounds.Upper.HasValue ? Math.Min(bounds.Upper.Value, limit) : limit;
            var candidates = new List<decimal>
            {
                upper,
                upper - step,
                1m,
                -1m
            };
            candidates.AddRange(bounds.DiscreteValues.Where(v => Math.Abs(v) <= limit));
            if (bounds.Lower.HasValue)
                candidates.Add(bounds.Lower.Value);

            foreach (var candidate in DeduplicateDecimalCandidates(candidates).OrderByDescending(v => v))
            {
                if (!TryNormalizeNumericCandidate(column, candidate, out var normalized, out var normalizedDecimal) ||
                    Math.Abs(normalizedDecimal) > limit ||
                    !IsWithinNumericBounds(normalizedDecimal, bounds) ||
                    !SatisfiesPositiveColumnConstraints(query, scenario, tableName, tableAlias, column, normalized))
                {
                    continue;
                }

                value = normalized;
                decimalValue = normalizedDecimal;
                return true;
            }

            return false;
        }

        private static bool IsComputedProductWithinRange(decimal left, decimal right, decimal maxAbs)
        {
            try
            {
                return Math.Abs(left * right) <= maxAbs;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool TryGetPositiveColumnMax(ColumnSchema column, out decimal max)
        {
            max = 0m;
            switch (column.TypeCategory)
            {
                case DataTypeCategory.Integer:
                    max = GetMaxIntegerValue(column);
                    return max > 0m;
                case DataTypeCategory.Decimal:
                    max = GetMaxDecimalValue(column);
                    return max > 0m;
                case DataTypeCategory.Float:
                    max = (decimal)GetMaxFloatValue(column);
                    return max > 0m;
                default:
                    // Computed columns may have a TypeCategory that isn't Integer/Decimal/Float
                    // (schema reader may not resolve the type). Derive a conservative max from
                    // NumericPrecision/NumericScale if available, otherwise assume decimal(10,2).
                    // Using decimal(10,2) instead of decimal(18,2) prevents astronomically large
                    // resultMax values that make computed bound checks ineffective.
                    if (column.IsComputed)
                    {
                        var precision = column.NumericPrecision ?? 10;
                        var scale = column.NumericScale ?? 2;
                        decimal wholePart = 1m;
                        for (int i = 0; i < Math.Max(1, precision - scale); i++)
                            wholePart *= 10m;
                        var step = scale > 0 ? (decimal)Math.Pow(10, -scale) : 1m;
                        max = wholePart - step;
                        return max > 0m;
                    }
                    return false;
            }
        }

        private static void SetComputedProductPreviewValue(
            GeneratedRow row,
            ComputedProductColumnPlan plan,
            decimal anchor,
            decimal adjustable)
        {
            try
            {
                var normalized = SqlServerValueNormalizer.NormalizeValue(plan.ResultColumn, anchor * adjustable);
                if (normalized != null)
                    row.SetValue(plan.ResultColumn.ColumnName, normalized);
            }
            catch
            {
            }
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
            var raw = BuildAdjustedMaxInteger(query, schema, columnName, 0, 0, fallback);
            var safeCap = maxDigits <= 0
                ? raw
                : (int)Math.Min(raw, Math.Pow(10, Math.Max(1, maxDigits)) - 1);
            var column = schema.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            var offset = column != null
                ? BuildNonLinearNumericOffset(column, rowIndex, 1m, Math.Max(1, safeCap)) + Math.Max(0, extraOffset)
                : rowIndex;
            var candidate = safeCap - (int)offset;
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

            var queryAwareMax = BuildAdjustedMaxDecimal(query, schema, columnName, 0, 0, fallback);
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var integerDigits = column.NumericPrecision.HasValue
                ? Math.Max(1, column.NumericPrecision.Value - scale)
                : Math.Max(1, maxIntegerDigits);
            var cappedIntegerDigits = maxIntegerDigits <= 0
                ? integerDigits
                : Math.Min(integerDigits, Math.Max(1, maxIntegerDigits));

            decimal wholePart = 1m;
            for (int i = 0; i < cappedIntegerDigits; i++)
            {
                wholePart *= 10m;
            }

            var step = GetNumericStep(column);
            var safeMax = wholePart - step;
            safeMax = Math.Min(safeMax, queryAwareMax);
            var offset = BuildNonLinearNumericOffset(column, rowIndex, step, safeMax) + Math.Max(0, extraOffset);
            var candidate = safeMax - (offset * step);
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

        private int ApplyPinnedUniqueKeyRowCountLimit(
            ParsedQuery query,
            BranchScenario scenario,
            TableSchema schema,
            string tableAlias,
            int rowCount,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            if (rowCount <= 1 || selfReferencePlans.ContainsKey(schema.TableName))
                return rowCount;

            foreach (var column in EnumerateSingleColumnUniqueKeyColumns(schema))
            {
                var domainLimit = FindPositiveDirectUniqueKeyConditions(query, scenario, tableAlias, column.ColumnName)
                    .Select(GetDirectUniqueKeyDomainSize)
                    .Concat(FindPositiveExpressionUniqueKeyDomainSizes(query, scenario, tableAlias, schema.TableName, column.ColumnName))
                    .Where(count => count > 0)
                    .DefaultIfEmpty(0)
                    .Min();

                if (domainLimit > 0)
                {
                    return Math.Min(rowCount, domainLimit);
                }
            }

            return rowCount;
        }

        private static IEnumerable<int> FindPositiveExpressionUniqueKeyDomainSizes(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string tableName,
            string columnName)
        {
            foreach (var condition in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if (condition.Operator != ComparisonOp.Equal ||
                    condition.HasSubquery ||
                    condition.IsColumnComparison ||
                    !TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) ||
                    !desiredTruth ||
                    !TryGetNonTargetComparisonExpression(condition, tableAlias, tableName, columnName, query.AliasToTableMap, out var expression) ||
                    !TryInferFiniteNumericExpressionDomainSize(expression, out var domainSize))
                {
                    continue;
                }

                yield return domainSize;
            }
        }

        private static bool TryGetNonTargetComparisonExpression(
            ConditionInfo condition,
            string tableAlias,
            string tableName,
            string columnName,
            IReadOnlyDictionary<string, string> aliasMap,
            out ScalarExpressionInfo? expression)
        {
            expression = null;
            var leftTargetsColumn = ExpressionReferencesColumn(condition.LeftExpression, tableAlias, tableName, columnName, aliasMap);
            var rightTargetsColumn = ExpressionReferencesColumn(condition.RightExpression, tableAlias, tableName, columnName, aliasMap);

            if (leftTargetsColumn && !rightTargetsColumn)
            {
                expression = condition.RightExpression;
                return expression != null;
            }

            if (rightTargetsColumn && !leftTargetsColumn)
            {
                expression = condition.LeftExpression;
                return expression != null;
            }

            return false;
        }

        private static bool TryInferFiniteNumericExpressionDomainSize(
            ScalarExpressionInfo? expression,
            out int domainSize)
        {
            if (TryInferFiniteNumericExpressionDomainValues(expression, out var values))
            {
                domainSize = values.Count;
                return true;
            }

            domainSize = 0;
            return false;
        }

        private static bool TryInferFiniteNumericExpressionDomainValues(
            ScalarExpressionInfo? expression,
            out List<decimal> domainValues)
        {
            var values = new HashSet<decimal>();
            TryCollectFiniteNumericExpressionValues(expression, values);
            TryCollectFiniteNumericExpressionTextValues(expression?.Text, values);

            domainValues = values
                .OrderBy(v => v)
                .ToList();
            return domainValues.Count > 0;
        }

        private static bool TryCollectFiniteNumericExpressionTextValues(string? expressionText, HashSet<decimal> values)
        {
            if (string.IsNullOrWhiteSpace(expressionText))
                return false;

            var collectedAny = false;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         expressionText,
                         @"\bBETWEEN\s+(?<lo>-?\d+)\s+AND\s+(?<hi>-?\d+)",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(match.Groups["lo"].Value, out var lower) ||
                    !int.TryParse(match.Groups["hi"].Value, out var upper))
                {
                    continue;
                }

                var start = Math.Min(lower, upper);
                var end = Math.Max(lower, upper);
                if (end - start > 100)
                    continue;

                for (var value = start; value <= end; value++)
                {
                    values.Add(value);
                }

                collectedAny = true;
            }

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         expressionText,
                         @"\b(?:THEN|ELSE)\s+(?<value>-?\d+)\b",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (decimal.TryParse(
                        match.Groups["value"].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    values.Add(value);
                    collectedAny = true;
                }
            }

            return collectedAny;
        }

        private static bool TryCollectFiniteNumericExpressionValues(
            ScalarExpressionInfo? expression,
            HashSet<decimal> values)
        {
            switch (expression)
            {
                case LiteralScalarExpressionInfo literal when TryConvertDecimal(literal.Value, out var literalValue):
                    values.Add(literalValue);
                    return true;

                case CaseScalarExpressionInfo caseExpression:
                {
                    var collectedAny = false;
                    foreach (var whenClause in caseExpression.WhenClauses)
                    {
                        collectedAny |= TryCollectCaseWhenDomainValues(whenClause, values);
                    }

                    if (caseExpression.ElseExpression != null)
                    {
                        collectedAny |= TryCollectFiniteNumericExpressionValues(caseExpression.ElseExpression, values);
                    }

                    return collectedAny;
                }

                default:
                    return false;
            }
        }

        private static bool TryCollectCaseWhenDomainValues(
            CaseWhenClauseInfo whenClause,
            HashSet<decimal> values)
        {
            if (TryCollectFiniteNumericExpressionValues(whenClause.ThenExpression, values))
                return true;

            return TryCollectPredicateRangeDomainValues(whenClause.Predicate, whenClause.ThenExpression, values);
        }

        private static bool TryCollectPredicateRangeDomainValues(
            PredicateExpression? predicate,
            ScalarExpressionInfo? resultExpression,
            HashSet<decimal> values)
        {
            return predicate switch
            {
                PredicateLeafExpression leaf =>
                    TryCollectConditionRangeDomainValues(leaf.Condition, resultExpression, values),
                PredicateBinaryExpression binary when binary.Operator == LogicalOp.And =>
                    TryCollectPredicateRangeDomainValues(binary.Left, resultExpression, values) ||
                    TryCollectPredicateRangeDomainValues(binary.Right, resultExpression, values),
                PredicateBinaryExpression binary when binary.Operator == LogicalOp.Or =>
                    TryCollectPredicateRangeDomainValues(binary.Left, resultExpression, values) |
                    TryCollectPredicateRangeDomainValues(binary.Right, resultExpression, values),
                _ => false
            };
        }

        private static bool TryCollectConditionRangeDomainValues(
            ConditionInfo condition,
            ScalarExpressionInfo? resultExpression,
            HashSet<decimal> values)
        {
            if (condition.Operator != ComparisonOp.Between ||
                resultExpression == null ||
                !ScalarExpressionTextEquals(condition.LeftExpression, resultExpression) ||
                !TryConvertDecimal(condition.Value, out var lower) ||
                !TryConvertDecimal(condition.SecondValue, out var upper))
            {
                return false;
            }

            var start = (int)Math.Ceiling(Math.Min(lower, upper));
            var end = (int)Math.Floor(Math.Max(lower, upper));
            if (end < start || end - start > 100)
                return false;

            for (var value = start; value <= end; value++)
            {
                values.Add(value);
            }

            return true;
        }

        private static bool ScalarExpressionTextEquals(ScalarExpressionInfo? left, ScalarExpressionInfo? right)
        {
            var leftText = NormalizeScalarExpressionText(left?.Text);
            var rightText = NormalizeScalarExpressionText(right?.Text);
            return !string.IsNullOrWhiteSpace(leftText) &&
                   leftText.Equals(rightText, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeScalarExpressionText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return new string(text
                .Where(ch => !char.IsWhiteSpace(ch) && ch is not '[' and not ']')
                .ToArray())
                .ToUpperInvariant();
        }

        private static IEnumerable<ColumnSchema> EnumerateSingleColumnUniqueKeyColumns(TableSchema schema)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in schema.Columns)
            {
                if ((column.IsIdentity || column.IsPrimaryKey) &&
                    !column.IsComputed &&
                    !column.IsStoreGenerated &&
                    seen.Add(column.ColumnName))
                {
                    yield return column;
                }
            }

            if (schema.PrimaryKey?.Columns.Count == 1)
            {
                var column = schema.GetColumn(schema.PrimaryKey.Columns[0]);
                if (column != null && !column.IsComputed && !column.IsStoreGenerated && seen.Add(column.ColumnName))
                {
                    yield return column;
                }
            }

            foreach (var unique in schema.UniqueConstraints.Where(u => u.Columns.Count == 1))
            {
                var column = schema.GetColumn(unique.Columns[0]);
                if (column != null && !column.IsComputed && !column.IsStoreGenerated && seen.Add(column.ColumnName))
                {
                    yield return column;
                }
            }
        }

        private static IEnumerable<ConditionInfo> FindPositiveDirectUniqueKeyConditions(
            ParsedQuery query,
            BranchScenario scenario,
            string tableAlias,
            string columnName)
        {
            return query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Where(condition =>
                    IsDirectUniqueKeyLiteralPredicate(condition) &&
                    TryGetDesiredTruthForCondition(scenario, condition, defaultTruth: true, out var desiredTruth) &&
                    desiredTruth &&
                    IsConditionTargetingColumn(query, condition, tableAlias, columnName));
        }

        private static bool IsDirectUniqueKeyLiteralPredicate(ConditionInfo condition)
        {
            if (condition.HasSubquery ||
                condition.IsColumnComparison ||
                HasScalarSubqueryPlaceholder(condition))
            {
                return false;
            }

            return condition.Operator switch
            {
                ComparisonOp.Equal => !string.IsNullOrWhiteSpace(condition.Value) &&
                                      IsDirectLiteralComparisonExpression(condition),
                ComparisonOp.In => condition.InValues.Count > 0,
                _ => false
            };
        }

        private static bool IsDirectLiteralComparisonExpression(ConditionInfo condition)
        {
            return condition.LeftExpression is LiteralScalarExpressionInfo ||
                   condition.RightExpression is LiteralScalarExpressionInfo;
        }

        private static int GetDirectUniqueKeyDomainSize(ConditionInfo condition) =>
            condition.Operator switch
            {
                ComparisonOp.Equal => 1,
                ComparisonOp.In => condition.InValues.Count,
                _ => 0
            };

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

            if (query.Subqueries.Any(s => s.SubquerySql.Contains("AVG", StringComparison.OrdinalIgnoreCase)) &&
                query.PredicateScopes.SelectMany(s => s.Conditions)
                    .Any(c => c.Operator is ComparisonOp.GreaterThan or ComparisonOp.LessThan))
            {
                foreach (var schema in schemas.Values)
                {
                    requirements[schema.TableName] = Math.Max(
                        requirements.GetValueOrDefault(schema.TableName),
                        2);
                }
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
            BranchScenario scenario,
            TableSchema schema,
            int startId,
            int rowCount)
        {
            tableRowIds[tableName] = ResolveGeneratedNumericKeyValues(scenario, schema, tableName, startId, rowCount);
        }

        private static void RegisterReferenceableIds(
            Dictionary<string, List<int>> tableRowIds,
            string tableName,
            BranchScenario scenario,
            TableSchema schema,
            int startId,
            int rowCount,
            Dictionary<string, SelfReferencePlan> selfReferencePlans)
        {
            var generatedIds = ResolveGeneratedNumericKeyValues(scenario, schema, tableName, startId, rowCount);

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

        private static List<int> ResolveGeneratedNumericKeyValues(
            BranchScenario scenario,
            TableSchema schema,
            string tableName,
            int fallbackStartId,
            int rowCount)
        {
            if (rowCount <= 0)
                return new List<int>();

            if (schema.PrimaryKey?.Columns.Count == 1 &&
                scenario.TableRows.TryGetValue(tableName, out var rows) &&
                rows.Count > 0)
            {
                var keyColumn = schema.PrimaryKey.Columns[0];
                var currentRows = rows
                    .Skip(Math.Max(0, rows.Count - rowCount))
                    .Take(rowCount)
                    .ToList();
                var ids = currentRows
                    .Select(r => r.GetValue(keyColumn))
                    .Where(v => v != null && v != DBNull.Value)
                    .Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))
                    .Where(v => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                    .Select(v => int.Parse(v!, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture))
                    .ToList();

                if (ids.Count == rowCount)
                    return ids;
            }

            return Enumerable
                .Range(fallbackStartId, Math.Max(0, rowCount))
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
            foreach (var agg in query.Aggregates)
            {
                if (agg.TableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(agg.Expression) &&
                    agg.Expression.Contains(alias + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var cond in query.PredicateScopes.SelectMany(s => s.Conditions))
            {
                if (TryFindScalarComparisonSubquery(query, cond, out var subquery) &&
                    subquery.SubquerySql.Contains("AVG", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(alias) || subquery.SubquerySql.Contains(alias + ".", StringComparison.OrdinalIgnoreCase) || subquery.SubquerySql.Contains(" " + alias + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
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


        private bool IsColumnProtected(ParsedQuery query, BranchScenario scenario, TableSchema tableSchema, ColumnSchema column)
        {
            if (column.IsPrimaryKey) return true;
            if (tableSchema.ForeignKeys.Any(fk => fk.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase))) return true;

            var tableAliases = ResolveAliasesForTable(query, tableSchema.TableName).ToList();

            // Protect if it's in a Join
            if (query.Joins.Any(j => tableAliases.Any(tableAlias =>
                (j.LeftTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) && j.LeftColumn.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)) ||
                (j.RightTableAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase) && j.RightColumn.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase))))) return true;

            // Protect only predicate columns whose truth can be changed by shuffling.
            // Length-only and single-character-presence predicates are still safe to diversify.
            if (tableAliases.Any(tableAlias =>
                    HasShuffleUnsafePredicateColumn(query, scenario, tableAlias, column.ColumnName)))
            {
                return true;
            }

            return false;
        }

        private void ValidateScenarioData(BranchScenario scenario)
        {
            // Implementation of final validation phase to catch any overflows before script generation
            foreach (var tableEntry in scenario.TableRows)
            {
                foreach (var row in tableEntry.Value)
                {
                    // Basic length checks can be added here
                }
            }
        }

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

