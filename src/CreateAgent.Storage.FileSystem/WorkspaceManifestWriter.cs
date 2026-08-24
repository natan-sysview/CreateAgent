using System.Security.Cryptography;

namespace CreateAgent.Storage.FileSystem;

internal sealed class WorkspaceManifestWriter
{
    private readonly string _workspaceRoot;

    public WorkspaceManifestWriter(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public async Task<WorkspaceFileManifest> CreateAsync(
        AgentWorkspaceManifestStage stage,
        CancellationToken cancellationToken)
    {
        var entries = new List<WorkspaceFileManifestEntry>();
        var errors = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_workspaceRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] children;
            try
            {
                children = Directory
                    .EnumerateFileSystemEntries(directory)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(
                    $"Could not enumerate '{ToRelativePath(directory)}': {exception.Message}");
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = ToRelativePath(child);
                try
                {
                    var attributes = File.GetAttributes(child);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var info = isDirectory
                        ? (FileSystemInfo)new DirectoryInfo(child)
                        : new FileInfo(child);
                    var isLink =
                        attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        info.LinkTarget is not null;

                    if (isLink)
                    {
                        entries.Add(
                            new WorkspaceFileManifestEntry(
                                relativePath,
                                "symbolic-link",
                                null,
                                null,
                                info.LastWriteTimeUtc,
                                info.LinkTarget));
                        continue;
                    }

                    if (isDirectory)
                    {
                        entries.Add(
                            new WorkspaceFileManifestEntry(
                                relativePath,
                                "directory",
                                null,
                                null,
                                info.LastWriteTimeUtc,
                                null));
                        pending.Push(child);
                        continue;
                    }

                    var file = (FileInfo)info;
                    var hash = await ComputeSha256Async(child, cancellationToken)
                        .ConfigureAwait(false);
                    entries.Add(
                        new WorkspaceFileManifestEntry(
                            relativePath,
                            "file",
                            file.Length,
                            hash,
                            file.LastWriteTimeUtc,
                            null));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Could not inspect '{relativePath}': {exception.Message}");
                    entries.Add(
                        new WorkspaceFileManifestEntry(
                            relativePath,
                            "unreadable",
                            null,
                            null,
                            null,
                            null));
                }
            }
        }

        entries.Sort(
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.Path, right.Path));
        return new WorkspaceFileManifest(
            "1.0",
            stage,
            DateTimeOffset.UtcNow,
            entries.Count,
            entries.Where(static entry => entry.SizeBytes.HasValue)
                .Sum(static entry => entry.SizeBytes!.Value),
            entries,
            errors);
    }

    public static WorkspaceFileChanges Compare(
        WorkspaceFileManifest? before,
        WorkspaceFileManifest after)
    {
        var beforeEntries = (before?.Entries ?? [])
            .Where(static entry => entry.Type != "directory")
            .ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        var afterEntries = after.Entries
            .Where(static entry => entry.Type != "directory")
            .ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        var changes = new List<WorkspaceFileChange>();

        foreach (var path in beforeEntries.Keys
                     .Union(afterEntries.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var existedBefore = beforeEntries.TryGetValue(path, out var beforeEntry);
            var existsAfter = afterEntries.TryGetValue(path, out var afterEntry);
            if (!existedBefore)
            {
                changes.Add(new WorkspaceFileChange(path, "created", null, afterEntry));
            }
            else if (!existsAfter)
            {
                changes.Add(new WorkspaceFileChange(path, "deleted", beforeEntry, null));
            }
            else if (!Equivalent(beforeEntry!, afterEntry!))
            {
                changes.Add(
                    new WorkspaceFileChange(path, "modified", beforeEntry, afterEntry));
            }
        }

        return new WorkspaceFileChanges(
            "1.0",
            DateTimeOffset.UtcNow,
            changes.Count,
            changes,
            (before?.Errors ?? []).Concat(after.Errors).ToArray());
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private string ToRelativePath(string path)
    {
        var relative = Path.GetRelativePath(_workspaceRoot, path);
        return relative == "."
            ? "."
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool Equivalent(
        WorkspaceFileManifestEntry before,
        WorkspaceFileManifestEntry after) =>
        before.Type == after.Type &&
        before.SizeBytes == after.SizeBytes &&
        string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal) &&
        string.Equals(before.LinkTarget, after.LinkTarget, StringComparison.Ordinal);
}

internal sealed record WorkspaceFileManifest(
    string SchemaVersion,
    AgentWorkspaceManifestStage Stage,
    DateTimeOffset GeneratedAtUtc,
    int EntryCount,
    long TotalFileBytes,
    IReadOnlyList<WorkspaceFileManifestEntry> Entries,
    IReadOnlyList<string> Errors);

internal sealed record WorkspaceFileManifestEntry(
    string Path,
    string Type,
    long? SizeBytes,
    string? Sha256,
    DateTimeOffset? ModifiedAtUtc,
    string? LinkTarget);

internal sealed record WorkspaceFileChanges(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    int ChangeCount,
    IReadOnlyList<WorkspaceFileChange> Changes,
    IReadOnlyList<string> Errors);

internal sealed record WorkspaceFileChange(
    string Path,
    string Change,
    WorkspaceFileManifestEntry? Before,
    WorkspaceFileManifestEntry? After);
