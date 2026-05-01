using System.Text;
using System.Text.RegularExpressions;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    internal static class SqlLikePattern
    {
        private const string DefaultFiller = "Like";

        public static string GenerateMatchingValue(string pattern, ColumnSchema column, string? escape = null)
        {
            var value = BuildMatchingCore(pattern, ResolveEscapeCharacter(escape));
            if (string.IsNullOrEmpty(value))
            {
                value = DefaultFiller;
            }

            return FitToColumn(value, column);
        }

        public static string GenerateNonMatchingValue(string pattern, ColumnSchema column, string? escape = null)
        {
            var escapeChar = ResolveEscapeCharacter(escape);
            foreach (var candidate in new[]
                     {
                         string.Empty,
                         "NoMatch",
                         "ZZZZZ_nomatch",
                         "A",
                         "B",
                         "C",
                         "0",
                         "%"
                     })
            {
                var fitted = FitToColumn(candidate, column);
                if (!IsMatch(fitted, pattern, escape))
                {
                    return fitted;
                }
            }

            var generated = "NoMatch_" + Math.Abs(pattern.GetHashCode(StringComparison.OrdinalIgnoreCase));
            var result = FitToColumn(generated, column);
            return IsMatch(result, pattern, escape) && escapeChar == null
                ? FitToColumn("!", column)
                : result;
        }

        public static bool IsMatch(string input, string pattern, string? escape = null)
        {
            var regex = BuildRegex(pattern, ResolveEscapeCharacter(escape));
            return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string BuildMatchingCore(string pattern, char? escapeChar)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (escapeChar.HasValue && ch == escapeChar.Value && i + 1 < pattern.Length)
                {
                    sb.Append(pattern[++i]);
                    continue;
                }

                switch (ch)
                {
                    case '%':
                        break;
                    case '_':
                        sb.Append('X');
                        break;
                    case '[' when TryReadBracket(pattern, i, out var endIndex, out var content):
                        sb.Append(ChooseBracketCharacter(content));
                        i = endIndex;
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }

        private static string BuildRegex(string pattern, char? escapeChar)
        {
            var sb = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (escapeChar.HasValue && ch == escapeChar.Value && i + 1 < pattern.Length)
                {
                    sb.Append(Regex.Escape(pattern[++i].ToString()));
                    continue;
                }

                switch (ch)
                {
                    case '%':
                        sb.Append(".*");
                        break;
                    case '_':
                        sb.Append('.');
                        break;
                    case '[' when TryReadBracket(pattern, i, out var endIndex, out var content):
                        sb.Append(BuildRegexBracket(content));
                        i = endIndex;
                        break;
                    default:
                        sb.Append(Regex.Escape(ch.ToString()));
                        break;
                }
            }

            sb.Append('$');
            return sb.ToString();
        }

        private static bool TryReadBracket(string pattern, int startIndex, out int endIndex, out string content)
        {
            endIndex = -1;
            content = string.Empty;
            if (startIndex < 0 || startIndex >= pattern.Length || pattern[startIndex] != '[')
            {
                return false;
            }

            endIndex = pattern.IndexOf(']', startIndex + 1);
            if (endIndex <= startIndex + 1)
            {
                return false;
            }

            content = pattern[(startIndex + 1)..endIndex];
            return true;
        }

        private static string BuildRegexBracket(string content)
        {
            var negated = content.StartsWith('^');
            var body = negated ? content[1..] : content;
            var escaped = EscapeRegexClassBody(body);
            return negated ? $"[^{escaped}]" : $"[{escaped}]";
        }

        private static string EscapeRegexClassBody(string body)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                var ch = body[i];
                if (ch == '-' && i > 0 && i < body.Length - 1)
                {
                    sb.Append(ch);
                    continue;
                }

                if (ch is '\\' or ']' or '[' or '^' or '-')
                {
                    sb.Append('\\');
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        private static char ChooseBracketCharacter(string content)
        {
            var negated = content.StartsWith('^');
            var body = negated ? content[1..] : content;
            if (string.IsNullOrEmpty(body))
            {
                return 'X';
            }

            if (!negated)
            {
                return ChoosePositiveBracketCharacter(body);
            }

            foreach (var candidate in new[] { 'Z', 'C', '0', 'x', 'N' })
            {
                if (!BracketContains(body, candidate))
                {
                    return candidate;
                }
            }

            return 'Z';
        }

        private static char ChoosePositiveBracketCharacter(string body)
        {
            for (var i = 0; i < body.Length; i++)
            {
                if (i + 2 < body.Length && body[i + 1] == '-')
                {
                    return body[i];
                }

                if (body[i] != '-')
                {
                    return body[i];
                }
            }

            return 'X';
        }

        private static bool BracketContains(string body, char candidate)
        {
            for (var i = 0; i < body.Length; i++)
            {
                if (i + 2 < body.Length && body[i + 1] == '-')
                {
                    var start = body[i];
                    var end = body[i + 2];
                    if (candidate >= start && candidate <= end)
                    {
                        return true;
                    }

                    i += 2;
                    continue;
                }

                if (body[i] == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        private static char? ResolveEscapeCharacter(string? escape)
        {
            return string.IsNullOrEmpty(escape) ? null : escape[0];
        }

        private static string FitToColumn(string value, ColumnSchema column)
        {
            var maxLen = NormalizeMaxLength(column.MaxLength);
            return value.Length <= maxLen ? value : value[..maxLen];
        }

        private static int NormalizeMaxLength(int? maxLength)
        {
            return !maxLength.HasValue || maxLength.Value <= 0 ? 4000 : maxLength.Value;
        }
    }
}
