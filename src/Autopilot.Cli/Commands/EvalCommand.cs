using System.CommandLine;
using System.Reflection;
using Autopilot.Core.Config;
using Autopilot.Core.Evals;
using Autopilot.Core.Local;

namespace Autopilot.Cli.Commands;

/// <summary><c>eval [--case &lt;id&gt;] [--runs N] [--keep]</c>: runs the project's eval cases (docs/EVALS.md).</summary>
public static class EvalCommand
{
    public static Command Create(Option<string> projectOption, CliContext context)
    {
        var caseOption = new Option<string?>("--case") { Description = "Run only this case (its folder name in .autopilot/evals/)." };
        var runsOption = new Option<int?>("--runs") { Description = $"Runs per case. Default: \"runs\" in .autopilot/evals/{EvalCaseLoader.SetFileName}, or {EvalCaseLoader.DefaultRuns}." };
        var keepOption = new Option<bool>("--keep") { Description = "Keep the temporary repositories instead of deleting them after each run." };
        var command = new Command(
            "eval",
            "Run the eval cases in .autopilot/evals/ several times each and write a report to .autopilot/out/evals/. Exit code 0 when the eval ran, even with failing cases; 1 when it could not run or stopped on an infrastructure error.")
        {
            caseOption,
            runsOption,
            keepOption,
        };
        command.SetAction((parse, ct) => RunAsync(
            parse.GetValue(projectOption), parse.GetValue(caseOption), parse.GetValue(runsOption), parse.GetValue(keepOption), context, ct));
        return command;
    }

    private static async Task<int> RunAsync(string? projectValue, string? caseId, int? runsValue, bool keep, CliContext context, CancellationToken ct)
    {
        if (runsValue is { } r && (r < 1 || r > EvalCaseLoader.MaxRuns))
        {
            throw new UsageException($"--runs must be from 1 to {EvalCaseLoader.MaxRuns}.");
        }

        var project = CliApp.ResolveProject(projectValue);
        AutopilotConfig config;
        try
        {
            config = ConfigLoader.Load(project);
        }
        catch (ConfigException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var configProblems = ConfigLoader.Validate(config);
        if (configProblems.Count > 0)
        {
            await context.Error.WriteLineAsync("The settings in .autopilot/autopilot.json have problems:").ConfigureAwait(false);
            foreach (var problem in configProblems)
            {
                await context.Error.WriteLineAsync("  - " + problem).ConfigureAwait(false);
            }

            return ExitCodes.Failure;
        }

        var set = await EvalCaseLoader.LoadAsync(project, new LocalWorkItemSource(project), ct).ConfigureAwait(false);
        var caseFolders = set.Cases.Select(c => c.Id).Concat(set.Problems.Select(p => p.CaseId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (caseId is not null && !caseFolders.Contains(caseId, StringComparer.OrdinalIgnoreCase))
        {
            throw new UsageException(caseFolders.Count == 0
                ? $"There is no case \"{caseId}\": .autopilot/evals/ has no cases."
                : $"There is no case \"{caseId}\". Cases: {string.Join(", ", set.Cases.Select(c => c.Id))}.");
        }

        var problems = set.Problems
            .Where(p => caseId is null || string.Equals(p.CaseId, caseId, StringComparison.OrdinalIgnoreCase) || p.CaseId == EvalCaseLoader.SetFileName)
            .ToList();
        var blocking = problems.Where(p => p.CaseId != EvalCaseLoader.SetFileName).ToList();
        foreach (var problem in problems)
        {
            await context.Error.WriteLineAsync($"{problem.CaseId}: {problem.Message}").ConfigureAwait(false);
        }

        if (blocking.Count > 0)
        {
            await context.Error.WriteLineAsync("Fix the cases above in .autopilot/evals/ (see docs/EVALS.md), or run one good case with --case <id>.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var cases = set.Cases.Where(c => caseId is null || string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cases.Count == 0)
        {
            await context.Error.WriteLineAsync("No eval cases. Add .autopilot/evals/<case-id>/case.json; docs/EVALS.md shows the format.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var runs = runsValue ?? set.DefaultRuns;
        var output = EvalReport.CreateOutputFolder(project, DateTimeOffset.Now);
        await context.Out.WriteLineAsync($"Running {cases.Count} case(s), {runs} run(s) each. Every run uses the Claude subscription's usage limits.").ConfigureAwait(false);
        var runner = new EvalRunner(new EvalRunnerOptions
        {
            ProjectRoot = project,
            Config = config,
            OutputFolder = output,
            Keep = keep,
            ToolVersion = ToolVersion(),
            Log = context.Out,
            CreatePipeline = (root, worktreeRoot, token) => ProjectServices.CreatePipelineAsync(root, context with { WorktreeRoot = worktreeRoot }, token),
        });

        var result = await runner.RunAsync(cases, runs, ct).ConfigureAwait(false);
        await EvalReport.WriteAsync(result, output, ct).ConfigureAwait(false);
        await context.Out.WriteAsync(EvalReport.ConsoleSummary(result, output)).ConfigureAwait(false);
        if (keep)
        {
            foreach (var kept in result.Cases.SelectMany(c => c.Runs).Select(r => r.KeptFolder).OfType<string>())
            {
                await context.Out.WriteLineAsync($"Kept: {kept}").ConfigureAwait(false);
            }
        }

        return result.StoppedEarly is null ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static string ToolVersion()
    {
        var version = typeof(EvalCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(EvalCommand).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }
}
