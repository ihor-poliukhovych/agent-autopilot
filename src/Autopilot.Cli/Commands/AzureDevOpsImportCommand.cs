using System.CommandLine;
using System.Text.Json;
using Autopilot.AzureDevOps;
using Autopilot.Core.Tasks;

namespace Autopilot.Cli.Commands;

/// <summary>
/// <c>ado import</c>: runs the project's configured query (read only) and stores the work items as local tasks.
/// Nothing is written to Azure DevOps.
/// </summary>
public static class AzureDevOpsImportCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Command Create(ProjectOptions options, CliContext context)
    {
        var limit = new Option<int?>("--limit") { Description = "Import at most this many items." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Only count what would change; write nothing." };
        var json = new Option<bool>("--json") { Description = "Print JSON." };
        var command = new Command("import", "Import the work items of the configured query as local tasks (read only on the Azure DevOps side).") { limit, dryRun, json };
        command.SetAction((parse, ct) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var tasks = new LocalTaskService(workspace, project.Key);
            return AzureDevOpsCommand.RunAsync(options, context, parse, requireRepository: false, async (module, output) =>
            {
                try
                {
                    var report = await new TrackerImportService(tasks).ImportAsync(module.WorkItems, parse.GetValue(limit), parse.GetValue(dryRun), ct).ConfigureAwait(false);
                    await output.WriteLineAsync(parse.GetValue(json)
                        ? JsonSerializer.Serialize(report, Json)
                        : $"{(report.DryRun ? "Dry run, nothing written. Would import" : "Imported")}: {report.Created} new, {report.Updated} updated, {report.Unchanged} unchanged.").ConfigureAwait(false);
                    return ExitCodes.Ok;
                }
                catch (Exception ex) when (ex is AzureDevOpsException or AzureDevOpsAuthException or ArgumentException)
                {
                    await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
            }, ct);
        });
        return command;
    }
}
