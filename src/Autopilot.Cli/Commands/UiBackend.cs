using System.CommandLine;
using Autopilot.AzureDevOps;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Tasks;
using Autopilot.Web;

namespace Autopilot.Cli.Commands;

/// <summary>Gives the web UI the command line project's code: the doctor checks, project defaults, tracker modules.</summary>
public sealed class UiBackend(CliContext context) : ISetupChecker, IAzureDevOpsProbe, IWorkItemSourceFactory
{
    public async Task<IReadOnlyList<CheckLine>> RunAsync(SetupCheckRequest request, CancellationToken ct)
    {
        var options = new DoctorOptions
        {
            SkipBuild = request.SkipBuild,
            CheckTools = context.DoctorChecksTools,
            WorktreeRoot = context.WorktreeRoot,
            DataFolder = request.DataFolder,
        };
        var results = await DoctorCommand.RunChecksAsync(request.RepositoryFolder, options, TextWriter.Null, ct).ConfigureAwait(false);
        return [.. results.Select(r => new CheckLine(
            r.Status switch
            {
                CheckStatus.Ok => CheckLine.Ok,
                CheckStatus.Fail => CheckLine.Fail,
                CheckStatus.Warn => CheckLine.Warn,
                _ => CheckLine.Skip,
            },
            r.Name,
            r.Detail,
            r.Fix))];
    }

    public Task<AutopilotConfig> DetectDefaultsAsync(string repositoryFolder, string? baseBranch, CancellationToken ct) =>
        InitCommand.BuildDefaultsAsync(repositoryFolder, ct, string.IsNullOrWhiteSpace(baseBranch) ? null : baseBranch);

    public async Task<Autopilot.Web.WorkItemPreviewPage> PreviewAsync(ModuleConfig module, string repositoryFolder, int skip, int take, CancellationToken ct)
    {
        try
        {
            using var ado = AzureDevOpsModule.Create(module, repositoryFolder, "main", requireRepository: false);
            var page = await ado.Reader.PreviewAsync(new WorkItemPreviewRequest(null, skip, take), ct).ConfigureAwait(false);
            return new Autopilot.Web.WorkItemPreviewPage(
                [.. page.Items.Select(i => new Autopilot.Web.WorkItemPreview(i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Title, i.Type, i.State, i.AssignedTo, i.Url))],
                page.Total, page.Skip, page.Take, page.HasMore, page.Truncated);
        }
        catch (AzureDevOpsSettingsException ex)
        {
            throw new ArgumentException(string.Join(" ", ex.Problems) + AdoFix, ex);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or AzureDevOpsAuthException)
        {
            throw new ArgumentException(ex.Message + AdoFix, ex);
        }
    }

    public async Task<TrackerImportReport> ImportAsync(ModuleConfig module, string repositoryFolder, LocalTaskService tasks, int? limit, bool dryRun, CancellationToken ct)
    {
        try
        {
            using var ado = AzureDevOpsModule.Create(module, repositoryFolder, "main", requireRepository: false);
            return await new TrackerImportService(tasks).ImportAsync(ado.WorkItems, limit, dryRun, ct).ConfigureAwait(false);
        }
        catch (AzureDevOpsSettingsException ex)
        {
            throw new ArgumentException(string.Join(" ", ex.Problems) + AdoFix, ex);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or AzureDevOpsAuthException)
        {
            throw new ArgumentException(ex.Message + AdoFix, ex);
        }
    }

    private const string AdoFix = " To fix it: open the project's Settings, go to the Azure DevOps step and press Check (or run: autopilot ado check).";

    public IWorkItemSource Create(string repositoryFolder, AutopilotConfig config, string dataFolder, LocalTaskService? tasks)
    {
        try
        {
            return ProjectServices.CreateModules(repositoryFolder, config, dataFolder, tasks).WorkItems;
        }
        catch (ProjectSetupException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }
}
