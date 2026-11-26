using System.Text;
using System.Text.RegularExpressions;

namespace DevAutopilot.Core.Jobs;

/// <summary>
/// A text writer for a job: every line goes to <c>log.txt</c> and, when it looks like the pipeline's
/// "&lt;id&gt;: &lt;step&gt;" progress line, becomes the job's current step.
/// </summary>
public sealed partial class JobLogWriter(string logPath, Action<string> onStep) : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                Flush(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        foreach (var c in value ?? string.Empty)
        {
            Write(c);
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    /// <summary>The step in a progress line such as "123: implementer", or null.</summary>
    public static string? StepOf(string line)
    {
        var match = Progress().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void Flush(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, $"{DateTimeOffset.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (StepOf(line) is { } step)
        {
            onStep(step);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+: (.+)$")]
    private static partial Regex Progress();
}
