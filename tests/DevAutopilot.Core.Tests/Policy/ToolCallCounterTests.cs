using DevAutopilot.Core.Policy;

namespace DevAutopilot.Core.Tests.Policy;

public sealed class ToolCallCounterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autopilot-counter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_starts_at_zero_and_returns_one()
    {
        var file = Path.Combine(_dir, "nested", "counter");

        Assert.Equal(1, ToolCallCounter.Increment(file));
        Assert.Equal("1", File.ReadAllText(file));
    }

    [Fact]
    public void Each_call_increments()
    {
        var file = Path.Combine(_dir, "counter");

        Assert.Equal(1, ToolCallCounter.Increment(file));
        Assert.Equal(2, ToolCallCounter.Increment(file));
        Assert.Equal(3, ToolCallCounter.Increment(file));
    }

    [Fact]
    public void Continues_from_existing_value()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "counter");
        File.WriteAllText(file, "41\n");

        Assert.Equal(42, ToolCallCounter.Increment(file));
        Assert.Equal("42", File.ReadAllText(file));
    }

    [Fact]
    public void Garbage_content_throws()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "counter");
        File.WriteAllText(file, "lots");

        Assert.Throws<InvalidDataException>(() => ToolCallCounter.Increment(file));
    }

    [Fact]
    public async Task Concurrent_increments_are_not_lost()
    {
        var file = Path.Combine(_dir, "counter");
        const int workers = 8;
        const int perWorker = 25;

        var results = await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            var values = new List<int>();
            for (var i = 0; i < perWorker; i++)
            {
                values.Add(ToolCallCounter.Increment(file));
            }

            return values;
        })));

        var all = results.SelectMany(v => v).Order().ToList();
        Assert.Equal(Enumerable.Range(1, workers * perWorker), all);
        Assert.Equal((workers * perWorker).ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(file));
    }

    [Fact]
    public void Gives_up_when_the_file_stays_locked()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "counter");
        using var holder = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => ToolCallCounter.Increment(file));
    }
}
