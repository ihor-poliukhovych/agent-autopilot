using Autopilot.Core.Pool;

namespace Autopilot.Cli.Commands;

/// <summary>The running UI as written in its pid file: the process id and the version that started it.</summary>
public sealed record UiProcessInfo(int Pid, string? Version);

/// <summary>
/// The <c>ui.pid</c> file in the workspace folder. <c>autopilot ui</c> writes it at start and deletes it on a normal stop;
/// <c>ui restart</c> and <c>ui status</c> read it. A missing, corrupt or stale file means no UI is running.
/// </summary>
public static class UiPidFile
{
    public const string FileName = "ui.pid";

    /// <summary>The version of this program, without the build metadata after '+'.</summary>
    public static string CurrentVersion()
    {
        var assembly = typeof(UiPidFile).Assembly;
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    public static string PathFor(string workspaceRoot) => Path.Combine(workspaceRoot, FileName);

    /// <summary>Writes the pid on the first line and the version on the second.</summary>
    public static void Write(string path, int pid, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{pid}\n{version}\n");
    }

    /// <summary>Reads the file. Null when it is missing, empty, unreadable or does not start with a positive number.</summary>
    public static UiProcessInfo? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        if (!int.TryParse(lines[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return null;
        }

        return new UiProcessInfo(pid, lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null);
    }

    /// <summary>The UI that is running now: the file is there, valid, and its process is alive.</summary>
    public static UiProcessInfo? ReadRunning(string path, Func<int, bool>? isAlive)
    {
        var info = Read(path);
        return info is not null && (isAlive ?? ProjectRunLock.IsAlive)(info.Pid) ? info : null;
    }

    /// <summary>Deletes the file. With <paramref name="onlyIfPid"/> it stays when another UI has written its own pid since.</summary>
    public static void Delete(string path, int? onlyIfPid = null)
    {
        try
        {
            if (onlyIfPid is { } pid && Read(path)?.Pid is { } found && found != pid)
            {
                return;
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process holds it; the next start overwrites it.
        }
    }
}
