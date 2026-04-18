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

    /// <summary>
    /// Generates integer values (int, bigint, smallint, tinyint).
    /// </summary>
    public class IntegerValueGenerator : IValueGenerator
    {
        private int _counter = 90000; // Start high to avoid conflicts

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Integer;

        public object GenerateDefault(ColumnSchema column) =>
            NormalizeIntegerForType(_counter++, column);

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!long.TryParse(value, out var numValue))
                return _counter++;

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
            var value = long.TryParse(literal, out var v) ? v : _counter++;
            return NormalizeIntegerForType(value, column);
        }

        public int GetNextId() => _counter++;

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
        private decimal _counter = 100.00m;

        public bool CanHandle(DataTypeCategory category) =>
            category == DataTypeCategory.Decimal || category == DataTypeCategory.Float;

        public object GenerateDefault(ColumnSchema column)
        {
            _counter += 10.50m;
            return _counter;
        }

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var numValue))
                return GenerateDefault(column);

            return op switch
            {
                "=" => numValue,
                "<>" or "!=" => numValue + 1.0m,
                ">" => numValue + 0.01m,
                ">=" => numValue,
                "<" => numValue - 0.01m,
                "<=" => numValue,
                _ => numValue
            };
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var numValue))
                return 0m;

            return op switch
            {
                "=" => numValue + 1.0m,
                "<>" or "!=" => numValue,
                ">" => numValue - 0.01m,
                ">=" => numValue - 0.01m,
                "<" => numValue + 0.01m,
                "<=" => numValue + 0.01m,
                _ => 0m
            };
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return decimal.TryParse(literal, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : GenerateDefault(column);
        }
    }

    /// <summary>
    /// Generates string values (varchar, nvarchar, char, text).
    /// </summary>
    public class StringValueGenerator : IValueGenerator
    {
        private int _counter;

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.String;

        public object GenerateDefault(ColumnSchema column)
        {
            var maxLen = NormalizeMaxLength(column.MaxLength);
            var val = $"TestData_{++_counter}";
            return val.Length > maxLen ? val[..maxLen] : val;
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
            return op switch
            {
                "=" => value + "_different",
                "<>" or "!=" => value,
                "LIKE" => "ZZZZZ_nomatch",
                _ => "NoMatch"
            };
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
    }

    /// <summary>
    /// Generates datetime values.
    /// </summary>
    public class DateTimeValueGenerator : IValueGenerator
    {
        public bool CanHandle(DataTypeCategory category) =>
            category == DataTypeCategory.DateTime || category == DataTypeCategory.DateTimeOffset;

        public object GenerateDefault(ColumnSchema column) => DateTime.Now;

        public object GenerateSatisfying(ColumnSchema column, string op, string value)
        {
            if (!DateTime.TryParse(value, out var dateValue))
                return GenerateDefault(column);

            return op switch
            {
                "=" => dateValue,
                "<>" or "!=" => dateValue.AddDays(30),
                ">" => dateValue.AddDays(1),
                ">=" => dateValue,
                "<" => dateValue.AddDays(-1),
                "<=" => dateValue,
                _ => dateValue
            };
        }

        public object GenerateViolating(ColumnSchema column, string op, string value)
        {
            if (!DateTime.TryParse(value, out var dateValue))
                return DateTime.MinValue;

            return op switch
            {
                "=" => dateValue.AddDays(1),
                "<>" or "!=" => dateValue,
                ">" => dateValue.AddDays(-1),
                ">=" => dateValue.AddDays(-1),
                "<" => dateValue.AddDays(1),
                "<=" => dateValue.AddDays(1),
                _ => DateTime.MinValue
            };
        }

        public object GenerateFromLiteral(string literal, ColumnSchema column)
        {
            return DateTime.TryParse(literal, out var v) ? v : DateTime.Now;
        }
    }

    /// <summary>
    /// Generates time values.
    /// </summary>
    public class TimeValueGenerator : IValueGenerator
    {
        private int _counter;

        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Time;

        public object GenerateDefault(ColumnSchema column)
        {
            _counter++;
            return new TimeSpan((_counter % 24), (_counter * 7) % 60, (_counter * 13) % 60);
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
    }

    /// <summary>
    /// Generates boolean/bit values.
    /// </summary>
    public class BooleanValueGenerator : IValueGenerator
    {
        public bool CanHandle(DataTypeCategory category) => category == DataTypeCategory.Boolean;

        public object GenerateDefault(ColumnSchema column) => true;

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
