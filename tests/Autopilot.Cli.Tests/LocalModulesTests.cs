using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;

namespace Autopilot.Cli.Tests;

public sealed class LocalWorkItemSourceTests
{
    [Fact]
    public void Parse_reads_id_title_labels_and_body()
    {
        var (item, closed) = LocalWorkItemSource.Parse("001-fix-404.md", """
            # Fix 404 for missing book

            Labels: bug, api
            GET /books/9 returns 500.

            It should return 404.
            """);

        Assert.False(closed);
        Assert.Equal("001", item.Id);
        Assert.Equal("Fix 404 for missing book", item.Title);
        Assert.Equal(["bug", "api"], item.Labels);
        Assert.Equal("GET /books/9 returns 500.\n\nIt should return 404.", item.Body);
    }

    [Theory]
    [InlineData("Status: done", true)]
    [InlineData("status: Closed", true)]
    [InlineData("Status: open", false)]
    public void Parse_reads_status(string statusLine, bool expectedClosed)
    {
        var (item, closed) = LocalWorkItemSource.Parse("7.md", $"# T\n{statusLine}\nbody");
        Assert.Equal(expectedClosed, closed);
        Assert.Equal("7", item.Id);
        Assert.Equal("body", item.Body);
    }

    [Fact]
    public async Task ListOpen_skips_closed_files_and_Get_finds_any()
    {
        using var temp = new TempFolder();
        temp.Write(".autopilot/tasks/002-second.md", "# Second\nbody two");
        temp.Write(".autopilot/tasks/001-first.md", "# First\nbody one");
        temp.Write(".autopilot/tasks/003-done.md", "# Done\nStatus: done\n");
        temp.Write(".autopilot/tasks/notes.txt", "ignored");
        var source = new LocalWorkItemSource(temp.Path);

        var open = await source.ListOpenAsync(CancellationToken.None);
        var done = await source.GetAsync("003", CancellationToken.None);
        var missing = await source.GetAsync("999", CancellationToken.None);

        Assert.Equal(["001", "002"], open.Select(i => i.Id));
        Assert.Equal("Done", done?.Title);
        Assert.Null(missing);
    }

    [Fact]
    public async Task ListOpen_without_tasks_folder_is_empty()
    {
        using var temp = new TempFolder();
        Assert.Empty(await new LocalWorkItemSource(temp.Path).ListOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RecordTriage_writes_json_and_markdown_that_the_store_reads_back()
    {
        using var temp = new TempFolder();
        temp.Write(".autopilot/tasks/001-fix.md", "# Fix it\nbody");
        var source = new LocalWorkItemSource(temp.Path);
        var triage = new TriageResult(TriageLabel.AgentReady, "Clear bug.", ["returns 404"], RiskLevel.Low);

        await source.RecordTriageAsync("001", triage, CancellationToken.None);

        var json = File.ReadAllText(temp.Combine(".autopilot", "out", "triage", "001.json"));
        var markdown = File.ReadAllText(temp.Combine(".autopilot", "out", "triage", "001.md"));
        Assert.Contains("\"agent-ready\"", json);
        Assert.Contains("Label: **agent-ready**", markdown);
        Assert.Contains("- returns 404", markdown);

        var loaded = await new TriageStore(temp.Path).LoadAsync("001", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(TriageLabel.AgentReady, loaded.Label);
        Assert.Equal(["returns 404"], loaded.AcceptanceCriteria);
        Assert.Equal(RiskLevel.Low, loaded.Risk);
    }
}

public sealed class LocalPublisherTests
{
    [Fact]
    public async Task Publish_writes_the_bundle_and_returns_its_folder()
    {
        using var temp = new TempFolder();
        var item = new WorkItem("001", "Fix 404", "GET returns 500.", []);
        var triage = new TriageResult(TriageLabel.AgentReady, "Clear.", ["returns 404"], RiskLevel.Low);
        var evidence = new Evidence(
            "001",
            triage,
            new SelfReview("Return NotFound when the book is missing.", ["BooksTests.Missing_book_returns_404"], ["None really"]),
            new ReviewResult(ReviewVerdict.Approve, []));
        var decision = new GateDecision(GateOutcome.Passed, []);

        var result = await new LocalPublisher(temp.Path).PublishAsync(
            new PublishRequest(item, "autopilot/001", "diff --git a/x b/x\n", decision, evidence), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(temp.Combine(".autopilot", "out", "runs", "001"), result.Location);
        Assert.Equal("diff --git a/x b/x\n", File.ReadAllText(Path.Combine(result.Location, "change.patch")));
        using var gate = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Location, "gate.json")));
        Assert.Equal("passed", gate.RootElement.GetProperty("outcome").GetString());
        using var evidenceJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Location, "evidence.json")));
        Assert.Equal("approve", evidenceJson.RootElement.GetProperty("review").GetProperty("verdict").GetString());

        var pr = File.ReadAllText(Path.Combine(result.Location, "PULL_REQUEST.md"));
        Assert.Contains("# Fix 404", pr);
        Assert.Contains("Work item: 001", pr);
        Assert.Contains("Return NotFound when the book is missing.", pr);
        Assert.Contains("BooksTests.Missing_book_returns_404", pr);
        Assert.Contains("## Reviewer verdict", pr);
        Assert.Contains("approve", pr);
        Assert.Contains("Passed.", pr);
        Assert.Contains("waiting for a person", pr);
    }
}
