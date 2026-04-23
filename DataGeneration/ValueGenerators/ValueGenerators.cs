using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration.ValueGenerators
{
    /// <summary>
    /// Interface for type-specific value generators.
    /// </summary>
    public interface IValueGenerator
    {
        bool CanHandle(DataTypeCategory category);
        object GenerateDefault(ColumnSchema column);
        object GenerateSatisfying(ColumnSchema column, string op, string value);
        object GenerateViolating(ColumnSchema column, string op, string value);
        object GenerateFromLiteral(string literal, ColumnSchema column);
    }

    internal static class ValueGeneratorKeyHelper
    {
        public static string GetColumnKey(ColumnSchema column)
        {
            if (!string.IsNullOrWhiteSpace(column.ColumnKey))
                return column.ColumnKey;

            return $"{column.ColumnName}|{column.DataType}|{column.MaxLength}|{column.NumericPrecision}|{column.NumericScale}";
        }

        public static int GetColumnVariantOffset(ColumnSchema column, int modulus)
        {
            if (modulus <= 0)
                return 0;

            var key = GetColumnKey(column);
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
            return Math.Abs(hash % modulus);
        }
    }

    /// <summary>
    /// Generates integer values (int, bigint, smallint, tinyint).
    /// </summary>
    public class IntegerValueGenerator : IValueGenerator
    {
        private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
        private const int StartValue = 90000;

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Integer;

        public object GenerateDefault(ColumnSchema column) =>
            NormalizeIntegerForType(
                GetNextId(column) + ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 1000),
                column);

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!long.TryParse(value, out var numValue))
                return NormalizeIntegerForType(GetNextId(column), column);

            var candidate = op switch
            {
                "=" => numValue,
                "<>" or "!=" => numValue + 1,
                ">" => numValue + 1,
                ">=" => numValue,
                "<" => numValue - 1,
                "<=" => numValue,
                _ => numValue
            };
            return NormalizeIntegerForType(candidate, column);
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (!long.TryParse(value, out var numValue))
                return 0;

            var candidate = op switch
            {
                "=" => numValue + 1,
                "<>" or "!=" => numValue,
                ">" => numValue - 1,
                ">=" => numValue - 1,
                "<" => numValue + 1,
                "<=" => numValue + 1,
                _ => 0
            };
            return NormalizeIntegerForType(candidate, column);
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            var value = long.TryParse(literal, out var v) ? v : GetNextId(column);
            return NormalizeIntegerForType(value, column);
        }

        public int GetNextId(ColumnSchema column)
        {
            var key = ValueGeneratorKeyHelper.GetColumnKey(column);
            if (!_counters.TryGetValue(key, out var next))
            {
                next = StartValue;
            }

            _counters[key] = next + 1;
            return next;
        }

        private static object NormalizeIntegerForType(long value, ColumnSchema column)
        {
            return column.DataType.ToLowerInvariant() switch
            {
                "tinyint" => (byte)(Math.Abs(value) % 256),
                "smallint" => (short)((value % 65536 + 65536) % 65536 - 32768),
                "int" => (int)value,
                _ => value
            };
        }
    }

    /// <summary>
    /// Generates decimal/numeric/money values.
    /// </summary>
    public class DecimalValueGenerator : IValueGenerator
    {
        private readonly Dictionary<string, long> _sequences = new(StringComparer.OrdinalIgnoreCase);

        public bool CanHandle(DataTypeCategory category) =>
            category == DataTypeCategory.Decimal || category == DataTypeCategory.Float;

        public object GenerateDefault(ColumnSchema column)
        {
            return GenerateSequentialValue(
                column,
                NextSequence(column) + ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 1000));
        }

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var numValue))
                return GenerateDefault(column);

            var candidate = op switch
            {
                "=" => numValue,
                "<>" or "!=" => numValue + 1.0m,
                ">" => numValue + 0.01m,
                ">=" => numValue,
                "<" => numValue - 0.01m,
                "<=" => numValue,
                _ => numValue
            };
            return NormalizeNumericValue(candidate, column);
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var numValue))
                return NormalizeNumericValue(0m, column);

            var candidate = op switch
            {
                "=" => numValue + 1.0m,
                "<>" or "!=" => numValue,
                ">" => numValue - 0.01m,
                ">=" => numValue - 0.01m,
                "<" => numValue + 0.01m,
                "<=" => numValue + 0.01m,
                _ => 0m
            };
            return NormalizeNumericValue(candidate, column);
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return decimal.TryParse(literal, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? NormalizeNumericValue(v, column)
                : GenerateDefault(column);
        }

        private static object GenerateSequentialValue(ColumnSchema column, long sequence)
        {
            if (IsFloatingType(column))
            {
                return column.DataType.Equals("real", StringComparison.OrdinalIgnoreCase)
                    ? (object)(float)(sequence + 0.125d)
                    : sequence + 0.125d;
            }

            var scale = GetScale(column);
            var step = GetStep(scale);
            var max = GetMaxAbsValue(column, step);
            if (max <= 0m)
                return NormalizeNumericValue(step, column);

            var slotCountDecimal = decimal.Floor(max / step);
            var slotCount = slotCountDecimal >= long.MaxValue
                ? long.MaxValue
                : Math.Max(1L, decimal.ToInt64(slotCountDecimal));

            var ordinal = ((sequence - 1) % slotCount) + 1;
            var candidate = ordinal * step;
            return NormalizeNumericValue(candidate, column);
        }

        private static object NormalizeNumericValue(decimal value, ColumnSchema column)
        {
            if (IsFloatingType(column))
            {
                var dbl = (double)value;
                return column.DataType.Equals("real", StringComparison.OrdinalIgnoreCase)
                    ? (object)(float)dbl
                    : dbl;
            }

            var scale = GetScale(column);
            var step = GetStep(scale);
            var max = GetMaxAbsValue(column, step);
            var rounded = decimal.Round(value, scale, MidpointRounding.AwayFromZero);

            if (rounded > max)
                rounded = max;
            else if (rounded < -max)
                rounded = -max;

            return rounded;
        }

        private static bool IsFloatingType(ColumnSchema column) =>
            column.DataType.Equals("float", StringComparison.OrdinalIgnoreCase) ||
            column.DataType.Equals("real", StringComparison.OrdinalIgnoreCase);

        private static int GetScale(ColumnSchema column)
        {
            if (column.NumericScale.HasValue)
                return Math.Max(0, column.NumericScale.Value);

            return column.DataType.ToLowerInvariant() switch
            {
                "money" or "smallmoney" => 4,
                _ => 0
            };
        }

        private static int GetPrecision(ColumnSchema column)
        {
            if (column.NumericPrecision.HasValue)
                return Math.Max(1, column.NumericPrecision.Value);

            return column.DataType.ToLowerInvariant() switch
            {
                "money" => 19,
                "smallmoney" => 10,
                "float" => 15,
                "real" => 7,
                _ => 18
            };
        }

        private static decimal GetStep(int scale)
        {
            decimal step = 1m;
            for (int i = 0; i < scale; i++)
            {
                step /= 10m;
            }

            return step;
        }

        private static decimal Pow10(int exponent)
        {
            decimal result = 1m;
            for (int i = 0; i < exponent; i++)
            {
                result *= 10m;
            }

            return result;
        }

        private static decimal GetMaxAbsValue(ColumnSchema column, decimal step)
        {
            var precision = GetPrecision(column);
            var scale = GetScale(column);
            var integerDigits = Math.Max(0, precision - scale);
            var wholePartLimit = Pow10(integerDigits);
            var max = wholePartLimit - step;
            return max > 0m ? max : step;
        }

        private long NextSequence(ColumnSchema column)
        {
            var key = ValueGeneratorKeyHelper.GetColumnKey(column);
            if (!_sequences.TryGetValue(key, out var next))
            {
                next = 1;
            }

            _sequences[key] = next + 1;
            return next;
        }
    }

    /// <summary>
    /// Generates string values (varchar, nvarchar, char, text).
    /// </summary>
    public class StringValueGenerator : IValueGenerator
    {
        private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
        private const string Base36Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.String;

        public object GenerateDefault(ColumnSchema column)
        {
            var maxLen = NormalizeMaxLength(column.MaxLength);
            return GenerateLengthSafeUniqueString(
                maxLen,
                NextSequence(column) + ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 100000));
        }

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            return op switch
            {
                "=" => value,
                "<>" or "!=" => value + "_alt",
                "LIKE" => GenerateFromLikePattern(value, column),
                _ => value
            };
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            var maxLen = NormalizeMaxLength(column.MaxLength);
            var raw = op switch
            {
                "=" => value + "_different",
                "<>" or "!=" => value,
                "LIKE" => "ZZZZZ_nomatch",
                _ => "NoMatch"
            };
            if (raw.Length <= maxLen)
                return raw;

            return GenerateLengthSafeUniqueString(maxLen, NextSequence(column));
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return literal;
        }

        /// <summary>
        /// Generate a string that matches a LIKE pattern.
        /// % = any chars, _ = single char
        /// </summary>
        private string GenerateFromLikePattern(string pattern, ColumnSchema column)
        {
            // Simple pattern matching
            var result = pattern
                .Replace("%", "Test")
                .Replace("_", "X");
            var maxLen = NormalizeMaxLength(column.MaxLength);
            return result.Length > maxLen ? result[..maxLen] : result;
        }

        private static int NormalizeMaxLength(int? maxLength)
        {
            // SQL Server reports (max) as -1; null also means no explicit bound.
            if (!maxLength.HasValue || maxLength.Value <= 0)
                return 4000;
            return maxLength.Value;
        }

        private static string GenerateCompactUniqueString(int maxLen, int sequence)
        {
            if (maxLen <= 0)
                return string.Empty;

            return EncodeBase36(sequence, maxLen);
        }

        private static string GenerateLengthSafeUniqueString(int maxLen, int sequence)
        {
            if (maxLen <= 0)
                return string.Empty;

            var verbose = $"TestData_{sequence}";
            if (verbose.Length <= maxLen)
                return verbose;

            if (maxLen <= 4)
                return GenerateCompactUniqueString(maxLen, sequence);

            var prefix = "TD";
            var payloadWidth = Math.Max(1, maxLen - prefix.Length);
            var payload = GenerateCompactUniqueString(payloadWidth, sequence);
            var candidate = prefix + payload;
            return candidate.Length > maxLen ? candidate[..maxLen] : candidate;
        }

        private static string EncodeBase36(int value, int width)
        {
            if (width <= 0)
                return string.Empty;

            var current = Math.Abs(value);
            var chars = new char[Math.Max(1, width)];
            int index = chars.Length - 1;

            do
            {
                chars[index--] = Base36Chars[current % Base36Chars.Length];
                current /= Base36Chars.Length;
            }
            while (current > 0 && index >= 0);

            while (index >= 0)
            {
                chars[index--] = '0';
            }

            return new string(chars);
        }

        private int NextSequence(ColumnSchema column)
        {
            var key = ValueGeneratorKeyHelper.GetColumnKey(column);
            if (!_counters.TryGetValue(key, out var next))
            {
                next = 1;
            }

            _counters[key] = next + 1;
            return next;
        }
    }

    /// <summary>
    /// Generates datetime values.
    /// </summary>
    public class DateTimeValueGenerator : IValueGenerator
    {
        private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly DateTime BaseDateTime = new(2024, 1, 1, 8, 0, 0, DateTimeKind.Unspecified);

        public bool CanHandle(DataTypeCategory category) =>
            category == DataTypeCategory.DateTime || category == DataTypeCategory.DateTimeOffset;

        public object GenerateDefault(ColumnSchema column)
        {
            var sequence = NextSequence(column) + ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 365);
            var next = column.DataType.Equals("date", StringComparison.OrdinalIgnoreCase)
                ? BaseDateTime.AddDays(sequence)
                : BaseDateTime.AddMinutes(sequence);
            return NormalizeDateValue(next, column);
        }

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!DateTime.TryParse(value, out var dateValue))
                return GenerateDefault(column);

            var candidate = op switch
            {
                "=" => dateValue,
                "<>" or "!=" => dateValue.AddDays(30),
                ">" => dateValue.AddDays(1),
                ">=" => dateValue,
                "<" => dateValue.AddDays(-1),
                "<=" => dateValue,
                _ => dateValue
            };
            return NormalizeDateValue(candidate, column);
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (!DateTime.TryParse(value, out var dateValue))
                return NormalizeDateValue(DateTime.MinValue, column);

            var candidate = op switch
            {
                "=" => dateValue.AddDays(1),
                "<>" or "!=" => dateValue,
                ">" => dateValue.AddDays(-1),
                ">=" => dateValue.AddDays(-1),
                "<" => dateValue.AddDays(1),
                "<=" => dateValue.AddDays(1),
                _ => DateTime.MinValue
            };
            return NormalizeDateValue(candidate, column);
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return DateTime.TryParse(literal, out var v) ? NormalizeDateValue(v, column) : GenerateDefault(column);
        }

        private static object NormalizeDateValue(DateTime value, ColumnSchema column)
        {
            return column.DataType.ToLowerInvariant() switch
            {
                "date" => value.Date,
                "smalldatetime" => new DateTime(
                    value.Year, value.Month, value.Day,
                    value.Hour, value.Minute, 0, value.Kind),
                "datetimeoffset" => new DateTimeOffset(value, TimeSpan.Zero),
                _ => value
            };
        }

        private int NextSequence(ColumnSchema column)
        {
            var key = ValueGeneratorKeyHelper.GetColumnKey(column);
            if (!_counters.TryGetValue(key, out var next))
            {
                next = 1;
            }

            _counters[key] = next + 1;
            return next;
        }
    }

    /// <summary>
    /// Generates time values.
    /// </summary>
    public class TimeValueGenerator : IValueGenerator
    {
        private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Time;

        public object GenerateDefault(ColumnSchema column)
        {
            var sequence = NextSequence(column) + ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 240);
            return new TimeSpan((sequence % 24), (sequence * 7) % 60, (sequence * 13) % 60);
        }

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (TimeSpan.TryParse(value, out var parsed))
                return parsed;

            if (DateTime.TryParse(value, out var dt))
                return dt.TimeOfDay;

            return GenerateDefault(column);
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (TimeSpan.TryParse(value, out var parsed))
                return parsed.Add(TimeSpan.FromMinutes(5));

            if (DateTime.TryParse(value, out var dt))
                return dt.TimeOfDay.Add(TimeSpan.FromMinutes(5));

            return new TimeSpan(23, 59, 59);
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            if (TimeSpan.TryParse(literal, out var parsed))
                return parsed;

            if (DateTime.TryParse(literal, out var dt))
                return dt.TimeOfDay;

            return GenerateDefault(column);
        }

        private int NextSequence(ColumnSchema column)
        {
            var key = ValueGeneratorKeyHelper.GetColumnKey(column);
            if (!_counters.TryGetValue(key, out var next))
            {
                next = 1;
            }

            _counters[key] = next + 1;
            return next;
        }
    }

    /// <summary>
    /// Generates boolean/bit values.
    /// </summary>
    public class BooleanValueGenerator : IValueGenerator
    {
        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Boolean;

        public object GenerateDefault(ColumnSchema column) =>
            (ValueGeneratorKeyHelper.GetColumnVariantOffset(column, 2) % 2) == 0;

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            var boolVal = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return op switch
            {
                "=" => boolVal,
                "<>" or "!=" => !boolVal,
                _ => boolVal
            };
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            var boolVal = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return op switch
            {
                "=" => !boolVal,
                "<>" or "!=" => boolVal,
                _ => !boolVal
            };
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return literal == "1" || literal.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Generates GUID values.
    /// </summary>
    public class GuidValueGenerator : IValueGenerator
    {
        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Guid;
        public object GenerateDefault(ColumnSchema column) => Guid.NewGuid();
        public object GenerateSatisfying(ColumnSchema column, string op, string value) =>
            Guid.TryParse(value, out var g) ? g : Guid.NewGuid();
        public object GenerateViolating(ColumnSchema column, string op, string value) => Guid.NewGuid();
        public object GenerateFromLiteral(string literal, ColumnSchema column) =>
            Guid.TryParse(literal, out var g) ? g : Guid.NewGuid();
    }

    /// <summary>
    /// Factory to get the appropriate value generator for a column type.
    /// </summary>
    public class ValueGeneratorFactory
    {
        private readonly List<IValueGenerator> _generators = new()
        {
            new IntegerValueGenerator(),
            new DecimalValueGenerator(),
            new StringValueGenerator(),
            new DateTimeValueGenerator(),
            new TimeValueGenerator(),
            new BooleanValueGenerator(),
            new GuidValueGenerator()
        };

        public IValueGenerator GetGenerator(DataTypeCategory category)
        {
            return _generators.FirstOrDefault(g => g.CanHandle(category))
                   ?? new StringValueGenerator(); // fallback
        }

        public IntegerValueGenerator IntegerGenerator =>
            (IntegerValueGenerator)_generators.First(g => g is IntegerValueGenerator);
    }
}
