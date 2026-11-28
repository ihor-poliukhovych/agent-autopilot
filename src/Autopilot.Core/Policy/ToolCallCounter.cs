using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Autopilot.Core.Policy;

public static class ToolCallCounter
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>Atomically increments the counter in the file (creating it at 0) and returns the new value.</summary>
    public static int Increment(string counterFile) => Increment(counterFile, MaxWait);

    /// <summary>Same as <see cref="Increment(string)"/> but throws an IOException after <paramref name="timeout"/> of contention.</summary>
    public static int Increment(string counterFile, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(counterFile);

        var directory = Path.GetDirectoryName(Path.GetFullPath(counterFile));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var clock = Stopwatch.StartNew();
        var delayMs = 5;
        while (true)
        {
            try
            {
                return IncrementOnce(counterFile);
            }
            catch (IOException) when (clock.Elapsed < timeout)
            {
                // Another process holds the file. Wait and try again.
            }
            catch (UnauthorizedAccessException) when (clock.Elapsed < timeout)
            {
                // On Windows a file in use can briefly refuse access.
            }

            Thread.Sleep(delayMs + Random.Shared.Next(delayMs));
            delayMs = Math.Min(delayMs * 2, 100);
        }
    }

    private static int IncrementOnce(string counterFile)
    {
        using var stream = new FileStream(counterFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        string text;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            text = reader.ReadToEnd().Trim();
        }

        var current = 0;
        if (text.Length > 0 && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out current))
        {
            throw new InvalidDataException($"Counter file '{counterFile}' does not hold a number.");
        }

        var next = checked(current + 1);
        var bytes = Encoding.ASCII.GetBytes(next.ToString(CultureInfo.InvariantCulture));
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return next;
    }
}
