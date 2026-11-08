using System.Text;

namespace Lighthouse.Utils;

/// <summary>
/// Minimal implementation of docker compose variable interpolation, used to resolve values such as
/// <c>image: ${REPO:-ghcr.io/owner/app}:${TAG:-latest}</c> before they are parsed as image references.
/// <para>
/// Supported forms: <c>$VAR</c>, <c>${VAR}</c>, <c>${VAR:-default}</c> (unset or empty),
/// <c>${VAR-default}</c> (unset), <c>${VAR:+alt}</c> / <c>${VAR+alt}</c>, <c>${VAR:?err}</c> /
/// <c>${VAR?err}</c> (treated as a plain lookup), nested interpolation inside defaults, and
/// <c>$$</c> as an escaped literal <c>$</c>.
/// </para>
/// </summary>
public static class ComposeVariableInterpolator
{
    /// <summary>
    /// Interpolates <paramref name="input"/> using <paramref name="lookup"/> (returns <c>null</c> for an
    /// unset variable). <paramref name="unresolved"/> lists the variables that were unset and had no
    /// default; like docker compose, they are replaced with an empty string.
    /// </summary>
    public static string Interpolate(string input, Func<string, string?> lookup, out IReadOnlyList<string> unresolved)
    {
        var missing = new List<string>();
        string result = InterpolateCore(input, lookup, missing);
        unresolved = missing;
        return result;
    }

    private static string InterpolateCore(string input, Func<string, string?> lookup, List<string> missing)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('$'))
        {
            return input;
        }

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c != '$' || i + 1 >= input.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }

            char next = input[i + 1];
            if (next == '$')
            {
                sb.Append('$');
                i += 2;
                continue;
            }

            if (next == '{')
            {
                int close = FindClosingBrace(input, i + 2);
                if (close < 0)
                {
                    // Unterminated expression: keep it verbatim.
                    sb.Append(input, i, input.Length - i);
                    break;
                }

                sb.Append(EvaluateBraced(input.Substring(i + 2, close - i - 2), lookup, missing));
                i = close + 1;
                continue;
            }

            if (IsNameStart(next))
            {
                int end = i + 1;
                while (end < input.Length && IsNameChar(input[end])) end++;
                string name = input.Substring(i + 1, end - i - 1);
                string? value = lookup(name);
                if (value == null) missing.Add(name);
                sb.Append(value ?? string.Empty);
                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string EvaluateBraced(string expression, Func<string, string?> lookup, List<string> missing)
    {
        int nameEnd = 0;
        while (nameEnd < expression.Length && IsNameChar(expression[nameEnd])) nameEnd++;
        string name = expression[..nameEnd];
        string rest = expression[nameEnd..];
        string? value = lookup(name);

        if (rest.Length == 0)
        {
            if (value == null) missing.Add(name);
            return value ?? string.Empty;
        }

        bool colon = rest[0] == ':';
        string op = colon ? rest[..Math.Min(2, rest.Length)] : rest[..1];
        string operand = rest[op.Length..];

        switch (op)
        {
            case ":-":
                return string.IsNullOrEmpty(value) ? InterpolateCore(operand, lookup, missing) : value;
            case "-":
                return value ?? InterpolateCore(operand, lookup, missing);
            case ":+":
                return string.IsNullOrEmpty(value) ? string.Empty : InterpolateCore(operand, lookup, missing);
            case "+":
                return value == null ? string.Empty : InterpolateCore(operand, lookup, missing);
            case ":?":
            case "?":
                if (value == null || (colon && value.Length == 0)) missing.Add(name);
                return value ?? string.Empty;
            default:
                // Unknown modifier: behave like a plain lookup of the name.
                if (value == null) missing.Add(name);
                return value ?? string.Empty;
        }
    }

    private static int FindClosingBrace(string input, int start)
    {
        int depth = 1;
        for (int i = start; i < input.Length; i++)
        {
            if (input[i] == '{') depth++;
            else if (input[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsAsciiLetter(c);

    private static bool IsNameChar(char c) => c == '_' || char.IsAsciiLetterOrDigit(c);

    /// <summary>
    /// Parses a docker compose style <c>.env</c> file: <c>KEY=VALUE</c> lines, blank lines and
    /// <c>#</c> comments ignored, optional <c>export </c> prefix, single / double quoted values
    /// (quotes stripped), and trailing <c> # comment</c> removed from unquoted values. Later keys win.
    /// </summary>
    public static Dictionary<string, string> ParseDotEnv(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
            {
                int closing = value.IndexOf(value[0], 1);
                value = closing > 0 ? value[1..closing] : value[1..];
            }
            else
            {
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }

            result[key] = value;
        }
        return result;
    }
}
