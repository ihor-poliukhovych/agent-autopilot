using DevAutopilot.Core.Config;

namespace DevAutopilot.Diagnostics.Tests;

public sealed class JsonlDiagnosticsSourceTests
{
    [Fact]
    public async Task Reads_good_lines_and_skips_bad_ones()
    {
        var source = new JsonlDiagnosticsSource(Fixtures.Path("app.jsonl"));

        var read = await source.ReadAllAsync(CancellationToken.None);

        // Not JSON, no timestamp, and a JSON array are skipped; the blank line is not counted.
        Assert.Equal(9, read.Events.Count);
        Assert.Equal(3, read.SkippedLines);

        var streamed = new List<Core.DiagnosticEvent>();
        await foreach (var item in source.ReadAsync(CancellationToken.None))
        {
            streamed.Add(item);
        }

        Assert.Equal(read.Events, streamed);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_known_fields_match_in_any_case()
    {
        Assert.True(JsonlDiagnosticsSource.TryParseLine(
            """{"Timestamp":"2026-11-20T10:00:00+01:00","LEVEL":"Warning","message":"m","traceId":"x","nested":{"a":[1]},"Route":"GET /x","statusCode":404,"durationMs":12.5}""",
            out var item));

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), item.Timestamp.ToUniversalTime());
        Assert.Equal("Warning", item.Level);
        Assert.Equal("GET /x", item.Route);
        Assert.Equal(404, item.StatusCode);
        Assert.Equal(12.5, item.DurationMs);
        Assert.Null(item.Exception);
    }

    [Fact]
    public void Numbers_as_strings_and_exception_objects_are_tolerated()
    {
        Assert.True(JsonlDiagnosticsSource.TryParseLine(
            """{"timestamp":"2026-11-20T10:00:00Z","statusCode":"503","durationMs":"7","exception":{"type":"X"}}""", out var item));

        Assert.Equal(503, item.StatusCode);
        Assert.Equal(7, item.DurationMs);
        Assert.Equal("""{"type":"X"}""", item.Exception);
        Assert.Equal("Information", item.Level);
        Assert.Equal(string.Empty, item.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"level":"Error"}""")]
    [InlineData("""{"timestamp":"yesterday"}""")]
    [InlineData("""{"timestamp":12345}""")]
    [InlineData("""{"timestamp":"2026-11-20T10:00:00Z",""")]
    public void Bad_lines_are_rejected(string line) =>
        Assert.False(JsonlDiagnosticsSource.TryParseLine(line, out _));

    [Fact]
    public void Out_of_range_numbers_become_null()
    {
        Assert.True(JsonlDiagnosticsSource.TryParseLine(
            """{"timestamp":"2026-11-20T10:00:00Z","statusCode":99999,"durationMs":-1}""", out var item));
        Assert.Null(item.StatusCode);
        Assert.Null(item.DurationMs);
    }

    [Fact]
    public async Task Missing_file_throws_file_not_found()
    {
        using var folder = new TempFolder();
        var source = new JsonlDiagnosticsSource(folder.Combine("missing.jsonl"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => source.ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public void Factory_returns_null_for_none_and_a_source_for_jsonl()
    {
        using var folder = new TempFolder();
        Assert.Null(DiagnosticsSourceFactory.Create(folder.Path, new ModuleConfig { Type = "none" }));

        var source = DiagnosticsSourceFactory.Create(folder.Path, new ModuleConfig
        {
            Type = "JSONL",
            Settings = new Dictionary<string, string> { ["Path"] = "logs/app.jsonl" },
        });
        Assert.Equal(folder.Combine("logs", "app.jsonl"), source!.FilePath);
    }

    [Fact]
    public void Factory_rejects_unknown_types_and_a_missing_path()
    {
        using var folder = new TempFolder();
        Assert.Throws<DiagnosticsConfigException>(() => DiagnosticsSourceFactory.Create(folder.Path, new ModuleConfig { Type = "seq" }));
        var ex = Assert.Throws<DiagnosticsConfigException>(() => DiagnosticsSourceFactory.Create(folder.Path, new ModuleConfig { Type = "jsonl" }));
        Assert.Contains("settings.path", ex.Message);
    }
}

public sealed class DiagnosticsPathTests
{
    [Theory]
    [InlineData("telemetry/app.jsonl")]
    [InlineData("telemetry\\app.jsonl")]
    [InlineData("./telemetry/../telemetry/app.jsonl")]
    public void Relative_paths_inside_the_root_are_accepted(string path)
    {
        using var folder = new TempFolder();
        Assert.Equal(folder.Combine("telemetry", "app.jsonl"), DiagnosticsPath.Resolve(folder.Path, path));
    }

    [Fact]
    public void Absolute_path_inside_the_root_is_accepted()
    {
        using var folder = new TempFolder();
        var inside = folder.Combine("logs", "app.jsonl");
        Assert.Equal(inside, DiagnosticsPath.Resolve(folder.Path, inside));
    }

    [Fact]
    public void Absolute_path_outside_the_root_is_rejected()
    {
        using var folder = new TempFolder();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.jsonl");
        var ex = Assert.Throws<DiagnosticsConfigException>(() => DiagnosticsPath.Resolve(folder.Path, outside));
        Assert.Contains("outside the project root", ex.Message);
    }

    [Theory]
    [InlineData("../secrets.jsonl")]
    [InlineData("logs/../../secrets.jsonl")]
    [InlineData(".")]
    [InlineData("//server/share/app.jsonl")]
    [InlineData("\\\\server\\share\\app.jsonl")]
    [InlineData("app.jsonl:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void Paths_that_leave_the_root_or_are_odd_are_rejected(string path)
    {
        using var folder = new TempFolder();
        Assert.Throws<DiagnosticsConfigException>(() => DiagnosticsPath.Resolve(folder.Path, path));
    }

    [Fact]
    public void A_sibling_folder_with_the_same_prefix_is_outside()
    {
        using var folder = new TempFolder();
        var root = folder.Combine("app");
        Directory.CreateDirectory(root);
        Assert.Throws<DiagnosticsConfigException>(() => DiagnosticsPath.Resolve(root, folder.Combine("app2", "log.jsonl")));
    }
}
