namespace Autopilot.ClaudeCode.Tests;

public class ClaudeExecutableResolverTests
{
    private const string NpmDir = @"C:\Users\u\AppData\Roaming\npm";
    private const string NpmExe = NpmDir + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";

    private static Func<string, bool> Exists(params string[] files) =>
        path => files.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Windows_npm_shim_is_replaced_by_its_native_binary()
    {
        string? found = ClaudeExecutableResolver.Resolve(
            "claude", @"C:\Windows;" + NpmDir, isWindows: true, Exists(NpmDir + @"\claude.cmd", NpmDir + @"\claude.ps1", NpmExe));
        Assert.Equal(NpmExe, found);
    }

    [Fact]
    public void Windows_exe_on_path_wins_over_an_earlier_shim()
    {
        const string native = @"C:\Users\u\.local\bin\claude.exe";
        string? found = ClaudeExecutableResolver.Resolve(
            "claude", NpmDir + @";C:\Users\u\.local\bin", isWindows: true, Exists(NpmDir + @"\claude.cmd", native));
        Assert.Equal(native, found);
    }

    [Fact]
    public void Windows_shim_without_binary_is_not_used()
    {
        Assert.Null(ClaudeExecutableResolver.Resolve("claude", NpmDir, isWindows: true, Exists(NpmDir + @"\claude.cmd")));
    }

    [Fact]
    public void Windows_absolute_ps1_path_is_mapped_to_the_binary()
    {
        Assert.Equal(NpmExe, ClaudeExecutableResolver.Resolve(NpmDir + @"\claude.ps1", null, isWindows: true, Exists(NpmDir + @"\claude.ps1", NpmExe)));
    }

    [Fact]
    public void Windows_absolute_exe_path_is_used_as_is()
    {
        const string exe = @"D:\tools\claude.exe";
        Assert.Equal(exe, ClaudeExecutableResolver.Resolve(exe, null, isWindows: true, Exists(exe)));
    }

    [Fact]
    public void Windows_absolute_path_without_extension_gets_exe()
    {
        Assert.Equal(@"D:\tools\claude.exe", ClaudeExecutableResolver.Resolve(@"D:\tools\claude", null, isWindows: true, Exists(@"D:\tools\claude.exe")));
    }

    [Fact]
    public void Windows_quoted_path_entries_are_unquoted()
    {
        const string exe = @"C:\Program Files\Claude\claude.exe";
        Assert.Equal(exe, ClaudeExecutableResolver.Resolve("claude", "\"C:\\Program Files\\Claude\"", isWindows: true, Exists(exe)));
    }

    [Fact]
    public void Unix_searches_path_in_order()
    {
        string? found = ClaudeExecutableResolver.Resolve(
            "claude", "/usr/bin:/home/u/.local/bin:/opt/bin", isWindows: false, Exists("/home/u/.local/bin/claude", "/opt/bin/claude"));
        Assert.Equal("/home/u/.local/bin/claude", found);
    }

    [Fact]
    public void Unix_absolute_path()
    {
        Assert.Equal("/opt/claude", ClaudeExecutableResolver.Resolve("/opt/claude", "", isWindows: false, Exists("/opt/claude")));
        Assert.Null(ClaudeExecutableResolver.Resolve("/opt/missing", "", isWindows: false, Exists("/opt/claude")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_name_resolves_to_nothing(string name)
    {
        Assert.Null(ClaudeExecutableResolver.Resolve(name, "/usr/bin", isWindows: false, _ => true));
    }

    [Fact]
    public void Not_found_returns_null()
    {
        Assert.Null(ClaudeExecutableResolver.Resolve("claude", "/usr/bin", isWindows: false, _ => false));
    }
}
