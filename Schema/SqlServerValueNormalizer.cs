using SqlTestDataGenerator.Schema.Models;
using System.Globalization;
using System.Text;

namespace SqlTestDataGenerator.Schema
{
    public sealed class SqlExpressionValue
    {
        public SqlExpressionValue(string expression, string displayValue)
        {
            Expression = expression;
            DisplayValue = displayValue;
        }

        public string Expression { get; }
        public string DisplayValue { get; }

        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Normalizes CLR values to SQL Server-compatible runtime values based on column schema.
    /// This keeps generation, validation, and execution aligned to the same final representation.
    /// </summary>
    public static class SqlServerValueNormalizer
    {
        public static object? NormalizeValue(ColumnSchema column, object? value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            if (value is SqlExpressionValue)
                return value;

            return column.EffectiveDataType.ToLowerInvariant() switch
            {
                "bigint" or "int" or "smallint" or "tinyint" => NormalizeInteger(column, value),
                "decimal" or "numeric" or "money" or "smallmoney" => NormalizeDecimal(column, value),
                "float" or "real" => NormalizeFloat(column, value),
                "bit" => NormalizeBoolean(value),
                "date" or "datetime" or "datetime2" or "smalldatetime" => NormalizeDateTime(column, value),
                "datetimeoffset" => NormalizeDateTimeOffset(value),
                "time" => NormalizeTime(value),
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => NormalizeString(column, value),
                "uniqueidentifier" => NormalizeGuid(value),
                "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => NormalizeBinary(column, value),
                "xml" => NormalizeXml(value),
                "geography" => NormalizeSpatial("geography", value),
                "geometry" => NormalizeSpatial("geometry", value),
                "hierarchyid" => NormalizeHierarchyId(value),
                "sql_variant" => NormalizeSqlVariant(value),
                _ => value
            };
        }

        private static object NormalizeInteger(ColumnSchema column, object value)
        {
            var parsed = ConvertToDecimal(value);
            var integral = decimal.Truncate(parsed);

            return column.EffectiveDataType.ToLowerInvariant() switch
            {
                "tinyint" => (byte)Clamp(integral, byte.MinValue, byte.MaxValue),
                "smallint" => (short)Clamp(integral, short.MinValue, short.MaxValue),
                "int" => (int)Clamp(integral, int.MinValue, int.MaxValue),
                _ => (long)Clamp(integral, long.MinValue, long.MaxValue)
            };
        }

        private static object NormalizeDecimal(ColumnSchema column, object value)
        {
            var parsed = ConvertToDecimal(value);
            var scale = GetScale(column);
            var step = GetStep(scale);
            var max = GetMaxAbsValue(column, step);
            if (parsed > max)
                return max;
            if (parsed < -max)
                return -max;

            var rounded = decimal.Round(parsed, scale, MidpointRounding.AwayFromZero);

            if (rounded > max)
                rounded = max;
            else if (rounded < -max)
                rounded = -max;

            return rounded;
        }

        private static object NormalizeFloat(ColumnSchema column, object value)
        {
            var parsed = ConvertToDouble(value);
            return column.EffectiveDataType.Equals("real", StringComparison.OrdinalIgnoreCase)
                ? (object)(float)parsed
                : parsed;
        }

        private static object NormalizeBoolean(object value)
        {
            return value switch
            {
                bool b => b,
                byte b => b != 0,
                short s => s != 0,
                int i => i != 0,
                long l => l != 0,
                decimal d => d != 0m,
                string s when bool.TryParse(s, out var parsedBool) => parsedBool,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) => parsedInt != 0,
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            };
        }

        private static object NormalizeDateTime(ColumnSchema column, object value)
        {
            var dateTime = ToDateTime(value);
            dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);

            return column.EffectiveDataType.ToLowerInvariant() switch
            {
                "date" => dateTime.Date,
                "smalldatetime" => new DateTime(
                    dateTime.Year, dateTime.Month, dateTime.Day,
                    dateTime.Hour, dateTime.Minute, 0, DateTimeKind.Unspecified),
                _ => dateTime
            };
        }

        private static object NormalizeDateTimeOffset(object value)
        {
            return value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
                string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDto) => parsedDto,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt) =>
                    new DateTimeOffset(DateTime.SpecifyKind(parsedDt, DateTimeKind.Unspecified), TimeSpan.Zero),
                _ => throw new InvalidOperationException($"Cannot normalize value '{value}' to datetimeoffset.")
            };
        }

        private static object NormalizeTime(object value)
        {
            return value switch
            {
                TimeSpan ts => ts,
                DateTime dt => dt.TimeOfDay,
                DateTimeOffset dto => dto.TimeOfDay,
                string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsedTs) => parsedTs,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt) => parsedDt.TimeOfDay,
                _ => throw new InvalidOperationException($"Cannot normalize value '{value}' to time.")
            };
        }

        private static object NormalizeString(ColumnSchema column, object value)
        {
            var text = value switch
            {
                string s => s,
                Guid g => g.ToString(),
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString(),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };

            if (column.MaxLength.HasValue && column.MaxLength.Value > 0 && text.Length > column.MaxLength.Value)
            {
                text = text[..column.MaxLength.Value];
            }

            return text;
        }

        private static object NormalizeGuid(object value)
        {
            return value switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var parsed) => parsed,
                _ => throw new InvalidOperationException($"Cannot normalize value '{value}' to uniqueidentifier.")
            };
        }

        private static object NormalizeBinary(ColumnSchema column, object value)
        {
            byte[] bytes = value switch
            {
                byte[] existing => existing,
                string s => Encoding.UTF8.GetBytes(s),
                _ => throw new InvalidOperationException($"Cannot normalize value '{value}' to binary.")
            };

            if (column.MaxLength.HasValue && column.MaxLength.Value > 0 && bytes.Length > column.MaxLength.Value)
            {
                return bytes.Take(column.MaxLength.Value).ToArray();
            }

            return bytes;
        }

        private static object NormalizeXml(object value)
        {
            return value switch
            {
                string s => s,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<root />"
            };
        }

        private static object NormalizeSpatial(string typeName, object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = typeName.Equals("geography", StringComparison.OrdinalIgnoreCase)
                    ? "POINT(139.6917 35.6895)"
                    : "POINT(139.6917 35.6895)";
            }

            if (text.Contains("::", StringComparison.Ordinal))
            {
                return new SqlExpressionValue(text, text);
            }

            var escaped = text.Replace("'", "''", StringComparison.Ordinal);
            var srid = typeName.Equals("geography", StringComparison.OrdinalIgnoreCase) ? 4326 : 0;
            return new SqlExpressionValue(
                $"{typeName}::STGeomFromText(N'{escaped}', {srid})",
                text);
        }

        private static object NormalizeHierarchyId(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "/1/";
            if (string.IsNullOrWhiteSpace(text))
                text = "/1/";

            if (text.Contains("::", StringComparison.Ordinal))
                return new SqlExpressionValue(text, text);

            if (!text.StartsWith("/", StringComparison.Ordinal))
                text = "/" + text;
            if (!text.EndsWith("/", StringComparison.Ordinal))
                text += "/";

            var escaped = text.Replace("'", "''", StringComparison.Ordinal);
            return new SqlExpressionValue($"hierarchyid::Parse(N'{escaped}')", text);
        }

        private static object NormalizeSqlVariant(object value)
        {
            if (value is string s)
                return NormalizeString(new ColumnSchema { DataType = "nvarchar", MaxLength = 4000 }, s);

            return value;
        }

        private static DateTime ToDateTime(object value)
        {
            return value switch
            {
                DateTime dt => dt,
                DateTimeOffset dto => dto.DateTime,
                string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDto) => parsedDto.DateTime,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt) => parsedDt,
                _ => throw new InvalidOperationException($"Cannot normalize value '{value}' to datetime.")
            };
        }

        private static decimal ConvertToDecimal(object value)
        {
            return value switch
            {
                decimal d => d,
                byte b => b,
                short s => s,
                int i => i,
                long l => l,
                float f => Convert.ToDecimal(f, CultureInfo.InvariantCulture),
                double d => Convert.ToDecimal(d, CultureInfo.InvariantCulture),
                string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            };
        }

        private static double ConvertToDouble(object value)
        {
            return value switch
            {
                double d => d,
                float f => f,
                decimal d => Convert.ToDouble(d, CultureInfo.InvariantCulture),
                byte b => b,
                short s => s,
                int i => i,
                long l => l,
                string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
            };
        }

        private static decimal Clamp(decimal value, decimal min, decimal max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int GetScale(ColumnSchema column)
        {
            if (column.NumericScale.HasValue)
                return Math.Min(28, Math.Max(0, column.NumericScale.Value));

            return column.EffectiveDataType.ToLowerInvariant() switch
            {
                "money" or "smallmoney" => 4,
                _ => 0
            };
        }

        private static int GetPrecision(ColumnSchema column)
        {
            if (column.NumericPrecision.HasValue)
                return Math.Max(1, column.NumericPrecision.Value);

            return column.EffectiveDataType.ToLowerInvariant() switch
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
            exponent = Math.Min(28, Math.Max(0, exponent));
            decimal result = 1m;
            for (int i = 0; i < exponent; i++)
            {
                result *= 10m;
            }

            return result;
        }

        private static decimal GetMaxAbsValue(ColumnSchema column, decimal step)
        {
            var type = column.EffectiveDataType.ToLowerInvariant();
            if (type == "money")
                return 922337203685477.5807m;
            if (type == "smallmoney")
                return 214748.3647m;

            var precision = GetPrecision(column);
            var scale = GetScale(column);
            var integerDigits = Math.Max(0, precision - scale);

            // SQL decimal/numeric can go up to precision 38, but CLR decimal cannot
            // represent all 38 digits. Use the largest insertable value that still
            // fits the declared integer digit budget instead of overflowing locally
            // or creating a parameter SQL Server cannot convert.
            var safeIntegerDigits = Math.Min(integerDigits, 28);
            var wholePartLimit = Pow10(safeIntegerDigits);
            var max = safeIntegerDigits + scale <= 28
                ? wholePartLimit - step
                : wholePartLimit - 1m;
            return max > 0m ? max : step;
        }
    }
}
