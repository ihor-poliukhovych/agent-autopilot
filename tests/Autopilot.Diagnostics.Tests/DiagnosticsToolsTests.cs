using System.Text.Json;
using Autopilot.Core;
using ModelContextProtocol;

namespace Autopilot.Diagnostics.Tests;

public sealed class DiagnosticsToolsTests
{
    private static DiagnosticsTools Tools() => new(new JsonlDiagnosticsSource(Fixtures.Path("app.jsonl")));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task List_recent_errors_groups_by_type_and_route()
    {
        var result = Parse(await Tools().ListRecentErrorsAsync());

        Assert.Equal(4, result.GetProperty("errorEvents").GetInt32());
        var groups = result.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(3, groups.Count);

        var first = groups[0];
        Assert.Equal("System.InvalidOperationException", first.GetProperty("exceptionType").GetString());
        Assert.Equal("GET /books/{id}", first.GetProperty("route").GetString());
        Assert.Equal(2, first.GetProperty("count").GetInt32());
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T11:00:00Z"), first.GetProperty("firstSeen").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T12:00:00Z"), first.GetProperty("lastSeen").GetDateTimeOffset());
        Assert.Equal("Book 7 lookup failed", first.GetProperty("sampleMessage").GetString());

        // An exception logged as a JSON object is grouped by its "type"; an error without a route has no route.
        var timeout = groups.Single(g => g.GetProperty("exceptionType").GetString() == "TimeoutException");
        Assert.False(timeout.TryGetProperty("route", out _));
        Assert.Contains(groups, g => g.GetProperty("exceptionType").GetString() == "System.NullReferenceException");
    }

    [Fact]
    public async Task List_recent_errors_window_counts_back_from_the_newest_event()
    {
        var result = Parse(await Tools().ListRecentErrorsAsync(sinceHours: 2, limit: 2));

        // Newest event 14:00, so the window starts at 12:00: one InvalidOperationException is left.
        Assert.Equal(3, result.GetProperty("errorEvents").GetInt32());
        Assert.Equal(3, result.GetProperty("groupCount").GetInt32());
        Assert.Equal(2, result.GetProperty("returned").GetInt32());
        Assert.Equal(2, result.GetProperty("groups").GetArrayLength());
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T12:00:00Z"), result.GetProperty("window").GetProperty("from").GetDateTimeOffset());
    }

    [Fact]
    public async Task List_recent_errors_rejects_a_non_positive_window()
    {
        await Assert.ThrowsAsync<McpException>(() => Tools().ListRecentErrorsAsync(sinceHours: 0));
    }

    [Fact]
    public async Task Get_error_details_returns_full_samples_newest_first()
    {
        var result = Parse(await Tools().GetErrorDetailsAsync("invalidoperationexception", "get /books/{id}"));

        Assert.Equal(2, result.GetProperty("totalMatches").GetInt32());
        var samples = result.GetProperty("samples").EnumerateArray().ToList();
        Assert.Equal(2, samples.Count);
        Assert.Equal("Book 7 lookup failed", samples[0].GetProperty("message").GetString());
        Assert.Contains("BookService.cs:line 17", samples[1].GetProperty("exception").GetString());
        Assert.Contains("Endpoints.GetBook", samples[1].GetProperty("exception").GetString());
        Assert.Equal(500, samples[0].GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task Get_error_details_caps_samples_at_three()
    {
        using var folder = new TempFolder();
        var lines = Enumerable.Range(0, 10).Select(i =>
            $$"""{"timestamp":"2026-11-20T10:{{i:00}}:00Z","level":"Error","message":"m{{i}}","exception":"System.Exception: boom"}""");
        var path = folder.Write("log.jsonl", string.Join('\n', lines));

        var result = Parse(await new DiagnosticsTools(new JsonlDiagnosticsSource(path)).GetErrorDetailsAsync("System.Exception"));

        Assert.Equal(10, result.GetProperty("totalMatches").GetInt32());
        Assert.Equal(3, result.GetProperty("samples").GetArrayLength());
        Assert.Equal("m9", result.GetProperty("samples")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Get_error_details_needs_a_type()
    {
        await Assert.ThrowsAsync<McpException>(() => Tools().GetErrorDetailsAsync(" "));
    }

    [Fact]
    public async Task Search_logs_matches_message_and_exception_ignoring_case()
    {
        var byMessage = Parse(await Tools().SearchLogsAsync("SLOW QUERY"));
        Assert.Equal(1, byMessage.GetProperty("totalMatches").GetInt32());
        Assert.Equal("Warning", byMessage.GetProperty("events")[0].GetProperty("level").GetString());

        var byException = Parse(await Tools().SearchLogsAsync("bookservice"));
        Assert.Equal(2, byException.GetProperty("totalMatches").GetInt32());
        // Search results carry only the first line of the exception.
        var exception = byException.GetProperty("events")[0].GetProperty("exception").GetString()!;
        Assert.Equal("System.InvalidOperationException: Sequence contains no elements", exception);

        var errorsOnly = Parse(await Tools().SearchLogsAsync("book", level: "ERROR", limit: 1));
        Assert.Equal(3, errorsOnly.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, errorsOnly.GetProperty("events").GetArrayLength());
        Assert.Equal("Loan for book 3 failed", errorsOnly.GetProperty("events")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Search_logs_needs_a_query()
    {
        await Assert.ThrowsAsync<McpException>(() => Tools().SearchLogsAsync(""));
    }

    [Fact]
    public async Task Endpoint_stats_give_count_error_rate_and_percentiles()
    {
        var result = Parse(await Tools().GetEndpointStatsAsync());

        var routes = result.GetProperty("routes").EnumerateArray().ToDictionary(r => r.GetProperty("route").GetString()!);
        Assert.Equal(3, routes.Count);

        var books = routes["GET /books"];
        Assert.Equal(4, books.GetProperty("count").GetInt32());
        Assert.Equal(0, books.GetProperty("errors").GetInt32());
        Assert.Equal(20, books.GetProperty("p50DurationMs").GetDouble());
        Assert.Equal(40, books.GetProperty("p95DurationMs").GetDouble());

        var book = routes["GET /books/{id}"];
        Assert.Equal(1.0, book.GetProperty("errorRate").GetDouble());

        var loans = routes["POST /loans"];
        Assert.Equal(0.5, loans.GetProperty("errorRate").GetDouble());
        Assert.Equal(55.5, loans.GetProperty("p50DurationMs").GetDouble());
        Assert.Equal(900, loans.GetProperty("p95DurationMs").GetDouble());

        var one = Parse(await Tools().GetEndpointStatsAsync("post /LOANS"));
        Assert.Equal(1, one.GetProperty("routeCount").GetInt32());
    }

    [Fact]
    public async Task Outputs_are_bounded()
    {
        using var folder = new TempFolder();
        var longText = new string('x', 10_000);
        var lines = Enumerable.Range(0, 120).Select(i => JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Parse("2026-11-20T10:00:00Z").AddMinutes(i),
            level = "Error",
            message = longText,
            route = $"GET /r{i}",
            durationMs = i,
            exception = $"System.Exception{i}: boom\n" + longText,
        }));
        var path = folder.Write("log.jsonl", string.Join('\n', lines));
        var tools = new DiagnosticsTools(new JsonlDiagnosticsSource(path));

        var errors = Parse(await tools.ListRecentErrorsAsync(limit: 500));
        Assert.Equal(DiagnosticsTools.MaxItems, errors.GetProperty("groups").GetArrayLength());
        Assert.True(errors.GetProperty("groups")[0].GetProperty("sampleMessage").GetString()!.Length < 600);

        var search = Parse(await tools.SearchLogsAsync("x", limit: 500));
        Assert.Equal(DiagnosticsTools.MaxItems, search.GetProperty("events").GetArrayLength());
        Assert.Equal(120, search.GetProperty("totalMatches").GetInt32());

        var details = Parse(await tools.GetErrorDetailsAsync("System.Exception7"));
        Assert.True(details.GetProperty("samples")[0].GetProperty("exception").GetString()!.Length < DiagnosticsTools.MaxExceptionLength + 100);

        var stats = Parse(await tools.GetEndpointStatsAsync());
        Assert.Equal(120, stats.GetProperty("routeCount").GetInt32());
        Assert.Equal(DiagnosticsTools.MaxItems, stats.GetProperty("routes").GetArrayLength());

        Assert.True(Parse(await tools.ListRecentErrorsAsync(limit: -3)).GetProperty("groups").GetArrayLength() == 1);
    }

    [Fact]
    public async Task An_unreadable_source_is_a_tool_error()
    {
        var tools = new DiagnosticsTools(new JsonlDiagnosticsSource(Path.Combine(Path.GetTempPath(), "autopilot-missing-" + Guid.NewGuid().ToString("N") + ".jsonl")));
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ListRecentErrorsAsync());
        Assert.Contains("could not be read", ex.Message);
    }

    [Theory]
    [InlineData("System.ArgumentException: bad\n at X", "System.ArgumentException")]
    [InlineData("Something odd happened here", "Something odd happened here")]
    [InlineData("{\"className\":\"Foo.BarException\"}", "Foo.BarException")]
    [InlineData(null, DiagnosticsTools.NoException)]
    public void Exception_type_is_read_from_the_first_line(string? exception, string expected) =>
        Assert.Equal(expected, DiagnosticsTools.ExceptionType(new DiagnosticEvent(DateTimeOffset.UnixEpoch, "Error", "m", exception, null, null, null)));
}
