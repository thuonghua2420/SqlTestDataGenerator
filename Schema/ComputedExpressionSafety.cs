using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.Schema
{
    internal sealed class ComputedNumericConversionPlan
    {
        public ComputedNumericConversionPlan(
            ColumnSchema computedColumn,
            ColumnSchema sourceColumn,
            ColumnSchema targetColumn,
            decimal minValue,
            decimal maxValue)
        {
            ComputedColumn = computedColumn;
            SourceColumn = sourceColumn;
            TargetColumn = targetColumn;
            MinValue = minValue;
            MaxValue = maxValue;
        }

        public ColumnSchema ComputedColumn { get; }
        public ColumnSchema SourceColumn { get; }
        public ColumnSchema TargetColumn { get; }
        public decimal MinValue { get; }
        public decimal MaxValue { get; }
    }

    internal static class ComputedExpressionSafety
    {
        private static readonly System.Text.RegularExpressions.Regex ConvertRegex = new(
            @"\b(?:TRY_)?CONVERT\s*\(\s*(?<type>\[?[A-Za-z][A-Za-z0-9_]*\]?)(?:\s*\(\s*(?<precision>\d+)\s*(?:,\s*(?<scale>\d+))?\s*\))?\s*,\s*(?<source>\(?\s*(?:\[[^\]]+\]|\b[A-Za-z_][A-Za-z0-9_]*\b)\s*\)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        private static readonly System.Text.RegularExpressions.Regex CastRegex = new(
            @"\b(?:TRY_)?CAST\s*\(\s*(?<source>\(?\s*(?:\[[^\]]+\]|\b[A-Za-z_][A-Za-z0-9_]*\b)\s*\)?)\s+AS\s+(?<type>\[?[A-Za-z][A-Za-z0-9_]*\]?)(?:\s*\(\s*(?<precision>\d+)\s*(?:,\s*(?<scale>\d+))?\s*\))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        public static IReadOnlyList<ComputedNumericConversionPlan> BuildNumericConversionPlans(TableSchema schema)
        {
            var plans = new List<ComputedNumericConversionPlan>();
            foreach (var computedColumn in schema.Columns.Where(c => c.IsComputed))
            {
                if (TryBuildNumericConversionPlan(schema, computedColumn, out var plan))
                    plans.Add(plan);
            }

            return plans;
        }

        public static bool TryBuildNumericConversionPlan(
            TableSchema schema,
            ColumnSchema computedColumn,
            out ComputedNumericConversionPlan plan)
        {
            plan = null!;
            if (!computedColumn.IsComputed ||
                string.IsNullOrWhiteSpace(computedColumn.ComputedExpression) ||
                !TryExtractNumericConversion(computedColumn.ComputedExpression, out var sourceName, out var targetType, out var precision, out var scale))
            {
                return false;
            }

            var sourceColumn = schema.GetColumn(sourceName);
            if (sourceColumn == null ||
                sourceColumn.IsComputed ||
                sourceColumn.IsStoreGenerated ||
                sourceColumn.TypeCategory is not (DataTypeCategory.Integer or DataTypeCategory.Decimal or DataTypeCategory.Float))
            {
                return false;
            }

            var targetColumn = new ColumnSchema
            {
                SchemaName = schema.SchemaName,
                TableName = schema.TableName,
                ColumnName = computedColumn.ColumnName,
                DataType = targetType,
                SystemDataType = targetType,
                NumericPrecision = precision,
                NumericScale = scale,
                IsNullable = computedColumn.IsNullable
            };

            if (!TryGetNumericBounds(targetColumn, out var min, out var max))
                return false;

            plan = new ComputedNumericConversionPlan(computedColumn, sourceColumn, targetColumn, min, max);
            return true;
        }

        public static bool TryGetNumericBounds(ColumnSchema column, out decimal min, out decimal max)
        {
            switch (column.EffectiveDataType.ToLowerInvariant())
            {
                case "bit":
                    min = 0m;
                    max = 1m;
                    return true;
                case "tinyint":
                    min = byte.MinValue;
                    max = byte.MaxValue;
                    return true;
                case "smallint":
                    min = short.MinValue;
                    max = short.MaxValue;
                    return true;
                case "int":
                    min = int.MinValue;
                    max = int.MaxValue;
                    return true;
                case "bigint":
                    min = long.MinValue;
                    max = long.MaxValue;
                    return true;
                case "money":
                    min = -922337203685477.5808m;
                    max = 922337203685477.5807m;
                    return true;
                case "smallmoney":
                    min = -214748.3648m;
                    max = 214748.3647m;
                    return true;
                case "decimal":
                case "numeric":
                    max = GetMaxDecimalValue(column);
                    min = -max;
                    return true;
                default:
                    min = 0m;
                    max = 0m;
                    return false;
            }
        }

        public static decimal GetNumericStep(ColumnSchema column)
        {
            if (column.TypeCategory == DataTypeCategory.Integer ||
                column.EffectiveDataType.Equals("bit", StringComparison.OrdinalIgnoreCase))
            {
                return 1m;
            }

            var scale = Math.Min(28, Math.Max(0, column.NumericScale ?? 0));
            decimal step = 1m;
            for (var i = 0; i < scale; i++)
                step /= 10m;

            return step;
        }

        private static bool TryExtractNumericConversion(
            string expression,
            out string sourceName,
            out string targetType,
            out int? precision,
            out int? scale)
        {
            sourceName = string.Empty;
            targetType = string.Empty;
            precision = null;
            scale = null;

            foreach (var regex in new[] { ConvertRegex, CastRegex })
            {
                var match = regex.Match(expression);
                if (!match.Success)
                    continue;

                var parsedType = NormalizeIdentifier(match.Groups["type"].Value);
                if (!IsSupportedNumericType(parsedType))
                    continue;

                var parsedSource = NormalizeIdentifier(match.Groups["source"].Value);
                if (string.IsNullOrWhiteSpace(parsedSource))
                    continue;

                targetType = parsedType;
                sourceName = parsedSource;
                precision = TryParseInt(match.Groups["precision"].Value);
                scale = TryParseInt(match.Groups["scale"].Value);
                return true;
            }

            return false;
        }

        private static bool IsSupportedNumericType(string dataType) =>
            dataType.Equals("bit", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("tinyint", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("smallint", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("int", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("bigint", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("numeric", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("money", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("smallmoney", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeIdentifier(string value)
        {
            var normalized = value.Trim();
            while (normalized.EndsWith(")", StringComparison.Ordinal) &&
                   !normalized.StartsWith("(", StringComparison.Ordinal) &&
                   normalized.Length > 1)
            {
                normalized = normalized[..^1].Trim();
            }

            while (normalized.StartsWith("(", StringComparison.Ordinal) &&
                   normalized.EndsWith(")", StringComparison.Ordinal) &&
                   normalized.Length > 1)
            {
                normalized = normalized[1..^1].Trim();
            }

            if (normalized.StartsWith("[", StringComparison.Ordinal) &&
                normalized.EndsWith("]", StringComparison.Ordinal) &&
                normalized.Length > 1)
            {
                var closeBracket = normalized.IndexOf(']', StringComparison.Ordinal);
                if (closeBracket > 0)
                    normalized = normalized[1..closeBracket];
                else
                    normalized = normalized[1..^1];
            }

            return normalized.Trim();
        }

        private static int? TryParseInt(string value)
        {
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static decimal GetMaxDecimalValue(ColumnSchema column)
        {
            var scale = Math.Max(0, column.NumericScale ?? 0);
            var precision = Math.Max(1, column.NumericPrecision ?? 18);
            var integerDigits = Math.Max(0, precision - scale);
            var safeIntegerDigits = Math.Min(integerDigits, 28);
            decimal wholePart = 1m;
            for (var i = 0; i < safeIntegerDigits; i++)
                wholePart *= 10m;

            var step = GetNumericStep(column);
            var max = safeIntegerDigits + Math.Min(scale, 28) <= 28
                ? wholePart - step
                : wholePart - 1m;

            return max > 0m ? max : step;
        }
    }
}
