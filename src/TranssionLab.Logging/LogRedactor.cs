using System.Text.RegularExpressions;

namespace TranssionLab.Logging;

/// <summary>
/// Redacts secrets, tokens, private keys, and sensitive credential arguments before persisting or displaying logs.
/// </summary>
public static partial class LogRedactor
{
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(@"(?i)(--?(?:token|password|passwd|secret|api[-_]?key|auth[-_]?key|psk|bearer)\s*[=:\s]\s*)([^\s""']+|\""[^\""]*\""|'[^']*')")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerHeaderRegex();

    public static string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = PrivateKeyBlockRegex().Replace(input, "[REDACTED_PRIVATE_KEY]");
        result = KeyValueSecretRegex().Replace(result, "$1[REDACTED]");
        result = BearerHeaderRegex().Replace(result, "$1[REDACTED_TOKEN]");
        return result;
    }
}
