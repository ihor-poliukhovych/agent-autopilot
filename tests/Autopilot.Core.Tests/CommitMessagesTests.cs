using System.Text.RegularExpressions;
using Autopilot.Core.Config;
using Autopilot.Core.Git;

namespace Autopilot.Core.Tests;

public sealed class CommitMessagesTests
{
    private static WorkItem Item(string title, params string[] labels) => new("185", title, "body", labels);

    [Theory]
    [InlineData("bug", "fix")]
    [InlineData("Docs", "docs")]
    [InlineData("test", "test")]
    [InlineData("refactor", "refactor")]
    [InlineData("perf", "perf")]
    [InlineData("ci", "ci")]
    [InlineData("build", "build")]
    [InlineData("maintenance", "chore")]
    [InlineData("chore", "chore")]
    [InlineData("enhancement", "feat")]
    public void Labels_choose_the_type(string label, string type) =>
        Assert.Equal(type, CommitMessages.TypeFor([label]));

    [Fact]
    public void No_labels_means_feat_and_bug_wins_over_docs()
    {
        Assert.Equal("feat", CommitMessages.TypeFor([]));
        Assert.Equal("fix", CommitMessages.TypeFor(["docs", "bug"]));
    }

    [Theory]
    [InlineData("scope:core", "core")]
    [InlineData("Scope:Azure DevOps", "azure-devops")]
    [InlineData("scope:", null)]
    [InlineData("area:core", null)]
    public void Scope_comes_from_a_scope_label(string label, string? scope) =>
        Assert.Equal(scope, CommitMessages.ScopeFor(["bug", label]));

    [Fact]
    public void Conventional_message_has_type_scope_lowercase_subject_and_refs_footer()
    {
        var message = CommitMessages.Build("conventional", Item("Triage and implement silently use a stale local base branch", "bug", "scope:core"), 1);

        Assert.Equal(
            "fix(core): triage and implement silently use a stale local base branch\n\nRefs: #185",
            message);
    }

    [Fact]
    public void Task_id_is_never_in_the_first_line_and_no_trailer_is_added()
    {
        var message = CommitMessages.Build(null, Item("Add a thing"), 1);

        Assert.StartsWith("feat: add a thing", message);
        Assert.DoesNotContain("185", message.Split('\n')[0]);
        Assert.DoesNotMatch(new Regex("co-authored-by|generated (by|with)", RegexOptions.IgnoreCase), message);
    }

    [Fact]
    public void Long_titles_are_cut_at_a_word_so_the_first_line_fits()
    {
        var title = "Make the pool master report every parked task with the reason and the category in the summary file";
        var first = CommitMessages.Subject(Item(title, "scope:workspace")).Split('\n')[0];

        Assert.True(first.Length <= 72, first);
        Assert.StartsWith("feat(workspace): make the pool master report", first);
        Assert.True(title.ToLowerInvariant().Contains(first["feat(workspace): ".Length..], StringComparison.Ordinal), first);
        Assert.False(first.EndsWith(' '));
    }

    [Fact]
    public void A_title_without_spaces_is_cut_hard()
    {
        var first = CommitMessages.Subject(Item(new string('a', 200)));

        Assert.Equal(72, first.Length);
    }

    [Fact]
    public void Odd_titles_are_cleaned()
    {
        Assert.Equal("fix: crash on start", CommitMessages.Subject(Item("  `Crash` on start.  ", "bug")).Replace("`", string.Empty).Replace("  ", " "));
        Assert.Equal("feat: update", CommitMessages.Subject(Item("***")));
        Assert.Equal("feat: first second", CommitMessages.Subject(Item("First\r\nsecond.")));
    }

    [Fact]
    public void Review_round_keeps_type_and_scope()
    {
        var message = CommitMessages.Build("conventional", Item("Anything", "bug", "scope:cli"), 2);

        Assert.Equal("fix(cli): address review findings\n\nRefs: #185", message);
    }

    [Fact]
    public void Plain_style_keeps_the_old_format_and_no_pull_request_title()
    {
        var item = Item("Fix the cart total", "bug");

        Assert.Equal("185: Fix the cart total", CommitMessages.Build(CommitStyles.Plain, item, 1));
        Assert.Equal("185: address review findings", CommitMessages.Build(CommitStyles.Plain, item, 2));
        Assert.Null(CommitMessages.PullRequestTitle(CommitStyles.Plain, item));
    }

    [Fact]
    public void Pull_request_title_is_the_commit_subject()
    {
        var item = Item("Fix the cart total", "bug");

        Assert.Equal("fix: fix the cart total", CommitMessages.PullRequestTitle(CommitStyles.Conventional, item));
        Assert.Equal(CommitMessages.Subject(item), CommitMessages.Build(CommitStyles.Conventional, item, 1).Split('\n')[0]);
    }

    [Fact]
    public void Commit_style_defaults_to_conventional_and_is_validated()
    {
        Assert.Equal(CommitStyles.Conventional, new AutopilotConfig().CommitStyle);
        Assert.Equal(CommitStyles.Plain, ConfigLoader.Parse("""{ "commitStyle": "plain" }""").CommitStyle);
        Assert.Empty(ConfigLoader.Validate(new AutopilotConfig { CommitStyle = CommitStyles.Plain }));
        Assert.Contains(ConfigLoader.Validate(new AutopilotConfig { CommitStyle = "fancy" }), p => p.Contains("commitStyle", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Triage and implement silently use a stale local base branch", "bug")]
    [InlineData("Add a SQLite project registry with a very long explanation that goes on and on", "enhancement")]
    [InlineData("42 things to do", "docs")]
    [InlineData("Ünïcode first letter and `backticks`", "chore")]
    [InlineData("", "bug")]
    public void Messages_pass_the_repository_commit_msg_hook_pattern(string title, string label)
    {
        var pattern = new Regex(HookPattern());
        foreach (var round in new[] { 1, 2 })
        {
            var first = CommitMessages.Build("conventional", Item(title, label, "scope:core"), round).Split('\n')[0];

            Assert.Matches(pattern, first);
            Assert.False(first.EndsWith('.'));
        }
    }

    private static string HookPattern()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var hook = Path.Combine(dir.FullName, ".githooks", "commit-msg");
            if (File.Exists(hook))
            {
                var line = File.ReadAllLines(hook).First(l => l.StartsWith("pattern='", StringComparison.Ordinal));
                return line["pattern='".Length..^1];
            }
        }

        throw new FileNotFoundException("The repository's .githooks/commit-msg was not found above the test folder.");
    }
}
