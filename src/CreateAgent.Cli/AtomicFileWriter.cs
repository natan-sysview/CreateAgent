using System.Text;

namespace CreateAgent.Cli;

internal static class AtomicFileWriter
{
    public static async Task WriteAsync(
        string path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && !overwrite)
        {
            throw new CliUsageException(
                $"File already exists: {fullPath}. Use --force to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            MakePrivate(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite);
            MakePrivate(fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
