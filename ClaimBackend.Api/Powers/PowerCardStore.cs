using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ClaimBackend.Api.Challenges;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Powers;

public enum PowerCardMutationStatus
{
    Success,
    NotFound,
    BuiltIn,
    InvalidKind,
}

/// <summary>
/// Player-written power cards, in the challenges table under a partition of their own — the same
/// arrangement as <see cref="PowerConfigurationStore"/>, for the same reasons. Anyone may add,
/// edit or delete one. A game copies the wording of every card in its deck when it starts, so
/// none of that reaches a game already under way.
/// </summary>
public class PowerCardStore(IAmazonDynamoDB dynamo, IOptions<ChallengesOptions> options)
{
    private const string Partition = "POWER_CARD";

    private const string PartitionAttribute = "Kind";
    private const string SortAttribute = "CreatedAtId";

    /// <summary>
    /// Sort key of a row recording that <see cref="PowerCatalogue.Seed"/> has been written. Its
    /// absence, not an empty partition, is what triggers seeding — otherwise deleting every card
    /// would bring the originals back.
    /// </summary>
    private const string SeededMarker = "SEEDED";

    private readonly ChallengesOptions _options = options.Value;

    /// <summary>Built-in cards first, then everyone else's, oldest first.</summary>
    public async Task<IReadOnlyList<PowerDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await QueryAllAsync(cancellationToken);

        if (!items.Any(IsMarker))
        {
            await SeedAsync(cancellationToken);
            items = await QueryAllAsync(cancellationToken);
        }

        return PowerCatalogue.BuiltIn
            .Concat(items.Where(item => !IsMarker(item)).Select(FromItem))
            .ToList();
    }

    public async Task<PowerDefinition?> FindAsync(string id, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(c => c.Id == id);

    /// <param name="durationMinutes">Kept for a curse only; a boost has nothing to time.</param>
    public async Task<(PowerCardMutationStatus Status, PowerDefinition? Card)> CreateAsync(
        PowerKind kind, string title, string description, int? durationMinutes, CancellationToken cancellationToken)
    {
        if (!IsWritable(kind))
        {
            return (PowerCardMutationStatus.InvalidKind, null);
        }

        var card = new PowerDefinition(
            Guid.NewGuid().ToString("n"), kind, title, description, DurationFor(kind, durationMinutes));
        await PutAsync(card, SortKeyFor(card.Id, DateTimeOffset.UtcNow), cancellationToken);

        return (PowerCardMutationStatus.Success, card);
    }

    /// <summary>Anyone may edit any card. Last write wins, same as configurations.</summary>
    public async Task<(PowerCardMutationStatus Status, PowerDefinition? Card)> UpdateAsync(
        string id,
        PowerKind kind,
        string title,
        string description,
        int? durationMinutes,
        CancellationToken cancellationToken)
    {
        if (PowerCatalogue.BuiltIn.Any(c => c.Id == id))
        {
            return (PowerCardMutationStatus.BuiltIn, null);
        }

        if (!IsWritable(kind))
        {
            return (PowerCardMutationStatus.InvalidKind, null);
        }

        if (await FindSortKeyAsync(id, cancellationToken) is not { } sortKey)
        {
            return (PowerCardMutationStatus.NotFound, null);
        }

        var card = new PowerDefinition(id, kind, title, description, DurationFor(kind, durationMinutes));
        await PutAsync(card, sortKey, cancellationToken);

        return (PowerCardMutationStatus.Success, card);
    }

    /// <summary>
    /// Configurations that counted it simply stop including it; games already dealt keep their
    /// copy.
    /// </summary>
    public async Task<PowerCardMutationStatus> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (PowerCatalogue.BuiltIn.Any(c => c.Id == id))
        {
            return PowerCardMutationStatus.BuiltIn;
        }

        if (await FindSortKeyAsync(id, cancellationToken) is not { } sortKey)
        {
            return PowerCardMutationStatus.NotFound;
        }

        await dynamo.DeleteItemAsync(
            new DeleteItemRequest { TableName = _options.TableName, Key = KeyFor(sortKey) },
            cancellationToken);

        return PowerCardMutationStatus.Success;
    }

    /// <summary>
    /// The marker goes in first, conditionally, so of two requests racing to seed only one writes
    /// the cards. The loser may briefly list without them; the next read has them.
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = _options.TableName,
                    Item = KeyFor(SeededMarker),
                    ConditionExpression = "attribute_not_exists(#kind)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#kind"] = PartitionAttribute },
                },
                cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var card in PowerCatalogue.Seed)
        {
            await PutAsync(card, SortKeyFor(card.Id, now), cancellationToken);
        }
    }

    private async Task<List<Dictionary<string, AttributeValue>>> QueryAllAsync(CancellationToken cancellationToken)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = _options.TableName,
                    KeyConditionExpression = "#kind = :kind",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#kind"] = PartitionAttribute },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":kind"] = new AttributeValue(Partition),
                    },
                    ExclusiveStartKey = start,
                },
                cancellationToken);

            items.AddRange(response.Items);
            start = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (start is not null);

        return items;
    }

    /// <summary>The key is creation time plus id, so an id alone can't address an item.</summary>
    private async Task<string?> FindSortKeyAsync(string id, CancellationToken cancellationToken) =>
        (await QueryAllAsync(cancellationToken))
            .FirstOrDefault(item => !IsMarker(item) && item["Id"].S == id)?[SortAttribute].S;

    /// <summary>Only honour-system cards can be written; the rest have rules and are built in.</summary>
    private static bool IsWritable(PowerKind kind) => kind is PowerKind.Curse or PowerKind.Boost;

    private static int? DurationFor(PowerKind kind, int? durationMinutes) =>
        kind is PowerKind.Curse ? durationMinutes : null;

    private static bool IsMarker(Dictionary<string, AttributeValue> item) =>
        item[SortAttribute].S == SeededMarker;

    private static string SortKeyFor(string id, DateTimeOffset createdAt) => $"{createdAt:O}#{id}";

    private static Dictionary<string, AttributeValue> KeyFor(string sortKey) => new()
    {
        [PartitionAttribute] = new AttributeValue(Partition),
        [SortAttribute] = new AttributeValue(sortKey),
    };

    private Task PutAsync(PowerDefinition card, string sortKey, CancellationToken cancellationToken)
    {
        var item = KeyFor(sortKey);
        item["Id"] = new AttributeValue(card.Id);
        item["CardKind"] = new AttributeValue(card.Kind.ToString());
        item["Title"] = new AttributeValue(card.Title);
        item["Description"] = new AttributeValue(card.Description);

        if (card.DurationMinutes is { } minutes)
        {
            item["DurationMinutes"] = new AttributeValue { N = minutes.ToString(CultureInfo.InvariantCulture) };
        }

        return dynamo.PutItemAsync(new PutItemRequest { TableName = _options.TableName, Item = item }, cancellationToken);
    }

    /// <summary>"Kind" is the partition key, so the card's own kind is stored as CardKind.</summary>
    private static PowerDefinition FromItem(Dictionary<string, AttributeValue> item) => new(
        item["Id"].S,
        Enum.Parse<PowerKind>(item["CardKind"].S),
        item["Title"].S,
        item["Description"].S,
        item.TryGetValue("DurationMinutes", out var minutes) && minutes.N is { } n
            ? int.Parse(n, CultureInfo.InvariantCulture)
            : null);
}
