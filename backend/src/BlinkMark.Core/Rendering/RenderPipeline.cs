using System.Text;
using AngleSharp.Html.Parser;
using BlinkMark.Core.Models;

namespace BlinkMark.Core.Rendering;

/// <summary>The output of rendering one upload.</summary>
public sealed record RenderResult
{
    /// <summary>The sanitized HTML the preview origin serves.</summary>
    public required string Html { get; init; }

    /// <summary>
    /// The normalized plain-text projection.
    /// </summary>
    /// <remarks>
    /// Anchors resolve against this and agents read it. If the two ever diverged, an agent could
    /// comment on text no human ever saw — which is why there is one projection rather than one
    /// for each.
    /// </remarks>
    public required string TextProjection { get; init; }

    /// <summary>Sanitizer plus renderer version, pinned onto the file.</summary>
    public required string RenderVersion { get; init; }
}

/// <summary>
/// Turns an upload into a stored render and a text projection (T038).
/// </summary>
/// <remarks>
/// One pipeline for both formats. Markdown becomes HTML first, then <em>all</em> HTML — uploaded
/// or generated — passes through the same allowlist sanitizer. Two paths would eventually
/// diverge, and the one that got less attention would be the one an attacker used.
/// </remarks>
public sealed class RenderPipeline(MarkdownRenderer markdown, HtmlSanitizerService sanitizer)
{
    private static readonly HtmlParser Parser = new();

    private readonly MarkdownRenderer _markdown = markdown;
    private readonly HtmlSanitizerService _sanitizer = sanitizer;

    /// <summary>
    /// The current render version.
    /// </summary>
    /// <remarks>
    /// Stored on each file at upload and never recomputed for it. Changing this constant affects
    /// new uploads only, so a sanitizer upgrade cannot orphan the comments on files that are
    /// already live (research.md R2).
    /// </remarks>
    public static string CurrentVersion => $"{HtmlSanitizerService.Version}+{MarkdownRenderer.Version}";

    public RenderResult Render(string content, FileContentType contentType)
    {
        var html = contentType switch
        {
            FileContentType.Markdown => _markdown.ToHtml(content),
            FileContentType.Html => content,
            _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
        };

        var sanitized = _sanitizer.Sanitize(html);

        return new RenderResult
        {
            Html = sanitized,
            // Projected from the *sanitized* output, not from the upload. An anchor computed
            // against text that was later removed could never resolve.
            TextProjection = ProjectText(sanitized),
            RenderVersion = CurrentVersion,
        };
    }

    /// <summary>
    /// Produces the normalized plain-text projection anchors resolve against.
    /// </summary>
    /// <remarks>
    /// Normalization has to be deterministic and stable, because a character offset stored today
    /// is compared against this output for the life of the file. Runs of whitespace collapse to a
    /// single space, block elements are separated by a newline so a quote cannot silently span a
    /// paragraph boundary, and nothing else is touched.
    /// </remarks>
    public static string ProjectText(string sanitizedHtml)
    {
        if (string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            return string.Empty;
        }

        var document = Parser.ParseDocument(sanitizedHtml);
        var builder = new StringBuilder();

        AppendText(document.Body ?? document.DocumentElement, builder);

        return NormalizeWhitespace(builder.ToString());
    }

    private static void AppendText(AngleSharp.Dom.INode? node, StringBuilder builder)
    {
        if (node is null)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case AngleSharp.Dom.NodeType.Text:
                    builder.Append(child.TextContent);
                    break;

                case AngleSharp.Dom.NodeType.Element:
                    var element = (AngleSharp.Dom.IElement)child;

                    if (IsBlock(element.TagName))
                    {
                        builder.Append('\n');
                    }

                    AppendText(element, builder);

                    if (IsBlock(element.TagName))
                    {
                        builder.Append('\n');
                    }

                    break;
            }
        }
    }

    private static bool IsBlock(string tagName) => tagName.ToUpperInvariant() switch
    {
        "P" or "DIV" or "SECTION" or "ARTICLE" or "HEADER" or "FOOTER" or "MAIN" or "ASIDE"
            or "NAV" or "FIGURE" or "FIGCAPTION" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6"
            or "UL" or "OL" or "LI" or "DL" or "DT" or "DD" or "TABLE" or "THEAD" or "TBODY"
            or "TFOOT" or "TR" or "TH" or "TD" or "CAPTION" or "BLOCKQUOTE" or "PRE" or "HR"
            or "BR" or "DETAILS" or "SUMMARY" => true,
        _ => false,
    };

    private static string NormalizeWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        var lastWasNewline = true;

        foreach (var character in text)
        {
            if (character is '\n' or '\r')
            {
                if (!lastWasNewline)
                {
                    builder.Append('\n');
                    lastWasNewline = true;
                    lastWasSpace = false;
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && !lastWasNewline)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
            lastWasNewline = false;
        }

        return builder.ToString().Trim();
    }
}
