using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DevAutopilot.Core.Facts;

/// <summary>Test counts summed over the TRX files of one test run.</summary>
public sealed record TrxCounts(int Files, int Total, int Skipped, int Failed)
{
    public static TrxCounts None { get; } = new(0, 0, 0, 0);

    public bool Found => Files > 0;
}

/// <summary>
/// The gate counts tests from TRX result files, not from console text the tests themselves could print.
/// For <c>dotnet test</c> the collector adds a TRX logger and a results folder that only the harness
/// writes to, then reads every TRX file in it.
/// </summary>
public static class TrxTestCounts
{
    /// <summary>
    /// Returns the test command with <c>--logger trx --results-directory "&lt;folder&gt;"</c> added, or null
    /// when the command does not start with <c>dotnet test</c> (then no TRX is written and the counts
    /// stay unknown, which the gate rejects).
    /// </summary>
    public static string? WithTrxLogger(string testCommand, string resultsDirectory)
    {
        ArgumentNullException.ThrowIfNull(testCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsDirectory);
        if (resultsDirectory.Contains('"'))
        {
            throw new ArgumentException("The results folder must not contain a double quote.", nameof(resultsDirectory));
        }

        var command = testCommand.Trim();
        var isDotnetTest = command.Equals("dotnet test", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("dotnet test ", StringComparison.OrdinalIgnoreCase);
        return isDotnetTest
            ? $"{command} --logger trx --results-directory \"{resultsDirectory}\""
            : null;
    }

    /// <summary>
    /// Sums ResultSummary/Counters over every <c>*.trx</c> file under <paramref name="resultsDirectory"/>
    /// written at or after <paramref name="notBeforeUtc"/>. Skipped is <c>notExecuted</c> (or
    /// <c>skipped</c> where a writer uses that name); failed is <c>failed</c> plus <c>error</c>.
    /// Throws <see cref="InvalidDataException"/> when a file has no counters, so the caller fails closed.
    /// </summary>
    public static TrxCounts Read(string resultsDirectory, DateTime notBeforeUtc)
    {
        if (!Directory.Exists(resultsDirectory))
        {
            return TrxCounts.None;
        }

        int files = 0, total = 0, skipped = 0, failed = 0;
        foreach (var file in Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) < notBeforeUtc)
            {
                continue;
            }

            var counts = ReadFile(file);
            files++;
            total += counts.Total;
            skipped += counts.Skipped;
            failed += counts.Failed;
        }

        return new TrxCounts(files, total, skipped, failed);
    }

    public static TrxCounts ReadFile(string path)
    {
        XDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            document = XDocument.Load(stream, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not valid TRX: {ex.Message}", ex);
        }

        var counters = document.Descendants()
            .Where(e => e.Name.LocalName == "Counters" && e.Parent?.Name.LocalName == "ResultSummary")
            .FirstOrDefault()
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no ResultSummary counters.");

        var total = Attribute(counters, "total")
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no total test count.");
        var skipped = Attribute(counters, "notExecuted") ?? Attribute(counters, "skipped") ?? 0;
        var failed = (Attribute(counters, "failed") ?? 0) + (Attribute(counters, "error") ?? 0);
        return new TrxCounts(1, total, skipped, failed);
    }

    private static int? Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
