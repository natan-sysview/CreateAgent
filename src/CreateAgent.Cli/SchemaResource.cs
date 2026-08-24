using System.Reflection;

namespace CreateAgent.Cli;

internal static class SchemaResource
{
    private const string ResourceName =
        "CreateAgent.Cli.Schemas.agent-run-request.schema.json";

    public static async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly()
                                     .GetManifestResourceStream(ResourceName) ??
                                 throw new InvalidOperationException(
                                     $"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
