using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreateAgent.Cli;

public sealed record CliHostConfiguration
{
    [JsonRequired]
    public string SchemaVersion { get; init; } = "1.0";

    public string? StorageRoot { get; init; }

    public int? MaxConcurrentRuns { get; init; }

    public int? EventChannelCapacity { get; init; }

    public int? ProcessOutputChannelCapacity { get; init; }

    public CliProviderConfiguration? Providers { get; init; } = new();
}

public sealed record CliProviderConfiguration
{
    public CliCodexConfiguration? Codex { get; init; } = new();
}

public sealed record CliCodexConfiguration
{
    public string Executable { get; init; } = "codex";
}

internal sealed record ResolvedCliHostConfiguration(
    string StorageRoot,
    int MaxConcurrentRuns,
    int EventChannelCapacity,
    int ProcessOutputChannelCapacity,
    string CodexExecutable)
{
    public CreateAgentOptions ToCreateAgentOptions() =>
        new()
        {
            StorageRoot = StorageRoot,
            MaxConcurrentRuns = MaxConcurrentRuns,
            EventChannelCapacity = EventChannelCapacity,
            ProcessOutputChannelCapacity = ProcessOutputChannelCapacity,
        };
}

internal static class CliHostConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

    public static async Task<ResolvedCliHostConfiguration> LoadAsync(
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var configurationPath = arguments.GetValue("--config");
        CliHostConfiguration configuration;
        string baseDirectory;
        if (configurationPath is null)
        {
            configuration = new CliHostConfiguration();
            baseDirectory = Environment.CurrentDirectory;
        }
        else
        {
            var fullPath = Path.GetFullPath(configurationPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Host configuration was not found.", fullPath);
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            configuration = JsonSerializer.Deserialize<CliHostConfiguration>(
                                json,
                                JsonOptions) ??
                            throw new JsonException("Host configuration is empty.");
            baseDirectory = Path.GetDirectoryName(fullPath)!;
        }

        if (!string.Equals(configuration.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw new CreateAgentValidationException(
                ["Host configuration SchemaVersion must be 1.0."]);
        }

        var storageValue = arguments.GetValue("--storage") ??
                           configuration.StorageRoot ??
                           ".createagent";
        var codexValue = arguments.GetValue("--codex-path") ??
                         configuration.Providers?.Codex?.Executable;

        var maxConcurrent = arguments.GetPositiveInt32("--max-concurrent") ??
                            configuration.MaxConcurrentRuns ??
                            Math.Clamp(Environment.ProcessorCount, 1, 4);
        var eventCapacity = arguments.GetPositiveInt32("--event-capacity") ??
                            configuration.EventChannelCapacity ??
                            1024;
        var processCapacity =
            arguments.GetPositiveInt32("--process-output-capacity") ??
            configuration.ProcessOutputChannelCapacity ??
            1024;

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(storageValue))
        {
            errors.Add("Host StorageRoot cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(codexValue))
        {
            errors.Add(
                "Host Providers.Codex.Executable is required and cannot be empty.");
        }

        if (maxConcurrent <= 0)
        {
            errors.Add("Host MaxConcurrentRuns must be greater than zero.");
        }

        if (eventCapacity <= 0)
        {
            errors.Add("Host EventChannelCapacity must be greater than zero.");
        }

        if (processCapacity <= 0)
        {
            errors.Add("Host ProcessOutputChannelCapacity must be greater than zero.");
        }

        if (errors.Count > 0)
        {
            throw new CreateAgentValidationException(errors);
        }

        var storage = ResolvePath(baseDirectory, storageValue);
        var codex = ResolveExecutablePath(baseDirectory, codexValue!);
        return new ResolvedCliHostConfiguration(
            storage,
            maxConcurrent,
            eventCapacity,
            processCapacity,
            codex);
    }

    public static string SerializeTemplate() =>
        JsonSerializer.Serialize(
            new CliHostConfiguration
            {
                StorageRoot = "./.createagent",
                MaxConcurrentRuns = Math.Clamp(Environment.ProcessorCount, 1, 4),
                EventChannelCapacity = 1024,
                ProcessOutputChannelCapacity = 1024,
            },
            JsonOptions);

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(baseDirectory, path));

    private static string ResolveExecutablePath(
        string baseDirectory,
        string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return Path.GetFullPath(executable);
        }

        return executable.Contains(Path.DirectorySeparatorChar) ||
               executable.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(Path.Combine(baseDirectory, executable))
            : executable;
    }
}
