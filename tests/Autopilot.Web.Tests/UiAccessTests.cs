using System.Net;
using Autopilot.Core.Workspace;

namespace Autopilot.Web.Tests;

public sealed class UiAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "autopilot-port-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned later.
        }
    }

    private WorkspaceSettings Settings() => new(new Workspace(_root));

    [Fact]
    public void The_first_start_picks_a_port_in_range_and_every_later_start_reuses_it()
    {
        var settings = Settings();
        var first = UiAccess.ResolvePort(settings, null, isFree: (_, _) => true);
        Assert.InRange(first, 5100, 5999);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, UiAccess.ResolvePort(settings, null, isFree: (_, _) => true, random: new Random(i)));
        }

        Assert.Equal(first, Settings().GetInt(WorkspaceSettings.UiPort));
    }

    [Fact]
    public void A_busy_saved_port_gives_the_next_free_one_and_keeps_the_saved_one()
    {
        var settings = Settings();
        var saved = UiAccess.ResolvePort(settings, null, isFree: (_, _) => true);
        var next = UiAccess.ResolvePort(settings, null, isFree: (_, p) => p != saved);
        Assert.NotEqual(saved, next);
        Assert.InRange(next, 5100, 5999);
        Assert.Equal(saved, settings.GetInt(WorkspaceSettings.UiPort));
        Assert.Equal(saved, UiAccess.ResolvePort(settings, null, isFree: (_, _) => true));
    }

    [Fact]
    public void The_scan_wraps_around_the_end_of_the_range()
    {
        var settings = Settings();
        settings.Set(WorkspaceSettings.UiPort, "5999");
        Assert.Equal(5100, UiAccess.ResolvePort(settings, null, isFree: (_, p) => p == 5100));
    }

    [Fact]
    public void An_explicit_port_is_used_as_given_and_not_saved()
    {
        var settings = Settings();
        Assert.Equal(6123, UiAccess.ResolvePort(settings, 6123));
        Assert.Equal(0, UiAccess.ResolvePort(settings, 0));
        Assert.Null(settings.GetInt(WorkspaceSettings.UiPort));
    }

    [Fact]
    public void No_free_port_is_a_clear_error()
    {
        Assert.Throws<InvalidOperationException>(() => UiAccess.ResolvePort(Settings(), null, isFree: (_, _) => false));
    }

    [Fact]
    public void The_key_is_persistent_until_rotated()
    {
        var settings = Settings();
        var key = UiAccess.GetOrCreateKey(settings);
        Assert.Equal(key, UiAccess.GetOrCreateKey(Settings()));
        Assert.NotEqual(key, UiAccess.Rotate(settings));
    }

    [Fact]
    public void A_busy_port_is_seen_as_busy()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.False(UiAccess.IsFree(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Binding_beyond_loopback_forces_the_key_and_serves_other_host_names_only_with_it()
    {
        await using var ui = new UiFixture(signedIn: false, requireKey: false, bind: IPAddress.Any);
        var response = await ui.Client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
