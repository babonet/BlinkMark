using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BlinkMark.Core.Rendering;

/// <summary>What kind of thing a block is.</summary>
/// <remarks>
/// A closed set, deliberately. The client renders each of these with its own component, so the
/// only markup that can ever reach the application origin is markup this application wrote. An
/// uploaded document contributes text and a choice from this list — never an element, an
/// attribute, or a URL.
/// </remarks>
public enum DocumentBlockType
{
    Paragraph,
    Heading,
    ListItem,
    Quote,
    Code,
    TableCell,
}

/// <summary>One addressable block of a document.</summary>
public sealed record DocumentBlock
{
    public required DocumentBlockType Type { get; init; }

    /// <summary>Heading level 1-6. Zero for everything else.</summary>
    public int Level { get; init; }

    /// <summary>True when this list item came from an ordered list.</summary>
    public bool Ordered { get; init; }

    public required string Text { get; init; }

    /// <summary>Offset of <see cref="Text"/> within the text projection.</summary>
    public required int Start { get; init; }

    public required int End { get; init; }
}

/// <summary>
/// Projects sanitized HTML into a structured, renderable document.
/// </summary>
/// <remarks>
/// This exists because of a genuine product problem. Anchoring has to happen somewhere the
/// application can observe selection, and the preview iframe is cross-origin and sandboxed by
/// design, so it cannot be that place (research.md R2). The first answer was to show reviewers
/// the raw text projection — which worked, and which nobody wanted to read, because a document
/// stripped of its headings and lists is markedly harder to review than the document itself.
/// <para>
/// The answer is not to put uploaded HTML on the application origin. That would hand a sanitizer
/// bypass the session it is isolated away from, and Principle IV exists precisely to prevent it.
/// Instead the server describes the document as <em>data</em> — a block type from a fixed enum,
/// a level, and text — and the client renders that data with its own components. No uploaded
/// element, attribute, style, or URL crosses into the application origin. The worst a hostile
/// document can achieve is a heading containing unpleasant words.
/// </para>
/// <para>
/// Offsets index into the same text projection that anchors resolve against, so a comment made
/// against the structured view and a comment made by an agent reading the flat text address the
/// same characters. There is still exactly one projection; this adds a description of its shape.
/// </para>
/// </remarks>
public static class DocumentProjection
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Tags that carry text directly and become a block of their own.</summary>
    private static readonly HashSet<string> TextBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "BLOCKQUOTE", "PRE",
        "TD", "TH", "DT", "DD", "FIGCAPTION", "SUMMARY", "CAPTION",
    };

    /// <summary>
    /// Produces the blocks for a document.
    /// </summary>
    /// <remarks>
    /// The concatenation of block texts, joined by newlines, is byte-identical to
    /// <see cref="RenderPipeline.ProjectText"/> for the same input. That is not a coincidence to
    /// be relied on quietly — <c>DocumentProjectionTests</c> asserts it against every fixture,
    /// because the moment the two disagree every offset in every stored comment is wrong.
    /// </remarks>
    public static IReadOnlyList<DocumentBlock> Project(string sanitizedHtml)
    {
        if (string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            return [];
        }

        var document = Parser.ParseDocument(sanitizedHtml);
        var raw = new List<RawBlock>();

        Collect(document.Body ?? document.DocumentElement, raw, new Ancestry());

        var blocks = new List<DocumentBlock>(raw.Count);
        var offset = 0;

        foreach (var block in raw)
        {
            var text = NormalizeInline(block.Text);
            if (text.Length == 0)
            {
                continue;
            }

            // A newline separates blocks, exactly as the flat projection does, so the running
            // offset stays in step with it.
            if (blocks.Count > 0)
            {
                offset += 1;
            }

            blocks.Add(new DocumentBlock
            {
                Type = block.Type,
                Level = block.Level,
                Ordered = block.Ordered,
                Text = text,
                Start = offset,
                End = offset + text.Length,
            });

            offset += text.Length;
        }

        return blocks;
    }

    /// <summary>The flat text these blocks correspond to.</summary>
    public static string ToText(IReadOnlyList<DocumentBlock> blocks) =>
        string.Join('\n', blocks.Select(block => block.Text));

    private static void Collect(INode? node, List<RawBlock> blocks, Ancestry ancestry)
    {
        if (node is null)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                // Text sitting outside any block — a bare string in <body>, for instance. It is
                // still content somebody may want to comment on, so it becomes a paragraph rather
                // than being silently dropped.
                if (!string.IsNullOrWhiteSpace(child.TextContent) && !ancestry.InsideTextBlock)
                {
                    blocks.Add(new RawBlock(DocumentBlockType.Paragraph, 0, false, child.TextContent));
                }

                continue;
            }

            if (child is not IElement element)
            {
                continue;
            }

            var tag = element.TagName;
            var nested = ancestry.Enter(tag);

            // Only the innermost text-bearing block is emitted. Emitting an outer <blockquote>
            // *and* the <p> inside it would duplicate the text and, worse, produce overlapping
            // offsets that no anchor could resolve unambiguously.
            if (TextBlocks.Contains(tag) && !ContainsTextBlock(element))
            {
                blocks.Add(new RawBlock(
                    ClassifyType(tag, nested),
                    HeadingLevel(tag),
                    nested.OrderedList,
                    element.TextContent));

                continue;
            }

            Collect(element, blocks, nested);
        }
    }

    private static bool ContainsTextBlock(IElement element) =>
        element.Children.Any(child => TextBlocks.Contains(child.TagName) || ContainsTextBlock(child));

    private static DocumentBlockType ClassifyType(string tag, Ancestry ancestry)
    {
        if (ancestry.InsidePre || string.Equals(tag, "PRE", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentBlockType.Code;
        }

        if (HeadingLevel(tag) > 0)
        {
            return DocumentBlockType.Heading;
        }

        if (string.Equals(tag, "LI", StringComparison.OrdinalIgnoreCase) || ancestry.InsideListItem)
        {
            return DocumentBlockType.ListItem;
        }

        if (ancestry.InsideQuote || string.Equals(tag, "BLOCKQUOTE", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentBlockType.Quote;
        }

        if (tag.Equals("TD", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("TH", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("CAPTION", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentBlockType.TableCell;
        }

        return DocumentBlockType.Paragraph;
    }

    private static int HeadingLevel(string tag) => tag.ToUpperInvariant() switch
    {
        "H1" => 1,
        "H2" => 2,
        "H3" => 3,
        "H4" => 4,
        "H5" => 5,
        "H6" => 6,
        _ => 0,
    };

    /// <summary>
    /// Collapses whitespace within one block.
    /// </summary>
    /// <remarks>
    /// Must agree character-for-character with the flat projection's normalization, because the
    /// offsets recorded here are compared against that text for the life of the file.
    /// </remarks>
    private static string NormalizeInline(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private readonly record struct RawBlock(DocumentBlockType Type, int Level, bool Ordered, string Text);

    /// <summary>Which enclosing elements a node sits inside.</summary>
    private readonly record struct Ancestry
    {
        public bool InsideQuote { get; init; }

        public bool InsidePre { get; init; }

        public bool InsideListItem { get; init; }

        public bool OrderedList { get; init; }

        public bool InsideTextBlock { get; init; }

        public Ancestry Enter(string tag) => new()
        {
            InsideQuote = InsideQuote || tag.Equals("BLOCKQUOTE", StringComparison.OrdinalIgnoreCase),
            InsidePre = InsidePre || tag.Equals("PRE", StringComparison.OrdinalIgnoreCase),
            InsideListItem = InsideListItem || tag.Equals("LI", StringComparison.OrdinalIgnoreCase),
            OrderedList = tag.Equals("OL", StringComparison.OrdinalIgnoreCase)
                || (OrderedList && !tag.Equals("UL", StringComparison.OrdinalIgnoreCase)),
            InsideTextBlock = InsideTextBlock || TextBlocks.Contains(tag),
        };
    }
}
