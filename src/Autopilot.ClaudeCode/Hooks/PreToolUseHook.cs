using System.Text.Json;
using System.Text.Json.Nodes;
using Autopilot.Core.Policy;

namespace Autopilot.ClaudeCode.Hooks;

/// <summary>
/// The PreToolUse hook. Claude Code starts it before every tool call with the call as JSON on
/// stdin. Allow: no output, exit 0, so Claude Code's own permission rules still apply.
/// Deny: the documented <c>hookSpecificOutput</c> JSON on stdout, the reason on stderr, exit 2.
/// Any failure denies (fails closed).
/// </summary>
public static class PreToolUseHook
{
    public const int AllowExitCode = 0;

    /// <summary>Exit code 2 blocks the call even if the JSON on stdout were ignored.</summary>
    public const int DenyExitCode = 2;

    public const string LogFileName = "hook-log.jsonl";

    /// <summary>
    /// Entry point of <c>autopilot hook pre-tool-use --policy &lt;file&gt;</c>. Reads Claude Code's hook
    /// JSON from stdin, writes the decision, returns the process exit code. Fails closed.
    /// </summary>
    public static Task<int> RunAsync(string policyFile, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        RunAsync(policyFile, stdin, stdout, stderr, ToolPolicyEvaluator.Evaluate, ToolCallCounter.Increment);

    /// <summary>Same as the other overload, with the evaluator and the counter injected.</summary>
    public static async Task<int> RunAsync(
        string policyFile,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<ToolCall, ToolPolicy, int, PolicyDecision> evaluate,
        Func<string, int> incrementCounter)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(incrementCounter);

        string toolName = "(unknown)";
        int? callNumber = null;
        PolicyDecision decision;

        try
        {
            string input = await stdin.ReadToEndAsync().ConfigureAwait(false);
            ToolCall call = ParseCall(input);
            toolName = call.ToolName;

            if (string.IsNullOrWhiteSpace(policyFile))
            {
                throw new InvalidDataException("No policy file was given.");
            }

            ToolPolicy policy = PolicyJson.Deserialize(await File.ReadAllTextAsync(policyFile).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(policy.CounterFile))
            {
                throw new InvalidDataException("The policy has no counter file.");
            }

            callNumber = incrementCounter(policy.CounterFile);
            decision = evaluate(call, policy, callNumber.Value)
                ?? throw new InvalidOperationException("The policy evaluator returned no decision.");
        }
        catch (Exception ex)
        {
            decision = PolicyDecision.Deny($"Autopilot hook failed, so the call is denied: {ex.GetType().Name}: {ex.Message}");
        }

        TryLog(policyFile, toolName, callNumber, decision);

        if (decision.Allowed)
        {
            return AllowExitCode;
        }

        await stdout.WriteAsync(BuildDenyJson(decision.Reason)).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
        await stderr.WriteLineAsync(decision.Reason).ConfigureAwait(false);
        await stderr.FlushAsync().ConfigureAwait(false);
        return DenyExitCode;
    }

    /// <summary>The deny output documented for PreToolUse hooks.</summary>
    public static string BuildDenyJson(string reason) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        },
    }.ToJsonString();

    /// <summary>Reads <c>tool_name</c> and <c>tool_input</c> from the hook's stdin JSON.</summary>
    public static ToolCall ParseCall(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidDataException("The hook received no input.");
        }

        using JsonDocument document = JsonDocument.Parse(input);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The hook input is not a JSON object.");
        }

        if (root.TryGetProperty("hook_event_name", out JsonElement eventName)
            && eventName.ValueKind == JsonValueKind.String
            && eventName.GetString() != "PreToolUse")
        {
            throw new InvalidDataException($"Unexpected hook event \"{eventName.GetString()}\".");
        }

        if (!root.TryGetProperty("tool_name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()))
        {
            throw new InvalidDataException("The hook input has no tool_name.");
        }

        JsonElement toolInput = root.TryGetProperty("tool_input", out JsonElement ti) && ti.ValueKind == JsonValueKind.Object
            ? ti.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        return new ToolCall(name.GetString()!, toolInput);
    }

    /// <summary>One line per decision: time, tool, call number, decision and reason. Never tool input.</summary>
    private static void TryLog(string policyFile, string toolName, int? callNumber, PolicyDecision decision)
    {
        try
        {
            string? directory = string.IsNullOrWhiteSpace(policyFile) ? null : Path.GetDirectoryName(Path.GetFullPath(policyFile));
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            string line = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow.ToString("O"),
                ["tool"] = toolName,
                ["call"] = callNumber,
                ["decision"] = decision.Allowed ? "allow" : "deny",
                ["reason"] = decision.Reason,
            }.ToJsonString();

            File.AppendAllText(Path.Combine(directory, LogFileName), line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never change the decision.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
