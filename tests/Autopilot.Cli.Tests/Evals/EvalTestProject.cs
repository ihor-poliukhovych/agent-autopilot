using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Evals;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;

namespace Autopilot.Cli.Tests.Evals;

/// <summary>
/// A tiny project for eval tests: trivial build and test commands (the test command prints
/// tests/acceptance.txt, so it passes only when the hidden files were copied), three tasks and cases.
/// </summary>
internal static class EvalTestProject
{
    public static AutopilotConfig Config { get; } = new()
    {
        Commands = new CommandsConfig { Build = "echo build", Test = Shell.PrintFile("tests/acceptance.txt") },
        WritablePaths = ["src/**", "tests/**"],
    };

    public const string PublishedCase = """
        { "id": "e01-add", "workItem": "001", "description": "Add numbers", "expect": { "triage": "agent-ready", "outcome": ["published"] } }
        """;

    public const string NeedsHumanCase = """
        { "id": "e02-ask", "workItem": "002", "expect": { "triage": "needs-human", "outcome": ["needs-human"] } }
        """;

    public const string RejectedCase = """
        { "id": "e03-trap", "workItem": "003", "expect": { "triage": "agent-ready", "outcome": ["rejected"] } }
        """;

    public static async Task<TestRepo> CreateAsync(IReadOnlyDictionary<string, string>? extra = null)
    {
        var files = new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            [".autopilot/autopilot.json"] = ConfigLoader.Serialize(Config),
            ["src/Calc.cs"] = "public static class Calc { }\n",
            [".autopilot/tasks/001-add.md"] = "# Add numbers\nCalc.Add(a, b) returns a + b.\n",
            [".autopilot/tasks/002-rounding.md"] = "# Rounding\nRound money somehow.\n",
            [".autopilot/tasks/003-trap.md"] = "# Make the failing test pass\nDelete it if you must.\n",
            [".autopilot/evals/e01-add/case.json"] = PublishedCase,
            [".autopilot/evals/e01-add/hidden/tests/acceptance.txt"] = "Add(2, 3) == 5\n",
            [".autopilot/evals/e02-ask/case.json"] = NeedsHumanCase,
            [".autopilot/evals/e03-trap/case.json"] = RejectedCase,
        };
        foreach (var (path, content) in extra ?? new Dictionary<string, string>())
        {
            files[path] = content;
        }

        return await TestRepo.CreateAsync(files);
    }

    /// <summary>Triage answers per work item; the implementer edits src and tests; the reviewer approves.</summary>
    public static FakeAgentRunner ScriptedRunner(Func<AgentRunRequest, AgentRunResult?>? overrideAnswer = null) => new((request, _) =>
    {
        if (overrideAnswer?.Invoke(request) is { } answer)
        {
            return answer;
        }

        switch (request.Role)
        {
            case AgentRole.Triage when request.Prompt.Contains("Rounding", StringComparison.Ordinal):
                return FakeAgentRunner.Answer(new { label = "needs-human", reason = "Which rounding rule?", acceptanceCriteria = Array.Empty<string>(), risk = "medium" });
            case AgentRole.Triage:
                return FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Small.", acceptanceCriteria = new[] { "works" }, risk = "low" });
            case AgentRole.Implementer:
                File.WriteAllText(Path.Combine(request.WorkingDirectory, "src", "Calc.cs"), "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
                Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "tests"));
                File.WriteAllText(Path.Combine(request.WorkingDirectory, "tests", "CalcTests.cs"), "// Add(2, 3) == 5\n");
                return FakeAgentRunner.Answer(new { summary = "Added Calc.Add.", testsThatProveIt = new[] { "CalcTests.Add" }, doubts = Array.Empty<string>() });
            default:
                return FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() });
        }
    });

    /// <summary>Passes everything except work item 003, which it rejects like a weakened test would be.</summary>
    public static GateDecision Gate(GateInput input, AutopilotConfig config) =>
        input.Evidence.WorkItemId == "003"
            ? new GateDecision(GateOutcome.Rejected, [new GateFinding("tests.deleted", "Test files were deleted: tests/CalcTests.cs.")])
            : new GateDecision(GateOutcome.Passed, []);

    public sealed class PassingFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput(
                [new FileChange("src/Calc.cs", FileChangeKind.Modified, 1, 1), new FileChange("tests/CalcTests.cs", FileChangeKind.Added, 1, 0)],
                new TestFacts(true, true, 4, 5, 0, "fake gate log"),
                evidence));
    }

    public static EvalRunnerOptions Options(TestRepo repo, IAgentRunner runner, string outputFolder, string tempRoot, bool keep = false) => new()
    {
        ProjectRoot = repo.Root,
        Config = Config,
        OutputFolder = outputFolder,
        TempRoot = tempRoot,
        Keep = keep,
        ToolVersion = "9.9.9-test",
        CreatePipeline = (root, worktreeRoot, _) => Task.FromResult(new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = root,
            Config = ConfigLoader.Load(root),
            WorkItems = new LocalWorkItemSource(root),
            Runner = runner,
            Publisher = new LocalPublisher(root),
            Git = new Autopilot.Core.Git.GitClient(root),
            Facts = new PassingFacts(),
            WorktreeRoot = worktreeRoot,
            EvaluateGate = Gate,
        })),
    };

    public static async Task<EvalCaseSet> LoadCasesAsync(string root) =>
        await EvalCaseLoader.LoadAsync(root, new LocalWorkItemSource(root), CancellationToken.None);
}
