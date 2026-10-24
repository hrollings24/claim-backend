using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ClaimBackend.Api.Challenges;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Powers;

public enum PowerConfigurationMutationStatus
{
    Success,
    NotFound,
    DuplicateName,
    InvalidCounts,
}

/// <summary>
/// Power card configurations live in the challenges table, in a partition of their own. They
/// are the same sort of thing — a small, shared, hand-edited list read whole — and the table's
/// partition key is already a kind, so this needs no table, permissions or infrastructure of its
/// own. The sort key follows the challenges' pattern of creation time then id, so the list
/// comes back oldest first.
/// </summary>
public class PowerConfigurationStore(IAmazonDynamoDB dynamo, IOptions<ChallengesOptions> options)
{
    private const string Partition = "POWER_CONFIG";

    private const string PartitionAttribute = "Kind";
    private const string SortAttribute = "CreatedAtId";

    /// <summary>A ceiling on any one card, so a typo can't build a deck of thousands.</summary>
    public const int MaxCopies = 50;

    private readonly ChallengesOptions _options = options.Value;

    public async Task<IReadOnlyList<PowerConfiguration>> ListAsync(CancellationToken cancellationToken)
    {
        var configurations = new List<PowerConfiguration>();
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

            configurations.AddRange(response.Items.Select(FromItem));
            start = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (start is not null);

        return configurations;
    }

    /// <summary>
    /// The key is creation time plus id, so an id alone can't address an item — the partition is
    /// read and filtered instead. There are only ever a handful.
    /// </summary>
    public async Task<PowerConfiguration?> FindAsync(string id, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(c => c.Id == id);

    /// <param name="cardIds">Every card that exists; counts for anything else are refused.</param>
    public async Task<(PowerConfigurationMutationStatus Status, PowerConfiguration? Configuration)> CreateAsync(
        string name,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlySet<string> cardIds,
        CancellationToken cancellationToken)
    {
        if (Normalize(counts, cardIds) is not { } normalized)
        {
            return (PowerConfigurationMutationStatus.InvalidCounts, null);
        }

        var existing = await ListAsync(cancellationToken);
        if (HasName(existing, name, exceptId: null))
        {
            return (PowerConfigurationMutationStatus.DuplicateName, null);
        }

        var configuration = new PowerConfiguration
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Counts = normalized,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await PutAsync(configuration, cancellationToken);
        return (PowerConfigurationMutationStatus.Success, configuration);
    }

    /// <summary>
    /// Anyone may edit any configuration. Last write wins — two people retuning the same deck at
    /// the same moment is not worth a version check.
    /// </summary>
    public async Task<(PowerConfigurationMutationStatus Status, PowerConfiguration? Configuration)> UpdateAsync(
        string id,
        string name,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlySet<string> cardIds,
        CancellationToken cancellationToken)
    {
        if (Normalize(counts, cardIds) is not { } normalized)
        {
            return (PowerConfigurationMutationStatus.InvalidCounts, null);
        }

        var existing = await ListAsync(cancellationToken);
        var configuration = existing.FirstOrDefault(c => c.Id == id);
        if (configuration is null)
        {
            return (PowerConfigurationMutationStatus.NotFound, null);
        }

        if (HasName(existing, name, exceptId: id))
        {
            return (PowerConfigurationMutationStatus.DuplicateName, null);
        }

        configuration.Name = name;
        configuration.Counts = normalized;

        await PutAsync(configuration, cancellationToken);
        return (PowerConfigurationMutationStatus.Success, configuration);
    }

    /// <summary>
    /// Drops zeros, and refuses cards that don't exist, counts out of range, or a deck with no
    /// cards at all — a game dealt from it would never draw anything.
    /// </summary>
    private static Dictionary<string, int>? Normalize(
        IReadOnlyDictionary<string, int> counts, IReadOnlySet<string> cardIds)
    {
        if (counts.Any(c => !cardIds.Contains(c.Key) || c.Value is < 0 or > MaxCopies))
        {
            return null;
        }

        var normalized = counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);
        return normalized.Count > 0 ? normalized : null;
    }

    /// <summary>Two configurations with the same name are indistinguishable in the lobby.</summary>
    private static bool HasName(IEnumerable<PowerConfiguration> configurations, string name, string? exceptId) =>
        configurations.Any(c => c.Id != exceptId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private Task PutAsync(PowerConfiguration configuration, CancellationToken cancellationToken) =>
        dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = _options.TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [PartitionAttribute] = new AttributeValue(Partition),
                    [SortAttribute] = new AttributeValue($"{configuration.CreatedAt:O}#{configuration.Id}"),
                    ["Id"] = new AttributeValue(configuration.Id),
                    ["Name"] = new AttributeValue(configuration.Name),
                    ["CreatedAt"] = new AttributeValue(configuration.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                    ["Counts"] = new AttributeValue
                    {
                        M = configuration.Counts.ToDictionary(
                            c => c.Key,
                            c => new AttributeValue { N = c.Value.ToString(CultureInfo.InvariantCulture) }),
                    },
                },
            },
            cancellationToken);

    private static PowerConfiguration FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        Id = item["Id"].S,
        Name = item["Name"].S,
        CreatedAt = DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture),
        Counts = item.TryGetValue("Counts", out var counts) && counts.M is not null
            ? counts.M.ToDictionary(c => c.Key, c => int.Parse(c.Value.N, CultureInfo.InvariantCulture))
            : [],
    };
}
