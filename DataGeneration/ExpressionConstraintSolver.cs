using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Inverts function-wrapped scalar expressions into concrete string constraints
    /// so TryResolveFunctionAwareStringValue can generate candidates that satisfy them.
    /// </summary>
    internal static class ExpressionConstraintSolver
    {
        public sealed record StringConstraintHints(
            string? RequiredPrefix = null,
            string? RequiredSuffix = null,
            string? RequiredContains = null,
            int? RequiredAtPosition = null,
            string? RequiredAtValue = null,
            char? FirstCharMin = null,
            char? FirstCharMax = null,
            int? ExactLength = null,
            int? MinLength = null)
        {
            public bool HasAnyHint =>
                RequiredPrefix != null || RequiredSuffix != null || RequiredContains != null ||
                FirstCharMin != null || FirstCharMax != null || ExactLength != null ||
                MinLength != null || (RequiredAtPosition != null && RequiredAtValue != null);

            public string BuildCandidate(string fallbackBase)
            {
                if (!HasAnyHint)
                    return fallbackBase;

                var sb = new System.Text.StringBuilder();

                // Prefix constraint
                if (RequiredPrefix != null)
                    sb.Append(RequiredPrefix);
                else if (FirstCharMin != null)
                    sb.Append(FirstCharMin.Value);

                // Body filler
                var body = fallbackBase.Trim();
                if (body.Length == 0) body = "sample";
                sb.Append(body);

                // Suffix
                if (RequiredSuffix != null)
                    sb.Append(RequiredSuffix);

                // Contains: append if not already present
                if (RequiredContains != null &&
                    sb.ToString().IndexOf(RequiredContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    sb.Append(RequiredContains);
                }

                var result = sb.ToString();

                // ExactLength: pad or truncate (preserve prefix/suffix)
                if (ExactLength.HasValue && ExactLength.Value > 0)
                {
                    if (result.Length < ExactLength.Value)
                        result += new string('x', ExactLength.Value - result.Length);
                    else if (result.Length > ExactLength.Value)
                        result = result[..ExactLength.Value];
                }

                return result;
            }
        }

        /// <summary>
        /// Attempts to invert a function-wrapped expression applied to the target column,
        /// returning string constraints the raw column value must satisfy.
        /// </summary>
        public static StringConstraintHints? Solve(
            ScalarExpressionInfo? leftExpr,
            ComparisonOp op,
            string? value,
            string? secondValue,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap = null)
        {
            if (leftExpr == null)
                return null;
            if (!ExpressionContainsTargetColumn(leftExpr, column, tableAlias, aliasMap))
                return null;
            return leftExpr switch
            {
                FunctionScalarExpressionInfo func => SolveFunction(func, op, value, secondValue, column, tableAlias, aliasMap),
                _ => null
            };
        }

        private static StringConstraintHints? SolveFunction(
            FunctionScalarExpressionInfo func,
            ComparisonOp op,
            string? value,
            string? secondValue,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            var name = func.Name.ToUpperInvariant();

            // Pass-through functions: delegate to inner column-bearing argument
            if (name is "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM" or
                "ISNULL" or "COALESCE" or "NULLIF" or
                "CAST" or "CONVERT" or "TRY_CAST" or "TRY_CONVERT")
            {
                var inner = GetColumnBearingArgument(func, column, tableAlias, aliasMap);
                if (inner == null) return null;
                return inner switch
                {
                    FunctionScalarExpressionInfo innerFunc => SolveFunction(innerFunc, op, value, secondValue, column, tableAlias, aliasMap),
                    ColumnScalarExpressionInfo => SolvePrimitive(op, value, secondValue),
                    _ => null
                };
            }

            // LEFT(col, n) <op> value
            if (name == "LEFT" && func.Arguments.Count >= 2)
            {
                if (GetColumnBearingArgument(func, column, tableAlias, aliasMap) == null) return null;
                if (!TryGetIntLiteral(func.Arguments[1], out var length)) return null;

                return op switch
                {
                    ComparisonOp.Equal when value != null && value.Length == length =>
                        new StringConstraintHints(RequiredPrefix: value),
                    ComparisonOp.Equal when value != null && value.Length < length =>
                        new StringConstraintHints(RequiredPrefix: value.PadRight(length)),
                    ComparisonOp.Like =>
                        SolvePrefixLike(value, length),
                    ComparisonOp.Between when length == 1 && value?.Length == 1 && secondValue?.Length == 1 =>
                        new StringConstraintHints(FirstCharMin: value[0], FirstCharMax: secondValue[0]),
                    ComparisonOp.Between when value != null =>
                        new StringConstraintHints(RequiredPrefix: value),
                    ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual when value != null =>
                        new StringConstraintHints(RequiredPrefix: value),
                    _ => null
                };
            }

            // RIGHT(col, n) <op> value
            if (name == "RIGHT" && func.Arguments.Count >= 2)
            {
                if (GetColumnBearingArgument(func, column, tableAlias, aliasMap) == null) return null;
                if (!TryGetIntLiteral(func.Arguments[1], out var length)) return null;

                return op switch
                {
                    ComparisonOp.Equal when value != null && value.Length == length =>
                        new StringConstraintHints(RequiredSuffix: value),
                    ComparisonOp.Equal when value != null && value.Length < length =>
                        new StringConstraintHints(RequiredSuffix: value.PadLeft(length)),
                    ComparisonOp.Like => SolveSuffixLike(value, length),
                    _ => null
                };
            }

            // SUBSTRING(col, start, len) = value
            if (name == "SUBSTRING" && func.Arguments.Count >= 3)
            {
                if (GetColumnBearingArgument(func, column, tableAlias, aliasMap) == null) return null;
                if (!TryGetIntLiteral(func.Arguments[1], out var start)) return null;
                if (op == ComparisonOp.Equal && value != null)
                    return new StringConstraintHints(RequiredAtPosition: start - 1, RequiredAtValue: value);
                return null;
            }

            // LEN(col) <op> n
            if (name == "LEN" && func.Arguments.Count >= 1)
            {
                if (GetColumnBearingArgument(func, column, tableAlias, aliasMap) == null) return null;
                if (op == ComparisonOp.Equal && int.TryParse(value, out var len))
                    return new StringConstraintHints(ExactLength: len);
                if ((op == ComparisonOp.GreaterThan || op == ComparisonOp.GreaterThanOrEqual) &&
                    int.TryParse(value, out var minLen))
                    return new StringConstraintHints(MinLength: minLen + (op == ComparisonOp.GreaterThan ? 1 : 0));
                if (op == ComparisonOp.Between && int.TryParse(value, out var loLen))
                    return new StringConstraintHints(MinLength: loLen);
                return null;
            }

            // CHARINDEX / PATINDEX: col contains search string
            if (name is "CHARINDEX" or "PATINDEX" && func.Arguments.Count >= 2)
            {
                if (func.Arguments[0] is LiteralScalarExpressionInfo searchLit &&
                    op is ComparisonOp.GreaterThan or ComparisonOp.GreaterThanOrEqual or ComparisonOp.NotEqual)
                {
                    var search = searchLit.Value.Trim('%');
                    if (!string.IsNullOrEmpty(search))
                        return new StringConstraintHints(RequiredContains: search);
                }
                return null;
            }

            // REPLACE(col, old, new) = value  →  ensure col contains old
            if (name == "REPLACE" && func.Arguments.Count >= 3)
            {
                if (GetColumnBearingArgument(func, column, tableAlias, aliasMap) == null) return null;
                if (op == ComparisonOp.Equal && value != null)
                    return new StringConstraintHints(RequiredContains: value);
                return null;
            }

            return null;
        }

        private static StringConstraintHints? SolvePrimitive(ComparisonOp op, string? value, string? secondValue)
        {
            return op switch
            {
                ComparisonOp.Equal when value != null => new StringConstraintHints(RequiredPrefix: value),
                ComparisonOp.Between when value != null => new StringConstraintHints(RequiredPrefix: value),
                ComparisonOp.Like when value != null => SolveLikePattern(value),
                _ => null
            };
        }

        private static StringConstraintHints? SolveLikePattern(string pattern)
        {
            if (pattern.StartsWith('%') || pattern.StartsWith('_'))
                return null;
            var prefix = pattern.TrimEnd('%').TrimEnd('_');
            return string.IsNullOrEmpty(prefix) ? null : new StringConstraintHints(RequiredPrefix: prefix);
        }

        private static StringConstraintHints? SolvePrefixLike(string? pattern, int prefixLength)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            var stripped = pattern.TrimEnd('%');
            return stripped.Length <= prefixLength
                ? new StringConstraintHints(RequiredPrefix: stripped)
                : null;
        }

        private static StringConstraintHints? SolveSuffixLike(string? pattern, int suffixLength)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            var stripped = pattern.TrimStart('%');
            return stripped.Length <= suffixLength
                ? new StringConstraintHints(RequiredSuffix: stripped)
                : null;
        }

        private static ScalarExpressionInfo? GetColumnBearingArgument(
            FunctionScalarExpressionInfo func,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            var startIndex = IsDatePartFunction(func.Name) ? 1 : 0;
            return func.Arguments.Skip(startIndex)
                .FirstOrDefault(a => ExpressionContainsTargetColumn(a, column, tableAlias, aliasMap));
        }

        private static bool IsDatePartFunction(string name) =>
            name.ToUpperInvariant() is "DATEADD" or "DATEDIFF" or "DATEPART" or "DATENAME";

        internal static bool ExpressionContainsTargetColumn(
            ScalarExpressionInfo? expr,
            ColumnSchema column,
            string tableAlias,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            return expr switch
            {
                null => false,
                ColumnScalarExpressionInfo c =>
                    c.ColumnName.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    TableAliasMatchesSolver(c.TableAlias, tableAlias, column.TableName, aliasMap),
                FunctionScalarExpressionInfo f =>
                    f.Arguments.Any(a => ExpressionContainsTargetColumn(a, column, tableAlias, aliasMap)),
                BinaryScalarExpressionInfo b =>
                    ExpressionContainsTargetColumn(b.Left, column, tableAlias, aliasMap) ||
                    ExpressionContainsTargetColumn(b.Right, column, tableAlias, aliasMap),
                UnaryScalarExpressionInfo u =>
                    ExpressionContainsTargetColumn(u.Operand, column, tableAlias, aliasMap),
                _ => false
            };
        }

        private static bool TableAliasMatchesSolver(
            string refAlias,
            string tableAlias,
            string tableName,
            IReadOnlyDictionary<string, string>? aliasMap)
        {
            if (string.IsNullOrWhiteSpace(refAlias)) return true;
            if (refAlias.Equals(tableAlias, StringComparison.OrdinalIgnoreCase)) return true;
            if (refAlias.Equals(tableName, StringComparison.OrdinalIgnoreCase)) return true;
            if (aliasMap == null) return false;
            if (!aliasMap.TryGetValue(refAlias, out var refTable)) return false;
            if (aliasMap.TryGetValue(tableAlias, out var targetTable))
                return refTable.Equals(targetTable, StringComparison.OrdinalIgnoreCase);
            var stripped = tableName.TrimStart('[').TrimEnd(']');
            return refTable.TrimStart('[').TrimEnd(']').Equals(stripped, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetIntLiteral(ScalarExpressionInfo? expr, out int value)
        {
            if (expr is LiteralScalarExpressionInfo lit && int.TryParse(lit.Value, out value))
                return true;
            value = 0;
            return false;
        }
    }
}
