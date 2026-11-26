using System.Text.Json;
using DevAutopilot.ClaudeCode.Hooks;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Policy;

namespace DevAutopilot.ClaudeCode.Tests;

public class PreToolUseHookTests : IDisposable
{
    // Shape taken from a real PreToolUse call of Claude Code 2.1.332 (ids and paths anonymised).
    private const string ReadCall = """
        {"session_id":"00000000-0000-0000-0000-000000000000","transcript_path":"C:\\work\\t.jsonl","cwd":"C:\\work\\repo","permission_mode":"dontAsk","hook_event_name":"PreToolUse","tool_name":"Read","tool_input":{"file_path":"C:\\work\\repo\\hello.txt"},"tool_use_id":"toolu_00000000000000000000"}
        """;

    private readonly string _dir = TestData.NewTempDirectory();
    private readonly string _policyFile;
    private readonly ToolPolicy _policy;

    public PreToolUseHookTests()
    {
        _policyFile = Path.Combine(_dir, "policy.json");
        _policy = new ToolPolicy(
            AgentRole.Implementer,
            Path.Combine(_dir, "wt"),
            ["Read"],
            ["src/**"],
            [".autopilot/**"],
            [],
            5,
            Path.Combine(_dir, "count"));
        File.WriteAllText(_policyFile, PolicyJson.Serialize(_policy));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Outcome(int ExitCode, string Stdout, string Stderr);

    private async Task<Outcome> Run(string input, Func<ToolCall, ToolPolicy, int, PolicyDecision> evaluate, Func<string, int>? counter = null, string? policyFile = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await PreToolUseHook.RunAsync(policyFile ?? _policyFile, new StringReader(input), stdout, stderr, evaluate, counter ?? (_ => 1));
        return new Outcome(code, stdout.ToString(), stderr.ToString());
    }

    private string[] LogLines() => File.ReadAllLines(Path.Combine(_dir, PreToolUseHook.LogFileName));

    [Fact]
    public async Task Allow_writes_nothing_and_returns_zero()
    {
        var outcome = await Run(ReadCall, (_, _, _) => PolicyDecision.Allow());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(string.Empty, outcome.Stdout);
        Assert.Equal(string.Empty, outcome.Stderr);
    }

    [Fact]
    public async Task Evaluator_gets_the_call_the_policy_and_the_count()
    {
        ToolCall? seenCall = null;
        ToolPolicy? seenPolicy = null;
        int seenNumber = 0;
        string? counterFile = null;

        await Run(
            ReadCall,
            (c, p, n) => { seenCall = c; seenPolicy = p; seenNumber = n; return PolicyDecision.Allow(); },
            f => { counterFile = f; return 7; });

        Assert.Equal("Read", seenCall!.ToolName);
        Assert.Equal("C:\\work\\repo\\hello.txt", seenCall.ToolInput.GetProperty("file_path").GetString());
        Assert.Equal(_policy.CounterFile, counterFile);
        Assert.Equal(7, seenNumber);
        Assert.Equal(_policy.WorktreeRoot, seenPolicy!.WorktreeRoot);
        Assert.Equal(_policy.AllowedTools, seenPolicy.AllowedTools);
        Assert.Equal(AgentRole.Implementer, seenPolicy.Role);
    }

    [Fact]
    public async Task Deny_writes_the_documented_json_and_returns_two()
    {
        var outcome = await Run(ReadCall, (_, _, _) => PolicyDecision.Deny("outside writable paths"));

        Assert.Equal(PreToolUseHook.DenyExitCode, outcome.ExitCode);
        Assert.Equal(2, outcome.ExitCode);
        using var doc = JsonDocument.Parse(outcome.Stdout);
        var output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("outside writable paths", output.GetProperty("permissionDecisionReason").GetString());
        Assert.Contains("outside writable paths", outcome.Stderr);
    }

    [Fact]
    public async Task Evaluator_exception_denies()
    {
        var outcome = await Run(ReadCall, (_, _, _) => throw new InvalidOperationException("bug"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("\"deny\"", outcome.Stdout);
        Assert.Contains("bug", outcome.Stderr);
    }

    [Fact]
    public async Task Counter_exception_denies()
    {
        var outcome = await Run(ReadCall, (_, _, _) => PolicyDecision.Allow(), _ => throw new IOException("locked"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("locked", outcome.Stderr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"tool_input\":{}}")]
    [InlineData("{\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Read\"}")]
    public async Task Unreadable_input_denies(string input)
    {
        bool evaluated = false;
        var outcome = await Run(input, (_, _, _) => { evaluated = true; return PolicyDecision.Allow(); });

        Assert.Equal(2, outcome.ExitCode);
        Assert.False(evaluated);
        Assert.NotEqual(string.Empty, outcome.Stderr);
    }

    [Fact]
    public async Task Missing_policy_file_denies()
    {
        var outcome = await Run(ReadCall, (_, _, _) => PolicyDecision.Allow(), policyFile: Path.Combine(_dir, "missing.json"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("FileNotFoundException", outcome.Stderr);
    }

    [Fact]
    public async Task Broken_policy_file_denies()
    {
        File.WriteAllText(_policyFile, "{ broken");
        var outcome = await Run(ReadCall, (_, _, _) => PolicyDecision.Allow());

        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public async Task Each_decision_is_logged_without_tool_input()
    {
        await Run(ReadCall, (_, _, _) => PolicyDecision.Allow("fine"), _ => 1);
        await Run(ReadCall, (_, _, _) => PolicyDecision.Deny("limit reached"), _ => 2);

        string[] lines = LogLines();
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Read", first.RootElement.GetProperty("tool").GetString());
        Assert.Equal("allow", first.RootElement.GetProperty("decision").GetString());
        Assert.Equal("fine", first.RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("call").GetInt32());
        Assert.True(first.RootElement.TryGetProperty("time", out _));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("deny", second.RootElement.GetProperty("decision").GetString());

        Assert.All(lines, l => Assert.DoesNotContain("hello.txt", l));
    }

    [Fact]
    public void Deny_json_matches_the_documented_shape()
    {
        Assert.Equal(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"r\"}}",
            PreToolUseHook.BuildDenyJson("r"));
    }

    [Fact]
    public async Task Log_lines_keep_quotes_and_plus_readable()
    {
        await Run(ReadCall, (_, _, _) => PolicyDecision.Deny("shell command 'a+b' contains '\"'"), _ => 1);

        string line = Assert.Single(LogLines());
        Assert.Contains("a+b", line);
        Assert.DoesNotContain(@"\u0022", line);
        Assert.DoesNotContain(@"\u002B", line);
        Assert.DoesNotContain(@"\u0027", line);
    }
}

public class TextCaptureTests
{
    /// <summary>A reader that returns some text once and then never ends, like a pipe a grandchild holds open.</summary>
    private sealed class HangingReader(string first) : TextReader
    {
        private bool _sent;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
            {
                return new ValueTask<int>(new TaskCompletionSource<int>().Task);
            }

            _sent = true;
            first.AsSpan().CopyTo(buffer.Span);
            return new ValueTask<int>(first.Length);
        }
    }

    [Fact]
    public async Task Returns_what_was_read_when_the_stream_never_ends()
    {
        var capture = DevAutopilot.Core.Process.TextCapture.Start(new HangingReader("partial output"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        string text = await capture.TextAfterAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal("partial output", text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(capture.Completion.IsCompleted);
    }

    [Fact]
    public async Task Reads_to_the_end_of_a_finished_stream()
    {
        var capture = DevAutopilot.Core.Process.TextCapture.Start(new StringReader(new string('x', 10_000)));

        Assert.Equal(10_000, (await capture.TextAfterAsync(TimeSpan.FromSeconds(10))).Length);
    }
}
