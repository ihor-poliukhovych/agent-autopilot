using System.CommandLine;
using Autopilot.Core;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;

namespace Autopilot.Cli.Commands;

/// <summary><c>triage [id]</c>, <c>implement &lt;id&gt;</c> and <c>run [--limit N]</c>.</summary>
public static class PipelineCommands
{
    public static Command CreateTriage(Option<string> projectOption, CliContext context)
    {
        var id = new Argument<string?>("id") { Description = "One work item id. Default: every open item.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("triage", "Sort work items into agent-ready and needs-human.") { id };
        command.SetAction((parse, ct) => WithPipelineAsync(parse.GetValue(projectOption), context, ct, async pipeline =>
        {
            var results = await pipeline.TriageAsync(parse.GetValue(id), ct).ConfigureAwait(false);
            await PrintTriageAsync(context.Out, results).ConfigureAwait(false);
            return results.Any(r => !r.Succeeded) ? ExitCodes.Failure : ExitCodes.Ok;
        }));
        return command;
    }

    public static Command CreateImplement(Option<string> projectOption, CliContext context)
    {
        var id = new Argument<string>("id") { Description = "Work item id; triage must have marked it agent-ready." };
        var command = new Command("implement", "Implement one agent-ready item: worktree, implementer, reviewer, merge gate, draft. Exit code 0 only when a draft was published.") { id };
        command.SetAction((parse, ct) => WithPipelineAsync(parse.GetValue(projectOption), context, ct, async pipeline =>
        {
            var result = await pipeline.ImplementAsync(parse.GetRequiredValue(id), ct).ConfigureAwait(false);
            await PrintImplementAsync(context.Out, result).ConfigureAwait(false);
            return result.Outcome == ImplementOutcome.Published ? ExitCodes.Ok : ExitCodes.Failure;
        }));
        return command;
    }

    public static Command CreateRun(Option<string> projectOption, CliContext context)
    {
        var limit = new Option<int?>("--limit") { Description = "Implement at most this many items." };
        var command = new Command("run", "Triage every open item, then implement the agent-ready ones. Exit code 1 when something broke.") { limit };
        command.SetAction((parse, ct) =>
        {
            var max = parse.GetValue(limit);
            if (max is <= 0)
            {
                throw new UsageException("--limit must be greater than 0.");
            }

            return WithPipelineAsync(parse.GetValue(projectOption), context, ct, async pipeline =>
            {
                var summary = await pipeline.RunAsync(max, ct).ConfigureAwait(false);
                await PrintTriageAsync(context.Out, summary.Triage).ConfigureAwait(false);
                foreach (var result in summary.Implemented)
                {
                    await context.Out.WriteLineAsync().ConfigureAwait(false);
                    await PrintImplementAsync(context.Out, result).ConfigureAwait(false);
                }

                foreach (var skipped in summary.Skipped)
                {
                    await context.Out.WriteLineAsync($"Skipped {skipped}").ConfigureAwait(false);
                }

                var broke = summary.Triage.Any(t => !t.Succeeded) || summary.Implemented.Any(i => i.Outcome == ImplementOutcome.Failed);
                return broke ? ExitCodes.Failure : ExitCodes.Ok;
            });
        });
        return command;
    }

    private static async Task<int> WithPipelineAsync(string? projectValue, CliContext context, CancellationToken ct, Func<AutopilotPipeline, Task<int>> body)
    {
        var project = CliApp.ResolveProject(projectValue);
        AutopilotPipeline pipeline;
        try
        {
            pipeline = await ProjectServices.CreatePipelineAsync(project, context, ct).ConfigureAwait(false);
        }
        catch (ProjectSetupException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        return await body(pipeline).ConfigureAwait(false);
    }

    private static async Task PrintTriageAsync(TextWriter output, IReadOnlyList<TriageItemResult> results)
    {
        if (results.Count == 0)
        {
            await output.WriteLineAsync("No open work items. Add task files to .autopilot/tasks/.").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Triage").ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Triage is null)
            {
                await output.WriteLineAsync($"  {result.WorkItemId}  ERROR        {result.Title}").ConfigureAwait(false);
                await output.WriteLineAsync($"        {result.Error}").ConfigureAwait(false);
                continue;
            }

            var label = TriageStore.Label(result.Triage.Label);
            var risk = result.Triage.Risk.ToString().ToLowerInvariant();
            await output.WriteLineAsync($"  {result.WorkItemId}  {label,-11}  {result.Title} (risk {risk})").ConfigureAwait(false);
            await output.WriteLineAsync($"        {result.Triage.Reason}").ConfigureAwait(false);
        }

        var ready = results.Count(r => r.Triage?.Label == TriageLabel.AgentReady);
        await output.WriteLineAsync($"{ready} agent-ready, {results.Count(r => r.Triage?.Label == TriageLabel.NeedsHuman)} needs-human, {results.Count(r => r.Triage is null)} error(s). Details in .autopilot/out/triage/.").ConfigureAwait(false);
    }

    private static async Task PrintImplementAsync(TextWriter output, ImplementResult result)
    {
        var outcome = result.Outcome switch
        {
            ImplementOutcome.Published => "PUBLISHED, waiting for a person",
            ImplementOutcome.Rejected => "REJECTED",
            ImplementOutcome.NotReady => "NOT READY",
            ImplementOutcome.NotFound => "NOT FOUND",
            _ => "FAILED",
        };
        await output.WriteLineAsync($"{result.WorkItemId}: {outcome}").ConfigureAwait(false);
        await output.WriteLineAsync($"  {result.Message}").ConfigureAwait(false);
        if (result.Decision is { Findings.Count: > 0 } decision)
        {
            foreach (var finding in decision.Findings)
            {
                await output.WriteLineAsync($"  - [{finding.RuleId}] {finding.Message}{(finding.HighRisk ? " (high risk)" : string.Empty)}").ConfigureAwait(false);
            }
        }

        if (result.BranchName is not null)
        {
            await output.WriteLineAsync($"  Branch: {result.BranchName}").ConfigureAwait(false);
        }

        if (result.Location is not null)
        {
            await output.WriteLineAsync($"  Files:  {result.Location}").ConfigureAwait(false);
        }
    }
}
