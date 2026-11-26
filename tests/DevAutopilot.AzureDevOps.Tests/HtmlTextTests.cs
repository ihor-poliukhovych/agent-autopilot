namespace DevAutopilot.AzureDevOps.Tests;

public class HtmlTextTests
{
    [Fact]
    public void Paragraphs_and_line_breaks_are_kept()
    {
        var text = HtmlText.ToPlainText("<div>First line<br>second line</div><p>Next   paragraph\n with  spaces</p>");

        Assert.Equal("First line\nsecond line\n\nNext paragraph with spaces", text);
    }

    [Fact]
    public void Lists_become_dashes_and_numbers()
    {
        var text = HtmlText.ToPlainText("<p>Steps:</p><ol><li>Open <b>cart</b></li><li>Pay</li></ol><ul><li>one</li><li>two</li></ul>");

        Assert.Equal("Steps:\n\n1. Open cart\n2. Pay\n\n- one\n- two", text);
    }

    [Fact]
    public void Entities_are_decoded_and_text_with_angle_brackets_survives()
    {
        var text = HtmlText.ToPlainText("<p>Price &lt; 10 &amp;&nbsp;stock &gt; 0, a < b</p>");

        Assert.Equal("Price < 10 & stock > 0, a < b", text);
    }

    [Fact]
    public void Script_style_and_comments_are_removed()
    {
        var text = HtmlText.ToPlainText("<style>p{color:red}</style><p>Keep</p><script>alert('x')</script><!-- hidden --><p>this</p>");

        Assert.Equal("Keep\n\nthis", text);
    }

    [Fact]
    public void Attributes_with_angle_brackets_do_not_break_parsing()
    {
        var text = HtmlText.ToPlainText("<a href=\"https://example.com/?q=a>b\" title='x>y'>link</a> text");

        Assert.Equal("link text", text);
    }

    [Fact]
    public void Preformatted_text_keeps_its_lines()
    {
        var text = HtmlText.ToPlainText("<pre>line 1\n  line 2</pre>");

        Assert.Equal("line 1\n  line 2", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<div></div>")]
    public void Empty_input_gives_empty_text(string? html) =>
        Assert.Equal(string.Empty, HtmlText.ToPlainText(html));

    [Fact]
    public void Plain_text_is_returned_as_is() =>
        Assert.Equal("Just text", HtmlText.ToPlainText("Just text"));

    [Fact]
    public void Unclosed_tag_is_kept_as_text() =>
        Assert.Equal("broken <b", HtmlText.ToPlainText("broken <b"));
}
