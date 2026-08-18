using System.ComponentModel.DataAnnotations;

namespace ClaimBackend.Api.Challenges;

/// <summary>
/// Further details are returned with the list rather than fetched on demand: the modal opens
/// from data the page already has, so opening one costs no round trip.
/// </summary>
public record ChallengeDto(
    string Id,
    string Title,
    string Summary,
    string FurtherDetails,
    string CreatedByName,
    DateTimeOffset CreatedAt);

public record ChallengePageDto(IReadOnlyList<ChallengeDto> Challenges, string? NextCursor);

public record CreateChallengeRequest(
    [property: Required, StringLength(120, MinimumLength = 1)] string Title,
    [property: Required, StringLength(300, MinimumLength = 1)] string Summary,
    [property: Required, StringLength(4000, MinimumLength = 1)] string FurtherDetails,
    string? DisplayName);
