namespace Lighthouse.Services.Notifications;

/// <summary>
/// Parsing, validation and display-masking for the free-form custom headers of the
/// generic webhook. The text holds one <c>Name: Value</c> header per line; blank
/// lines are ignored and the value is everything after the first colon.
/// </summary>
public static class WebhookHeaders
{
    /// <summary>Placeholder returned by the API instead of the configured headers.</summary>
    public const string MaskedValue = "••••••••";

    // Set by the HTTP client itself; overriding them would break the request.
    private static readonly string[] ForbiddenNames = { "Content-Type", "Content-Length", "Host" };

    // RFC 7230 "tchar" punctuation (letters and digits are also allowed).
    private const string TokenPunctuation = "!#$%&'*+-.^_`|~";

    /// <summary>
    /// Parses the header text. Null/blank text yields an empty list. On failure
    /// <paramref name="error"/> names the 1-based line number and the reason.
    /// </summary>
    public static bool TryParse(
        string? text,
        out IReadOnlyList<KeyValuePair<string, string>> headers,
        out string? error)
    {
        var parsed = new List<KeyValuePair<string, string>>();
        headers = parsed;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int lineNumber = i + 1;
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                error = $"Line {lineNumber}: missing ':' (expected 'Name: Value')";
                return false;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (!IsValidToken(name))
            {
                error = $"Line {lineNumber}: invalid header name '{name}'";
                return false;
            }

            if (ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Line {lineNumber}: header '{name}' cannot be overridden";
                return false;
            }

            parsed.Add(new KeyValuePair<string, string>(name, value));
        }

        return true;
    }

    /// <summary>Returns <see cref="MaskedValue"/> when headers are configured, otherwise an empty string.</summary>
    public static string Mask(string? value) => string.IsNullOrEmpty(value) ? string.Empty : MaskedValue;

    private static bool IsValidToken(string name)
    {
        return name.Length > 0
            && name.All(c => char.IsAsciiLetterOrDigit(c) || TokenPunctuation.Contains(c));
    }
}
