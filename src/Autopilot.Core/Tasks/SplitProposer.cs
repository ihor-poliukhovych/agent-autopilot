using System.Text.Json;
using Autopilot.Core.Config;

namespace Autopilot.Core.Tasks;

public sealed record SplitProposalResult(bool Success, string? Error, IReadOnlyList<SplitProposal> Proposals)
{
    public static SplitProposalResult Fail(string error) => new(false, error, []);
}

/// <summary>
/// Asks an agent to propose child tasks for a task and parses the structured answer into
/// <see cref="SplitProposal"/> items that <see cref="TaskSplit.Split"/> takes. Never throws on bad agent output.
/// </summary>
public sealed class SplitProposer(IAgentRunner runner, string workingDirectory, string runFolder, string? model = null)
{
    public const string SystemPrompt =
        "You split a large task into small child tasks that can each be done and reviewed on their own. " +
        "Keep the order in which the children should be done. Give each child a short title, a body that says what " +
        "to do and how to know it is done, optional labels, and the path globs it is expected to change.";

    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "children": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "body": { "type": "string" },
                  "labels": { "type": "array", "items": { "type": "string" } },
                  "paths": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["title"]
              }
            }
          },
          "required": ["children"]
        }
        """;

    public async Task<SplitProposalResult> ProposeAsync(string title, string body, CancellationToken ct = default)
    {
        AgentRunResult run;
        try
        {
            var folder = Path.Combine(runFolder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-split");
            Directory.CreateDirectory(folder);
            var role = new RoleConfig { Model = model, MaxToolCalls = 12, AllowedTools = ["Read", "Grep", "Glob"] };
            var prompt = $"Task: {title}\n\n{body}\n\nPropose the child tasks.";
            run = await runner.RunAsync(
                new AgentRunRequest(AgentRole.Triage, workingDirectory, SystemPrompt, prompt, role, [], Schema, folder), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SplitProposalResult.Fail("The split agent run failed: " + ex.Message);
        }

        if (!run.Succeeded)
        {
            return SplitProposalResult.Fail("The split agent run failed: " + (string.IsNullOrWhiteSpace(run.Error) ? "no error given." : run.Error));
        }

        if (run.StructuredOutput is not { } json || json.ValueKind != JsonValueKind.Object)
        {
            return SplitProposalResult.Fail("The split agent returned no structured output.");
        }

        return Parse(json);
    }

    public static SplitProposalResult Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Array)
        {
            return SplitProposalResult.Fail("The split output has no children list.");
        }

        var proposals = new List<SplitProposal>();
        foreach (var item in children.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return SplitProposalResult.Fail("A child task in the split output is not an object.");
            }

            var childTitle = Text(item, "title").Trim();
            if (childTitle.Length == 0)
            {
                return SplitProposalResult.Fail("Every child task in a split needs a title.");
            }

            proposals.Add(new SplitProposal(childTitle, Text(item, "body"), Strings(item, "labels"), Strings(item, "paths")));
        }

        return proposals.Count == 0
            ? SplitProposalResult.Fail("The split agent proposed no child tasks.")
            : new SplitProposalResult(true, null, proposals);
    }

    private static string Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static List<string> Strings(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => e.GetString()!.Trim())
            .ToList();
    }
}
