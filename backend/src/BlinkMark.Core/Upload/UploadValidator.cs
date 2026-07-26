using System.Text;
using BlinkMark.Core.Models;

namespace BlinkMark.Core.Upload;

/// <summary>Why an upload was refused.</summary>
public enum UploadRejection
{
    None,
    Empty,
    TooLarge,
    UnsupportedExtension,
    ContentDoesNotMatchExtension,
    NotText,
}

/// <summary>The outcome of validating an upload.</summary>
public sealed record UploadValidationResult
{
    public required bool IsValid { get; init; }

    public required UploadRejection Rejection { get; init; }

    public string? Message { get; init; }

    /// <summary>The content type determined from the bytes, not from the filename.</summary>
    public FileContentType ContentType { get; init; }

    public string Content { get; init; } = string.Empty;

    public static UploadValidationResult Valid(FileContentType contentType, string content) => new()
    {
        IsValid = true,
        Rejection = UploadRejection.None,
        ContentType = contentType,
        Content = content,
    };

    public static UploadValidationResult Invalid(UploadRejection rejection, string message) => new()
    {
        IsValid = false,
        Rejection = rejection,
        Message = message,
    };
}

/// <summary>
/// Validates an upload before anything is stored (T034).
/// </summary>
/// <remarks>
/// Four checks, and the fourth is the one that matters.
/// <para>
/// Size and extension are ordinary input validation. Text-ness rules out binaries dressed as
/// documents. But <strong>extension versus content agreement</strong> exists because the two
/// formats take different rendering paths: Markdown is rendered with raw HTML disabled, HTML is
/// not. A file accepted as Markdown on the strength of its name, while actually containing HTML,
/// would take the safe path <em>around</em> the content that path is designed to neutralise.
/// </para>
/// <para>
/// FR-007 therefore says the content type is determined from validated content. This class does
/// that, and refuses the disagreement rather than silently picking a winner — because either
/// choice would be a guess about what the uploader meant, and one of the guesses is dangerous.
/// </para>
/// </remarks>
public sealed class UploadValidator(long maxSizeBytes = 10 * 1024 * 1024)
{
    private static readonly string[] HtmlExtensions = [".html", ".htm"];
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    private readonly long _maxSizeBytes = maxSizeBytes;

    public UploadValidationResult Validate(string fileName, Stream content)
    {
        if (content.CanSeek && content.Length == 0)
        {
            return UploadValidationResult.Invalid(UploadRejection.Empty, "The file is empty.");
        }

        if (content.CanSeek && content.Length > _maxSizeBytes)
        {
            return UploadValidationResult.Invalid(
                UploadRejection.TooLarge,
                $"The file is larger than the {_maxSizeBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var declared = ClassifyExtension(extension);

        if (declared is null)
        {
            return UploadValidationResult.Invalid(
                UploadRejection.UnsupportedExtension,
                "BlinkMark accepts .html, .htm, .md, and .markdown files.");
        }

        string text;
        using (var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            text = reader.ReadToEnd();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return UploadValidationResult.Invalid(UploadRejection.Empty, "The file is empty.");
        }

        if (ContainsControlCharacters(text))
        {
            return UploadValidationResult.Invalid(
                UploadRejection.NotText,
                "The file does not appear to be a text document.");
        }

        var actual = ClassifyContent(text);

        if (actual != declared)
        {
            // Named rather than resolved. Silently treating an .md file full of HTML as HTML
            // would be a security decision made on the uploader's behalf, and treating it as
            // Markdown would render markup as literal text — neither is what they meant.
            return UploadValidationResult.Invalid(
                UploadRejection.ContentDoesNotMatchExtension,
                $"The file is named as {declared.Value.ToString().ToLowerInvariant()} but its contents look like "
                + $"{actual.ToString().ToLowerInvariant()}. Rename it to match, or upload it in the format it is written in.");
        }

        return UploadValidationResult.Valid(actual, text);
    }

    private static FileContentType? ClassifyExtension(string extension)
    {
        if (HtmlExtensions.Contains(extension))
        {
            return FileContentType.Html;
        }

        return MarkdownExtensions.Contains(extension) ? FileContentType.Markdown : null;
    }

    /// <summary>
    /// Classifies content by looking for HTML document structure.
    /// </summary>
    /// <remarks>
    /// Markdown legitimately contains occasional inline HTML, so the presence of a tag is not
    /// enough to call something HTML — the test is for <em>document</em> structure: a doctype, or
    /// an <c>html</c>, <c>head</c>, or <c>body</c> element. A Markdown file with a stray
    /// <c>&lt;br&gt;</c> stays Markdown; a file that opens with <c>&lt;!doctype html&gt;</c> does
    /// not.
    /// </remarks>
    private static FileContentType ClassifyContent(string text)
    {
        var head = text.Length > 4096 ? text[..4096] : text;
        var trimmed = head.TrimStart();

        if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return FileContentType.Html;
        }

        string[] structuralMarkers = ["<html", "<head", "<body"];
        if (structuralMarkers.Any(marker => head.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return FileContentType.Html;
        }

        return FileContentType.Markdown;
    }

    private static bool ContainsControlCharacters(string text)
    {
        // A sample is enough: a binary that reaches 4 KB without a control character is not one.
        var sample = text.Length > 4096 ? text[..4096] : text;

        foreach (var character in sample)
        {
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            {
                return true;
            }
        }

        return false;
    }
}
