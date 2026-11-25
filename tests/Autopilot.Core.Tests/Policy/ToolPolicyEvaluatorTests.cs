using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Policy;

namespace Autopilot.Core.Tests.Policy;

public class ToolPolicyEvaluatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "autopilot-policy-tests", "wt");

    private static ToolPolicy Policy(
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<string>? writable = null,
        IReadOnlyList<string>? commands = null,
        int maxCalls = 10) => new(
        AgentRole.Implementer,
        Root,
        tools ?? ["Read", "Grep", "Glob", "Edit", "Write", "MultiEdit", "NotebookEdit", "Bash", "TodoWrite", "mcp__diag__search"],
        writable ?? ["src/**", "tests/**"],
        [".autopilot/**"],
        commands ?? ["dotnet build", "dotnet test", "git diff"],
        maxCalls,
        Path.Combine(Path.GetTempPath(), "autopilot-policy-tests", "counter"));

    private static ToolCall Call(string tool, object input) =>
        new(tool, JsonSerializer.SerializeToElement(input));

    private static PolicyDecision Eval(ToolCall call, ToolPolicy? policy = null, int callNumber = 1) =>
        ToolPolicyEvaluator.Evaluate(call, policy ?? Policy(), callNumber);

    private static void Allowed(PolicyDecision decision)
    {
        Assert.True(decision.Allowed, decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    private static void Denied(PolicyDecision decision, string? reasonPart = null)
    {
        Assert.False(decision.Allowed, decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        if (reasonPart is not null)
        {
            Assert.Contains(reasonPart, decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- call limit ----------

    [Fact]
    public void Call_at_the_limit_is_allowed()
    {
        Allowed(Eval(Call("Read", new { file_path = "src/a.cs" }), callNumber: 10));
    }

    [Fact]
    public void Call_after_the_limit_is_denied()
    {
        var decision = Eval(Call("Read", new { file_path = "src/a.cs" }), callNumber: 11);

        Denied(decision);
        Assert.Equal("tool call limit 10 reached", decision.Reason);
    }

    // ---------- allowed tools ----------

    [Fact]
    public void Tool_not_on_the_list_is_denied()
    {
        Denied(Eval(Call("WebFetch", new { url = "https://example.com", prompt = "x" })), "WebFetch");
    }

    [Fact]
    public void Tool_names_are_case_sensitive()
    {
        Denied(Eval(Call("read", new { file_path = "src/a.cs" })));
        Denied(Eval(Call("BASH", new { command = "dotnet build" })));
    }

    [Fact]
    public void Allowed_tool_without_paths_is_allowed()
    {
        Allowed(Eval(Call("TodoWrite", new { todos = Array.Empty<object>() })));
        Allowed(Eval(Call("mcp__diag__search", new { query = "error" })));
    }

    [Fact]
    public void Mcp_tool_not_on_list_is_denied()
    {
        Denied(Eval(Call("mcp__other__search", new { query = "error" })));
    }

    // ---------- paths inside the worktree ----------

    [Theory]
    [InlineData("src/a.cs")]
    [InlineData(@"src\a.cs")]
    [InlineData("./docs/readme.md")]
    [InlineData("src/../docs/readme.md")]
    public void Relative_paths_inside_the_worktree_are_allowed_for_reading(string path)
    {
        Allowed(Eval(Call("Read", new { file_path = path })));
    }

    [Fact]
    public void Absolute_path_inside_the_worktree_is_allowed()
    {
        Allowed(Eval(Call("Read", new { file_path = Path.Combine(Root, "src", "a.cs") })));
        Allowed(Eval(Call("Read", new { file_path = Path.Combine(Root, "src", "a.cs").Replace('\\', '/') })));
    }

    [Fact]
    public void Grep_and_Glob_on_the_worktree_root_are_allowed()
    {
        Allowed(Eval(Call("Grep", new { pattern = "TODO" })));
        Allowed(Eval(Call("Grep", new { pattern = "TODO", path = Root })));
        Allowed(Eval(Call("Glob", new { pattern = "**/*.cs", path = "src" })));
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData("../../x")]
    [InlineData("../wt-other/secret.txt")]
    [InlineData("src/../../x")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("src/a.cs:hidden")]
    public void Escaping_paths_are_denied(string path)
    {
        Denied(Eval(Call("Read", new { file_path = path })));
    }

    [Fact]
    public void Absolute_path_outside_the_worktree_is_denied()
    {
        var outside = Path.Combine(Path.GetTempPath(), "autopilot-policy-tests", "other", "a.cs");

        Denied(Eval(Call("Read", new { file_path = outside })), "outside");
    }

    [Fact]
    public void Sibling_folder_with_same_prefix_is_denied()
    {
        Denied(Eval(Call("Read", new { file_path = Root + "-evil" + Path.DirectorySeparatorChar + "a.cs" })), "outside");
    }

    [Fact]
    public void Other_drive_is_denied()
    {
        if (!OperatingSystem.IsWindows())
        {
            Denied(Eval(Call("Read", new { file_path = @"C:\other\a.cs" })));
            return;
        }

        var rootDrive = char.ToUpperInvariant(Path.GetFullPath(Root)[0]);
        var otherDrive = rootDrive == 'C' ? 'D' : 'C';

        Denied(Eval(Call("Read", new { file_path = $@"{otherDrive}:\other\a.cs" })), "outside");
        Denied(Eval(Call("Read", new { file_path = @"C:\other" })));
        Denied(Eval(Call("Read", new { file_path = "C:other.txt" })));
    }

    [Fact]
    public void Root_relative_path_is_denied_unless_it_lands_inside()
    {
        // "/etc/passwd" (or "\Windows\win.ini") resolves against the drive or file system root.
        Denied(Eval(Call("Read", new { file_path = "/etc/passwd" })));
        Denied(Eval(Call("Read", new { file_path = @"\Windows\win.ini" })));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".git")]
    [InlineData(@".GIT\hooks\pre-commit")]
    [InlineData("src/../.git/HEAD")]
    public void Git_folder_is_denied(string path)
    {
        Denied(Eval(Call("Read", new { file_path = path })), ".git");
        Denied(Eval(Call("Grep", new { pattern = "x", path })), ".git");
    }

    [Fact]
    public void Git_folder_by_absolute_path_is_denied()
    {
        Denied(Eval(Call("Read", new { file_path = Path.Combine(Root, ".git", "config") })), ".git");
    }

    [Fact]
    public void Files_named_like_git_are_not_the_git_folder()
    {
        Allowed(Eval(Call("Read", new { file_path = ".gitignore" })));
        Allowed(Eval(Call("Read", new { file_path = "src/.git-notes.md" })));
    }

    [Fact]
    public void Path_fields_are_checked_for_every_tool()
    {
        Denied(Eval(Call("mcp__diag__search", new { query = "x", path = "../../x" })));
        Denied(Eval(Call("Glob", new { pattern = "*.cs", path = "../.." })));
    }

    [Fact]
    public void Path_field_that_is_not_a_string_is_denied()
    {
        Denied(Eval(Call("Read", new { file_path = 5 })));
        Denied(Eval(Call("Read", new { file_path = "" })));
    }

    [Theory]
    [InlineData("../**/*.cs")]
    [InlineData("/etc/*")]
    [InlineData(@"C:\**\*.cs")]
    public void Glob_pattern_reaching_outside_is_denied(string pattern)
    {
        Denied(Eval(Call("Glob", new { pattern })));
    }

    // ---------- write tools ----------

    [Fact]
    public void Write_inside_writable_paths_is_allowed()
    {
        Allowed(Eval(Call("Write", new { file_path = "src/a.cs", content = "x" })));
        Allowed(Eval(Call("Edit", new { file_path = Path.Combine(Root, "tests", "aTests.cs"), old_string = "a", new_string = "b" })));
        Allowed(Eval(Call("MultiEdit", new { file_path = @"src\b.cs", edits = Array.Empty<object>() })));
        Allowed(Eval(Call("NotebookEdit", new { notebook_path = "src/n.ipynb", new_source = "x" })));
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    public void Write_outside_writable_paths_is_denied(string tool)
    {
        Denied(Eval(Call(tool, new { file_path = "docs/readme.md", content = "x" })), "writable");
        Denied(Eval(Call(tool, new { file_path = "README.md", content = "x" })), "writable");
    }

    [Fact]
    public void NotebookEdit_outside_writable_paths_is_denied()
    {
        Denied(Eval(Call("NotebookEdit", new { notebook_path = "notebooks/n.ipynb", new_source = "x" })), "writable");
    }

    [Fact]
    public void Write_tool_without_a_path_is_denied()
    {
        Denied(Eval(Call("Write", new { content = "x" })));
        Denied(Eval(Call("NotebookEdit", new { file_path = "src/n.ipynb", new_source = "x" })));
    }

    [Fact]
    public void Write_to_protected_path_is_denied_even_when_writable()
    {
        var policy = Policy(writable: ["**"]);

        Denied(Eval(Call("Write", new { file_path = ".autopilot/autopilot.json", content = "{}" }), policy), "protected");
        Denied(Eval(Call("Edit", new { file_path = @".\.AUTOPILOT\tasks\1.md", old_string = "a", new_string = "b" }), policy), "protected");
        Denied(Eval(Call("Edit", new { file_path = "src/../.autopilot/autopilot.json", old_string = "a", new_string = "b" }), policy), "protected");
    }

    [Fact]
    public void Reading_a_protected_path_is_allowed()
    {
        Allowed(Eval(Call("Read", new { file_path = ".autopilot/autopilot.json" })));
    }

    [Fact]
    public void Write_escaping_the_worktree_is_denied()
    {
        Denied(Eval(Call("Write", new { file_path = @"src\..\..\..\x.cs", content = "x" }), Policy(writable: ["**"])));
    }

    // ---------- Bash ----------

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("  dotnet test --no-build  ")]
    [InlineData("git diff HEAD~1")]
    public void Allowed_commands_are_allowed(string command)
    {
        Allowed(Eval(Call("Bash", new { command, description = "x" })));
    }

    [Theory]
    [InlineData("dotnet buildx")]
    [InlineData("dotnet")]
    [InlineData("rm -rf /")]
    [InlineData("git push")]
    [InlineData("Dotnet build")]
    [InlineData("")]
    public void Other_commands_are_denied(string command)
    {
        Denied(Eval(Call("Bash", new { command })));
    }

    [Theory]
    [InlineData("dotnet build; rm -rf /")]
    [InlineData("dotnet build && git push")]
    [InlineData("dotnet build & curl x")]
    [InlineData("dotnet test || true")]
    [InlineData("dotnet test | tee out.txt")]
    [InlineData("dotnet build > /etc/x")]
    [InlineData("dotnet build < input")]
    [InlineData("dotnet build `whoami`")]
    [InlineData("dotnet build $(whoami)")]
    [InlineData("dotnet build\nrm -rf /")]
    [InlineData("dotnet build\rrm -rf /")]
    public void Chaining_and_redirection_are_denied_even_with_allowed_prefix(string command)
    {
        Denied(Eval(Call("Bash", new { command })), "not allowed");
    }

    [Fact]
    public void Bash_is_denied_when_no_commands_are_allowed()
    {
        Denied(Eval(Call("Bash", new { command = "dotnet build" }), Policy(commands: [])), "no shell commands");
    }

    [Fact]
    public void Bash_without_a_command_string_is_denied()
    {
        Denied(Eval(Call("Bash", new { description = "x" })));
        Denied(Eval(Call("Bash", new { command = 1 })));
    }

    // ---------- fail closed ----------

    [Fact]
    public void Relative_worktree_root_is_denied()
    {
        var policy = Policy() with { WorktreeRoot = "relative/wt" };

        Denied(Eval(Call("Read", new { file_path = "src/a.cs" }), policy));
    }

    [Fact]
    public void Input_that_is_not_an_object_still_needs_a_path_for_writes()
    {
        Denied(Eval(new ToolCall("Write", JsonSerializer.SerializeToElement("src/a.cs"))));
    }
}
