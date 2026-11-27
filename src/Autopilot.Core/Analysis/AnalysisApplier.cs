using System.Text;
using Autopilot.Core.Config;
using Autopilot.Core.Workspace;

namespace Autopilot.Core.Analysis;

/// <summary>Turns an analysis into project settings, and into text that says what was chosen and why.</summary>
public static class AnalysisApplier
{
    /// <summary>
    /// The settings an analysis suggests. Context documents of low confidence are left out; everything else is
    /// used, so the result is a complete starting point that a person can still edit.
    /// </summary>
    public static AutopilotConfig ToConfig(ProjectAnalysis analysis, AutopilotConfig? start = null)
    {
        var config = start ?? new AutopilotConfig();
        config = config with
        {
            BaseBranch = analysis.BaseBranch.Value,
            Commands = new CommandsConfig
            {
                Build = analysis.Build?.Value ?? config.Commands.Build,
                Test = analysis.Test?.Value ?? config.Commands.Test,
            },
            WritablePaths = [.. analysis.WritablePaths.Select(p => p.Value)],
            RiskPaths = [.. analysis.RiskPaths.Select(p => p.Value)],
            Context = [.. analysis.Context.Where(c => c.Confidence != Confidence.Low).Select(c => c.Value)],
        };

        if (analysis.TaskSource.Value == ProjectRegistry.AzureDevOpsSource && analysis.AzureDevOps is { } ado)
        {
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["organizationUrl"] = ado.OrganizationUrl.Value,
                ["project"] = ado.Project.Value,
                ["repository"] = ado.Repository.Value,
                ["auth"] = "pat",
            };
            config = config with
            {
                Modules = config.Modules with
                {
                    WorkItems = new ModuleConfig { Type = ProjectRegistry.AzureDevOpsSource, Settings = settings },
                    Publisher = new ModuleConfig { Type = ProjectRegistry.AzureDevOpsSource, Settings = settings },
                },
            };
        }

        return config;
    }

    /// <summary>A readable report: each value, how sure the analyzer is, and the evidence.</summary>
    public static string Describe(ProjectAnalysis a)
    {
        var text = new StringBuilder();
        void Line(string label, Detected? d)
        {
            text.Append("  ").Append(label.PadRight(14));
            if (d is null)
            {
                text.AppendLine("(not detected)");
                return;
            }

            text.Append(d.Value).Append("   [").Append(d.Confidence.ToString().ToLowerInvariant()).Append("] ").AppendLine(d.Evidence);
            if (d.Alternatives is { Count: > 0 })
            {
                text.Append(new string(' ', 16)).Append("other choices: ").AppendLine(string.Join("; ", d.Alternatives));
            }
        }

        text.AppendLine($"Folder: {a.Folder}");
        Line("Git root", a.GitRoot);
        text.AppendLine($"  Remotes       {(a.Remotes.Count == 0 ? "(none)" : string.Join("; ", a.Remotes.Select(r => $"{r.Name} = {r.Url} ({r.Kind})")))}");
        Line("Base branch", a.BaseBranch);
        text.AppendLine($"  Stacks        {(a.Stacks.Count == 0 ? "(none recognised)" : string.Join("; ", a.Stacks.Select(s => $"{s.Kind} in {s.Path} ({s.Evidence})")))}");
        Line("Build", a.Build);
        Line("Test", a.Test);
        Line("Task source", a.TaskSource);
        if (a.AzureDevOps is { } ado)
        {
            Line("Organisation", ado.OrganizationUrl);
            Line("Project", ado.Project);
            Line("Repository", ado.Repository);
        }

        Line("Key", a.Key);
        Line("Name", a.DisplayName);
        Group(text, "Writable paths", a.WritablePaths);
        Group(text, "Risk paths", a.RiskPaths);
        Group(text, "Context", a.Context);
        if (a.NeedsInput.Count > 0)
        {
            text.AppendLine("Needs a person:");
            foreach (var n in a.NeedsInput)
            {
                text.AppendLine($"  - {n.Question}");
            }
        }

        foreach (var w in a.Warnings)
        {
            text.AppendLine($"Note: {w}");
        }

        return text.ToString().TrimEnd();
    }

    private static void Group(StringBuilder text, string title, IReadOnlyList<Detected> items)
    {
        text.AppendLine($"{title}:");
        if (items.Count == 0)
        {
            text.AppendLine("  (none found)");
            return;
        }

        foreach (var d in items)
        {
            text.AppendLine($"  {d.Value}   [{d.Confidence.ToString().ToLowerInvariant()}] {d.Evidence}");
        }
    }
}
