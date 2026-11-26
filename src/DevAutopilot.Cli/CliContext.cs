using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Facts;
using DevAutopilot.Core.Gate;

namespace DevAutopilot.Cli;

/// <summary>Streams and replaceable parts for one CLI invocation. Tests swap the agent runner and the gate.</summary>
public sealed record CliContext(TextReader In, TextWriter Out, TextWriter Error)
{
    /// <summary>Builds the agent runner. Null means Claude Code headless.</summary>
    public Func<IAgentRunner>? RunnerFactory { get; init; }

    /// <summary>The gate. Null means <see cref="MergeGate.Evaluate"/>.</summary>
    public Func<GateInput, AutopilotConfig, GateDecision>? EvaluateGate { get; init; }

    /// <summary>Gate facts collector. Null means <see cref="GateFactsCollector"/>.</summary>
    public Func<AutopilotConfig, IGateFactsCollector>? FactsCollectorFactory { get; init; }

    /// <summary>Folder for worktrees. Null means the per-user default outside the project.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>False skips the dotnet, git and claude version checks in doctor (tests never start claude).</summary>
    public bool DoctorChecksTools { get; init; } = true;

    public static CliContext FromConsole() => new(Console.In, Console.Out, Console.Error);
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
}
