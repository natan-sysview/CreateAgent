using System.IO.Compression;
using System.Security.Cryptography;

namespace CreateAgent.Storage.FileSystem;

public sealed class ZipFailureBundler : IAgentFailureBundler
{
    private readonly string _storageRoot;

    public ZipFailureBundler(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new ArgumentException("Storage root cannot be empty.", nameof(storageRoot));
        }

        _storageRoot = Path.GetFullPath(storageRoot);
    }

    public async Task<FailureBundle> BundleAsync(
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var quarantineDirectory = Path.Combine(
            _storageRoot,
            "quarantine",
            DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(quarantineDirectory);
        FileSystemSecurity.MakeDirectoryPrivate(quarantineDirectory);

        var archivePath = Path.Combine(
            quarantineDirectory,
            Path.GetFileName(workspace.RunDirectory) + ".zip");
        var temporaryPath = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await CreateArchiveSafelyAsync(
                    workspace.RunDirectory,
                    temporaryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            FileSystemSecurity.MakeFilePrivate(temporaryPath);
            File.Move(temporaryPath, archivePath, overwrite: true);
            FileSystemSecurity.MakeFilePrivate(archivePath);

            await using var archive = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(archive, cancellationToken)
                .ConfigureAwait(false);
            var info = new FileInfo(archivePath);
            return new FailureBundle(
                archivePath,
                Convert.ToHexStringLower(hash),
                info.Length);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task CreateArchiveSafelyAsync(
        string sourceRoot,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(sourceRoot);
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Create,
            leaveOpen: true);
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            EnsureNotLink(new DirectoryInfo(directory));

            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entryPath);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                FileSystemInfo fileSystemInfo = isDirectory
                    ? new DirectoryInfo(entryPath)
                    : new FileInfo(entryPath);
                EnsureNotLink(fileSystemInfo);

                if (isDirectory)
                {
                    pending.Push(entryPath);
                    continue;
                }

                var relativePath = Path.GetRelativePath(normalizedRoot, entryPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var archiveEntry = archive.CreateEntry(
                    relativePath,
                    CompressionLevel.Optimal);
                await using var source = new FileStream(
                    entryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = archiveEntry.Open();
                await source.CopyToAsync(destination, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNotLink(FileSystemInfo fileSystemInfo)
    {
        if (fileSystemInfo.LinkTarget is not null ||
            fileSystemInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"Failure archive rejected a symbolic link: {fileSystemInfo.FullName}");
        }
    }
}
