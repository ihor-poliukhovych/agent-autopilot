using System.CommandLine;
using System.Text.Json;
using Autopilot.AzureDevOps;
using Autopilot.Core.Config;

namespace Autopilot.Cli.Commands;

/// <summary>
/// <c>ado preview</c> and <c>ado check</c>: what the configured Azure DevOps query would read, and the
/// connection checks. Both only call the services of Autopilot.AzureDevOps and print.
/// </summary>
public static class AzureDevOpsCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Command Create(ProjectOptions options, CliContext context)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON." };

        var query = new Option<string?>("--query") { Description = "A WIQL query to try instead of the configured one." };
        var skip = new Option<int>("--skip") { Description = "Items to skip (paging).", DefaultValueFactory = _ => 0 };
        var take = new Option<int>("--take") { Description = $"Items to show, at most {WorkItemPreviewRequest.MaxTake}.", DefaultValueFactory = _ => WorkItemPreviewRequest.DefaultTake };
        var preview = new Command("preview", "Show the work items the query would read, without changing anything.") { query, skip, take, json };
        preview.SetAction((parse, ct) => RunAsync(options, context, parse, requireRepository: false, async (module, output) =>
        {
            try
            {
                var page = await module.Reader.PreviewAsync(new WorkItemPreviewRequest(parse.GetValue(query), parse.GetValue(skip), parse.GetValue(take)), ct).ConfigureAwait(false);
                if (parse.GetValue(json))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(page, Json)).ConfigureAwait(false);
                    return ExitCodes.Ok;
                }

                foreach (var item in page.Items)
                {
                    await output.WriteLineAsync($"{item.Id,7}  {item.Type,-12} {item.State,-10} {item.Title}").ConfigureAwait(false);
                }

                var shown = page.Items.Count == 0 ? "no items" : $"items {page.Skip + 1}-{page.Skip + page.Items.Count}";
                var more = page.HasMore ? $"; next page: --skip {page.Skip + page.Take}" : string.Empty;
                var cap = page.Truncated ? $" (the query matches more than {WorkItemPreviewRequest.MaxTotal}; only the first {WorkItemPreviewRequest.MaxTotal} are counted)" : string.Empty;
                await output.WriteLineAsync($"{shown} of {page.Total}{cap}{more}").ConfigureAwait(false);
                return ExitCodes.Ok;
            }
            catch (Exception ex) when (ex is AzureDevOpsException or AzureDevOpsAuthException or ArgumentException)
            {
                await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Failure;
            }
        }, ct));

        var check = new Command("check", "Check the Azure DevOps connection: sign-in, query, work item access, branch rights, branch policy.") { json };
        check.SetAction((parse, ct) => RunAsync(options, context, parse, requireRepository: false, async (module, output) =>
        {
            var results = await module.Checks.CheckAllAsync(ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(results, Json)).ConfigureAwait(false);
            }
            else
            {
                foreach (var result in results)
                {
                    await output.WriteLineAsync($"{result.Outcome.ToString().ToUpperInvariant(),-7} {result.Title}: {result.Message}").ConfigureAwait(false);
                    if (result.Fix is not null)
                    {
                        await output.WriteLineAsync($"        Fix: {result.Fix}").ConfigureAwait(false);
                    }
                }
            }

            return results.Any(r => r.Outcome == CheckOutcome.Fail) ? ExitCodes.Failure : ExitCodes.Ok;
        }, ct));

        return new Command("ado", "Azure DevOps: preview the query, check the connection, import work items as local tasks.") { preview, check, AzureDevOpsImportCommand.Create(options, context) };
    }

    internal static async Task<int> RunAsync(
        ProjectOptions options, CliContext context, ParseResult parse, bool requireRepository, Func<AzureDevOpsModule, TextWriter, Task<int>> action, CancellationToken ct)
    {
        _ = ct;
        var target = options.Resolve(parse);
        AutopilotConfig config;
        try
        {
            config = ConfigLoader.Load(target.RepoRoot, target.DataFolder);
        }
        catch (ConfigException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // Both module blocks use the same settings; the publisher's wins when both name Azure DevOps.
        var module = AzureDevOpsModule.IsSelected(config.Modules.Publisher) ? config.Modules.Publisher
            : AzureDevOpsModule.IsSelected(config.Modules.WorkItems) ? config.Modules.WorkItems
            : null;
        if (module is null)
        {
            await context.Error.WriteLineAsync("This project does not use Azure DevOps. Set modules.workItems.type to \"azure-devops\" in the project settings.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        try
        {
            using var ado = AzureDevOpsModule.Create(module, target.RepoRoot, config.BaseBranch, config.BranchPrefix, requireRepository);
            return await action(ado, context.Out).ConfigureAwait(false);
        }
        catch (AzureDevOpsSettingsException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }
}
