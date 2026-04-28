using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration;

/// <summary>
/// Inverts scalar function expressions to derive column-level string generation hints.
/// Given f(col) OP value, derives what the raw column value must look like to make
/// the expression satisfy the constraint — enabling targeted candidate generation
/// instead of random-and-verify.
///
/// Supports composition: LEFT(UPPER(TRIM(col)), 1) BETWEEN 'A' AND 'Z'
/// → unfolds layer by layer to produce FirstCharMin='A', FirstCharMax='Z'.
/// </summary>
internal static class ExpressionConstraintSolver
{
    // ── Public surface ───────────────────────────────────────────────────────

    /// <summary>
    /// Hints describing what a raw string column value must satisfy
    /// so that the wrapping expression passes the given constraint.
    /// </summary>
    internal sealed class StringConstraintHints
    {
        /// <summary>Column value must start with this prefix (case-insensitive).</summary>
        public string? RequiredPrefix { get; init; }

        /// <summary>Column value must end with this suffix (case-insensitive).</summary>
        public string? RequiredSuffix { get; init; }

        /// <summary>Column value must contain this substring.</summary>
        public string? RequiredContains { get; init; }

        /// <summary>
        /// When set together with RequiredContains, the substring must start at
        /// this 1-indexed position (SQL Server SUBSTRING convention).
        /// </summary>
        public int? RequiredAtPosition { get; init; }

        /// <summary>First character must be >= this uppercase letter.</summary>
        public char? FirstCharMin { get; init; }

        /// <summary>First character must be <= this uppercase letter.</summary>
        public char? FirstCharMax { get; init; }

        /// <summary>Column value must be exactly this many characters.</summary>
        public int? ExactLength { get; init; }

        /// <summary>Column value must be at least this many characters.</summary>
        public int? MinLength { get; init; }

        public bool HasAnyHint =>
            RequiredPrefix != null   || RequiredSuffix != null  ||
            RequiredContains != null || FirstCharMin.HasValue   ||
            FirstCharMax.HasValue    || ExactLength.HasValue    ||
            MinLength.HasValue;

        /// <summary>
        /// Builds a concrete string candidate that satisfies these hints.
        /// Returns null when no hints are present or a useful string cannot be built.
        /// </summary>
        public string? BuildCandidate(string fallbackBase)
        {
            var sb = new System.Text.StringBuilder();

            // 1. Prefix or first-char range
            if (!string.IsNullOrEmpty(RequiredPrefix))
            {
                sb.Append(RequiredPrefix);
            }
            else if (FirstCharMin.HasValue || FirstCharMax.HasValue)
            {
                var minC = FirstCharMin ?? 'A';
                var maxC = FirstCharMax ?? 'Z';
                if (minC > maxC) minC = maxC;
                var mid = (char)(((int)minC + (int)maxC) / 2);
                if (mid < 'A' || mid > 'Z') mid = 'M';
                sb.Append(mid);
            }

            // 2. Required-contains (possibly at a specific position)
            if (RequiredContains != null)
            {
                if (RequiredAtPosition.HasValue)
                {
                    // Place at 1-indexed position: pad to (pos-1) chars then insert
                    var pos = RequiredAtPosition.Value - 1;
                    while (sb.Length < pos)
                        sb.Append('A');
                    if (!sb.ToString().Contains(RequiredContains, StringComparison.OrdinalIgnoreCase))
                        sb.Append(RequiredContains);
                }
                else if (!sb.ToString().Contains(RequiredContains, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(RequiredContains);
                }
            }

            // 3. Suffix (append if not already ending with it)
            if (!string.IsNullOrEmpty(RequiredSuffix) &&
                !sb.ToString().EndsWith(RequiredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0) sb.Append('_');
                sb.Append(RequiredSuffix);
            }

            // 4. Pad to minimum length using letters from fallbackBase
            var minLen = MinLength ?? 0;
            if (sb.Length < minLen)
            {
                var letters = fallbackBase.Where(char.IsLetter).ToArray();
                var idx = 0;
                while (sb.Length < minLen)
                    sb.Append(idx < letters.Length ? letters[idx++] : 'A');
            }

            // 5. Trim or pad to exact length
            var result = sb.ToString();
            if (ExactLength.HasValue)
            {
                if (result.Length > ExactLength.Value)
                    result = result[..ExactLength.Value];
                else if (result.Length < ExactLength.Value)
                    result = result.PadRight(ExactLength.Value, 'A');
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }

    /// <summary>
    /// Given expression f(col) and operator+values, returns hints describing
    /// what the raw column value must look like to satisfy f(col) OP value.
    /// Returns null when the expression is too complex or unrecognised.
    /// </summary>
    public static StringConstraintHints? Solve(
        ScalarExpressionInfo? expression,
        ComparisonOp op,
        string? value,
        string? secondValue,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap = null)
    {
        if (expression == null) return null;
        if (!ExpressionContainsTargetColumn(expression, targetColumn, tableAlias, aliasMap))
            return null;
        return SolveCore(expression, op, value, secondValue, targetColumn, tableAlias, aliasMap, depth: 0);
    }

    // ── Core recursion ───────────────────────────────────────────────────────

    private static StringConstraintHints? SolveCore(
        ScalarExpressionInfo expression,
        ComparisonOp op,
        string? value,
        string? secondValue,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap,
        int depth)
    {
        if (depth > 8) return null;

        return expression switch
        {
            ColumnScalarExpressionInfo => SolveDirectConstraint(op, value, secondValue),
            FunctionScalarExpressionInfo func =>
                SolveFunctionConstraint(func, op, value, secondValue, targetColumn, tableAlias, aliasMap, depth),
            _ => null
        };
    }

    // ── Direct column-reference constraints ──────────────────────────────────

    private static StringConstraintHints? SolveDirectConstraint(
        ComparisonOp op, string? value, string? secondValue) =>
        op switch
        {
            ComparisonOp.Equal when !string.IsNullOrEmpty(value) =>
                new StringConstraintHints { RequiredPrefix = value, ExactLength = value.Length },

            ComparisonOp.Between
                when !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(secondValue) =>
                BuildBetweenHints(value, secondValue),

            ComparisonOp.Like when !string.IsNullOrEmpty(value) =>
                BuildLikeHints(value),

            _ => null
        };

    private static StringConstraintHints BuildBetweenHints(string lower, string upper)
    {
        if (lower.Length == 1 && upper.Length == 1)
            return new StringConstraintHints
            {
                FirstCharMin = char.ToUpperInvariant(lower[0]),
                FirstCharMax = char.ToUpperInvariant(upper[0])
            };
        return new StringConstraintHints { RequiredPrefix = lower };
    }

    private static StringConstraintHints BuildLikeHints(string pattern)
    {
        // Fixed prefix before first wildcard
        var sb = new System.Text.StringBuilder();
        foreach (var c in pattern)
        {
            if (c is '%' or '_') break;
            sb.Append(c);
        }
        if (sb.Length > 0)
            return new StringConstraintHints { RequiredPrefix = sb.ToString() };

        // No fixed prefix — extract any non-wildcard content as a "contains" hint
        var inner = pattern.Trim('%').Trim('_').Split('%')[0].Split('_')[0].Trim();
        return !string.IsNullOrWhiteSpace(inner)
            ? new StringConstraintHints { RequiredContains = inner }
            : new StringConstraintHints();
    }

    // ── Function-level inversion ─────────────────────────────────────────────

    private static StringConstraintHints? SolveFunctionConstraint(
        FunctionScalarExpressionInfo func,
        ComparisonOp op,
        string? value,
        string? secondValue,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap,
        int depth)
    {
        var name = func.Name.ToUpperInvariant();

        // ── Pass-through: UPPER / LOWER / TRIM / LTRIM / RTRIM ───────────────
        // These do not change A-Z membership or string prefix/suffix structure
        if (name is "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM")
        {
            var inner = FindColumnBearingArg(func, startIdx: 0, targetColumn, tableAlias, aliasMap);
            if (inner != null)
                return SolveCore(inner, op, value, secondValue, targetColumn, tableAlias, aliasMap, depth + 1);
        }

        // ── LEFT(col, n): first n chars of col must satisfy constraint ────────
        if (name == "LEFT" && func.Arguments.Count >= 2)
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null && TryGetIntLiteral(func.Arguments[1], out var n) && n > 0)
            {
                var hints = SolveLeftConstraint(n, op, value, secondValue);
                return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
            }
        }

        // ── RIGHT(col, n): last n chars of col must satisfy constraint ────────
        if (name == "RIGHT" && func.Arguments.Count >= 2)
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null && TryGetIntLiteral(func.Arguments[1], out var n) && n > 0)
            {
                var hints = SolveRightConstraint(n, op, value, secondValue);
                return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
            }
        }

        // ── SUBSTRING(col, start, len): positional substring constraint ───────
        if (name == "SUBSTRING" && func.Arguments.Count >= 3)
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null &&
                TryGetIntLiteral(func.Arguments[1], out var start) && start >= 1 &&
                TryGetIntLiteral(func.Arguments[2], out var len)   && len > 0)
            {
                var hints = SolveSubstringConstraint(start, len, op, value, secondValue);
                return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
            }
        }

        // ── LEN(col): length constraint ───────────────────────────────────────
        if (name == "LEN" && func.Arguments.Count >= 1)
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null)
            {
                var hints = SolveLenConstraint(op, value, secondValue);
                return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
            }
        }

        // ── CHARINDEX(search, col [, startPos]): contains/not-contains ────────
        if (name == "CHARINDEX" && func.Arguments.Count >= 2)
        {
            var searchLit = func.Arguments[0] as LiteralScalarExpressionInfo;
            var inner     = FindColumnBearingArgAt(func, 1, targetColumn, tableAlias, aliasMap);
            if (searchLit != null && inner != null)
            {
                var hints = SolveCharIndexConstraint(searchLit.Value, op, value);
                return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
            }
        }

        // ── PATINDEX('%pattern%', col): contains constraint ───────────────────
        if (name == "PATINDEX" && func.Arguments.Count >= 2)
        {
            var patLit = func.Arguments[0] as LiteralScalarExpressionInfo;
            var inner  = FindColumnBearingArgAt(func, 1, targetColumn, tableAlias, aliasMap);
            if (patLit != null && inner != null)
            {
                var stripped = patLit.Value.Trim('%').Trim('_').Split('%')[0].Trim();
                if (!string.IsNullOrEmpty(stripped))
                {
                    var hints = SolveCharIndexConstraint(stripped, op, value);
                    return hints != null ? Propagate(inner, hints, targetColumn, tableAlias, aliasMap, depth) : null;
                }
            }
        }

        // ── REPLACE(col, old, new): approximate equality hint ─────────────────
        if (name == "REPLACE" && func.Arguments.Count >= 3 &&
            op == ComparisonOp.Equal && !string.IsNullOrEmpty(value))
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null)
                return Propagate(inner, new StringConstraintHints { RequiredContains = value },
                    targetColumn, tableAlias, aliasMap, depth);
        }

        // ── ISNULL / COALESCE / NULLIF: forward constraint to inner arg ───────
        if (name is "ISNULL" or "COALESCE" or "NULLIF" && func.Arguments.Count >= 1)
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null)
                return SolveCore(inner, op, value, secondValue, targetColumn, tableAlias, aliasMap, depth + 1);
        }

        // ── CAST / CONVERT / TRY_CAST / TRY_CONVERT: forward if string result ─
        if (name is "CAST" or "CONVERT" or "TRY_CAST" or "TRY_CONVERT" &&
            op == ComparisonOp.Equal && !string.IsNullOrEmpty(value))
        {
            var inner = FindColumnBearingArg(func, 0, targetColumn, tableAlias, aliasMap);
            if (inner != null)
                return Propagate(inner, new StringConstraintHints { RequiredContains = value },
                    targetColumn, tableAlias, aliasMap, depth);
        }

        return null;
    }

    // ── Per-function constraint builders ─────────────────────────────────────

    private static StringConstraintHints? SolveLeftConstraint(
        int n, ComparisonOp op, string? value, string? secondValue) =>
        op switch
        {
            ComparisonOp.Equal when !string.IsNullOrEmpty(value) =>
                new StringConstraintHints { RequiredPrefix = value.Length > n ? value[..n] : value },

            ComparisonOp.Between
                when !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(secondValue) =>
                n == 1 && value.Length >= 1 && secondValue.Length >= 1
                    ? new StringConstraintHints
                      {
                          FirstCharMin = char.ToUpperInvariant(value[0]),
                          FirstCharMax = char.ToUpperInvariant(secondValue[0])
                      }
                    : new StringConstraintHints { RequiredPrefix = value },

            ComparisonOp.Like when !string.IsNullOrEmpty(value) =>
                new StringConstraintHints { RequiredPrefix = GetLikeFixedPrefix(value, n) },

            ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual
                when !string.IsNullOrEmpty(value) && value.Length == 1 =>
                    new StringConstraintHints { FirstCharMin = char.ToUpperInvariant(value[0]) },

            ComparisonOp.LessThan or ComparisonOp.LessThanOrEqual
                when !string.IsNullOrEmpty(value) && value.Length == 1 =>
                    new StringConstraintHints { FirstCharMax = char.ToUpperInvariant(value[0]) },

            _ => null
        };

    private static StringConstraintHints? SolveRightConstraint(
        int n, ComparisonOp op, string? value, string? secondValue) =>
        op switch
        {
            ComparisonOp.Equal when !string.IsNullOrEmpty(value) =>
                new StringConstraintHints
                {
                    RequiredSuffix = value.Length > n ? value[^n..] : value
                },
            _ => null
        };

    private static StringConstraintHints? SolveSubstringConstraint(
        int start, int len, ComparisonOp op, string? value, string? secondValue)
    {
        if (op != ComparisonOp.Equal || string.IsNullOrEmpty(value)) return null;
        var content = value.Length <= len ? value : value[..len];
        return start == 1
            ? new StringConstraintHints { RequiredPrefix = content, MinLength = len }
            : new StringConstraintHints
              {
                  RequiredContains  = content,
                  RequiredAtPosition = start,
                  MinLength         = start + content.Length - 1
              };
    }

    private static StringConstraintHints? SolveLenConstraint(
        ComparisonOp op, string? value, string? secondValue)
    {
        if (!int.TryParse(value, out var len)) return null;
        return op switch
        {
            ComparisonOp.Equal =>
                new StringConstraintHints { ExactLength = Math.Max(0, len) },
            ComparisonOp.GreaterThan =>
                new StringConstraintHints { MinLength = Math.Max(0, len + 1) },
            ComparisonOp.GreaterThanOrEqual =>
                new StringConstraintHints { MinLength = Math.Max(0, len) },
            ComparisonOp.Between when int.TryParse(secondValue, out var upper) =>
                new StringConstraintHints
                {
                    MinLength   = Math.Max(0, len),
                    ExactLength = Math.Max(0, upper)
                },
            _ => null
        };
    }

    private static StringConstraintHints? SolveCharIndexConstraint(
        string searchStr, ComparisonOp op, string? value)
    {
        if (string.IsNullOrEmpty(searchStr)) return null;
        int.TryParse(value, out var intVal); // default 0

        return (op, intVal) switch
        {
            (ComparisonOp.GreaterThan,         >= 0) => new StringConstraintHints { RequiredContains = searchStr },
            (ComparisonOp.GreaterThanOrEqual,  >= 1) => new StringConstraintHints { RequiredContains = searchStr },
            (ComparisonOp.NotEqual,             0)   => new StringConstraintHints { RequiredContains = searchStr },
            (ComparisonOp.Equal,                0)   => new StringConstraintHints(), // does NOT contain — no hint
            _ => null
        };
    }

    // ── Propagation helper ───────────────────────────────────────────────────

    /// <summary>
    /// Applies outerHints to innerExpr. If innerExpr is a direct column reference
    /// the hints are returned as-is. If it is a pass-through wrapper (UPPER/LOWER/TRIM)
    /// around the column, recurse through it. Otherwise return outerHints directly.
    /// </summary>
    private static StringConstraintHints? Propagate(
        ScalarExpressionInfo innerExpr,
        StringConstraintHints outerHints,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap,
        int depth)
    {
        if (depth > 8) return outerHints;
        if (innerExpr is ColumnScalarExpressionInfo) return outerHints;

        if (innerExpr is FunctionScalarExpressionInfo inner &&
            inner.Name.ToUpperInvariant() is "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM")
        {
            var arg = FindColumnBearingArg(inner, 0, targetColumn, tableAlias, aliasMap);
            if (arg != null)
                return Propagate(arg, outerHints, targetColumn, tableAlias, aliasMap, depth + 1);
        }

        return outerHints; // apply outer hints regardless
    }

    // ── Column-reference detection (alias-map aware) ─────────────────────────

    /// <summary>Returns true when any sub-expression references the target column,
    /// resolving aliases via the optional aliasMap.</summary>
    internal static bool ExpressionContainsTargetColumn(
        ScalarExpressionInfo expression,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap)
    {
        return expression switch
        {
            ColumnScalarExpressionInfo col =>
                col.ColumnName.Equals(targetColumn.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                TableAliasMatches(col.TableAlias, targetColumn.TableName, tableAlias, aliasMap),

            FunctionScalarExpressionInfo func =>
                func.Arguments.Any(a => ExpressionContainsTargetColumn(a, targetColumn, tableAlias, aliasMap)),

            BinaryScalarExpressionInfo bin =>
                (bin.Left  != null && ExpressionContainsTargetColumn(bin.Left,  targetColumn, tableAlias, aliasMap)) ||
                (bin.Right != null && ExpressionContainsTargetColumn(bin.Right, targetColumn, tableAlias, aliasMap)),

            UnaryScalarExpressionInfo un =>
                un.Operand != null && ExpressionContainsTargetColumn(un.Operand, targetColumn, tableAlias, aliasMap),

            _ => false
        };
    }

    private static bool TableAliasMatches(
        string? refAlias,
        string tableName,
        string outerAlias,
        IReadOnlyDictionary<string, string>? aliasMap)
    {
        if (string.IsNullOrWhiteSpace(refAlias)) return true;
        if (refAlias.Equals(outerAlias, StringComparison.OrdinalIgnoreCase)) return true;
        if (refAlias.Equals(tableName, StringComparison.OrdinalIgnoreCase))  return true;
        if (aliasMap != null && aliasMap.TryGetValue(refAlias, out var resolved))
            return resolved.Equals(tableName, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // ── Utility helpers ──────────────────────────────────────────────────────

    private static ScalarExpressionInfo? FindColumnBearingArg(
        FunctionScalarExpressionInfo func,
        int startIdx,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap)
    {
        for (var i = startIdx; i < func.Arguments.Count; i++)
        {
            if (ExpressionContainsTargetColumn(func.Arguments[i], targetColumn, tableAlias, aliasMap))
                return func.Arguments[i];
        }
        return null;
    }

    private static ScalarExpressionInfo? FindColumnBearingArgAt(
        FunctionScalarExpressionInfo func,
        int index,
        ColumnSchema targetColumn,
        string tableAlias,
        IReadOnlyDictionary<string, string>? aliasMap)
    {
        if (index >= func.Arguments.Count) return null;
        var arg = func.Arguments[index];
        return ExpressionContainsTargetColumn(arg, targetColumn, tableAlias, aliasMap) ? arg : null;
    }

    private static bool TryGetIntLiteral(ScalarExpressionInfo expr, out int value)
    {
        if (expr is LiteralScalarExpressionInfo lit && int.TryParse(lit.Value, out value))
            return true;
        value = 0;
        return false;
    }

    private static string? GetLikeFixedPrefix(string pattern, int maxLen)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in pattern)
        {
            if (c is '%' or '_') break;
            if (sb.Length >= maxLen) break;
            sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
