using BlinkMark.Core.Models;
using BlinkMark.Core.Rendering;

namespace BlinkMark.UnitTests;

/// <summary>
/// The structured projection (FR-017, research.md R2).
/// </summary>
/// <remarks>
/// The first test is the one that matters. Comments store character offsets into the flat text
/// projection, and the structured projection describes the *same* characters. If the two ever
/// disagree by so much as one space, every offset in every stored comment on every file shifts,
/// and comments silently start pointing at the wrong words. That failure is invisible — nothing
/// throws, the anchors still resolve, they just resolve somewhere else — which is exactly why it
/// is asserted here rather than trusted.
/// </remarks>
public sealed class DocumentProjectionTests
{
    private static readonly RenderPipeline Pipeline = new(new MarkdownRenderer(), new HtmlSanitizerService());

    private static (string Text, IReadOnlyList<DocumentBlock> Blocks) Project(string markdown)
    {
        var render = Pipeline.Render(markdown, FileContentType.Markdown);
        return (render.TextProjection, DocumentProjection.Project(render.Html));
    }

    [Theory]
    [InlineData("# Title\n\nA paragraph.\n\nAnother one.")]
    [InlineData("## Heading\n\n- first\n- second\n- third")]
    [InlineData("1. one\n2. two\n\n> A quotation.\n\nAfter the quote.")]
    [InlineData("Text with **bold** and _italic_ and `code` inline.")]
    [InlineData("```\nconst x = 1;\n```\n\nAfter the code.")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
    [InlineData("Just one line.")]
    [InlineData("# A\n## B\n### C\n#### D\n##### E\n###### F")]
    public void The_blocks_reconstruct_the_flat_projection_exactly(string markdown)
    {
        var (text, blocks) = Project(markdown);

        Assert.Equal(text, DocumentProjection.ToText(blocks));
    }

    [Theory]
    [InlineData("# Title\n\nA paragraph.\n\n- a list item\n\n> a quote")]
    [InlineData("## Heading\n\nSome prose with a [link](https://example.invalid) in it.")]
    public void Every_block_offset_indexes_the_passage_it_claims(string markdown)
    {
        var (text, blocks) = Project(markdown);

        // The property anchoring depends on: slicing the projection at a block's offsets must
        // return that block's text. Without it, selecting a heading would comment on a paragraph.
        foreach (var block in blocks)
        {
            Assert.Equal(block.Text, text[block.Start..block.End]);
        }
    }

    [Fact]
    public void Headings_keep_their_level()
    {
        var (_, blocks) = Project("# One\n\n### Three");

        Assert.Collection(
            blocks,
            first =>
            {
                Assert.Equal(DocumentBlockType.Heading, first.Type);
                Assert.Equal(1, first.Level);
            },
            second =>
            {
                Assert.Equal(DocumentBlockType.Heading, second.Type);
                Assert.Equal(3, second.Level);
            });
    }

    [Fact]
    public void List_items_are_marked_and_know_whether_they_are_numbered()
    {
        var (_, unordered) = Project("- alpha\n- beta");
        var (_, ordered) = Project("1. alpha\n2. beta");

        Assert.All(unordered, block => Assert.Equal(DocumentBlockType.ListItem, block.Type));
        Assert.All(unordered, block => Assert.False(block.Ordered));

        Assert.All(ordered, block => Assert.Equal(DocumentBlockType.ListItem, block.Type));
        Assert.All(ordered, block => Assert.True(block.Ordered));
    }

    [Fact]
    public void A_quotation_is_marked_as_one()
    {
        var (_, blocks) = Project("> Quoted.\n\nNot quoted.");

        Assert.Equal(DocumentBlockType.Quote, blocks[0].Type);
        Assert.Equal(DocumentBlockType.Paragraph, blocks[1].Type);
    }

    [Fact]
    public void Nested_blocks_are_emitted_once()
    {
        // A blockquote containing a paragraph must produce one block, not two. Emitting both
        // would duplicate the text and create overlapping offsets that no anchor could resolve.
        var (text, blocks) = Project("> A quoted paragraph.");

        Assert.Single(blocks);
        Assert.Equal(text, blocks[0].Text);
    }

    [Fact]
    public void A_hostile_document_contributes_text_and_nothing_else()
    {
        // The security property this whole design rests on. The client renders blocks with its
        // own components, so the only thing an uploaded document can influence is the text and a
        // choice from a fixed enum — never an element, an attribute, or a URL.
        var render = Pipeline.Render(
            "<script>alert(1)</script><p onclick=\"steal()\">Ordinary text.</p><img src=x onerror=alert(2)>",
            FileContentType.Html);

        var blocks = DocumentProjection.Project(render.Html);

        Assert.All(blocks, block => Assert.DoesNotContain("alert", block.Text, StringComparison.OrdinalIgnoreCase));
        Assert.All(blocks, block => Assert.DoesNotContain("onerror", block.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(blocks, block => block.Text.Contains("Ordinary text.", StringComparison.Ordinal));

        // Every type is one this application knows how to render.
        Assert.All(blocks, block => Assert.True(Enum.IsDefined(block.Type)));
    }

    [Fact]
    public void An_empty_document_produces_no_blocks()
    {
        Assert.Empty(DocumentProjection.Project(string.Empty));
        Assert.Empty(DocumentProjection.Project("<p>   </p>"));
    }
}
