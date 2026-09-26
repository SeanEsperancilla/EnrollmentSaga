using System.Text.Json;
using RabbitMQ.Client;

namespace Contracts;

public static class Bus
{
    // camelCase on the wire ("enrollmentId"), case-insensitive on read.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task PublishAsync<T>(IChannel channel, string routingKey, T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            Type = typeof(T).Name,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        await channel.BasicPublishAsync(Topology.Exchange, routingKey,
            mandatory: false, basicProperties: props, body: body);
    }

    public static T Deserialize<T>(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<T>(body.Span, Json)!;

    // Declares the topic exchange every service publishes to. Idempotent.
    public static Task DeclareExchangeAsync(IChannel channel) =>
        channel.ExchangeDeclareAsync(Topology.Exchange, ExchangeType.Topic, durable: true);

    // Connects to RabbitMQ, retrying while the broker container is still starting.
    public static async Task<IConnection> ConnectAsync(string hostName, string clientName,
        CancellationToken ct = default)
    {
        var factory = new ConnectionFactory { HostName = hostName, ClientProvidedName = clientName };
        for (var attempt = 1; ; attempt++)
        {
            try { return await factory.CreateConnectionAsync(ct); }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                Console.WriteLine($"[{clientName}] RabbitMQ not ready ({ex.GetType().Name}), retry {attempt}/30...");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
