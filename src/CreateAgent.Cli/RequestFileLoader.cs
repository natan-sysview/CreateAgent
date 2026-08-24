namespace CreateAgent.Cli;

internal static class RequestFileLoader
{
    public static async Task<AgentRunRequest> LoadAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(requestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Agent request was not found.", fullPath);
        }

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var request = CreateAgentJson.DeserializeRequest(json);
        var baseDirectory = Path.GetDirectoryName(fullPath)!;
        return request with
        {
            Workspace = request.Workspace with
            {
                Sources = request.Workspace.Sources
                    .Select(
                        source => source with
                        {
                            Path = Path.GetFullPath(
                                Path.IsPathRooted(source.Path)
                                    ? source.Path
                                    : Path.Combine(baseDirectory, source.Path)),
                        })
                    .ToArray(),
                ReadOnlyPaths = request.Workspace.ReadOnlyPaths
                    .Select(
                        path => Path.GetFullPath(
                            Path.IsPathRooted(path)
                                ? path
                                : Path.Combine(baseDirectory, path)))
                    .ToArray(),
            },
        };
    }
}
