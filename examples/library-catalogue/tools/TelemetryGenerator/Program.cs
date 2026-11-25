// Writes the example's fake telemetry: two days of requests to the catalogue API in the format of
// Catalogue.Api's RequestLoggingMiddleware. The output is deterministic: the same seed, start date and
// line endings every run, so the file only changes when this program changes.
//
// Run from examples/library-catalogue:
//   dotnet run --project tools/TelemetryGenerator -- telemetry/telemetry.jsonl

using System.Globalization;
using System.Text;
using System.Text.Json;

var path = args.Length > 0 ? args[0] : Path.Combine("telemetry", "telemetry.jsonl");
var random = new Random(20261116);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var days = new[] { new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero) };
const int RequestsPerDay = 150;

var output = new StringBuilder();
var written = 0;
foreach (var day in days)
{
    // Opening hours 08:00 to 20:00 UTC; sorted so the file reads like a real log.
    var times = Enumerable.Range(0, RequestsPerDay)
        .Select(_ => day.AddHours(8).AddMilliseconds(random.NextDouble() * TimeSpan.FromHours(12).TotalMilliseconds))
        .Order()
        .ToList();
    foreach (var time in times)
    {
        var record = NextRequest(random, time);
        output.Append(JsonSerializer.Serialize(record, options)).Append('\n');
        written++;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"Wrote {written} records to {path}");

static Record NextRequest(Random random, DateTimeOffset time)
{
    var roll = random.NextDouble();
    if (roll < 0.30)
    {
        // GET /books: usually fast, sometimes very slow (the list is not paged).
        var slow = random.NextDouble() < 0.05;
        var ms = slow ? 1800 + (random.NextDouble() * 2400) : 6 + (random.NextDouble() * 40);
        return Ok(time, "GET", "/books", 200, ms);
    }

    if (roll < 0.60)
    {
        // GET /books/{id}: unknown ids crash with KeyNotFoundException (seeded bug, task 001).
        if (random.NextDouble() < 0.13)
        {
            var id = random.Next(7, 120);
            return Crash(time, "GET", "/books/{id}", 1 + (random.NextDouble() * 4), BookNotFound(id));
        }

        return Ok(time, "GET", "/books/{id}", 200, 2 + (random.NextDouble() * 15));
    }

    if (roll < 0.70)
    {
        var found = random.NextDouble() < 0.85;
        return Ok(time, "GET", "/members/{id}", found ? 200 : 404, 1 + (random.NextDouble() * 6));
    }

    if (roll < 0.85)
    {
        var outcome = random.NextDouble();
        var status = outcome < 0.75 ? 201 : outcome < 0.90 ? 409 : 400;
        return Ok(time, "POST", "/loans", status, 4 + (random.NextDouble() * 20));
    }

    if (roll < 0.93)
    {
        var status = random.NextDouble() < 0.9 ? 200 : 404;
        return Ok(time, "POST", "/loans/{id}/return", status, 3 + (random.NextDouble() * 12));
    }

    // POST /books: bad JSON bodies fail model binding with 400.
    var bad = random.NextDouble() < 0.3;
    return Ok(time, "POST", "/books", bad ? 400 : 201, 5 + (random.NextDouble() * 25));
}

static Record Ok(DateTimeOffset time, string method, string route, int status, double ms)
{
    var duration = Math.Round(ms, 1);
    var level = status >= 500 ? "Error" : status >= 400 ? "Warning" : "Information";
    return new Record(time, level, Message(method, route, status, duration), method, route, status, duration, null);
}

static Record Crash(DateTimeOffset time, string method, string route, double ms, string exception)
{
    var duration = Math.Round(ms, 1);
    return new Record(time, "Error", Message(method, route, 500, duration), method, route, 500, duration, exception);
}

static string Message(string method, string route, int status, double duration) =>
    string.Create(CultureInfo.InvariantCulture, $"HTTP {method} {route} responded {status} in {duration} ms");

// Matches the real stack trace of the seeded code (built in a container, so paths start at /src).
static string BookNotFound(int id) => string.Join(
    '\n',
    $"System.Collections.Generic.KeyNotFoundException: The given key '{id}' was not present in the dictionary.",
    "   at System.Collections.Generic.Dictionary`2.get_Item(TKey key)",
    "   at Catalogue.Api.Store.CatalogueStore.GetBook(Int32 id) in /src/Catalogue.Api/Store/CatalogueStore.cs:line 37",
    "   at Catalogue.Api.Books.BookEndpoints.GetBook(Int32 id, CatalogueStore store) in /src/Catalogue.Api/Books/BookEndpoints.cs:line 18",
    "   at lambda_method2(Closure, Object, HttpContext)",
    "   at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext httpContext)",
    "   at Catalogue.Api.Telemetry.RequestLoggingMiddleware.InvokeAsync(HttpContext context) in /src/Catalogue.Api/Telemetry/RequestLoggingMiddleware.cs:line 20");

internal sealed record Record(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string Method,
    string Route,
    int StatusCode,
    double DurationMs,
    string? Exception);
