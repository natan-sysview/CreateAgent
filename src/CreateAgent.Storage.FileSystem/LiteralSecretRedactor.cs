namespace CreateAgent.Storage.FileSystem;

public sealed class LiteralSecretRedactor : ISecretRedactor
{
    public string Redact(string value, IReadOnlyList<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(secrets);

        var redacted = value;
        foreach (var secret in secrets
                     .Where(static secret => !string.IsNullOrEmpty(secret))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(static secret => secret.Length))
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return redacted;
    }
}
