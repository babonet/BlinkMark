using BlinkMark.Core.Models;

namespace BlinkMark.Api.Contracts;

/// <summary>An anchor as it crosses the wire.</summary>
public sealed record AnchorRequest
{
    public required string Kind { get; init; }

    public required string Exact { get; init; }

    public string Prefix { get; init; } = string.Empty;

    public string Suffix { get; init; } = string.Empty;

    public int Start { get; init; }

    public int End { get; init; }

    public string? ContainerPath { get; init; }

    public FractionalRectRequest? FractionalRect { get; init; }
}

/// <summary>Normalized offsets within a container element. Values are 0..1.</summary>
public sealed record FractionalRectRequest
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }
}

/// <summary>The body of a comment creation request.</summary>
/// <remarks>
/// Note what is absent: an author. Identity comes from the token and a client-supplied author is
/// ignored, always (FR-005, FR-023). Leaving the field off the contract entirely is stronger than
/// accepting and discarding it, because there is then nothing for a future handler to start
/// trusting.
/// </remarks>
public sealed record CreateCommentRequest
{
    public required string Body { get; init; }

    public required AnchorRequest Anchor { get; init; }

    public string? ParentId { get; init; }
}

/// <summary>The body of a comment edit.</summary>
public sealed record EditCommentRequest
{
    public required string Body { get; init; }
}

/// <summary>A comment as returned to a client.</summary>
public sealed record CommentResponse
{
    public required string Id { get; init; }

    public required string FileId { get; init; }

    public required string ThreadId { get; init; }

    public required string? ParentId { get; init; }

    /// <summary>Literal text. Never markup, never interpreted (FR-024).</summary>
    public required string Body { get; init; }

    public required string AuthorId { get; init; }

    public required string AuthorDisplayName { get; init; }

    /// <summary>Set when an agent wrote this on the author's behalf (FR-048).</summary>
    public required string? ActingAgentId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? EditedAt { get; init; }

    public required AnchorResponse Anchor { get; init; }

    /// <summary><c>anchored</c> or <c>orphaned</c> (FR-022).</summary>
    public required string AnchorState { get; init; }

    public required DateTimeOffset? DeletedAt { get; init; }

    public static CommentResponse From(Comment comment) => new()
    {
        Id = comment.Id,
        FileId = comment.FileId,
        ThreadId = comment.ThreadId,
        ParentId = comment.ParentId,
        Body = comment.Body,
        AuthorId = comment.AuthorId,
        AuthorDisplayName = comment.AuthorDisplayName,
        ActingAgentId = comment.ActingAgentId,
        CreatedAt = comment.CreatedAt,
        EditedAt = comment.EditedAt,
        Anchor = AnchorResponse.From(comment.Anchor),
        AnchorState = comment.AnchorState.ToString().ToLowerInvariant(),
        DeletedAt = comment.DeletedAt,
    };
}

/// <summary>An anchor as returned to a client.</summary>
public sealed record AnchorResponse
{
    public required string Kind { get; init; }

    public required string Exact { get; init; }

    public required string Prefix { get; init; }

    public required string Suffix { get; init; }

    public required int Start { get; init; }

    public required int End { get; init; }

    public required string? ContainerPath { get; init; }

    public required FractionalRectRequest? FractionalRect { get; init; }

    public required string RenderVersion { get; init; }

    public static AnchorResponse From(Anchor anchor) => new()
    {
        Kind = anchor.Kind.ToString().ToLowerInvariant(),
        Exact = anchor.Exact,
        Prefix = anchor.Prefix,
        Suffix = anchor.Suffix,
        Start = anchor.Start,
        End = anchor.End,
        ContainerPath = anchor.ContainerPath,
        FractionalRect = anchor.FractionalRect is null
            ? null
            : new FractionalRectRequest
            {
                X = anchor.FractionalRect.X,
                Y = anchor.FractionalRect.Y,
                Width = anchor.FractionalRect.Width,
                Height = anchor.FractionalRect.Height,
            },
        RenderVersion = anchor.RenderVersion,
    };
}
