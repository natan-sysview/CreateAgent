namespace CreateAgent.Cli;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options;

    private CliArguments(
        IReadOnlyList<string> positionals,
        Dictionary<string, string?> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }

    public static CliArguments Parse(
        IReadOnlyList<string> arguments,
        IReadOnlySet<string> valueOptions,
        IReadOnlySet<string> flagOptions)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionalOnly = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (positionalOnly)
            {
                positionals.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = separator < 0 ? argument : argument[..separator];
            if (!valueOptions.Contains(name) && !flagOptions.Contains(name))
            {
                throw new CliUsageException($"Unknown option '{name}'.");
            }

            if (!options.TryAdd(name, null))
            {
                throw new CliUsageException($"Option '{name}' was supplied more than once.");
            }

            if (flagOptions.Contains(name))
            {
                if (separator >= 0)
                {
                    throw new CliUsageException($"Flag '{name}' does not accept a value.");
                }

                continue;
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else
            {
                if (index + 1 >= arguments.Count ||
                    arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException($"Option '{name}' requires a value.");
                }

                value = arguments[++index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliUsageException($"Option '{name}' cannot be empty.");
            }

            options[name] = value;
        }

        return new CliArguments(positionals, options);
    }

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string? GetValue(string name) =>
        _options.TryGetValue(name, out var value) ? value : null;

    public int? GetPositiveInt32(string name)
    {
        var value = GetValue(name);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new CliUsageException($"Option '{name}' must be a positive integer.");
        }

        return parsed;
    }
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
