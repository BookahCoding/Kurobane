using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace Kurobane.App;

/// <summary>
/// Minimal Markdown renderer: fenced code blocks become monospace paragraphs,
/// the rest is inline **bold**, *italic*, `code` and [links](url).
/// Deliberately small — this is a chat client, not a browser.
/// </summary>
public static partial class MarkdownLite
{
    public static List<Block> Render(string markdown)
    {
        var blocks = new List<Block>();
        using var reader = new StringReader(markdown.Replace("\r\n", "\n"));
        var paragraph = new StringBuilder();
        string? line;
        var inCode = false;
        var code = new StringBuilder();
        void FlushParagraph()
        {
            var text = paragraph.ToString().Trim('\n');
            paragraph.Clear();
            if (text.Length > 0)
            {
                var p = new Paragraph { Margin = new System.Windows.Thickness(0, 2, 0, 2) };
                foreach (var inline in RenderInlines(text)) p.Inlines.Add(inline);
                blocks.Add(p);
            }
        }
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    inCode = false;
                    FlushParagraph();
                    blocks.Add(new Paragraph(new Run(code.ToString().TrimEnd('\n'))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                    })
                    {
                        Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                        Padding = new System.Windows.Thickness(8),
                        Margin = new System.Windows.Thickness(0, 4, 0, 4),
                    });
                    code.Clear();
                }
                else { FlushParagraph(); inCode = true; }
                continue;
            }
            if (inCode) { code.AppendLine(line); continue; }
            if (line.Trim().Length == 0) { FlushParagraph(); continue; }
            paragraph.AppendLine(line);
        }
        FlushParagraph();
        if (inCode && code.Length > 0)
            blocks.Add(new Paragraph(new Run(code.ToString().TrimEnd('\n')) { FontFamily = new FontFamily("Consolas") }));
        if (blocks.Count == 0) blocks.Add(new Paragraph());
        return blocks;
    }

    /// <summary>Inline-level rendering for a single paragraph of markdown.</summary>
    public static List<Inline> RenderInlines(string text)
    {
        var inlines = new List<Inline>();
        var pattern = InlinePattern();
        var pos = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            var g = m.Groups;
            if (g["bold"].Success) inlines.Add(new Bold(new Run(g["bold"].Value)));
            else if (g["italic"].Success) inlines.Add(new Italic(new Run(g["italic"].Value)));
            else if (g["code"].Success) inlines.Add(new Run(g["code"].Value) { FontFamily = new FontFamily("Consolas") });
            else if (g["link"].Success)
            {
                var link = new Hyperlink(new Run(g["text"].Value)) { NavigateUri = new Uri(g["url"].Value) };
                link.RequestNavigate += (_, e) => { if (Uri.TryCreate(e.Uri.AbsoluteUri, UriKind.Absolute, out _)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); };
                inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
        if (inlines.Count == 0) inlines.Add(new Run(""));
        return inlines;
    }

    [GeneratedRegex(@"(?<bold>\*\*(?<bold>[^*]+)\*\*)|(?<italic>\*(?<italic>[^*]+)\*)|(?<code>`(?<code>[^`]+)`)|(?<link>\[(?<text>[^\]]+)\]\((?<url>https?://[^)]+)\))", RegexOptions.Compiled)]
    private static partial Regex InlinePattern();
}
