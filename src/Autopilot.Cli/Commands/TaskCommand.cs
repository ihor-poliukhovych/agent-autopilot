using System.CommandLine;
using System.Text.Json;
using Autopilot.Core.Local;
using Autopilot.Core.Tasks;

namespace Autopilot.Cli.Commands;

/// <summary>
/// <c>autopilot task add|list|show|comment|close|reopen|label|import-files</c>: the tasks of a registered project,
/// kept in the workspace. Each command parses arguments and prints; the work is in <see cref="LocalTaskService"/>.
/// </summary>
public static class TaskCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Command Create(ProjectOptions options, CliContext context) => new("task", "Write and manage a project's tasks inside Autopilot (no tracker needed). Needs a registered project.")
    {
        CreateAdd(options, context),
        CreateList(options, context),
        CreateShow(options, context),
        CreateComment(options, context),
        CreateState(options, context, "close", "Close a task.", close: true),
        CreateState(options, context, "reopen", "Reopen a closed task.", close: false),
        CreateLabel(options, context),
        CreateImportFiles(options, context),
    };

    private static Argument<string> IdArgument() => new("id") { Description = "Task number, such as 12 or #12." };

    private static Option<bool> JsonOption() => new("--json") { Description = "Print JSON." };

    private static LocalTaskService Service(ProjectOptions options, ParseResult parse)
    {
        var (project, workspace) = options.Resolve(parse).RequireRegistered();
        return new LocalTaskService(workspace, project.Key);
    }

    private static int Number(string id) =>
        LocalTaskService.TryParseNumber(id, out var number) ? number : throw new UsageException($"\"{id}\" is not a task number. Use a number such as 12.");

    private static Task Print(CliContext context, string text) => context.Out.WriteLineAsync(text);

    private static Command CreateAdd(ProjectOptions options, CliContext context)
    {
        var title = new Argument<string>("title") { Description = "Short title." };
        var body = new Option<string?>("--body") { Description = "Description in markdown." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Read the description from a file ('-' reads standard input)." };
        var label = new Option<string[]>("--label") { Description = "A label. Repeat or separate with commas.", AllowMultipleArgumentsPerToken = true };
        var json = JsonOption();
        var command = new Command("add", "Create a task.") { title, body, bodyFile, label, json };
        command.SetAction(async (parse, ct) =>
        {
            var service = Service(options, parse);
            var text = parse.GetValue(body);
            var file = parse.GetValue(bodyFile);
            if (text is not null && file is not null)
            {
                throw new UsageException("Use either --body or --body-file, not both.");
            }

            if (file is not null)
            {
                text = file == "-" ? await context.In.ReadToEndAsync(ct).ConfigureAwait(false) : await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }

            var task = service.Add(parse.GetRequiredValue(title), text?.Trim(), parse.GetValue(label));
            await Print(context, parse.GetValue(json) ? JsonSerializer.Serialize(task, Json) : $"Created task {task.Number}: {task.Title}").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateList(ProjectOptions options, CliContext context)
    {
        var all = new Option<bool>("--all") { Description = "Include closed tasks." };
        var label = new Option<string?>("--label") { Description = "Only tasks with this label." };
        var json = JsonOption();
        var command = new Command("list", "List open tasks.") { all, label, json };
        command.SetAction(async (parse, _) =>
        {
            var tasks = Service(options, parse).List(parse.GetValue(all), parse.GetValue(label));
            if (parse.GetValue(json))
            {
                await Print(context, JsonSerializer.Serialize(tasks, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (tasks.Count == 0)
            {
                await Print(context, "No tasks. Add one with 'autopilot task add \"title\"'.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            foreach (var t in tasks)
            {
                var labels = t.Labels.Count == 0 ? string.Empty : "  [" + string.Join(", ", t.Labels) + "]";
                await Print(context, $"{t.Number,-5} {t.State,-7} {t.Title}{labels}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateShow(ProjectOptions options, CliContext context)
    {
        var id = IdArgument();
        var json = JsonOption();
        var command = new Command("show", "Show a task with its comments and history.") { id, json };
        command.SetAction(async (parse, _) =>
        {
            var number = Number(parse.GetRequiredValue(id));
            var detail = Service(options, parse).Show(number) ?? throw new UsageException($"There is no task {number}.");
            if (parse.GetValue(json))
            {
                await Print(context, JsonSerializer.Serialize(detail, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            var t = detail.Task;
            await Print(context, $"#{t.Number} {t.Title}").ConfigureAwait(false);
            await Print(context, $"State: {t.State}   Labels: {(t.Labels.Count == 0 ? "-" : string.Join(", ", t.Labels))}   Author: {t.Author}   Created: {t.CreatedAt:u}").ConfigureAwait(false);
            if (t.ExternalId is not null)
            {
                await Print(context, $"External: {t.ExternalId} {t.ExternalUrl}").ConfigureAwait(false);
            }

            if (t.Body.Length > 0)
            {
                await Print(context, string.Empty).ConfigureAwait(false);
                await Print(context, t.Body).ConfigureAwait(false);
            }

            if (detail.Comments.Count > 0)
            {
                await Print(context, string.Empty).ConfigureAwait(false);
                await Print(context, "Comments:").ConfigureAwait(false);
                foreach (var c in detail.Comments)
                {
                    await Print(context, $"  {c.CreatedAt:u} {c.Author}: {c.Body}").ConfigureAwait(false);
                }
            }

            await Print(context, string.Empty).ConfigureAwait(false);
            await Print(context, "History:").ConfigureAwait(false);
            foreach (var h in detail.History)
            {
                await Print(context, $"  {h.CreatedAt:u} [{h.Kind}] {h.Summary}").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(h.Detail))
                {
                    await Print(context, "    " + h.Detail.Replace("\n", "\n    ", StringComparison.Ordinal)).ConfigureAwait(false);
                }
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateComment(ProjectOptions options, CliContext context)
    {
        var id = IdArgument();
        var text = new Argument<string>("text") { Description = "The comment (markdown)." };
        var command = new Command("comment", "Add a comment to a task.") { id, text };
        command.SetAction(async (parse, _) =>
        {
            var number = Number(parse.GetRequiredValue(id));
            Service(options, parse).Comment(number, parse.GetRequiredValue(text));
            await Print(context, $"Commented on task {number}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateState(ProjectOptions options, CliContext context, string name, string description, bool close)
    {
        var id = IdArgument();
        var command = new Command(name, description) { id };
        command.SetAction(async (parse, _) =>
        {
            var number = Number(parse.GetRequiredValue(id));
            var service = Service(options, parse);
            var changed = close ? service.Close(number) : service.Reopen(number);
            await Print(context, changed ? $"Task {number} {(close ? "closed" : "reopened")}." : $"Task {number} was already {(close ? "closed" : "open")}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateLabel(ProjectOptions options, CliContext context)
    {
        var id = IdArgument();
        var add = new Option<string[]>("--add") { Description = "Labels to add. Repeat or separate with commas.", AllowMultipleArgumentsPerToken = true };
        var remove = new Option<string[]>("--remove") { Description = "Labels to remove.", AllowMultipleArgumentsPerToken = true };
        var json = JsonOption();
        var command = new Command("label", "Add or remove labels on a task.") { id, add, remove, json };
        command.SetAction(async (parse, _) =>
        {
            var number = Number(parse.GetRequiredValue(id));
            var toAdd = parse.GetValue(add);
            var toRemove = parse.GetValue(remove);
            if ((toAdd is null || toAdd.Length == 0) && (toRemove is null || toRemove.Length == 0))
            {
                throw new UsageException("Give --add and/or --remove.");
            }

            var labels = Service(options, parse).ChangeLabels(number, toAdd, toRemove);
            await Print(context, parse.GetValue(json) ? JsonSerializer.Serialize(labels, Json) : $"Task {number} labels: {(labels.Count == 0 ? "(none)" : string.Join(", ", labels))}").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static bool HasTaskFiles(string dataFolder) =>
        Directory.Exists(Path.Combine(dataFolder, "tasks")) && Directory.EnumerateFiles(Path.Combine(dataFolder, "tasks"), "*.md").Any();

    private static Command CreateImportFiles(ProjectOptions options, CliContext context)
    {
        var from = new Option<string?>("--from") { Description = "A repository folder whose .autopilot/tasks/ to read. Default: the task files in the project's workspace folder, else the project's repository." };
        var command = new Command("import-files", "Load task files (.autopilot/tasks/*.md) into the project's tasks. Safe to repeat: a file already imported is updated, not duplicated.") { from };
        command.SetAction(async (parse, ct) =>
        {
            var target = options.Resolve(parse);
            var (project, workspace) = target.RequireRegistered();
            // Default: the task files in the project's workspace folder when there are some, else the repository's .autopilot/tasks.
            var files = parse.GetValue(from) is { } given
                ? new LocalWorkItemSource(CliApp.ResolveProject(given))
                : new LocalWorkItemSource(target.RepoRoot, HasTaskFiles(workspace.ProjectFolder(project.Key)) ? workspace.ProjectFolder(project.Key) : null);
            var sync = new TaskSyncService(new LocalTaskService(workspace, project.Key), new FileTaskTracker(files));
            var report = await sync.ImportAsync(ct).ConfigureAwait(false);
            await Print(context, $"Imported from {files.TasksFolder}: {report.Created} new, {report.Updated} updated, {report.Unchanged} unchanged.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }
}
