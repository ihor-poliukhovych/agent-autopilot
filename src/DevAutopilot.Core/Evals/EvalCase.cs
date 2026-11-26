using System.Text.Json;
using DevAutopilot.Core.Local;

namespace DevAutopilot.Core.Evals;

/// <summary>How one eval run ended, in the words of <c>docs/EVALS.md</c>.</summary>
public enum EvalOutcome
{
    /// <summary>The gate passed and a draft was published.</summary>
    Published,

    /// <summary>The gate (or the harness) rejected the change.</summary>
    Rejected,

    /// <summary>Triage sent the item to a person; nothing was implemented.</summary>
    NeedsHuman,

    /// <summary>Something broke: triage gave no answer, an agent run failed, publishing failed. Never an expected outcome.</summary>
    Failed,
}

/// <summary>Expected result. A null <see cref="Triage"/> means any triage label is fine.</summary>
public sealed record EvalExpectation(TriageLabel? Triage, IReadOnlyList<EvalOutcome> Outcomes);

/// <summary>One case from <c>.autopilot/evals/&lt;id&gt;/case.json</c>.</summary>
public sealed record EvalCase(
    string Id,
    string WorkItem,
    string Description,
    EvalExpectation Expect,
    /// <summary>Full path of the case folder.</summary>
    string Folder)
{
    public string HiddenFolder => Path.Combine(Folder, EvalCaseLoader.HiddenFolderName);
}

/// <summary>Every case found, the problems with the broken ones, and the default number of runs.</summary>
public sealed record EvalCaseSet(IReadOnlyList<EvalCase> Cases, IReadOnlyList<EvalCaseProblem> Problems, int DefaultRuns);

/// <summary>A case that cannot run, with the reason in plain English.</summary>
public sealed record EvalCaseProblem(string CaseId, string Message);

/// <summary>Reads and checks the eval cases of a project.</summary>
public static class EvalCaseLoader
{
    public const string CaseFileName = "case.json";
    public const string HiddenFolderName = "hidden";

    /// <summary>Optional settings for the whole case set, for now only <c>{ "runs": 3 }</c>.</summary>
    public const string SetFileName = "evals.json";

    public const int DefaultRuns = 3;
    public const int MaxRuns = 100;

    public static string EvalsFolder(string projectRoot) => Path.Combine(ProjectPaths.AutopilotFolder(projectRoot), "evals");

    public static string OutcomeName(EvalOutcome outcome) => outcome switch
    {
        EvalOutcome.Published => "published",
        EvalOutcome.Rejected => "rejected",
        EvalOutcome.NeedsHuman => "needs-human",
        _ => "failed",
    };

    public static async Task<EvalCaseSet> LoadAsync(string projectRoot, IWorkItemSource workItems, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        var folder = EvalsFolder(projectRoot);
        var cases = new List<EvalCase>();
        var problems = new List<EvalCaseProblem>();
        if (!Directory.Exists(folder))
        {
            return new EvalCaseSet(cases, problems, DefaultRuns);
        }

        var runs = ReadDefaultRuns(folder, problems);
        foreach (var caseFolder in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(caseFolder);
            var file = Path.Combine(caseFolder, CaseFileName);
            if (!File.Exists(file))
            {
                problems.Add(new(name, $"The folder has no {CaseFileName}."));
                continue;
            }

            var (evalCase, caseProblems) = await ParseAsync(name, caseFolder, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), workItems, ct).ConfigureAwait(false);
            problems.AddRange(caseProblems.Select(p => new EvalCaseProblem(name, p)));
            if (evalCase is not null && caseProblems.Count == 0)
            {
                cases.Add(evalCase);
            }
        }

        return new EvalCaseSet(cases, problems, runs);
    }

    /// <summary>Parses and checks one case.json. Returns the case (null when unreadable) and every problem found.</summary>
    public static async Task<(EvalCase? Case, IReadOnlyList<string> Problems)> ParseAsync(
        string folderName, string caseFolder, string json, IWorkItemSource workItems, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        var problems = new List<string>();
        if (!ProjectPaths.IsValidId(folderName))
        {
            problems.Add($"The folder name \"{folderName}\" is not a valid case id: use only letters, digits, '-' and '_'.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            problems.Add($"{CaseFileName} is not valid JSON: {ex.Message}");
            return (null, problems);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{CaseFileName} must hold one JSON object.");
                return (null, problems);
            }

            var id = GetString(root, "id") ?? folderName;
            if (!string.Equals(id, folderName, StringComparison.Ordinal))
            {
                problems.Add($"\"id\" is \"{id}\" but the folder is \"{folderName}\". They must be the same.");
            }

            var workItem = GetString(root, "workItem");
            if (string.IsNullOrWhiteSpace(workItem))
            {
                problems.Add("\"workItem\" is missing. Set it to the id of a task in .autopilot/tasks/.");
            }
            else if (!ProjectPaths.IsValidId(workItem))
            {
                problems.Add(ProjectPaths.InvalidIdMessage(workItem));
            }
            else if (await workItems.GetAsync(workItem, ct).ConfigureAwait(false) is null)
            {
                problems.Add($"Work item \"{workItem}\" is unknown: there is no such task in .autopilot/tasks/.");
            }

            var description = GetString(root, "description") ?? string.Empty;
            var expect = ParseExpect(root, problems);
            if (expect is not null && expect.Outcomes.Contains(EvalOutcome.Published))
            {
                var hidden = Path.Combine(caseFolder, HiddenFolderName);
                if (!Directory.Exists(hidden) || !Directory.EnumerateFiles(hidden, "*", SearchOption.AllDirectories).Any())
                {
                    problems.Add($"\"published\" is an expected outcome, so the case needs a {HiddenFolderName}/ folder with the hidden acceptance tests.");
                }
            }

            var evalCase = expect is null || string.IsNullOrWhiteSpace(workItem)
                ? null
                : new EvalCase(id, workItem, description, expect, Path.GetFullPath(caseFolder));
            return (evalCase, problems);
        }
    }

    private static EvalExpectation? ParseExpect(JsonElement root, List<string> problems)
    {
        if (!TryGetProperty(root, "expect", out var expect) || expect.ValueKind != JsonValueKind.Object)
        {
            problems.Add("\"expect\" is missing. Add \"expect\": { \"triage\": \"agent-ready\", \"outcome\": [\"published\"] }.");
            return null;
        }

        // "triage" is optional: leave it out when more than one triage label is right and
        // only the outcome matters (for example a trap task that may be refused or rejected).
        var triageGiven = TryGetProperty(expect, "triage", out _);
        TriageLabel? label = GetString(expect, "triage") switch
        {
            "agent-ready" => TriageLabel.AgentReady,
            "needs-human" => TriageLabel.NeedsHuman,
            _ => null,
        };
        if (triageGiven && label is null)
        {
            problems.Add($"\"expect.triage\" is \"{GetString(expect, "triage")}\". Use \"agent-ready\" or \"needs-human\".");
        }

        var outcomes = new List<EvalOutcome>();
        var values = new List<string?>();
        if (TryGetProperty(expect, "outcome", out var outcome))
        {
            if (outcome.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(outcome.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
            }
            else if (outcome.ValueKind == JsonValueKind.String)
            {
                values.Add(outcome.GetString());
            }
        }

        if (values.Count == 0)
        {
            problems.Add("\"expect.outcome\" is missing or empty. List one or more of \"published\", \"rejected\", \"needs-human\".");
        }

        foreach (var value in values)
        {
            EvalOutcome? parsed = value switch
            {
                "published" => EvalOutcome.Published,
                "rejected" => EvalOutcome.Rejected,
                "needs-human" => EvalOutcome.NeedsHuman,
                _ => null,
            };
            if (parsed is null)
            {
                problems.Add($"\"expect.outcome\" has \"{value}\". Use \"published\", \"rejected\" or \"needs-human\".");
            }
            else if (!outcomes.Contains(parsed.Value))
            {
                outcomes.Add(parsed.Value);
            }
        }

        if (label == TriageLabel.NeedsHuman && outcomes.Any(o => o != EvalOutcome.NeedsHuman))
        {
            problems.Add("\"expect.triage\" is \"needs-human\", so nothing is implemented and the only possible outcome is \"needs-human\".");
        }

        if (label == TriageLabel.AgentReady && outcomes.Contains(EvalOutcome.NeedsHuman))
        {
            problems.Add("\"expect.triage\" is \"agent-ready\", so the outcome cannot be \"needs-human\".");
        }

        return (triageGiven && label is null) || outcomes.Count == 0 ? null : new EvalExpectation(label, outcomes);
    }

    private static int ReadDefaultRuns(string folder, List<EvalCaseProblem> problems)
    {
        var file = Path.Combine(folder, SetFileName);
        if (!File.Exists(file))
        {
            return DefaultRuns;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind == JsonValueKind.Object && TryGetProperty(document.RootElement, "runs", out var runs))
            {
                if (runs.ValueKind == JsonValueKind.Number && runs.TryGetInt32(out var value) && value is > 0 and <= MaxRuns)
                {
                    return value;
                }

                problems.Add(new(SetFileName, $"\"runs\" must be a whole number from 1 to {MaxRuns}; using {DefaultRuns}."));
            }
        }
        catch (JsonException ex)
        {
            problems.Add(new(SetFileName, $"{SetFileName} is not valid JSON ({ex.Message}); using {DefaultRuns} runs."));
        }

        return DefaultRuns;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
