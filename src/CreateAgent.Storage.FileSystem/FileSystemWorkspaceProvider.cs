using System.Globalization;
using System.Text;

namespace CreateAgent.Storage.FileSystem;

public sealed class FileSystemWorkspaceProvider : IAgentWorkspaceProvider
{
    private static readonly HashSet<string> ExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".createagent",
        };

    private readonly string _storageRoot;
    private readonly object _directoryAllocationLock = new();

    public FileSystemWorkspaceProvider(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new ArgumentException("Storage root cannot be empty.", nameof(storageRoot));
        }

        _storageRoot = Path.GetFullPath(storageRoot);
    }

    public async Task<AgentWorkspace> PrepareAsync(
        string runId,
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var localNow = DateTimeOffset.Now;
        var dayDirectory = Path.Combine(
            _storageRoot,
            "runs",
            localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var runDirectory = AllocateRunDirectory(
            dayDirectory,
            CreateDirectorySlug(request.Name),
            localNow);
        var workspaceDirectory = Path.Combine(runDirectory, "workspace");
        var auditDirectory = Path.Combine(runDirectory, "audit");

        CreatePrivateDirectory(workspaceDirectory);
        CreatePrivateDirectory(auditDirectory);

        foreach (var source in request.Workspace.Sources)
        {
            await CopySourceAsync(
                    source,
                    workspaceDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var workingDirectory = workspaceDirectory;

        return new AgentWorkspace(
            runId,
            runDirectory,
            workspaceDirectory,
            workingDirectory,
            auditDirectory,
            Path.Combine(auditDirectory, "output.json"));
    }

    public Task<AgentWorkspace> PrepareResumeAsync(
        string runId,
        AgentRunRequest request,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedWorkspace = Path.GetFullPath(workspacePath);
        var runsRoot = Path.Combine(_storageRoot, "runs");
        if (!IsSameOrChildPath(normalizedWorkspace, runsRoot) ||
            !string.Equals(
                Path.GetFileName(normalizedWorkspace),
                "workspace",
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session workspace is outside the configured runs directory: {normalizedWorkspace}");
        }

        if (!Directory.Exists(normalizedWorkspace))
        {
            throw new AgentSessionUnavailableException(
                $"The workspace for session '{request.Session.Id}' no longer exists: {normalizedWorkspace}");
        }

        if (IsSymlink(new DirectoryInfo(normalizedWorkspace)))
        {
            throw new InvalidOperationException(
                $"Session workspace cannot be a symbolic link: {normalizedWorkspace}");
        }

        var localNow = DateTimeOffset.Now;
        var dayDirectory = Path.Combine(
            _storageRoot,
            "runs",
            localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var runDirectory = AllocateRunDirectory(
            dayDirectory,
            CreateDirectorySlug(request.Name),
            localNow);
        var auditDirectory = Path.Combine(runDirectory, "audit");
        CreatePrivateDirectory(auditDirectory);

        return Task.FromResult(
            new AgentWorkspace(
                runId,
                runDirectory,
                normalizedWorkspace,
                normalizedWorkspace,
                auditDirectory,
                Path.Combine(auditDirectory, "output.json")));
    }

    public Task CleanupSuccessfulWorkspaceAsync(
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(workspace.WorkspaceDirectory))
        {
            Directory.Delete(workspace.WorkspaceDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task CopySourceAsync(
        AgentWorkspaceSource source,
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(source.Path);
        if (File.Exists(sourcePath))
        {
            var file = new FileInfo(sourcePath);
            if (IsSymlink(file))
            {
                throw new InvalidOperationException(
                    $"Workspace source cannot be a symbolic link: {sourcePath}");
            }

            await CopyFileAsync(
                    sourcePath,
                    SafeCombine(workspaceDirectory, file.Name),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var directory = new DirectoryInfo(sourcePath);
        var destination = source.Copy == AgentWorkspaceCopyMode.Contents
            ? workspaceDirectory
            : SafeCombine(workspaceDirectory, directory.Name);
        await CopyDirectoryAsync(sourcePath, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CopyDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (IsSymlink(new DirectoryInfo(sourceRoot)))
        {
            throw new InvalidOperationException(
                $"Workspace source cannot be a symbolic link: {sourceRoot}");
        }

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            CreatePrivateDirectory(destination);

            foreach (var entryPath in Directory.EnumerateFileSystemEntries(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new FileInfo(entryPath);
                var attributes = File.GetAttributes(entryPath);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                FileSystemInfo fileSystemInfo = isDirectory
                    ? new DirectoryInfo(entryPath)
                    : entry;

                if (IsSymlink(fileSystemInfo))
                {
                    throw new InvalidOperationException(
                        $"Symbolic links are not allowed in copied workspaces: {entryPath}");
                }

                if (isDirectory &&
                    (ExcludedDirectoryNames.Contains(fileSystemInfo.Name) ||
                     IsSameOrChildPath(entryPath, _storageRoot)))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destination, fileSystemInfo.Name);
                if (isDirectory)
                {
                    pending.Push((entryPath, destinationPath));
                    continue;
                }

                await CopyFileAsync(entryPath, destinationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        FileSystemSecurity.MakeFilePrivate(destinationPath);
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!combined.Equals(normalizedRoot, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path escapes its allowed root: {relativePath}");
        }

        return combined;
    }

    private static bool IsSameOrChildPath(string path, string potentialParent)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedParent = Path.GetFullPath(potentialParent);
        var parentWithSeparator = normalizedParent.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedPath.Equals(normalizedParent, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(parentWithSeparator, StringComparison.Ordinal);
    }

    private static bool IsSymlink(FileSystemInfo info) =>
        info.LinkTarget is not null ||
        info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private string AllocateRunDirectory(
        string dayDirectory,
        string requestSlug,
        DateTimeOffset localNow)
    {
        lock (_directoryAllocationLock)
        {
            CreatePrivateDirectory(dayDirectory);
            var baseName = string.Concat(
                requestSlug,
                "-",
                localNow.ToString("HHmmss", CultureInfo.InvariantCulture));

            for (var suffix = 1; suffix <= 999; suffix++)
            {
                var directoryName = suffix == 1
                    ? baseName
                    : string.Concat(
                        baseName,
                        "-",
                        suffix.ToString(CultureInfo.InvariantCulture));
                var candidate = SafeCombine(dayDirectory, directoryName);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    continue;
                }

                CreatePrivateDirectory(candidate);
                return candidate;
            }
        }

        throw new IOException(
            $"Could not allocate a readable run directory for '{requestSlug}'.");
    }

    private static string CreateDirectorySlug(string value)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = true;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) ==
                UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }

            if (builder.Length >= 80)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "agent-run" : slug;
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        FileSystemSecurity.MakeDirectoryPrivate(path);
    }
}
