using System.Text;

namespace Autopilot.Core.Process;

/// <summary>
/// Reads a text stream into memory in the background. Unlike <c>ReadToEndAsync</c>, the text read so
/// far can be taken at any time, so a caller that stops waiting still gets what arrived.
/// </summary>
public sealed class TextCapture
{
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    private TextCapture(TextReader reader)
    {
        Completion = PumpAsync(reader);
    }

    /// <summary>Finishes when the stream ends (or fails).</summary>
    public Task Completion { get; }

    public static TextCapture Start(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TextCapture(reader);
    }

    /// <summary>The text read so far.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Waits for the end of the stream for at most <paramref name="limit"/>, then returns the text read so far.</summary>
    public async Task<string> TextAfterAsync(TimeSpan limit)
    {
        await ProcessRunner.WaitAtMostAsync(Completion, limit).ConfigureAwait(false);
        return Text;
    }

    private async Task PumpAsync(TextReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                lock (_gate)
                {
                    _text.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe broke; keep what was read.
        }
    }
}
