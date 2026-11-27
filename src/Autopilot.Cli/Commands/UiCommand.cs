using System.CommandLine;
using Autopilot.AzureDevOps;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Tasks;
using Autopilot.Web;

namespace Autopilot.Cli.Commands;

/// <summary><c>autopilot ui [--port N]</c>: the local web UI on 127.0.0.1.</summary>
public static class UiCommand
{
    public static Command Create(ProjectOptions projectOption, CliContext context)
    {
        var port = new Option<int>("--port") { Description = "Port on 127.0.0.1. 0 picks a free one.", DefaultValueFactory = _ => 5178 };
        var open = new Option<bool>("--open") { Description = "Open the page in the default browser." };
        var command = new Command("ui", "Start the local web UI (127.0.0.1 only). Open the printed link once: it signs the browser in.") { port, open };
        command.SetAction(async (parse, ct) =>
        {
            var number = parse.GetValue(port);
            if (number is < 0 or > 65535)
            {
                throw new UsageException("--port must be between 0 and 65535.");
            }

            var backend = new UiBackend(context);
            var (file, arguments) = ProgramLocation.Current(context);
            var options = new WebUiOptions
            {
                Workspace = projectOption.OpenWorkspace(parse),
                Port = number,
                Checker = backend,
                Sources = backend,
                AzureDevOps = backend,
                Program = new ProgramInfo(file, arguments),
                Launcher = context.JobLauncher ?? new Autopilot.Core.Jobs.DetachedProcessLauncher(),
                Killer = context.ProcessKiller ?? new Autopilot.Core.Jobs.ProcessTreeKiller(),
                Scheduler = context.Scheduler,
                IsProcessAlive = context.IsProcessAlive,
            };
            var openBrowser = parse.GetValue(open);
            await WebUi.RunAsync(options, url =>
            {
                context.Out.WriteLine($"Autopilot UI is running. Open this link once to sign in (it works one time only):{Environment.NewLine}  {url}");
                context.Out.WriteLine($"Workspace: {options.Workspace.Root}. Press Ctrl+C to stop.");
                if (openBrowser)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        context.Out.WriteLine("The browser could not be opened; open the link yourself.");
                    }
                }
            }, ct).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }
}

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
