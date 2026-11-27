namespace Autopilot.Core.Analysis;

/// <summary>How sure the analyzer is about a detected value.</summary>
public enum Confidence
{
    /// <summary>A guess from a convention or a fallback. Show it and ask the person to confirm.</summary>
    Low,

    /// <summary>Probably right, but the folder had more than one candidate or the rule is a convention.</summary>
    Medium,

    /// <summary>Read from a file or a git setting that says exactly this.</summary>
    High,
}

/// <summary>A value found in the folder, with the file or rule that produced it.</summary>
/// <param name="Value">The detected value (a command, a path pattern, a branch name, ...).</param>
/// <param name="Evidence">The file, git setting or rule that produced the value, in plain English.</param>
/// <param name="Confidence">How sure the analyzer is.</param>
/// <param name="Alternatives">Other values that would also be reasonable (for example the build command of another stack).</param>
public sealed record Detected(string Value, string Evidence, Confidence Confidence, IReadOnlyList<string>? Alternatives = null);

/// <summary>A language or build system found in the folder, and where its project files are.</summary>
/// <param name="Kind">dotnet, node, python, go, rust, maven or gradle.</param>
/// <param name="Path">Folder relative to the analyzed folder, with forward slashes; "." for the root.</param>
/// <param name="Evidence">The files that show it.</param>
public sealed record DetectedStack(string Kind, string Path, string Evidence);

/// <summary>A value the analyzer cannot know and the person has to give or confirm.</summary>
/// <param name="Field">The setting, for example <c>buildCommand</c> or <c>ado.tokenVariable</c>.</param>
/// <param name="Question">What to ask, in plain English.</param>
public sealed record NeedsInput(string Field, string Question);
