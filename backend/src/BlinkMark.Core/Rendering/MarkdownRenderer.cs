using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace BlinkMark.Core.Rendering;

/// <summary>
/// Renders Markdown to HTML (T036).
/// </summary>
/// <remarks>
/// Raw HTML is disabled outright. Markdig can be configured to pass HTML blocks through, and
/// that is exactly the configuration this product must not use: a Markdown upload containing a
/// <c>&lt;script&gt;</c> tag would otherwise take the "safe" path and arrive at the sanitizer
/// with the payload already assembled.
/// <para>
/// The output still goes through the sanitizer afterwards. Disabling raw HTML here is defence in
/// depth, not the control — the control is the allowlist. Markdown can still produce a
/// <c>javascript:</c> link through ordinary link syntax, which is a Markdig feature rather than
/// an HTML passthrough, and only the sanitizer catches that.
/// </para>
/// </remarks>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            // Tables, footnotes, task lists, and definition lists: the things reviewers actually
            // use. Notably absent are the extensions that emit script or arbitrary attributes.
            .UsePipeTables()
            .UseGridTables()
            .UseFootnotes()
            .UseTaskLists()
            .UseDefinitionLists()
            .UseEmphasisExtras()
            .UseListExtras()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)

            // Disables raw HTML blocks and inline HTML. Everything the author wrote as markup
            // becomes literal text instead.
            .DisableHtml()

            .Build();
    }

    /// <summary>The renderer version, pinned per file so a file always renders identically.</summary>
    public const string Version = "markdig-0.38";

    public string ToHtml(string markdown) => Markdown.ToHtml(markdown ?? string.Empty, _pipeline);
}
