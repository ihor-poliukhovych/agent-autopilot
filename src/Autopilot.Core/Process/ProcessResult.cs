namespace Autopilot.Core.Process;

/// <summary>Outcome of one finished (or killed) child process.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    /// <summary>Standard output and standard error interleaved in the order they arrived.</summary>
    string Output,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}
