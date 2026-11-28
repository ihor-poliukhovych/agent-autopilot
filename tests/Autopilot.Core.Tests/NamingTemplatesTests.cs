using Autopilot.Core.Config;
using Autopilot.Core.Git;

namespace Autopilot.Core.Tests;

public sealed class NamingTemplatesTests
{
    private static WorkItem Item(string title = "Add Retry to the Upload client!", params string[] labels) => new("185", title, "body", labels);

    private static AutopilotConfig Config(string? branch = null, string? commit = null, string? pr = null) =>
        new() { BranchTemplate = branch, CommitTemplate = commit, PrTitleTemplate = pr };

    [Fact]
    public void Placeholder_id() => Assert.Equal("task/task-185", NamingTemplates.Render("task/task-{id}", Item(), null));

    [Fact]
    public void Placeholder_slug_is_made_from_the_title() =>
        Assert.Equal("t/185-add-retry-to-the-upload-client", NamingTemplates.Render("t/{id}-{slug}", Item(), null));

    [Fact]
    public void Placeholder_slug_is_cut_and_never_empty()
    {
        Assert.True(NamingTemplates.Slug(new string('a', 100)).Length <= NamingTemplates.MaxSlugLength);
        Assert.Equal("change", NamingTemplates.Slug("!!!"));
    }

    [Fact]
    public void Placeholder_type_comes_from_the_labels() =>
        Assert.Equal("fix/185", NamingTemplates.Render("{type}/{id}", Item("x", "bug"), null));

    [Fact]
    public void Placeholder_type_defaults_to_feat() =>
        Assert.Equal("feat", NamingTemplates.Render("{type}", Item(), null));

    [Fact]
    public void Placeholder_title_is_one_line() =>
        Assert.Equal("a b", NamingTemplates.Render("{title}", Item("a\r\nb"), null));

    [Fact]
    public void Placeholder_subject_is_the_title_in_round_one_and_the_review_subject_later()
    {
        var config = Config(commit: "#{id} {subject}");
        Assert.Equal("#185 Add Retry to the Upload client!", NamingTemplates.Commit(config, Item(), 1));
        Assert.Equal("#185 address review findings", NamingTemplates.Commit(config, Item(), 2));
    }

    [Fact]
    public void Unknown_text_in_braces_is_left_alone() => Assert.Equal("{nope}-185", NamingTemplates.Render("{nope}-{id}", Item(), null));

    [Fact]
    public void Default_branch_is_the_prefix_and_id()
    {
        Assert.Equal("autopilot/185", NamingTemplates.Branch(new AutopilotConfig(), Item()));
        Assert.Equal("work/185", NamingTemplates.Branch(new AutopilotConfig { BranchPrefix = "work/" }, Item()));
    }

    [Theory]
    [InlineData("conventional")]
    [InlineData("plain")]
    public void Default_commit_equals_CommitMessages(string style)
    {
        var item = Item("Fix the parser", "bug", "scope:core");
        var config = new AutopilotConfig { CommitStyle = style };
        Assert.Equal(CommitMessages.Build(style, item, 1), NamingTemplates.Commit(config, item, 1));
        Assert.Equal(CommitMessages.Build(style, item, 2), NamingTemplates.Commit(config, item, 2));
    }

    [Fact]
    public void Default_pr_title_is_autopilot_prefix_and_title()
    {
        Assert.Equal("[autopilot] Fix the parser", NamingTemplates.PullRequestTitle(new AutopilotConfig(), Item("Fix the parser")));
        Assert.Equal("[autopilot] two lines", NamingTemplates.PullRequestTitle(new AutopilotConfig(), Item("two\r\nlines")));
        Assert.Equal("[autopilot] change", NamingTemplates.PullRequestTitle(new AutopilotConfig(), Item("")));
    }

    [Fact]
    public void Configured_templates_are_used()
    {
        var config = Config("task/task-{id}", "#{id} {subject}", "#{id} {subject}");
        Assert.Equal("task/task-185", NamingTemplates.Branch(config, Item()));
        Assert.Equal("#185 Add Retry to the Upload client!", NamingTemplates.PullRequestTitle(config, Item()));
    }

    [Fact]
    public void Unset_templates_load_unchanged_and_are_valid()
    {
        Assert.Null(new AutopilotConfig().BranchTemplate);
        Assert.DoesNotContain(ConfigLoader.Validate(new AutopilotConfig()), p => p.Contains("Template", StringComparison.Ordinal));
        Assert.DoesNotContain("Template", ConfigLoader.Serialize(new AutopilotConfig()), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_templates_pass() =>
        Assert.DoesNotContain(ConfigLoader.Validate(Config("task/task-{id}", "#{id} {subject}", "{type}: {title}")), p => p.Contains("Template", StringComparison.Ordinal));

    [Fact]
    public void Branch_template_without_id_is_reported()
    {
        var problems = ConfigLoader.Validate(Config(branch: "task/{slug}"));
        Assert.Contains(problems, p => p.StartsWith("branchTemplate", StringComparison.Ordinal) && p.Contains("{id}", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_placeholder_is_reported_with_the_setting_name()
    {
        var problems = ConfigLoader.Validate(Config(commit: "{id} {bogus}"));
        Assert.Contains(problems, p => p.StartsWith("commitTemplate", StringComparison.Ordinal) && p.Contains("{bogus}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{id")]
    [InlineData("id}")]
    [InlineData("{{id}")]
    public void Unbalanced_brace_is_reported(string template)
    {
        var problems = ConfigLoader.Validate(Config(pr: template));
        Assert.Contains(problems, p => p.StartsWith("prTitleTemplate", StringComparison.Ordinal) && p.Contains("unbalanced", StringComparison.Ordinal));
    }
}
