using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing
{
    /// <summary>
    /// Computes minimal truth assignments for predicate trees.
    /// Used both for exact scenario enumeration and for deterministic positive-path generation.
    /// </summary>
    public static class PredicateTruthPlanner
    {
        public static IReadOnlyList<Dictionary<string, bool>> GetMinimalAssignments(
            PredicateExpression? root,
            bool desiredTruth)
        {
            if (root == null)
                return Array.Empty<Dictionary<string, bool>>();

            var sets = desiredTruth
                ? GetMinimalTrueSets(root)
                : GetMinimalFalseSets(root);

            return Minimize(sets)
                .OrderBy(s => s.Count)
                .ThenBy(BuildAssignmentKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static Dictionary<string, bool> ChooseCanonicalAssignment(
            PredicateExpression? root,
            bool desiredTruth)
        {
            return GetMinimalAssignments(root, desiredTruth)
                .FirstOrDefault() ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public static IEnumerable<PredicateLeafExpression> EnumerateLeaves(PredicateExpression? root)
        {
            if (root == null)
                yield break;

            switch (root)
            {
                case PredicateLeafExpression leaf:
                    yield return leaf;
                    yield break;
                case PredicateBinaryExpression binary:
                    foreach (var leftLeaf in EnumerateLeaves(binary.Left))
                        yield return leftLeaf;
                    foreach (var rightLeaf in EnumerateLeaves(binary.Right))
                        yield return rightLeaf;
                    yield break;
                case PredicateNotExpression notExpr:
                    foreach (var innerLeaf in EnumerateLeaves(notExpr.Inner))
                        yield return innerLeaf;
                    yield break;
            }
        }

        private static List<Dictionary<string, bool>> GetMinimalTrueSets(PredicateExpression expression)
        {
            return expression switch
            {
                PredicateLeafExpression leaf => Single(leaf.Condition.Key, true),
                PredicateBinaryExpression { Operator: LogicalOp.And } andExpr =>
                    Combine(GetMinimalTrueSets(andExpr.Left), GetMinimalTrueSets(andExpr.Right)),
                PredicateBinaryExpression { Operator: LogicalOp.Or } orExpr =>
                    Merge(GetMinimalTrueSets(orExpr.Left), GetMinimalTrueSets(orExpr.Right)),
                PredicateNotExpression notExpr =>
                    GetMinimalFalseSets(notExpr.Inner),
                _ => new List<Dictionary<string, bool>>()
            };
        }

        private static List<Dictionary<string, bool>> GetMinimalFalseSets(PredicateExpression expression)
        {
            return expression switch
            {
                PredicateLeafExpression leaf => Single(leaf.Condition.Key, false),
                PredicateBinaryExpression { Operator: LogicalOp.And } andExpr =>
                    Merge(GetMinimalFalseSets(andExpr.Left), GetMinimalFalseSets(andExpr.Right)),
                PredicateBinaryExpression { Operator: LogicalOp.Or } orExpr =>
                    Combine(GetMinimalFalseSets(orExpr.Left), GetMinimalFalseSets(orExpr.Right)),
                PredicateNotExpression notExpr =>
                    GetMinimalTrueSets(notExpr.Inner),
                _ => new List<Dictionary<string, bool>>()
            };
        }

        private static List<Dictionary<string, bool>> Single(string key, bool value)
        {
            return new List<Dictionary<string, bool>>
            {
                new(StringComparer.OrdinalIgnoreCase)
                {
                    [key] = value
                }
            };
        }

        private static List<Dictionary<string, bool>> Merge(
            IEnumerable<Dictionary<string, bool>> left,
            IEnumerable<Dictionary<string, bool>> right)
        {
            return left
                .Concat(right)
                .Select(Clone)
                .ToList();
        }

        private static List<Dictionary<string, bool>> Combine(
            IReadOnlyCollection<Dictionary<string, bool>> left,
            IReadOnlyCollection<Dictionary<string, bool>> right)
        {
            if (left.Count == 0)
                return right.Select(Clone).ToList();
            if (right.Count == 0)
                return left.Select(Clone).ToList();

            var combined = new List<Dictionary<string, bool>>();
            foreach (var leftSet in left)
            {
                foreach (var rightSet in right)
                {
                    if (TryMerge(leftSet, rightSet, out var merged))
                    {
                        combined.Add(merged);
                    }
                }
            }

            return combined;
        }

        private static bool TryMerge(
            IReadOnlyDictionary<string, bool> left,
            IReadOnlyDictionary<string, bool> right,
            out Dictionary<string, bool> merged)
        {
            merged = new Dictionary<string, bool>(left, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in right)
            {
                if (merged.TryGetValue(kvp.Key, out var existing) && existing != kvp.Value)
                {
                    merged = null!;
                    return false;
                }

                merged[kvp.Key] = kvp.Value;
            }

            return true;
        }

        private static IEnumerable<Dictionary<string, bool>> Minimize(IEnumerable<Dictionary<string, bool>> sets)
        {
            var ordered = sets
                .Select(Clone)
                .Distinct(new AssignmentSetComparer())
                .OrderBy(s => s.Count)
                .ThenBy(BuildAssignmentKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var minimized = new List<Dictionary<string, bool>>();
            foreach (var candidate in ordered)
            {
                if (minimized.Any(existing => IsSubset(existing, candidate)))
                    continue;

                minimized.Add(candidate);
            }

            return minimized;
        }

        private static bool IsSubset(
            IReadOnlyDictionary<string, bool> subsetCandidate,
            IReadOnlyDictionary<string, bool> supersetCandidate)
        {
            if (subsetCandidate.Count > supersetCandidate.Count)
                return false;

            foreach (var kvp in subsetCandidate)
            {
                if (!supersetCandidate.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                    return false;
            }

            return true;
        }

        private static Dictionary<string, bool> Clone(IReadOnlyDictionary<string, bool> source)
        {
            return new Dictionary<string, bool>(source, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildAssignmentKey(IReadOnlyDictionary<string, bool> assignment)
        {
            return string.Join("|",
                assignment
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => $"{kvp.Key}:{(kvp.Value ? "1" : "0")}"));
        }

        private sealed class AssignmentSetComparer : IEqualityComparer<Dictionary<string, bool>>
        {
            public bool Equals(Dictionary<string, bool>? x, Dictionary<string, bool>? y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x == null || y == null || x.Count != y.Count)
                    return false;

                foreach (var kvp in x)
                {
                    if (!y.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                        return false;
                }

                return true;
            }

            public int GetHashCode(Dictionary<string, bool> obj)
            {
                return BuildAssignmentKey(obj).GetHashCode(StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
