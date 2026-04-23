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

            var positiveTruthMap = BuildGlobalPositiveTruthMap(query.PredicateScopes);
            scenarios.Add(CreatePositiveScenario(positiveTruthMap));

            foreach (var scope in query.PredicateScopes.Where(s => s.Root != null))
            {
                var falseAssignments = PredicateTruthPlanner.GetMinimalAssignments(scope.Root, desiredTruth: false);
                foreach (var assignment in falseAssignments)
                {
                    var mergedTruthMap = MergeTruthMaps(positiveTruthMap, assignment);
                    var scenarioType = ClassifyNegativeScenario(scope, assignment, conditionByKey);
                    scenarios.Add(CreateNegativeScenario(scope, scenarioType, assignment, mergedTruthMap, conditionByKey));
                }
            }

            foreach (var join in query.Joins.Where(j => j.Type == JoinType.Left))
            {
                if (CanCreateJoinMissScenario(join, query))
                {
                    scenarios.Add(CreateJoinMissScenario(join, positiveTruthMap));
                }
            }

            return DeduplicateAndRenumber(scenarios);
        }

        private BranchScenario CreatePositiveScenario(Dictionary<string, bool> truthMap)
        {
            return new BranchScenario
            {
                Id = _nextId++,
                Name = "Positive: query returns rows",
                Description = "Canonical positive path. Every required WHERE, HAVING, and subquery predicate is satisfied so the query should return rows.",
                Type = ScenarioType.Positive,
                ExpectedToReturnRows = true,
                PredicateTruthMap = truthMap
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

        private static Dictionary<string, bool> BuildGlobalPositiveTruthMap(IEnumerable<PredicateScope> scopes)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in scopes.Where(s => s.Root != null))
            {
                foreach (var kvp in PredicateTruthPlanner.ChooseCanonicalAssignment(scope.Root, desiredTruth: true))
                {
                    map[kvp.Key] = kvp.Value;
                }
            }

            return map;
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

        private sealed record ScenarioAssignment(
            string ConditionKey,
            bool DesiredTruth,
            ConditionInfo? Condition);
    }
}
