using System.Text.RegularExpressions;

namespace Autopilot.Web.Tests;

public sealed class NavigationLayoutTests
{
    private static string Wwwroot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative + " not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Tab_bar_is_exactly_Inbox_Tasks_Pipeline_Pool_Settings_in_order()
    {
        var js = Wwwroot("js/app.js");
        var block = Regex.Match(js, @"const TABS = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var labels = Regex.Matches(block, @"\['(\w+)', '([^']+)'").Select(m => m.Groups[2].Value).ToArray();
        Assert.Equal(new[] { "Inbox", "Tasks", "Pipeline", "Pool", "Settings" }, labels);
    }

    [Fact]
    public void A_project_address_without_a_tab_lands_on_Inbox()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("tab: parts[2] || 'inbox'", js);
        Assert.Contains("let lastTab = 'inbox'", js);
    }

    [Fact]
    public void Old_runs_run_and_needs_addresses_still_route_to_their_pages()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("const LEGACY = { runs: runs, run: runs, needs: needs }", js);
        Assert.Contains("r.tab === 'run' ? 'runs'", js);
    }

    [Fact]
    public void Inbox_is_a_placeholder_linking_to_needs_and_runs_without_api_calls()
    {
        var js = Wwwroot("js/inbox.js");
        Assert.Contains("content comes later", js);
        Assert.Contains("/needs`", js);
        Assert.Contains("/runs`", js);
        Assert.DoesNotContain("get(", js);
        Assert.DoesNotContain("api.js", js);
    }

    [Fact]
    public void Css_defines_left_col_420px_and_the_shared_layout_class()
    {
        var css = Wwwroot("app.css");
        Assert.Matches(@"--left-col:\s*420px", css);
        Assert.Matches(@"\.app-layout\s*\{[^}]*grid-template-columns:\s*var\(--left-col\)\s+minmax\(0,\s*1fr\)", css);
        Assert.Matches(@"@media \(max-width: 760px\)\s*\{[^@]*\.app-right[^@]*position: fixed", css);
    }

    [Fact]
    public void Pipeline_tasks_pool_and_needs_pages_are_drawn_in_the_shared_layout()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("const LAYOUT = new Set(['pipeline', 'tasks', 'pool', 'needs'])", js);
        Assert.Contains("class: 'app-layout'", js);
    }

    [Fact]
    public void Left_column_is_filled_and_never_hidden_as_empty()
    {
        var js = Wwwroot("js/app.js");
        var css = Wwwroot("app.css");
        Assert.Contains("function leftColumn(", js);
        Assert.DoesNotContain("app-links", js);
        Assert.DoesNotContain("app-links", css);
        Assert.DoesNotContain(":empty", css);
    }

    [Fact]
    public void Drawer_can_be_opened_and_closed_on_phone_width()
    {
        var js = Wwwroot("js/app.js");
        var css = Wwwroot("app.css");
        Assert.Contains("target.classList.add('open')", js);
        Assert.Contains("target.classList.remove('open')", js);
        Assert.Matches(@"\.app-right\.open\s*\{\s*transform:\s*none", css);
        Assert.Matches(@"\.drawer-open,\s*\.drawer-close\s*\{\s*display:\s*none", css);
    }

    [Fact]
    public void Legacy_lookup_ignores_inherited_object_keys()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("Object.hasOwn(LEGACY, r.tab)", js);
        Assert.DoesNotContain("LEGACY[r.tab] ?", js);
        Assert.DoesNotContain("LEGACY[page] ?", js);
    }
}
