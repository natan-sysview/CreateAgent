namespace CreateAgent.Providers.OpenCode;

internal static class ExecutableLocator
{
    public static string? Find(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (Path.IsPathRooted(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? GetWindowsExtensions()
            : [string.Empty];

        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] GetWindowsExtensions()
    {
        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        return string.IsNullOrWhiteSpace(pathExtensions)
            ? [".exe", ".cmd", ".bat"]
            : pathExtensions.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
