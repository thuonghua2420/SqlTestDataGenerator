using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Analyzes a ParsedQuery and determines all branch scenarios that need test data.
    /// Creates: positive path + negative for each WHERE condition + HAVING failures +
    /// JOIN misses + subquery misses + boundary values.
    /// </summary>
    public class BranchCoverageAnalyzer
    {
        private int _nextId = 1;

        /// <summary>
        /// Analyze the query and produce a list of scenarios to generate data for.
        /// </summary>
        public List<BranchScenario> AnalyzeBranches(ParsedQuery query)
        {
            _nextId = 1;
            var scenarios = new List<BranchScenario>();

            // 1. POSITIVE: All conditions satisfied → query returns rows
            scenarios.Add(CreatePositiveScenario(query));

            // 2. WHERE NEGATIVE: For each WHERE condition, create a scenario where it fails
            foreach (var condition in query.WhereConditions)
            {
                // Skip conditions that are part of subqueries (handled separately)
                if (condition.HasSubquery) continue;

                scenarios.Add(CreateWhereNegativeScenario(condition, query));
            }

            // 3. JOIN MISS: For non-inner joins, create scenario with no match
            foreach (var join in query.Joins.Where(j => j.Type != Parsing.Models.JoinType.Inner))
            {
                scenarios.Add(CreateJoinMissScenario(join, query));
            }

            // 4. HAVING NEGATIVE: For each HAVING condition, create a failure scenario
            foreach (var condition in query.HavingConditions)
            {
                scenarios.Add(CreateHavingNegativeScenario(condition, query));
            }

            // 5. SUBQUERY MISS: For each IN/EXISTS subquery, create a miss scenario
            foreach (var subquery in query.Subqueries)
            {
                scenarios.Add(CreateSubqueryMissScenario(subquery, query));
            }

            // 6. BOUNDARY: For range conditions, create boundary value scenarios
            foreach (var condition in query.WhereConditions)
            {
                if (IsRangeCondition(condition))
                {
                    scenarios.Add(CreateBoundaryScenario(condition, query));
                }
            }

            return DeduplicateAndRenumber(scenarios);
        }

        // ═════════════════════════════════════════════════════════════════

        private BranchScenario CreatePositiveScenario(ParsedQuery query)
        {
            return new BranchScenario
            {
                Id = _nextId++,
                Name = "Positive - All conditions met",
                Description = "All WHERE, HAVING, JOIN, and subquery conditions are satisfied. " +
                              "The query should return data for this scenario.",
                Type = ScenarioType.Positive,
                ExpectedToReturnRows = true
            };
        }

        private BranchScenario CreateWhereNegativeScenario(ConditionInfo condition, ParsedQuery query)
        {
            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"WHERE fail: {condition}",
                Description = $"Condition [{condition}] is violated. Row should NOT appear in query results.",
                Type = ScenarioType.WhereNegative,
                ExpectedToReturnRows = false,
                TestedCondition = condition.ToString()
            };
        }

        private BranchScenario CreateJoinMissScenario(JoinInfo join, ParsedQuery query)
        {
            var joinTypeStr = join.Type switch
            {
                Parsing.Models.JoinType.Left => "LEFT",
                Parsing.Models.JoinType.Right => "RIGHT",
                Parsing.Models.JoinType.Full => "FULL",
                _ => join.Type.ToString()
            };

            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"{joinTypeStr} JOIN miss: {join.RightTableAlias}",
                Description = $"{joinTypeStr} JOIN on {join.RightTableAlias} has no matching row. " +
                              $"Columns from {join.RightTableAlias} should be NULL.",
                Type = ScenarioType.JoinMiss,
                ExpectedToReturnRows = true, // LEFT/RIGHT/FULL joins still return rows
                TestedCondition = $"{join.LeftTableAlias}.{join.LeftColumn} = {join.RightTableAlias}.{join.RightColumn}"
            };
        }

        private BranchScenario CreateHavingNegativeScenario(ConditionInfo condition, ParsedQuery query)
        {
            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"HAVING fail: {condition}",
                Description = $"HAVING condition [{condition}] is not met. " +
                              "Group should be filtered out of results.",
                Type = ScenarioType.HavingNegative,
                ExpectedToReturnRows = false,
                TestedCondition = condition.ToString()
            };
        }

        private BranchScenario CreateSubqueryMissScenario(SubqueryInfo subquery, ParsedQuery query)
        {
            var opStr = subquery.Operator switch
            {
                SubqueryOperator.In => "IN",
                SubqueryOperator.NotIn => "NOT IN",
                SubqueryOperator.Exists => "EXISTS",
                SubqueryOperator.NotExists => "NOT EXISTS",
                _ => subquery.Operator.ToString()
            };

            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"Subquery miss: {subquery.ParentTableAlias}.{subquery.ParentColumnName} {opStr}",
                Description = $"The value of {subquery.ParentTableAlias}.{subquery.ParentColumnName} " +
                              $"does NOT match the {opStr} subquery result. Row should be excluded.",
                Type = ScenarioType.SubqueryMiss,
                ExpectedToReturnRows = false,
                TestedCondition = $"{subquery.ParentTableAlias}.{subquery.ParentColumnName} {opStr} (subquery)"
            };
        }

        private BranchScenario CreateBoundaryScenario(ConditionInfo condition, ParsedQuery query)
        {
            return new BranchScenario
            {
                Id = _nextId++,
                Name = $"Boundary: {condition}",
                Description = $"Boundary value test for [{condition}]. " +
                              "Value is at the exact boundary of the condition.",
                Type = ScenarioType.Boundary,
                ExpectedToReturnRows = true,
                TestedCondition = condition.ToString()
            };
        }

        private bool IsRangeCondition(ConditionInfo condition)
        {
            return condition.Operator is
                ComparisonOp.GreaterThan or
                ComparisonOp.GreaterThanOrEqual or
                ComparisonOp.LessThan or
                ComparisonOp.LessThanOrEqual or
                ComparisonOp.Between;
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
            return string.Join("|",
                scenario.Type,
                scenario.ExpectedToReturnRows ? "1" : "0",
                NormalizeScenarioText(scenario.Name),
                NormalizeScenarioText(scenario.TestedCondition));
        }

        private static string NormalizeScenarioText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return string.Join(" ",
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
