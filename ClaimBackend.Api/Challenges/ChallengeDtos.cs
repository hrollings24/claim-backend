using System.ComponentModel.DataAnnotations;

namespace ClaimBackend.Api.Challenges;

/// <summary>
/// Further details are returned with the list rather than fetched on demand: the modal opens
/// from data the page already has, so opening one costs no round trip.
/// </summary>
public record ChallengeDto(
    string Id,
    string Type,
    string Title,
    string Summary,
    string FurtherDetails,
    /// <summary>Steal only. Null means the default — the challenge predates this setting.</summary>
    int? StealMinutes,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    /// <summary>Whether the caller wrote it, and may therefore change or remove it.</summary>
    bool IsYours);

public record CreateChallengeRequest(
    [Required] ChallengeType Type,
    [Required, StringLength(120, MinimumLength = 1)] string Title,
    [Required, StringLength(300, MinimumLength = 1)] string Summary,
    [Required, StringLength(4000, MinimumLength = 1)] string FurtherDetails,
    [Range(1, 120)] int? StealMinutes,
    string? DisplayName);

/// <summary>
/// Authorship isn't part of an edit — who wrote a challenge doesn't change because they fixed
/// a typo — so it carries no display name.
/// </summary>
public record UpdateChallengeRequest(
    [Required] ChallengeType Type,
    [Required, StringLength(120, MinimumLength = 1)] string Title,
    [Required, StringLength(300, MinimumLength = 1)] string Summary,
    [Required, StringLength(4000, MinimumLength = 1)] string FurtherDetails,
    [Range(1, 120)] int? StealMinutes);
