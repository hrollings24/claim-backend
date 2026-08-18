using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Challenges;

public class ChallengeStore(IAmazonDynamoDB dynamo, IOptions<ChallengesOptions> options)
{
    /// <summary>
    /// Every challenge shares a single partition, so the whole list is one query that comes back
    /// in order. Keying on the challenge id instead would mean scanning the entire table on every
    /// page load. The trade is that the list is bounded by one partition's throughput, which is
    /// orders of magnitude beyond anything this will see.
    /// </summary>
    private const string Partition = "CHALLENGE";

    private const string PartitionAttribute = "Kind";
    private const string SortAttribute = "CreatedAtId";

    private readonly ChallengesOptions _options = options.Value;

    public async Task<ChallengePage> ListAsync(
        int? pageSize, string? cursor, CancellationToken cancellationToken)
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
                // Descending over a time-ordered sort key, so newest challenges come first.
                ScanIndexForward = false,
                Limit = Math.Clamp(pageSize ?? _options.DefaultPageSize, 1, _options.MaxPageSize),
                ExclusiveStartKey = DecodeCursor(cursor),
            },
            cancellationToken);

        return new ChallengePage(
            response.Items.Select(FromItem).ToList(),
            EncodeCursor(response.LastEvaluatedKey));
    }

    public async Task<Challenge> CreateAsync(Challenge challenge, CancellationToken cancellationToken)
    {
        await dynamo.PutItemAsync(
            new PutItemRequest { TableName = _options.TableName, Item = ToItem(challenge) },
            cancellationToken);

        return challenge;
    }

    /// <summary>
    /// The partition is a constant, so only the sort key needs carrying between pages. Base64 so
    /// it survives a query string without escaping surprises.
    /// </summary>
    private static string? EncodeCursor(Dictionary<string, AttributeValue>? lastEvaluatedKey) =>
        lastEvaluatedKey is null || lastEvaluatedKey.Count == 0
            ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(lastEvaluatedKey[SortAttribute].S));

    private static Dictionary<string, AttributeValue>? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string sortKey;
        try
        {
            sortKey = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The paging cursor is not valid.", nameof(cursor), exception);
        }

        return new Dictionary<string, AttributeValue>
        {
            [PartitionAttribute] = new AttributeValue(Partition),
            [SortAttribute] = new AttributeValue(sortKey),
        };
    }

    private static Dictionary<string, AttributeValue> ToItem(Challenge challenge) => new()
    {
        [PartitionAttribute] = new AttributeValue(Partition),
        [SortAttribute] = new AttributeValue(challenge.SortKey),
        ["Id"] = new AttributeValue(challenge.Id),
        ["Title"] = new AttributeValue(challenge.Title),
        ["Summary"] = new AttributeValue(challenge.Summary),
        ["FurtherDetails"] = new AttributeValue(challenge.FurtherDetails),
        ["CreatedBySub"] = new AttributeValue(challenge.CreatedBySub),
        ["CreatedByName"] = new AttributeValue(challenge.CreatedByName),
        ["CreatedAt"] = new AttributeValue(challenge.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
    };

    private static Challenge FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        Id = item["Id"].S,
        Title = item["Title"].S,
        Summary = item["Summary"].S,
        FurtherDetails = item["FurtherDetails"].S,
        CreatedBySub = item["CreatedBySub"].S,
        CreatedByName = item["CreatedByName"].S,
        CreatedAt = DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture),
    };
}
