namespace CreateAgent.Storage.FileSystem;

internal static class FileSystemSecurity
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void MakeDirectoryPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    public static void MakeFilePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }
}
