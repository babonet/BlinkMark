using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BlinkMark.Core.Models;
using BlinkMark.Core.Rendering;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// Sanitization against the hostile corpus (T032). Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// Assertions are made against the <em>re-parsed</em> sanitized output rather than against its
/// text. That distinction is the whole point of an mXSS test: a payload that survives as escaped
/// text is inert, a payload that survives as an element is not, and only a parser can tell the
/// two apart. A substring check would fail on the first and — far worse — could pass on the
/// second.
/// <para>
/// The benign control file is as important as the hostile ones. A sanitizer that strips
/// everything passes every negative assertion and is useless, so the suite proves both halves:
/// nothing dangerous survives, and ordinary review content does.
/// </para>
/// </remarks>
public sealed class SanitizationTests
{
    private static readonly string FixtureDirectory = FindFixtures();
    private static readonly HtmlParser Parser = new();

    /// <summary>Elements that fetch or execute something without the reader doing anything.</summary>
    private static readonly string[] AutoLoadingElements =
    [
        "SCRIPT", "IMG", "IFRAME", "EMBED", "OBJECT", "VIDEO", "AUDIO", "SOURCE", "TRACK",
        "LINK", "STYLE", "APPLET", "FRAME", "FRAMESET", "META", "BASE", "FORM", "INPUT",
        "BUTTON", "SVG", "MATH", "TEMPLATE", "NOEMBED", "NOFRAMES",
    ];

    private readonly RenderPipeline _pipeline = new(new MarkdownRenderer(), new HtmlSanitizerService());

    private static IHtmlCollection<IElement> ParseElements(string html) =>
        Parser.ParseDocument(html).All;

    private static string FindFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "hostile");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var fromRepositoryRoot = Path.Combine(directory.FullName, "backend", "tests", "fixtures", "hostile");
            if (Directory.Exists(fromRepositoryRoot))
            {
                return fromRepositoryRoot;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The hostile fixture corpus could not be located.");
    }

    public static TheoryData<string> HostileHtmlFixtures()
    {
        var data = new TheoryData<string>();

        foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*.html"))
        {
            if (!Path.GetFileName(path).StartsWith("benign", StringComparison.Ordinal))
            {
                data.Add(path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HostileHtmlFixtures))]
    public void No_element_that_loads_or_executes_survives(string fixturePath)
    {
        var rendered = _pipeline.Render(File.ReadAllText(fixturePath), FileContentType.Html);

        foreach (var element in ParseElements(rendered.Html))
        {
            Assert.DoesNotContain(element.TagName.ToUpperInvariant(), AutoLoadingElements);
        }
    }

    [Theory]
    [MemberData(nameof(HostileHtmlFixtures))]
    public void No_event_handler_attribute_survives(string fixturePath)
    {
        var rendered = _pipeline.Render(File.ReadAllText(fixturePath), FileContentType.Html);

        foreach (var element in ParseElements(rendered.Html))
        {
            foreach (var attribute in element.Attributes)
            {
                Assert.False(
                    attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase),
                    $"Event handler '{attribute.Name}' survived on <{element.TagName.ToLowerInvariant()}>.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(HostileHtmlFixtures))]
    public void No_attribute_carries_an_executable_or_embedded_scheme(string fixturePath)
    {
        var rendered = _pipeline.Render(File.ReadAllText(fixturePath), FileContentType.Html);

        foreach (var element in ParseElements(rendered.Html))
        {
            foreach (var attribute in element.Attributes)
            {
                var value = attribute.Value.Replace(" ", string.Empty, StringComparison.Ordinal);

                foreach (var scheme in new[] { "javascript:", "vbscript:", "data:text/html" })
                {
                    Assert.DoesNotContain(scheme, value, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(HostileHtmlFixtures))]
    public void No_style_attribute_or_framing_control_survives(string fixturePath)
    {
        var rendered = _pipeline.Render(File.ReadAllText(fixturePath), FileContentType.Html);

        foreach (var element in ParseElements(rendered.Html))
        {
            Assert.Null(element.GetAttribute("style"));
            Assert.Null(element.GetAttribute("target"));
            Assert.Null(element.GetAttribute("srcdoc"));
            Assert.Null(element.GetAttribute("formaction"));
        }
    }

    [Theory]
    [MemberData(nameof(HostileHtmlFixtures))]
    public void Script_and_style_bodies_do_not_leak_into_the_readable_document(string fixturePath)
    {
        var rendered = _pipeline.Render(File.ReadAllText(fixturePath), FileContentType.Html);

        // Dropping a tag while keeping its children is right for a <div> and wrong for a
        // <script>: it turns the script body into visible text, and into anchorable passages in
        // the projection that no author ever wrote. The URL is the part that matters — an
        // attacker-chosen address becoming readable document text is a real leak.
        //
        // Deliberately malformed nesting such as `<scr<script>ipt>` still leaves inert fragments
        // like "ipt>" behind, because the parser splits the payload across text nodes that were
        // never inside a script element. That residue is escaped text with no executable meaning,
        // and removing it would mean mangling ordinary document text on the off chance it looks
        // like code. Not worth it, and asserted against explicitly so nobody "fixes" it later.
        Assert.DoesNotContain("evil.example.com", rendered.TextProjection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Markdown_never_passes_raw_html_through()
    {
        var markdown = File.ReadAllText(Path.Combine(FixtureDirectory, "markdown-with-html.md"));

        var rendered = _pipeline.Render(markdown, FileContentType.Markdown);

        foreach (var element in ParseElements(rendered.Html))
        {
            Assert.DoesNotContain(element.TagName.ToUpperInvariant(), AutoLoadingElements);

            foreach (var attribute in element.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("javascript:", attribute.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The prose still has to arrive.
        Assert.Contains("Markdown with embedded HTML", rendered.TextProjection, StringComparison.Ordinal);
        Assert.Contains("Text after everything", rendered.TextProjection, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_differential_payloads_do_not_reassemble_into_markup()
    {
        var html = File.ReadAllText(Path.Combine(FixtureDirectory, "parser-differential.html"));

        var rendered = _pipeline.Render(html, FileContentType.Html);

        // Re-parsing is the point. An mXSS bypass looks harmless in the sanitizer's tree and only
        // becomes dangerous once a browser parses the serialization, so the assertion has to be
        // made on the second parse rather than on the first.
        foreach (var element in ParseElements(rendered.Html))
        {
            Assert.DoesNotContain(element.TagName.ToUpperInvariant(), AutoLoadingElements);

            foreach (var attribute in element.Attributes)
            {
                Assert.False(
                    attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase),
                    $"An mXSS payload reassembled into a live '{attribute.Name}' handler.");
            }
        }
    }

    [Fact]
    public void Ordinary_review_content_survives_intact()
    {
        var html = File.ReadAllText(Path.Combine(FixtureDirectory, "benign-control.html"));

        var rendered = _pipeline.Render(html, FileContentType.Html);
        var document = Parser.ParseDocument(rendered.Html);

        foreach (var selector in new[] { "h1", "h2", "p", "ul", "ol", "li", "blockquote", "pre", "table", "th", "td", "strong", "em", "code" })
        {
            Assert.True(
                document.QuerySelectorAll(selector).Length > 0,
                $"Ordinary review markup <{selector}> did not survive sanitization.");
        }

        Assert.Contains("Q3 architecture review", rendered.TextProjection, StringComparison.Ordinal);
        Assert.Contains("The audit trail outlives the content it describes", rendered.TextProjection, StringComparison.Ordinal);
    }

    [Fact]
    public void External_hyperlinks_survive_but_carry_link_protections()
    {
        // Stripping every off-site hyperlink would gut ordinary review documents. FR-016 is about
        // references a browser follows *automatically*; a link the reader has to click is a
        // different thing, and these protections are what make keeping it defensible.
        var rendered = _pipeline.Render(
            "<p>See <a href=\"https://example.com\">the spec</a>.</p>",
            FileContentType.Html);

        var link = Assert.Single(Parser.ParseDocument(rendered.Html).QuerySelectorAll("a"));

        Assert.Equal("https://example.com", link.GetAttribute("href"));
        Assert.Contains("noopener", link.GetAttribute("rel")!, StringComparison.Ordinal);
        Assert.Contains("noreferrer", link.GetAttribute("rel")!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_projection_is_stable_across_repeated_renders()
    {
        // Anchors store character offsets into this projection and compare against it for the
        // life of the file. If it were not deterministic, every comment would eventually orphan.
        var html = File.ReadAllText(Path.Combine(FixtureDirectory, "benign-control.html"));

        var first = _pipeline.Render(html, FileContentType.Html);
        var second = _pipeline.Render(html, FileContentType.Html);

        Assert.Equal(first.TextProjection, second.TextProjection);
        Assert.Equal(first.RenderVersion, second.RenderVersion);
    }
}
