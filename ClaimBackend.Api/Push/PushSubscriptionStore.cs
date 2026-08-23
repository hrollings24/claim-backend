using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Push;

public class PushSubscriptionStore(IAmazonDynamoDB dynamo, IOptions<PushOptions> options)
{
    private const string PartitionAttribute = "Sub";
    private const string SortAttribute = "EndpointHash";

    private readonly PushOptions _options = options.Value;

    /// <summary>
    /// Saving the same endpoint twice replaces it rather than duplicating, so a browser that
    /// re-subscribes — which it does whenever its subscription is refreshed — doesn't accumulate
    /// entries that would each deliver the same notification.
    /// </summary>
    public Task SaveAsync(PushSubscription subscription, CancellationToken cancellationToken) =>
        dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = _options.TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [PartitionAttribute] = new AttributeValue(subscription.Sub),
                    [SortAttribute] = new AttributeValue(HashOf(subscription.Endpoint)),
                    ["Endpoint"] = new AttributeValue(subscription.Endpoint),
                    ["P256dh"] = new AttributeValue(subscription.P256dh),
                    ["Auth"] = new AttributeValue(subscription.Auth),
                    ["CreatedAt"] = new AttributeValue(
                        subscription.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                },
            },
            cancellationToken);

    public async Task<IReadOnlyList<PushSubscription>> ForPlayersAsync(
        IEnumerable<string> subs, CancellationToken cancellationToken)
    {
        var found = new List<PushSubscription>();

        // A handful of players per game, so querying each is a few small reads — cheaper and
        // simpler than a BatchGetItem across an unknown set of endpoints.
        foreach (var sub in subs.Distinct())
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = _options.TableName,
                    KeyConditionExpression = "#sub = :sub",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#sub"] = PartitionAttribute },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":sub"] = new AttributeValue(sub),
                    },
                },
                cancellationToken);

            found.AddRange(response.Items.Select(item => new PushSubscription
            {
                Sub = item[PartitionAttribute].S,
                Endpoint = item["Endpoint"].S,
                P256dh = item["P256dh"].S,
                Auth = item["Auth"].S,
                CreatedAt = DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture),
            }));
        }

        return found;
    }

    public Task DeleteAsync(string sub, string endpoint, CancellationToken cancellationToken) =>
        dynamo.DeleteItemAsync(
            new DeleteItemRequest
            {
                TableName = _options.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PartitionAttribute] = new AttributeValue(sub),
                    [SortAttribute] = new AttributeValue(HashOf(endpoint)),
                },
            },
            cancellationToken);

    /// <summary>
    /// Endpoints are long vendor URLs, so they are keyed by hash. The full endpoint is kept as an
    /// attribute — it is what actually gets posted to.
    /// </summary>
    private static string HashOf(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
}
