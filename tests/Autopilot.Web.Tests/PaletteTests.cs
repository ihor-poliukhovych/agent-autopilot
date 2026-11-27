using System.Globalization;
using System.Text.RegularExpressions;

namespace Autopilot.Web.Tests;

public sealed class PaletteTests
{
    private static string Css()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", "app.css");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("wwwroot/app.css not found above " + AppContext.BaseDirectory);
    }

    // The three token blocks: light :root, the prefers-color-scheme dark block, and :root[data-theme="dark"].
    private static (string Light, string AutoDark, string ForcedDark) Blocks()
    {
        var css = Css();
        var light = Regex.Match(css, @"^:root\s*\{(.*?)\n\}", RegexOptions.Singleline | RegexOptions.Multiline).Groups[1].Value;
        var auto = Regex.Match(css, @"@media \(prefers-color-scheme: dark\)\s*\{\s*:root:not\(\[data-theme=""light""\]\)\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        var forced = Regex.Match(css, @":root\[data-theme=""dark""\]\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(light);
        Assert.NotEmpty(auto);
        Assert.NotEmpty(forced);
        return (light, auto, forced);
    }

    private static string Token(string block, string name)
    {
        var m = Regex.Match(block, @"(?<![\w-])" + Regex.Escape(name) + @":\s*(#[0-9a-fA-F]{6})");
        Assert.True(m.Success, "token " + name + " missing");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    [Fact]
    public void Dark_theme_uses_the_neutral_grey_tokens_in_both_dark_blocks()
    {
        var (_, auto, forced) = Blocks();
        foreach (var block in new[] { auto, forced })
        {
            Assert.Equal("#1a1b1d", Token(block, "--bg"));
            Assert.Equal("#222326", Token(block, "--surface"));
            Assert.Equal("#3a3c41", Token(block, "--border"));
            Assert.Equal("#e8eaed", Token(block, "--text"));
            Assert.Equal("#9aa0a8", Token(block, "--muted"));
            Assert.Equal("#4da3ff", Token(block, "--accent"));
            Assert.Equal("#5aa9ff", Token(block, "--link"));
            Assert.Equal("#4cbb6c", Token(block, "--ok"));
            Assert.Equal("#e5594f", Token(block, "--bad"));
            Assert.Equal("#e8b04a", Token(block, "--warn"));
        }
    }

    [Fact]
    public void No_warm_palette_values_remain()
    {
        var css = Css().ToLowerInvariant();
        foreach (var old in new[] { "#faf9f8", "#252423", "#f2bd5c", "#1e1e1e", "#f3f2f1", "#201f1e", "#605e5c" })
            Assert.DoesNotContain(old, css);
    }

    [Fact]
    public void Light_theme_is_neutral_grey_with_the_same_blue_hue()
    {
        var (light, _, _) = Blocks();
        foreach (var name in new[] { "--bg", "--surface", "--surface-2", "--border", "--border-strong", "--text", "--muted", "--code-bg" })
        {
            var (r, g, b) = Rgb(Token(light, name));
            // Neutral: no warm tint (red never leads blue by more than a hair), a faint cool cast is fine.
            Assert.True(b >= r - 1 && Math.Abs(r - g) <= 4, name + " is not neutral: " + Token(light, name));
        }
        Assert.InRange(Hue(Token(light, "--accent")), 200, 220);
        Assert.InRange(Hue("#4da3ff"), 200, 220);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("auto-dark")]
    [InlineData("forced-dark")]
    public void Text_muted_and_accent_meet_AA_on_bg_and_surface(string theme)
    {
        var (light, auto, forced) = Blocks();
        var block = theme switch { "light" => light, "auto-dark" => auto, _ => forced };
        foreach (var bgName in new[] { "--bg", "--surface" })
            foreach (var fg in new[] { "--text", "--muted", "--accent", "--link" })
            {
                var ratio = Contrast(Token(block, fg), Token(block, bgName));
                Assert.True(ratio >= 4.5, $"{theme}: {fg} on {bgName} is {ratio:F2}:1");
            }
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (int.Parse(hex[1..3], NumberStyles.HexNumber), int.Parse(hex[3..5], NumberStyles.HexNumber), int.Parse(hex[5..7], NumberStyles.HexNumber));

    private static double Hue(string hex)
    {
        var (r, g, b) = Rgb(hex);
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var d = max - Math.Min(rf, Math.Min(gf, bf));
        double h = max == rf ? ((gf - bf) / d) % 6 : max == gf ? (bf - rf) / d + 2 : (rf - gf) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double C(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
