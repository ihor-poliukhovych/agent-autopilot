using Autopilot.Cli.Commands;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Process;

namespace Autopilot.Cli.Tests;

public sealed class CliTests
{
    private sealed class PassingFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 1, 0, string.Empty), evidence));
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(CliContext context, params string[] args)
    {
        var exit = await CliApp.RunAsync(args, context);
        return (exit, context.Out.ToString()!, context.Error.ToString()!);
    }

    private static CliContext NewContext() => new(TextReader.Null, new StringWriter(), new StringWriter());

    [Theory]
    [InlineData("implement")]
    [InlineData("no-such-command")]
    [InlineData("run", "--limit", "abc")]
    [InlineData("hook", "pre-tool-use")]
    public async Task Usage_errors_exit_with_2(params string[] args)
    {
        var (exit, _, error) = await RunAsync(NewContext(), args);
        Assert.Equal(2, exit);
        Assert.Contains("autopilot --help", error);
    }

    [Fact]
    public async Task Help_exits_with_0()
    {
        var (exit, output, _) = await RunAsync(NewContext(), "--help");
        Assert.Equal(0, exit);
        Assert.Contains("doctor", output);
    }

    [Fact]
    public async Task Triage_without_config_says_to_run_init()
    {
        using var temp = new TempFolder();
        var (exit, _, error) = await RunAsync(NewContext(), "triage", "--project", temp.Path);
        Assert.Equal(1, exit);
        Assert.Contains("autopilot init", error);
    }

    [Fact]
    public async Task Triage_and_implement_through_the_cli_with_a_fake_agent()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { [".gitignore"] = ".autopilot/out/\n" });
        ConfigLoader.Save(new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = "echo ok" }, WritablePaths = ["**"] }, repo.Root);
        repo.Write(".autopilot/tasks/001-hello.md", "# Say hello\nAdd hello.txt.\n");
        var runner = new FakeAgentRunner((request, _) => request.Role switch
        {
            AgentRole.Triage => FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Trivial.", acceptanceCriteria = new[] { "hello.txt exists" }, risk = "low" }),
            AgentRole.Implementer => Write(request.WorkingDirectory, FakeAgentRunner.Answer(new { summary = "Added hello.txt.", testsThatProveIt = new[] { "none" }, doubts = Array.Empty<string>() })),
            _ => FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() }),
        });
        CliContext Context() => NewContext() with
        {
            RunnerFactory = () => runner,
            FactsCollectorFactory = _ => new PassingFacts(),
            EvaluateGate = (_, _) => new GateDecision(GateOutcome.Passed, []),
            WorktreeRoot = repo.WorktreeRoot,
        };

        var triage = await RunAsync(Context(), "triage", "--project", repo.Root);
        var implement = await RunAsync(Context(), "implement", "001", "--project", repo.Root);

        Assert.Equal(0, triage.Exit);
        Assert.Contains("001  agent-ready", triage.Out);
        Assert.Equal(0, implement.Exit);
        Assert.Contains("001: PUBLISHED, waiting for a person", implement.Out);
        Assert.Contains("Branch: autopilot/001", implement.Out);

        static AgentRunResult Write(string folder, AgentRunResult result)
        {
            File.WriteAllText(Path.Combine(folder, "hello.txt"), "hello\n");
            return result;
        }
    }

    [Fact]
    public async Task Doctor_reports_each_check_and_fails_without_config()
    {
        using var temp = new TempFolder();
        var (exit, output, _) = await RunAsync(NewContext() with { DoctorChecksTools = false }, "doctor", "--skip-build", "--project", temp.Path);

        Assert.Equal(1, exit);
        Assert.Contains("SKIP  tools:", output);
        Assert.Contains("FAIL  git repository:", output);
        Assert.Contains("FAIL  config:", output);
        Assert.Contains("Fix: Run 'autopilot init'", output);
        Assert.Contains("SKIP  build command: --skip-build", output);
    }

    [Fact]
    public async Task Doctor_passes_on_a_set_up_project_with_trivial_commands()
    {
        using var repo = await TestRepo.CreateAsync();
        Assert.Equal(0, await CliApp.RunAsync(["init", "--project", repo.Root], NewContext()));
        var config = ConfigLoader.Load(repo.Root);
        ConfigLoader.Save(config with { Commands = new CommandsConfig { Build = "echo built", Test = "echo Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2" } }, repo.Root);

        var results = await DoctorCommand.RunChecksAsync(repo.Root, skipBuild: false, TextWriter.Null, CancellationToken.None, checkTools: false);

        string Status(string name) => results.Single(r => r.Name == name).Status.ToString();
        Assert.Equal("Ok", Status("git repository"));
        Assert.Equal("Ok", Status("config"));
        Assert.Equal("Ok", Status("base branch"));
        Assert.Equal("Ok", Status("writable paths"));
        Assert.Equal("Ok", Status("build command"));
        Assert.Equal("Ok", Status("test command"));
        Assert.Contains("2 tests", results.Single(r => r.Name == "test command").Detail);
        Assert.Equal("Ok", Status("work items"));
        Assert.Equal("Skip", Status("diagnostics"));
    }

    [Theory]
    [InlineData(@"C:\tools\autopilot.exe", @"C:\tools\autopilot.dll", "\"C:\\tools\\autopilot.exe\" hook pre-tool-use")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"C:\x y\autopilot.dll", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\x y\\autopilot.dll\" hook pre-tool-use")]
    [InlineData("/usr/share/dotnet/dotnet", "/opt/a/autopilot.dll", "\"/usr/share/dotnet/dotnet\" \"/opt/a/autopilot.dll\" hook pre-tool-use")]
    public void Hook_command_line_adds_the_assembly_only_for_the_dotnet_host(string processPath, string assembly, string expected)
    {
        Assert.Equal(expected, HookCommandLine.Build(processPath, assembly, _ => false));
    }

    [Fact]
    public void Hook_command_line_prefers_the_apphost_next_to_the_assembly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tools");
        var assembly = Path.Combine(folder, "autopilot.dll");
        var appHost = HookCommandLine.AppHostPath(assembly, OperatingSystem.IsWindows())!;

        var line = HookCommandLine.Build(Path.Combine(folder, "dotnet.exe"), assembly, p => p == appHost);

        Assert.Equal($"\"{appHost}\" hook pre-tool-use", line);
        Assert.Equal(Path.Combine(folder, "autopilot.exe"), HookCommandLine.AppHostPath(assembly, windows: true));
        Assert.Equal(Path.Combine(folder, "autopilot"), HookCommandLine.AppHostPath(assembly, windows: false));
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override Task WriteAsync(string? value) => throw new IOException("stdout is gone");

        public override Task WriteLineAsync(string? value) => throw new IOException("stderr is gone");
    }

    [Fact]
    public async Task Hook_path_exits_2_on_any_exception()
    {
        // Bad input makes the hook deny; writing the denial then throws. The CLI must still exit 2.
        var context = new CliContext(new StringReader("not json"), new ThrowingWriter(), new ThrowingWriter());

        var exit = await CliApp.RunAsync(["hook", "pre-tool-use", "--policy", "missing.json"], context);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Hook_path_turns_every_failure_code_into_2()
    {
        var (exit, _, _) = await RunAsync(new CliContext(new StringReader("{}"), new StringWriter(), new StringWriter()),
            "hook", "pre-tool-use", "--policy", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        var (unknownOption, _, _) = await RunAsync(NewContext(), "hook", "pre-tool-use", "--policy", "p", "--bogus");

        Assert.Equal(2, exit);
        Assert.Equal(2, unknownOption);
        Assert.True(HookCommand.IsHookInvocation(["hook", "pre-tool-use"]));
        Assert.False(HookCommand.IsHookInvocation(["triage", "hook"]));
    }
}

public sealed class ShellRunnerTests
{
    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        using var temp = new TempFolder();
        var result = await ShellRunner.RunAsync("echo hello && exit 4", temp.Path);

        Assert.Equal(4, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task Quoted_arguments_survive_the_shell()
    {
        using var temp = new TempFolder();
        var result = await ShellRunner.RunAsync("git -c \"user.name=A B\" config user.name", temp.Path);

        Assert.True(result.Succeeded, result.Output);
        Assert.Equal("A B", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Times_out_and_kills_the_process()
    {
        using var temp = new TempFolder();
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";

        var result = await ShellRunner.RunAsync(command, temp.Path, TimeSpan.FromMilliseconds(500));

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Shell_commands_start_from_the_allow_list()
    {
        using var temp = new TempFolder();
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY_TEST_X", "a-secret");
        Environment.SetEnvironmentVariable("RANDOM_SECRET_TEST_X", "b-secret");
        Environment.SetEnvironmentVariable("ANTHROPIC_PASSED_TEST_X", "c-secret");
        try
        {
            var result = await ShellRunner.RunAsync(OperatingSystem.IsWindows() ? "set" : "env", temp.Path,
                passEnvironment: ["RANDOM_SECRET_TEST_X", "ANTHROPIC_PASSED_TEST_X"]);

            Assert.True(result.Succeeded, result.Output);
            Assert.Contains("RANDOM_SECRET_TEST_X=b-secret", result.Output);
            Assert.DoesNotContain("a-secret", result.Output);
            Assert.DoesNotContain("c-secret", result.Output);
            Assert.Contains("PATH=", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY_TEST_X", null);
            Environment.SetEnvironmentVariable("RANDOM_SECRET_TEST_X", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_PASSED_TEST_X", null);
        }
    }

    [Fact]
    public async Task Timed_out_command_keeps_the_output_it_printed()
    {
        using var temp = new TempFolder();
        var command = OperatingSystem.IsWindows() ? "echo started && ping -n 30 127.0.0.1 > nul" : "echo started && sleep 30";

        var result = await ShellRunner.RunAsync(command, temp.Path, TimeSpan.FromSeconds(2));

        Assert.True(result.TimedOut);
        Assert.Contains("started", result.Output);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Wait_at_most_gives_up_on_a_task_that_never_ends()
    {
        var never = new TaskCompletionSource().Task;

        Assert.False(await ProcessRunner.WaitAtMostAsync(never, TimeSpan.FromMilliseconds(100)));
        Assert.True(await ProcessRunner.WaitAtMostAsync(Task.CompletedTask, TimeSpan.FromSeconds(5)));
        Assert.True(await ProcessRunner.WaitAtMostAsync(Task.FromException(new IOException("pipe broke")), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Kill_never_throws_for_an_exited_process()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();

        ProcessRunner.Kill(process);
    }

    [Fact]
    public async Task Missing_executable_is_a_failed_result()
    {
        using var temp = new TempFolder();
        var result = await ProcessRunner.RunAsync("definitely-not-a-real-program-xyz", [], temp.Path);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Could not start", result.StandardError);
    }
}
