using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace Autopilot.Web.Tests;

public sealed class FeedbackTests
{
    [Theory]
    [InlineData("token=abc123def456", "abc123def456")]
    [InlineData("password: hunter2hunter2", "hunter2hunter2")]
    [InlineData("AUTOPILOT_ADO_PAT=zzzz1111zzzz", "zzzz1111zzzz")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "abcdefgh12345678")]
    [InlineData("pushed with ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://user:pa55word@example.com/x", "pa55word")]
    [InlineData("key sk-abcdefghijklmnopqrstuvwxyz", "sk-abcdefghijklmnopqrstuvwxyz")]
    public void Redact_removes_secrets(string input, string secret)
    {
        var result = Feedback.Redact(input);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public void Redact_removes_a_private_key_block()
    {
        var result = Feedback.Redact("a\n-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----\nb");
        Assert.DoesNotContain("MIIabc", result);
    }

    [Theory]
    [InlineData(@"failed in C:\Users\jane\repo\src\a.cs line 3", "jane")]
    [InlineData("open /home/jane/work/x.log now", "jane")]
    [InlineData("see /Users/jane/Library/x", "jane")]
    [InlineData(@"share \\server01\data\file.txt", "server01")]
    public void Redact_removes_local_paths(string input, string private_)
    {
        var result = Feedback.Redact(input);
        Assert.DoesNotContain(private_, result);
        Assert.Contains("<path>", result);
    }

    [Fact]
    public void Redact_removes_the_user_name_and_keeps_normal_text()
    {
        Assert.Equal("hello <user>, the run fails", Feedback.Redact("hello Jane, the run fails", ["jane"]));
        Assert.Equal("The pool stops after 3 tasks.", Feedback.Redact("The pool stops after 3 tasks."));
    }

    [Fact]
    public void A_bug_uses_the_bug_template_and_its_field_ids()
    {
        var p = Feedback.Build(new FeedbackBody("bug", "Pool stops", "It stops at C:\\Users\\jane\\x token=secret99", true, "#/p/demo/pool"), "1.2.3", "Windows 11");

        var uri = new Uri(p.Url);
        Assert.StartsWith(Feedback.Repository + "/issues/new", p.Url);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("bug_report.yml", query["template"]);
        Assert.Equal("bug", query["labels"]);
        Assert.Equal("Pool stops", query["title"]);
        Assert.Contains("<path>", query["what"]);
        Assert.DoesNotContain("secret99", p.Url);
        Assert.DoesNotContain("jane", p.Url);
        Assert.Equal("Autopilot 1.2.3, Windows 11, page #/p/demo/pool", query["version"]);
    }

    [Fact]
    public void An_idea_uses_the_feature_template_and_a_question_is_marked()
    {
        var idea = new Uri(Feedback.Build(new FeedbackBody("idea", "Dark mode", "Please add it", false, null), "1", "os").Url);
        var q = HttpUtility.ParseQueryString(idea.Query);
        Assert.Equal("feature_request.yml", q["template"]);
        Assert.Equal("enhancement", q["labels"]);
        Assert.Equal("Please add it", q["problem"]);

        var question = HttpUtility.ParseQueryString(new Uri(Feedback.Build(new FeedbackBody("question", "How", "How do I?", false, null), "1", "os").Url).Query);
        Assert.Equal("question", question["labels"]);
        Assert.StartsWith("Question: ", question["title"]);
    }

    [Fact]
    public void Context_is_left_out_when_not_asked_for()
    {
        var p = Feedback.Build(new FeedbackBody("bug", "t", "d", false, "#/x"), "9.9.9", "os");
        Assert.DoesNotContain("9.9.9", p.Url);
        Assert.DoesNotContain("Context:", p.Text);
    }

    [Fact]
    public void A_very_long_description_is_cut_to_fit_the_address()
    {
        var p = Feedback.Build(new FeedbackBody("idea", "t", new string('x', 50_000), false, null), "1", "os");
        Assert.True(p.Url.Length <= Feedback.MaxUrlLength);
        Assert.Contains("%5Bcut%5D", p.Url);
    }

    [Theory]
    [InlineData("bug", "", "d")]
    [InlineData("bug", "t", "  ")]
    [InlineData("other", "t", "d")]
    public void Invalid_input_is_refused(string kind, string title, string description) =>
        Assert.Throws<ArgumentException>(() => Feedback.Build(new FeedbackBody(kind, title, description, false, null), "1", "os"));

    [Fact]
    public async Task The_endpoint_returns_the_preview_and_rejects_bad_input()
    {
        await using var ui = new UiFixture();
        var ok = await ui.Client.SendAsync(ui.Request(HttpMethod.Post, "/api/feedback", new { kind = "bug", title = "Broken", description = "It broke", includeContext = true, page = "#/projects" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith(Feedback.Repository + "/issues/new?template=bug_report.yml", json.GetProperty("url").GetString());
        Assert.Contains("It broke", json.GetProperty("text").GetString());

        var bad = await ui.Client.SendAsync(ui.Request(HttpMethod.Post, "/api/feedback", new { kind = "bug", title = "", description = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
