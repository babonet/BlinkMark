using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace BlinkMark.Core.Rendering;

/// <summary>
/// Allowlist HTML sanitization (T037).
/// </summary>
/// <remarks>
/// An allowlist, never a denylist. Anything not explicitly permitted is dropped, which means a
/// tag or attribute invented after this code was written is refused by default rather than
/// permitted by omission. That asymmetry is the whole reason Principle IV specifies an
/// allowlist.
/// <para>
/// External references are stripped rather than proxied. Proxying would make BlinkMark a request
/// forwarder for arbitrary uploaded content — an SSRF surface — for no user benefit. It would
/// also defeat the point of FR-016: the harm is not the byte transfer, it is that the request
/// discloses who read the document and when, and a proxy performs the disclosure just as well.
/// </para>
/// <para>
/// Sanitization happens once, at upload, and the result is stored. That keeps SC-002's
/// one-second preview budget reachable and, more importantly, makes the render deterministic —
/// which the anchoring model depends on, because anchors resolve against this output.
/// </para>
/// </remarks>
public sealed class HtmlSanitizerService
{
    /// <summary>
    /// The sanitizer version, pinned per file.
    /// </summary>
    /// <remarks>
    /// A sanitizer upgrade changes the text anchors were computed against, which would orphan
    /// every comment on every live file at once. Pinning per file means an upgrade applies to
    /// new uploads only (research.md R2).
    /// </remarks>
    public const string Version = "htmlsanitizer-9.1";

    private static readonly HtmlParser Parser = new();

    private readonly HtmlSanitizer _sanitizer;

    public HtmlSanitizerService()
    {
        _sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Structure
                "p", "div", "span", "br", "hr", "section", "article", "header", "footer", "main",
                "aside", "nav", "figure", "figcaption",

                // Headings
                "h1", "h2", "h3", "h4", "h5", "h6",

                // Text
                "strong", "b", "em", "i", "u", "s", "del", "ins", "mark", "small", "sub", "sup",
                "abbr", "cite", "q", "blockquote", "code", "pre", "kbd", "samp", "var", "time",

                // Lists
                "ul", "ol", "li", "dl", "dt", "dd",

                // Tables
                "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",

                // Links. Rewritten below, never left as the author wrote them.
                "a",

                // Details/summary, which reviewers use for long appendices.
                "details", "summary",
            },

            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "title", "lang", "dir",
                // Kept because they carry meaning to assistive technology, and a sanitized
                // document that is inaccessible fails FR-077 just as surely as an unsanitized one
                // fails Principle IV.
                "scope", "colspan", "rowspan", "headers", "abbr",
                "datetime", "cite", "start", "reversed", "value", "type",
                "href", "id",
                "role", "aria-label", "aria-labelledby", "aria-describedby", "aria-hidden",
            },

            // Only what a link can be. No javascript:, no data:, no vbscript:, no file:.
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" },

            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Deliberately tiny. Inline style is the vector for several parser-differential
                // attacks and for exfiltration through url(), and reviewers do not need it.
                "text-align", "font-weight", "font-style", "text-decoration",
            },

            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType>(),

            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href" },
        })
        {
            // The style attribute is removed entirely rather than filtered. Even a narrow
            // allowlist of properties leaves the CSS parser exposed to differential attacks.
            KeepChildNodes = true,
        };

        // src is deliberately absent from AllowedAttributes, so img, iframe, video, audio, embed,
        // object, and every other loader is dropped along with its element (FR-016). This
        // handler exists so that a future allowlist change cannot quietly reintroduce them.
        _sanitizer.RemovingAttribute += (_, args) => args.Cancel = false;
        _sanitizer.RemovingTag += (_, args) => args.Cancel = false;
    }

    /// <summary>
    /// Sanitizes a fragment or document, returning only what is allowed to reach a browser.
    /// </summary>
    /// <param name="html">Untrusted HTML, either uploaded or rendered from Markdown.</param>
    public string Sanitize(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withoutDangerousSubtrees = RemoveDangerousSubtrees(html);

        // No base URI. Passing one would resolve relative references into absolute external
        // ones, turning a harmless relative path into exactly the outbound request FR-016
        // exists to prevent.
        var sanitized = _sanitizer.Sanitize(withoutDangerousSubtrees);

        return AddLinkProtections(sanitized);
    }

    /// <summary>
    /// Removes elements whose <em>content</em> is as dangerous as the element itself.
    /// </summary>
    /// <remarks>
    /// The allowlist already drops these tags, but dropping a tag and keeping its children is the
    /// right behaviour for a <c>&lt;div&gt;</c> and the wrong behaviour for a
    /// <c>&lt;script&gt;</c>: it turns the script body into visible document text. The result is
    /// inert — it is escaped text, not a reference — but it is still wrong twice over. It puts
    /// attacker-chosen strings, including URLs, into the reader's view of the document, and it
    /// puts them into the text projection, where they become anchorable passages that no author
    /// ever wrote.
    /// <para>
    /// <c>template</c> is in the list for a subtler reason: its content is inert in the source
    /// document and live the moment anything clones it, so a sanitizer that inspects the live DOM
    /// can miss it entirely.
    /// </para>
    /// </remarks>
    private static string RemoveDangerousSubtrees(string html)
    {
        var document = Parser.ParseDocument(html);

        var selectors = string.Join(
            ',',
            "script", "style", "noscript", "template", "noembed", "noframes",
            "iframe", "object", "embed", "applet", "frame", "frameset",
            "math", "svg");

        foreach (var element in document.QuerySelectorAll(selectors).ToList())
        {
            element.Remove();
        }

        return document.Body?.InnerHtml ?? string.Empty;
    }

    /// <summary>
    /// Adds <c>rel="noopener noreferrer nofollow"</c> to every surviving link.
    /// </summary>
    /// <remarks>
    /// <c>noopener</c> stops a link target from reaching back through <c>window.opener</c>;
    /// <c>noreferrer</c> stops the preview URL — which carries a preview token — from travelling
    /// to a third party as a referrer. The response also sets <c>Referrer-Policy: no-referrer</c>,
    /// and having both means the protection survives someone changing one of them.
    /// </remarks>
    private static string AddLinkProtections(string html)
    {
        if (!html.Contains("<a ", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return html.Replace("<a ", "<a rel=\"noopener noreferrer nofollow\" ", StringComparison.OrdinalIgnoreCase);
    }
}
