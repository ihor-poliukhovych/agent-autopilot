using System.Net;
using System.Net.Http.Json;

namespace DevAutopilot.Web.Tests;

public sealed class SecurityTests
{
    // A script, stylesheet, import, font or fetch that points outside this page would break offline use.
    private static readonly System.Text.RegularExpressions.Regex OutsideReference = new(
        @"(src|href)\s*=\s*[""']https?:|url\(\s*[""']?https?:|@import|from\s+[""']https?:|fetch\(\s*[""']https?:|import\(\s*[""']https?:",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Fact]
    public async Task The_api_needs_the_session_cookie()
    {
        await using var ui = new UiFixture(signedIn: false);
        var response = await ui.Client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Each_port_gets_its_own_session_cookie_name()
    {
        Assert.NotEqual(
            SessionGuard.CookieNameFor(new Microsoft.AspNetCore.Http.HostString("127.0.0.1", 5290)),
            SessionGuard.CookieNameFor(new Microsoft.AspNetCore.Http.HostString("127.0.0.1", 5292)));
    }

    [Fact]
    public async Task The_key_works_once_and_starts_a_cookie_session()
    {
        await using var ui = new UiFixture(signedIn: false);
        var first = await ui.Client.GetAsync($"/?key={UiFixture.Key}");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var cookie = first.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/", first.Headers.Location?.OriginalString);

        var second = await ui.Client.GetAsync($"/?key={UiFixture.Key}");
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.DoesNotContain(second.Headers, h => h.Key == "Set-Cookie");
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        await using var ui = new UiFixture(signedIn: false);
        var response = await ui.Client.GetAsync("/?key=nope");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The wrong key did not use up the real one.
        Assert.Equal(HttpStatusCode.Redirect, (await ui.Client.GetAsync($"/?key={UiFixture.Key}")).StatusCode);
    }

    [Fact]
    public async Task A_change_needs_the_custom_header()
    {
        await using var ui = new UiFixture();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/x/pool") { Content = JsonContent.Create(new { id = "1" }) };
        request.Headers.Add("Origin", "http://localhost");
        var response = await ui.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]
    public async Task A_change_from_another_origin_is_refused(string origin)
    {
        await using var ui = new UiFixture();
        using var request = ui.Request(HttpMethod.Post, "/api/projects", ui.Draft());
        request.Headers.Remove("Origin");
        request.Headers.Add("Origin", origin);
        var response = await ui.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(new DevAutopilot.Core.Workspace.ProjectRegistry(ui.Workspace).List());
    }

    [Fact]
    public async Task A_change_without_an_origin_is_refused()
    {
        await using var ui = new UiFixture();
        using var request = ui.Request(HttpMethod.Post, "/api/projects", ui.Draft());
        request.Headers.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await ui.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Other_host_names_are_not_served()
    {
        await using var ui = new UiFixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Host = "attacker.example";
        var response = await ui.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_pages_are_served_with_a_strict_content_policy_and_no_outside_addresses()
    {
        await using var ui = new UiFixture(signedIn: false);
        var response = await ui.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("js/app.js", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(OutsideReference, html);
        foreach (var asset in new[] { "/app.css", "/js/app.js", "/js/wizard.js", "/js/tasks.js" })
        {
            Assert.Equal(HttpStatusCode.OK, (await ui.Client.GetAsync(asset)).StatusCode);
        }
    }

    [Fact]
    public void Static_files_have_no_outside_dependencies()
    {
        var root = FindWwwroot();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotMatch(OutsideReference, File.ReadAllText(file));
        }
    }

    private static string FindWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DevAutopilot.Web", "wwwroot")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "DevAutopilot.Web", "wwwroot");
    }
}
