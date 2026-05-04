using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Parsing;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Produces exact user-visible scenarios from the preserved boolean predicate structure.
    /// Scenarios are derived from minimal truth assignments, so OR/NOT/subquery groups are
    /// represented without over-generation or under-generation.
    /// </summary>
    public class BranchCoverageAnalyzer
    {
        private int _nextId = 1;

        public List<BranchScenario> AnalyzeBranches(ParsedQuery query)
        {
            _nextId = 1;
            var scenarios = new List<BranchScenario>();
            var conditionByKey = query.PredicateScopes
                .SelectMany(s => s.Conditions)
                .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var positiveTruthMap = BuildGlobalPositiveTruthMap(query.PredicateScopes, conditionByKey);
            scenarios.Add(CreatePositiveScenario(positiveTruthMap, conditionByKey));

            foreach (var scope in query.PredicateScopes.Where(s => s.Root != null))
            {
                var falseAssignments = PredicateTruthPlanner.GetMinimalAssignments(scope.Root, desiredTruth: false);
                foreach (var assignment in falseAssignments)
                {
                    var mergedTruthMap = MergeTruthMaps(positiveTruthMap, assignment);
                    if (!IsFeasibleTruthMap(mergedTruthMap, conditionByKey))
                        continue;

                    var scenarioType = ClassifyNegativeScenario(scope, assignment, conditionByKey);
                    scenarios.Add(CreateNegativeScenario(scope, scenarioType, assignment, mergedTruthMap, conditionByKey));
                }
            }

            AppendSubqueryScenarios(query.Subqueries, positiveTruthMap, scenarios, conditionByKey);

            foreach (var join in query.Joins.Where(j => j.Type == JoinType.Left))
            {
                if (CanCreateJoinMissScenario(join, query))
                {
                    scenarios.Add(CreateJoinMissScenario(join, positiveTruthMap));
                }
            }

            return DeduplicateAndRenumber(scenarios);
        }

        private BranchScenario CreatePositiveScenario(
            Dictionary<string, bool> truthMap,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var testedConditions = truthMap
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new ScenarioAssignment(
                    kvp.Key,
                    kvp.Value,
                    conditionByKey.TryGetValue(kvp.Key, out var condition) ? condition : null))
                .Select(BuildReadableAssignment)
                .ToList();

            return new BranchScenario
            {
                Id = _nextId++,
                Name = "Positive: query returns rows",
                Description = testedConditions.Count == 0
                    ? "Canonical positive path. Every required WHERE, HAVING, and subquery predicate is satisfied so the query should return rows."
                    : $"Canonical positive path uses these exact predicate assignments: {string.Join("; ", testedConditions)}.",
                Type = ScenarioType.Positive,
                ExpectedToReturnRows = true,
                PredicateTruthMap = truthMap,
                TestedCondition = string.Join(" | ", testedConditions),
                TestedConditions = testedConditions
            };
        }

        private BranchScenario CreateNegativeScenario(
            PredicateScope scope,
            ScenarioType type,
            IReadOnlyDictionary<string, bool> falsifyingAssignment,
            Dictionary<string, bool> truthMap,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var items = falsifyingAssignment
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new ScenarioAssignment(
                    kvp.Key,
                    kvp.Value,
                    conditionByKey.TryGetValue(kvp.Key, out var condition) ? condition : null))
                .ToList();

            var testedConditions = items
                .Select(BuildReadableAssignment)
                .ToList();

            var shortLabel = items.Count == 1
                ? BuildReadableAssignment(items[0])
                : $"{items.Count} exact predicate assignments";

            var scopePrefix = type == ScenarioType.HavingNegative ? "HAVING negative" :
                type == ScenarioType.SubqueryMiss ? "Subquery negative" :
                scope.Source == ConditionSource.JoinOn ? "JOIN negative" :
                "WHERE negative";

            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"{scopePrefix}: {shortLabel}",
                Description = $"{scope.ScopeLabel} is forced to FALSE by these exact predicate assignments: {string.Join("; ", testedConditions)}.",
                Type = type,
                ScopeLabel = scope.ScopeLabel,
                ExpectedToReturnRows = false,
                PredicateTruthMap = truthMap,
                TestedCondition = string.Join(" | ", testedConditions),
                TestedConditions = testedConditions
            };
        }

        private IEnumerable<BranchScenario> CreateBoundaryScenarios(
            PredicateScope scope,
            IReadOnlyDictionary<string, bool> globalPositiveTruthMap)
        {
            if (scope.Root == null)
                yield break;

            var positiveAssignments = PredicateTruthPlanner.GetMinimalAssignments(scope.Root, desiredTruth: true);
            var rangeLeaves = PredicateTruthPlanner.EnumerateLeaves(scope.Root)
                .Where(l => IsRangeCondition(l.Condition))
                .ToList();

            foreach (var leaf in rangeLeaves)
            {
                var supportingAssignment = positiveAssignments
                    .FirstOrDefault(a => a.TryGetValue(leaf.Condition.Key, out var desiredTruth) && desiredTruth);

                if (supportingAssignment == null)
                    continue;

                var truthMap = MergeTruthMaps(globalPositiveTruthMap, supportingAssignment);
                var readable = leaf.Condition.ToString();

                yield return new BranchScenario
                {
                    Id = _nextId++,
                    Name = $"Boundary: {readable} at exact limit",
                    Description = $"{scope.ScopeLabel} stays TRUE while [{readable}] is generated at its exact boundary value.",
                    Type = ScenarioType.Boundary,
                    ScopeLabel = scope.ScopeLabel,
                    ExpectedToReturnRows = true,
                    PredicateTruthMap = truthMap,
                    BoundaryConditionKey = leaf.Condition.Key,
                    TestedCondition = readable,
                    TestedConditions = new List<string> { readable }
                };
            }
        }

        private BranchScenario CreateJoinMissScenario(
            JoinInfo join,
            IReadOnlyDictionary<string, bool> positiveTruthMap)
        {
            var readable = $"{join.LeftTableAlias}.{join.LeftColumn} = {join.RightTableAlias}.{join.RightColumn}";
            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"LEFT JOIN miss: no row for {join.RightTableAlias}",
                Description = $"The LEFT JOIN row for alias [{join.RightTableAlias}] is intentionally absent while the rest of the query stays on its positive path.",
                Type = ScenarioType.JoinMiss,
                ScopeLabel = "Join",
                ExpectedToReturnRows = true,
                PredicateTruthMap = new Dictionary<string, bool>(positiveTruthMap, StringComparer.OrdinalIgnoreCase),
                JoinKey = BuildJoinKey(join),
                TestedCondition = readable,
                TestedConditions = new List<string> { readable }
            };
        }

        private static ScenarioType ClassifyNegativeScenario(
            PredicateScope scope,
            IReadOnlyDictionary<string, bool> assignment,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            if (scope.Source == ConditionSource.Having)
                return ScenarioType.HavingNegative;

            if (assignment.Count > 0 &&
                assignment.Keys.All(key =>
                    conditionByKey.TryGetValue(key, out var condition) &&
                    condition.IsSubqueryPredicate))
            {
                return ScenarioType.SubqueryMiss;
            }

            return ScenarioType.WhereNegative;
        }

        private static Dictionary<string, bool> BuildGlobalPositiveTruthMap(
            IEnumerable<PredicateScope> scopes,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in scopes.Where(s => s.Root != null))
            {
                foreach (var kvp in ChoosePositiveAssignment(scope, conditionByKey))
                {
                    map[kvp.Key] = kvp.Value;
                }
            }

            return map;
        }

        private static Dictionary<string, bool> ChoosePositiveAssignment(
            PredicateScope scope,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var positiveAssignments = PredicateTruthPlanner.GetMinimalAssignments(scope.Root, desiredTruth: true)
                .OrderBy(a => ScorePositiveAssignment(a, conditionByKey))
                .ThenBy(a => a.Count)
                .ThenBy(BuildAssignmentKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chosen = positiveAssignments.FirstOrDefault() ??
                         new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            chosen = new Dictionary<string, bool>(chosen, StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in positiveAssignments
                         .SelectMany(a => a)
                         .Where(kvp =>
                             kvp.Value &&
                             conditionByKey.TryGetValue(kvp.Key, out var condition) &&
                             condition.AggregateFunc.HasValue)
                         .GroupBy(kvp => $"{kvp.Key}:{kvp.Value}", StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .OrderBy(kvp => ScorePositiveAssignment(
                             new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                             {
                                 [kvp.Key] = kvp.Value
                             },
                             conditionByKey))
                         .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (chosen.TryGetValue(candidate.Key, out var existing))
                {
                    if (existing != candidate.Value)
                        continue;

                    continue;
                }

                var trial = new Dictionary<string, bool>(chosen, StringComparer.OrdinalIgnoreCase)
                {
                    [candidate.Key] = candidate.Value
                };

                if (IsFeasibleTruthMap(trial, conditionByKey))
                {
                    chosen = trial;
                }
            }

            return chosen;
        }

        private static int ScorePositiveAssignment(
            IReadOnlyDictionary<string, bool> assignment,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var score = 0;
            foreach (var (key, desiredTruth) in assignment)
            {
                if (!conditionByKey.TryGetValue(key, out var condition))
                    continue;

                if (desiredTruth &&
                    condition.Operator == ComparisonOp.IsNull)
                {
                    score += 100;
                }

                if (desiredTruth &&
                    condition.Operator is ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual &&
                    (condition.Value.Contains("SELECT", StringComparison.OrdinalIgnoreCase) ||
                     condition.RightExpression?.Text.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true))
                {
                    score -= 25;
                }

                if (desiredTruth &&
                    condition.Operator is ComparisonOp.Between or ComparisonOp.In or ComparisonOp.Like or
                        ComparisonOp.Equal or ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual or
                        ComparisonOp.LessThan or ComparisonOp.LessThanOrEqual)
                {
                    score -= 1;
                }
            }

            return score;
        }

        private static string BuildAssignmentKey(IReadOnlyDictionary<string, bool> assignment)
        {
            return string.Join("|",
                assignment
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => $"{kvp.Key}:{(kvp.Value ? "1" : "0")}"));
        }

        private static Dictionary<string, bool> MergeTruthMaps(
            IReadOnlyDictionary<string, bool> baseline,
            IReadOnlyDictionary<string, bool> overrideMap)
        {
            var merged = new Dictionary<string, bool>(baseline, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in overrideMap)
            {
                merged[kvp.Key] = kvp.Value;
            }

            return merged;
        }

        private static bool IsFeasibleTruthMap(
            IReadOnlyDictionary<string, bool> truthMap,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            var ranges = new Dictionary<string, NumericFeasibilityRange>(StringComparer.OrdinalIgnoreCase);

            foreach (var (conditionKey, desiredTruth) in truthMap)
            {
                if (!conditionByKey.TryGetValue(conditionKey, out var condition) ||
                    !TryBuildNumericRangeConstraint(condition, desiredTruth, out var targetKey, out var constraint))
                {
                    continue;
                }

                if (!ranges.TryGetValue(targetKey, out var range))
                {
                    range = new NumericFeasibilityRange();
                    ranges[targetKey] = range;
                }

                range.Apply(constraint);
                if (!range.IsFeasible)
                    return false;
            }

            return true;
        }

        private static bool TryBuildNumericRangeConstraint(
            ConditionInfo condition,
            bool desiredTruth,
            out string targetKey,
            out NumericRangeConstraint constraint)
        {
            targetKey = string.Empty;
            constraint = new NumericRangeConstraint();

            if (condition.HasSubquery ||
                condition.IsColumnComparison ||
                string.IsNullOrWhiteSpace(condition.ColumnName))
            {
                return false;
            }

            var effectiveTruth = condition.IsNegated ? !desiredTruth : desiredTruth;
            if (!TryParseDecimalLiteral(condition.Value, out var value))
                return false;

            targetKey = condition.AggregateFunc.HasValue
                ? $"AGG:{condition.AggregateFunc}:{condition.TableAlias}.{condition.ColumnName}"
                : $"{condition.TableAlias}.{condition.ColumnName}";
            switch (condition.Operator)
            {
                case ComparisonOp.GreaterThan:
                    constraint = effectiveTruth
                        ? NumericRangeConstraint.LowerBound(value, inclusive: false)
                        : NumericRangeConstraint.UpperBound(value, inclusive: true);
                    return true;

                case ComparisonOp.GreaterThanOrEqual:
                    constraint = effectiveTruth
                        ? NumericRangeConstraint.LowerBound(value, inclusive: true)
                        : NumericRangeConstraint.UpperBound(value, inclusive: false);
                    return true;

                case ComparisonOp.LessThan:
                    constraint = effectiveTruth
                        ? NumericRangeConstraint.UpperBound(value, inclusive: false)
                        : NumericRangeConstraint.LowerBound(value, inclusive: true);
                    return true;

                case ComparisonOp.LessThanOrEqual:
                    constraint = effectiveTruth
                        ? NumericRangeConstraint.UpperBound(value, inclusive: true)
                        : NumericRangeConstraint.LowerBound(value, inclusive: false);
                    return true;

                case ComparisonOp.Between when effectiveTruth &&
                                               TryParseDecimalLiteral(condition.SecondValue, out var secondValue):
                    constraint = NumericRangeConstraint.Between(value, secondValue);
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseDecimalLiteral(string? value, out decimal result)
        {
            result = 0m;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim().Trim('\'');
            return decimal.TryParse(
                trimmed,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);
        }

        private static bool IsRangeCondition(ConditionInfo condition)
        {
            return condition.Operator is
                ComparisonOp.GreaterThan or
                ComparisonOp.GreaterThanOrEqual or
                ComparisonOp.LessThan or
                ComparisonOp.LessThanOrEqual or
                ComparisonOp.Between;
        }

        private static bool CanCreateJoinMissScenario(JoinInfo join, ParsedQuery query)
        {
            var alias = join.RightTableAlias;
            if (string.IsNullOrWhiteSpace(alias))
                return false;

            return !query.PredicateScopes
                .Where(s => s.Source != ConditionSource.JoinOn)
                .SelectMany(s => s.Conditions)
                .Any(c => MatchesAlias(query, c, alias));
        }

        private static bool MatchesAlias(ParsedQuery query, ConditionInfo condition, string alias)
        {
            if (string.Equals(condition.TableAlias, alias, StringComparison.OrdinalIgnoreCase))
                return true;

            var resolvedAlias = query.ResolveAlias(alias);
            return !string.IsNullOrWhiteSpace(condition.TableAlias) &&
                   string.Equals(query.ResolveAlias(condition.TableAlias), resolvedAlias, StringComparison.OrdinalIgnoreCase);
        }

        private List<BranchScenario> DeduplicateAndRenumber(List<BranchScenario> scenarios)
        {
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueScenarios = new List<BranchScenario>();

            foreach (var scenario in scenarios)
            {
                if (!seenKeys.Add(BuildScenarioKey(scenario)))
                    continue;

                scenario.Id = uniqueScenarios.Count + 1;
                uniqueScenarios.Add(scenario);
            }

            _nextId = uniqueScenarios.Count + 1;
            return uniqueScenarios;
        }

        private void AppendSubqueryScenarios(
            IEnumerable<SubqueryInfo> subqueries,
            IReadOnlyDictionary<string, bool> positiveTruthMap,
            List<BranchScenario> scenarios,
            IReadOnlyDictionary<string, ConditionInfo> conditionByKey)
        {
            foreach (var subquery in subqueries)
            {
                if (subquery.WherePredicateScope?.Root != null &&
                    !string.IsNullOrWhiteSpace(subquery.PredicateConditionKey))
                {
                    var shouldReturnRows = IsNegativeSubqueryOperator(subquery.Operator);
                    var assignments = PredicateTruthPlanner.GetMinimalAssignments(
                        subquery.WherePredicateScope.Root,
                        desiredTruth: shouldReturnRows);

                    foreach (var assignment in assignments)
                    {
                        var truthMap = MergeTruthMaps(positiveTruthMap, assignment);
                        truthMap[subquery.PredicateConditionKey] = false;
                        if (!IsFeasibleTruthMap(truthMap, conditionByKey))
                            continue;

                        scenarios.Add(CreateNegativeScenario(
                            subquery.WherePredicateScope,
                            ScenarioType.SubqueryMiss,
                            assignment,
                            truthMap,
                            conditionByKey));
                    }
                }

                AppendSubqueryScenarios(subquery.NestedSubqueries, positiveTruthMap, scenarios, conditionByKey);
            }
        }

        private static bool IsNegativeSubqueryOperator(SubqueryOperator op)
        {
            return op is SubqueryOperator.NotExists or SubqueryOperator.NotIn;
        }

        private static string BuildScenarioKey(BranchScenario scenario)
        {
            var truthMapKey = string.Join("|",
                scenario.PredicateTruthMap
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => $"{kvp.Key}:{(kvp.Value ? "1" : "0")}"));

            return string.Join("|",
                scenario.Type,
                scenario.ScopeLabel,
                scenario.BoundaryConditionKey ?? string.Empty,
                scenario.JoinKey ?? string.Empty,
                truthMapKey);
        }

        private static string BuildJoinKey(JoinInfo join)
        {
            return string.Join("|",
                join.Type,
                join.LeftTableAlias,
                join.LeftColumn,
                join.RightTableAlias,
                join.RightColumn);
        }

        private static string BuildReadableAssignment(ScenarioAssignment assignment)
        {
            var predicateText = assignment.Condition?.ToString() ?? assignment.ConditionKey;
            return $"force {(assignment.DesiredTruth ? "TRUE" : "FALSE")}: {predicateText}";
        }

        private sealed class NumericFeasibilityRange
        {
            private decimal? _lower;
            private bool _lowerInclusive = true;
            private decimal? _upper;
            private bool _upperInclusive = true;

            public bool IsFeasible
            {
                get
                {
                    if (!_lower.HasValue || !_upper.HasValue)
                        return true;
                    if (_lower.Value < _upper.Value)
                        return true;
                    if (_lower.Value > _upper.Value)
                        return false;
                    return _lowerInclusive && _upperInclusive;
                }
            }

            public void Apply(NumericRangeConstraint constraint)
            {
                if (constraint.Lower.HasValue)
                    ApplyLower(constraint.Lower.Value, constraint.LowerInclusive);
                if (constraint.Upper.HasValue)
                    ApplyUpper(constraint.Upper.Value, constraint.UpperInclusive);
            }

            private void ApplyLower(decimal value, bool inclusive)
            {
                if (!_lower.HasValue || value > _lower.Value)
                {
                    _lower = value;
                    _lowerInclusive = inclusive;
                    return;
                }

                if (value == _lower.Value)
                {
                    _lowerInclusive = _lowerInclusive && inclusive;
                }
            }

            private void ApplyUpper(decimal value, bool inclusive)
            {
                if (!_upper.HasValue || value < _upper.Value)
                {
                    _upper = value;
                    _upperInclusive = inclusive;
                    return;
                }

                if (value == _upper.Value)
                {
                    _upperInclusive = _upperInclusive && inclusive;
                }
            }
        }

        private readonly struct NumericRangeConstraint
        {
            public decimal? Lower { get; init; }
            public bool LowerInclusive { get; init; }
            public decimal? Upper { get; init; }
            public bool UpperInclusive { get; init; }

            public static NumericRangeConstraint LowerBound(decimal value, bool inclusive) =>
                new() { Lower = value, LowerInclusive = inclusive };

            public static NumericRangeConstraint UpperBound(decimal value, bool inclusive) =>
                new() { Upper = value, UpperInclusive = inclusive };

            public static NumericRangeConstraint Between(decimal lower, decimal upper) =>
                new()
                {
                    Lower = Math.Min(lower, upper),
                    LowerInclusive = true,
                    Upper = Math.Max(lower, upper),
                    UpperInclusive = true
                };
        }

        private sealed record ScenarioAssignment(
            string ConditionKey,
            bool DesiredTruth,
            ConditionInfo? Condition);
    }
}
