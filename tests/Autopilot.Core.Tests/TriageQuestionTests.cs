using Autopilot.Core.Tasks;

namespace Autopilot.Core.Tests;

public sealed class TriageQuestionTests
{
    [Fact]
    public void Prose_with_three_lettered_options_and_a_recommendation_is_split()
    {
        var q = TriageQuestion.FromProse("Which search should the page use? (A) the product search, (B) the order search, or (C) both. I recommend B because it is smaller.");

        Assert.Equal("Which search should the page use?", q.Text);
        Assert.Equal(["the product search", "the order search", "both"], q.Options);
        Assert.Equal(1, q.Recommended);
    }

    [Theory]
    [InlineData("Keep it? (A) yes (B) no. Recommend A.", 0)]
    [InlineData("Keep it? (A) yes; (B) no; I recommend option (B), it is safer.", 1)]
    [InlineData("Keep it? (1) yes, (2) no - I suggest 2.", 1)]
    [InlineData("Keep it? (A) yes, (B) no. My recommendation: A", 0)]
    public void The_recommendation_is_found_in_the_usual_phrasings(string reason, int expected)
    {
        var q = TriageQuestion.FromProse(reason);

        Assert.Equal(2, q.Options.Count);
        Assert.Equal(expected, q.Recommended);
        Assert.DoesNotContain("recommend", string.Concat(q.Options), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_option_that_ends_in_or_keeps_its_last_letters()
    {
        var q = TriageQuestion.FromProse("Who owns it? (A) the vendor, or (B) us.");

        Assert.Equal(["the vendor", "us"], q.Options);
        Assert.Null(q.Recommended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Should the page show a count? Say yes or no.")]
    [InlineData("Only (A) one option is listed here.")]
    [InlineData("See (B) and (C) in the spec.")]
    public void Text_without_a_run_of_options_stays_whole(string reason)
    {
        var q = TriageQuestion.FromProse(reason);

        Assert.Empty(q.Options);
        Assert.Null(q.Recommended);
        Assert.Equal(reason.Trim(), q.Text);
    }

    [Fact]
    public void Structured_options_win_and_the_recommendation_is_one_based()
    {
        var q = TriageQuestion.Resolve("Keep it? (A) yes (B) no. I recommend A.", ["Keep it", "Drop it", "Ask later"], 2);

        Assert.Equal(["Keep it", "Drop it", "Ask later"], q.Options);
        Assert.Equal(1, q.Recommended);
        Assert.Equal("Keep it?", q.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(null)]
    public void A_recommendation_outside_the_options_is_ignored(int? recommended)
    {
        var q = TriageQuestion.Resolve("Which?", ["a", "b"], recommended);

        Assert.Equal(2, q.Options.Count);
        Assert.Null(q.Recommended);
        Assert.Equal("Which?", q.Text);
    }

    [Fact]
    public void Old_results_without_fields_fall_back_to_the_prose()
    {
        var q = TriageQuestion.Resolve("Which? (A) one (B) two. I recommend B.", null, null);

        Assert.Equal(["one", "two"], q.Options);
        Assert.Equal(1, q.Recommended);
    }
}
