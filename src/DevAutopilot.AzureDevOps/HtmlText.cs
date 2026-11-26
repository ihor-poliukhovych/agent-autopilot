using System.Net;
using System.Text;

namespace DevAutopilot.AzureDevOps;

/// <summary>
/// Turns the HTML of a rich-text work item field into plain text: tags are dropped, entities decoded,
/// line breaks and paragraphs kept, list items written as "- item" or "1. item". Script and style
/// content is removed. A single forward pass over the text, no regular expressions.
/// </summary>
public static class HtmlText
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "blockquote", "pre", "table", "tr",
        "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "dl", "dt", "dd",
    };

    private static readonly HashSet<string> SkipContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "template", "noscript",
    };

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var output = new StringBuilder(html.Length);
        var lists = new Stack<(bool Ordered, int Count)>();
        var preDepth = 0;
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                var next = html.IndexOf('<', i);
                var end = next < 0 ? html.Length : next;
                AppendText(output, WebUtility.HtmlDecode(html[i..end]), preDepth > 0);
                i = end;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            var opener = i + 1 < html.Length ? html[i + 1] : ' ';
            if (!char.IsAsciiLetter(opener) && opener is not ('/' or '!'))
            {
                // "a < b" is text, not a tag.
                AppendText(output, "<", preDepth > 0);
                i++;
                continue;
            }

            var tagEnd = FindTagEnd(html, i + 1);
            if (tagEnd < 0)
            {
                // A '<' that never closes is text.
                AppendText(output, html[i..], preDepth > 0);
                break;
            }

            var (name, closing) = ReadTagName(html, i + 1, tagEnd);
            i = tagEnd + 1;
            if (name.Length == 0)
            {
                continue;
            }

            if (!closing && SkipContentTags.Contains(name))
            {
                var closeTag = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (closeTag < 0)
                {
                    i = html.Length;
                }
                else
                {
                    var after = FindTagEnd(html, closeTag + 2);
                    i = after < 0 ? html.Length : after + 1;
                }

                continue;
            }

            switch (name.ToLowerInvariant())
            {
                case "br":
                    output.Append('\n');
                    break;
                case "li":
                    if (!closing)
                    {
                        NewLine(output);
                        if (lists.Count > 0 && lists.Peek().Ordered)
                        {
                            var (ordered, count) = lists.Pop();
                            lists.Push((ordered, count + 1));
                            output.Append(count + 1).Append(". ");
                        }
                        else
                        {
                            output.Append("- ");
                        }
                    }
                    else
                    {
                        NewLine(output);
                    }

                    break;
                case "ul":
                case "ol":
                    if (closing)
                    {
                        if (lists.Count > 0)
                        {
                            lists.Pop();
                        }
                    }
                    else
                    {
                        lists.Push((name.Equals("ol", StringComparison.OrdinalIgnoreCase), 0));
                    }

                    BlankLine(output);
                    break;
                case "pre":
                    preDepth = closing ? Math.Max(0, preDepth - 1) : preDepth + 1;
                    BlankLine(output);
                    break;
                case "td":
                case "th":
                    if (closing)
                    {
                        output.Append(' ');
                    }

                    break;
                default:
                    if (BlockTags.Contains(name))
                    {
                        BlankLine(output);
                    }

                    break;
            }
        }

        return Normalize(output.ToString());
    }

    private static int FindTagEnd(string html, int start)
    {
        char? quote = null;
        for (var j = start; j < html.Length; j++)
        {
            var c = html[j];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return j;
            }
            else if (c == '<' && j == start)
            {
                return -1;
            }
        }

        return -1;
    }

    private static (string Name, bool Closing) ReadTagName(string html, int start, int end)
    {
        var j = start;
        var closing = false;
        if (j < end && html[j] == '/')
        {
            closing = true;
            j++;
        }

        var nameStart = j;
        while (j < end && (char.IsAsciiLetterOrDigit(html[j]) || html[j] == '-'))
        {
            j++;
        }

        return (html[nameStart..j], closing);
    }

    private static void AppendText(StringBuilder output, string text, bool preformatted)
    {
        if (preformatted)
        {
            output.Append(text.Replace("\r\n", "\n", StringComparison.Ordinal));
            return;
        }

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) && ch != ' ')
            {
                if (output.Length > 0 && output[^1] is not (' ' or '\n'))
                {
                    output.Append(' ');
                }
            }
            else
            {
                output.Append(ch == ' ' ? ' ' : ch);
            }
        }
    }

    private static void NewLine(StringBuilder output)
    {
        TrimTrailingSpaces(output);
        if (output.Length > 0 && output[^1] != '\n')
        {
            output.Append('\n');
        }
    }

    private static void BlankLine(StringBuilder output)
    {
        NewLine(output);
        if (output.Length > 0 && (output.Length < 2 || output[^2] != '\n'))
        {
            output.Append('\n');
        }
    }

    private static void TrimTrailingSpaces(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] == ' ')
        {
            output.Length--;
        }
    }

    private static string Normalize(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(blank > 0 ? "\n\n" : "\n");
            }

            result.Append(line);
            blank = 0;
        }

        return result.ToString();
    }
}
