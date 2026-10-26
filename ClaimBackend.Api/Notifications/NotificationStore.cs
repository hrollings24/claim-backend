using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Notifications;

/// <summary>
/// Each player's inbox, one item per notification, keyed by player then by a time-ordered id. Old
/// entries are removed by DynamoDB's TTL on ExpiresAt rather than by us.
/// </summary>
public class NotificationStore(IAmazonDynamoDB dynamo, IOptions<NotificationsOptions> options)
{
    private const string PartitionAttribute = "Sub";
    private const string SortAttribute = "Id";

    private readonly NotificationsOptions _options = options.Value;

    /// <summary>
    /// One small write per player. A game has a handful, so this is simpler than a batch — and the
    /// only call the Lambda's role needs is PutItem.
    /// </summary>
    public async Task AddAsync(IEnumerable<InboxNotification> notifications, CancellationToken cancellationToken)
    {
        foreach (var notification in notifications)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                [PartitionAttribute] = new AttributeValue(notification.Sub),
                [SortAttribute] = new AttributeValue(notification.Id),
                ["Title"] = new AttributeValue(notification.Title),
                ["Body"] = new AttributeValue(notification.Body),
                ["CreatedAt"] = new AttributeValue(notification.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                ["ExpiresAt"] = new AttributeValue
                {
                    N = notification.CreatedAt.AddDays(_options.KeepDays).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                },
            };

            if (notification.GameCode is { } code)
            {
                item["GameCode"] = new AttributeValue(code);
            }

            if (notification.Url is { } url)
            {
                item["Url"] = new AttributeValue(url);
            }

            await dynamo.PutItemAsync(new PutItemRequest { TableName = _options.TableName, Item = item }, cancellationToken);
        }
    }

    /// <summary>Newest first. TTL deletes lazily, so anything already past its expiry is left out here.</summary>
    public async Task<IReadOnlyList<InboxNotification>> ListAsync(string sub, CancellationToken cancellationToken)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = _options.TableName,
                KeyConditionExpression = "#sub = :sub",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#sub"] = PartitionAttribute },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":sub"] = new AttributeValue(sub) },
                ScanIndexForward = false,
                Limit = _options.MaxListed,
            },
            cancellationToken);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return response.Items
            .Where(item => !item.TryGetValue("ExpiresAt", out var expires) || long.Parse(expires.N, CultureInfo.InvariantCulture) > now)
            .Select(item => new InboxNotification
            {
                Sub = item[PartitionAttribute].S,
                Id = item[SortAttribute].S,
                GameCode = item.TryGetValue("GameCode", out var code) ? code.S : null,
                Title = item["Title"].S,
                Body = item["Body"].S,
                Url = item.TryGetValue("Url", out var url) ? url.S : null,
                CreatedAt = DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture),
            })
            .ToList();
    }

    /// <summary>Dismissing removes it. Deleting one that's already gone is not an error.</summary>
    public Task DeleteAsync(string sub, string id, CancellationToken cancellationToken) =>
        dynamo.DeleteItemAsync(
            new DeleteItemRequest
            {
                TableName = _options.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PartitionAttribute] = new AttributeValue(sub),
                    [SortAttribute] = new AttributeValue(id),
                },
            },
            cancellationToken);

    public async Task DeleteAllAsync(string sub, CancellationToken cancellationToken)
    {
        foreach (var notification in await ListAsync(sub, cancellationToken))
        {
            await DeleteAsync(sub, notification.Id, cancellationToken);
        }
    }
}
