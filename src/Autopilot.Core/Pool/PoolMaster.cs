using System.Text;
using System.Text.Json;
using Autopilot.Core.Config;

namespace Autopilot.Core.Pool;

/// <summary>A question a task is waiting on. <see cref="Question"/> is the triage's reason, with its options and recommendation if it has any.</summary>
public sealed record MasterQuestion(string WorkItemId, string Title, string TaskText, string Question);

/// <summary>
/// What the master said about a question. The pool checks it in code (see <see cref="MasterGuard"/>):
/// the master only suggests, it never has the last word on a category in <c>alwaysAsk</c>.
/// </summary>
public sealed record MasterAnswer(bool Decide, int? Option, string Answer, string Reason, string Category, bool Reversible);

public enum FailureKind
{
    /// <summary>The machine, the network or the subscription, not the task.</summary>
    Infrastructure,

    /// <summary>The task itself: the agent did not manage it.</summary>
    Task,

    /// <summary>A defect in Autopilot, not in the task.</summary>
    AutopilotDefect,
}

public sealed record FailureInfo(string WorkItemId, string Title, string Outcome, string Message, string? LogExcerpt);

public sealed record FailureVerdict(FailureKind Kind, string Notes, string Reason, string? BugTitle);

/// <summary>The role that answers needs-human questions and classifies failures while nobody is watching.</summary>
public interface IPoolMaster
{
    Task<MasterAnswer> DecideAsync(MasterQuestion question, CancellationToken ct);

    Task<FailureVerdict> ClassifyFailureAsync(FailureInfo failure, CancellationToken ct);
}

/// <summary>Code that checks the master's answer. The model suggests, the code decides what is allowed.</summary>
public static class MasterGuard
{
    private static readonly Dictionary<string, string[]> Keywords = new(StringComparer.Ordinal)
    {
        [PoolCategories.Secrets] = ["secret", "password", "passphrase", "credential", "api key", "api-key", "apikey", "access token", "private key", "certificate", "connection string"],
        [PoolCategories.StoredData] = ["migration", "drop table", "delete data", "delete all", "existing data", "stored data", "backfill", "truncate", "production data", "database schema"],
        [PoolCategories.Contracts] = ["public api", "breaking change", "contract", "backwards compat", "backward compat", "wire format", "public interface"],
        [PoolCategories.Paid] = ["billing", "paid ", "subscription", "pricing", "invoice", "per month", "cost money", "credit card", "payment"],
        [PoolCategories.Settings] = ["setting", "configuration", "feature flag", "environment variable", "permission", "access rights"],
        [PoolCategories.Rules] = ["project rules", "agent rules", "coding rules", "the rules", "policy", "gate limit", "risk path", "writable path"],
    };

    /// <summary>The first <c>alwaysAsk</c> category whose words appear in the text, or null. A cheap check before any model is asked.</summary>
    public static string? MatchedCategory(string text, IReadOnlyList<string> alwaysAsk)
    {
        foreach (var category in alwaysAsk)
        {
            if (Keywords.TryGetValue(category, out var words) && words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                return category;
            }
        }

        return null;
    }

    /// <summary>A reason to park the task instead of using the master's answer, or null when the answer may be used.</summary>
    public static string? ParkReason(MasterAnswer answer, IReadOnlyList<string> alwaysAsk)
    {
        if (!answer.Decide)
        {
            return $"The pool master did not decide: {answer.Reason}";
        }

        if (alwaysAsk.Contains(answer.Category, StringComparer.OrdinalIgnoreCase))
        {
            return $"The question is about {answer.Category}, which is always left to a person.";
        }

        if (!answer.Reversible)
        {
            return "The pool master called the answer irreversible, so a person decides.";
        }

        if (string.IsNullOrWhiteSpace(answer.Answer) && answer.Option is null)
        {
            return "The pool master gave no answer.";
        }

        return null;
    }

    /// <summary>The text written into the task: <c>Answer: option N (pool master)</c> with the reason.</summary>
    public static string AnswerLine(MasterAnswer answer, string reason)
    {
        var head = answer.Option is { } n ? $"Answer: option {n} (pool master)" : "Answer: (pool master)";
        var text = new StringBuilder(head);
        if (!string.IsNullOrWhiteSpace(answer.Answer))
        {
            text.Append(' ').Append(answer.Answer.Trim().ReplaceLineEndings(" "));
        }

        text.Append("\nReason: ").Append(reason.Trim().ReplaceLineEndings(" "));
        return text.ToString();
    }

    /// <summary>Removes the answer blocks the master wrote, so a person's undo makes the task a question again.</summary>
    public static string RetractAnswers(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("Answer:", StringComparison.Ordinal) || !lines[i].Contains("(pool master)", StringComparison.Ordinal))
            {
                continue;
            }

            var end = i + 1 < lines.Count && lines[i + 1].StartsWith("Reason:", StringComparison.Ordinal) ? i + 1 : i;
            lines.RemoveRange(i, end - i + 1);
            i--;
        }

        return string.Join('\n', lines).TrimEnd();
    }
}

/// <summary>The master as a Claude Code run (<c>claude -p</c>) with read-only tools.</summary>
public sealed class ClaudePoolMaster(IAgentRunner runner, PoolMasterConfig config, string workingDirectory, string runFolder, string rules) : IPoolMaster
{
    private const string AnswerSchema = """
        {"type":"object","additionalProperties":false,
         "required":["decide","option","answer","reason","category","reversible"],
         "properties":{
           "decide":{"type":"boolean","description":"false when a person must answer"},
           "option":{"type":["integer","null"],"description":"number of the chosen option, when the question lists options"},
           "answer":{"type":"string","description":"the answer in one or two sentences"},
           "reason":{"type":"string","description":"why this answer, in one or two sentences"},
           "category":{"type":"string","enum":["secrets","stored-data","contracts","paid","settings","rules","other"]},
           "reversible":{"type":"boolean","description":"true when the answer can be changed later without harm"}}}
        """;

    private const string FailureSchema = """
        {"type":"object","additionalProperties":false,
         "required":["kind","reason","notes","bugTitle"],
         "properties":{
           "kind":{"type":"string","enum":["infrastructure","task","autopilot-defect"]},
           "reason":{"type":"string"},
           "notes":{"type":"string","description":"for kind task: what the next attempt should do differently"},
           "bugTitle":{"type":["string","null"],"description":"for kind autopilot-defect: a short title for the bug task"}}}
        """;

    private const string AnswerPrompt = """
        You are the pool master of Autopilot. Nobody is watching. A task waits on a question. Choose the answer
        that is safest and easiest to undo, using the project rules and the task text. If the question lists
        options and a recommendation, prefer the recommendation unless it breaks a rule.
        Set decide to false when you cannot answer from what you were given. Classify the question into one
        category; use "other" when none fits. Set reversible to false when the answer cannot be undone cheaply.
        Never invent facts about secrets, money, stored data or public contracts.
        """;

    private const string FailurePrompt = """
        You are the pool master of Autopilot. A task run failed. Classify the failure:
        - infrastructure: no network, a missing tool such as az or git, a usage or subscription limit, a full disk.
        - task: the agent could not do this task (tests fail, wrong approach, gate rejected it). Give notes for a second attempt.
        - autopilot-defect: Autopilot itself misbehaved (a crash with a stack trace in Autopilot code, a broken state, bad output format). Give a short bug title.
        Base the answer on the log excerpt. When unsure between task and autopilot-defect, answer task.
        """;

    public async Task<MasterAnswer> DecideAsync(MasterQuestion question, CancellationToken ct)
    {
        var prompt = new StringBuilder()
            .AppendLine("Task " + question.WorkItemId + ": " + question.Title)
            .AppendLine()
            .AppendLine("Task text:").AppendLine(question.TaskText)
            .AppendLine()
            .AppendLine("Question:").AppendLine(question.Question)
            .AppendLine()
            .AppendLine("Project rules:").AppendLine(string.IsNullOrWhiteSpace(rules) ? "(none given)" : rules)
            .ToString();
        var result = await RunAsync(AnswerPrompt, prompt, AnswerSchema, "answer-" + question.WorkItemId, ct).ConfigureAwait(false);
        if (result is not { } json)
        {
            return new MasterAnswer(false, null, string.Empty, "The master run failed or gave no structured answer.", PoolCategories.Other, false);
        }

        return new MasterAnswer(
            Bool(json, "decide"),
            json.TryGetProperty("option", out var option) && option.ValueKind == JsonValueKind.Number ? option.GetInt32() : null,
            Text(json, "answer"),
            Text(json, "reason"),
            Text(json, "category") is { Length: > 0 } category ? category : PoolCategories.Other,
            Bool(json, "reversible"));
    }

    public async Task<FailureVerdict> ClassifyFailureAsync(FailureInfo failure, CancellationToken ct)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Task {failure.WorkItemId}: {failure.Title}")
            .AppendLine($"Outcome: {failure.Outcome}")
            .AppendLine("Message: " + failure.Message)
            .AppendLine()
            .AppendLine("Log excerpt:").AppendLine(failure.LogExcerpt ?? "(none)")
            .ToString();
        var result = await RunAsync(FailurePrompt, prompt, FailureSchema, "failure-" + failure.WorkItemId, ct).ConfigureAwait(false);
        if (result is not { } json)
        {
            return new FailureVerdict(FailureKind.Task, string.Empty, "The master run failed, so the failure is treated as the task's own.", null);
        }

        var kind = Text(json, "kind") switch
        {
            "infrastructure" => FailureKind.Infrastructure,
            "autopilot-defect" => FailureKind.AutopilotDefect,
            _ => FailureKind.Task,
        };
        return new FailureVerdict(kind, Text(json, "notes"), Text(json, "reason"), json.TryGetProperty("bugTitle", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null);
    }

    private async Task<JsonElement?> RunAsync(string system, string prompt, string schema, string name, CancellationToken ct)
    {
        var folder = Path.Combine(runFolder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-{name}");
        Directory.CreateDirectory(folder);
        var role = new RoleConfig { Model = config.Model, MaxToolCalls = 12, AllowedTools = ["Read", "Grep", "Glob"] };
        var run = await runner.RunAsync(new AgentRunRequest(AgentRole.Triage, workingDirectory, system, prompt, role, [], schema, folder), ct).ConfigureAwait(false);
        return run.Succeeded ? run.StructuredOutput : null;
    }

    private static string Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
