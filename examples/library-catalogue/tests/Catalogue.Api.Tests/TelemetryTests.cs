using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "catalogue-telemetry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Each_request_is_written_as_one_json_line()
    {
        var path = Path.Combine(_folder, "telemetry.jsonl");
        await using var app = new CatalogueApp().WithWebHostBuilder(b => b.UseSetting("Telemetry:Path", path));
        var client = app.CreateClient();

        await client.GetAsync("/books");
        await client.GetAsync("/members/99");

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);

        using var ok = JsonDocument.Parse(lines[0]);
        Assert.Equal("/books", ok.RootElement.GetProperty("route").GetString());
        Assert.Equal(200, ok.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("Information", ok.RootElement.GetProperty("level").GetString());
        Assert.True(ok.RootElement.GetProperty("durationMs").GetDouble() >= 0);
        Assert.True(ok.RootElement.TryGetProperty("timestamp", out _));
        Assert.Equal(JsonValueKind.Null, ok.RootElement.GetProperty("exception").ValueKind);

        using var notFound = JsonDocument.Parse(lines[1]);
        Assert.Equal("/members/{id}", notFound.RootElement.GetProperty("route").GetString());
        Assert.Equal(404, notFound.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("Warning", notFound.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Telemetry_is_off_without_a_path()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await client.GetAsync("/books");

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(Directory.Exists(_folder));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
