using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using flight_booking.Infrastructure.Messaging;

namespace flight_booking.Infrastructure.Outbox;
public sealed class OutboxPublisherHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRabbitMqConnectionFactory _connectionFactory;
    private readonly ILogger<OutboxPublisherHostedService> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public OutboxPublisherHostedService(
        IServiceScopeFactory scopeFactory,
        IRabbitMqConnectionFactory connectionFactory,
        ILogger<OutboxPublisherHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = _connectionFactory.GetConnection().CreateModel();
        channel.ConfirmSelect();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                var messages = await repository.GetUnpublishedAsync(100, stoppingToken);

                foreach (var message in messages)
                {
                    var props = channel.CreateBasicProperties();
                    props.Persistent = true;
                    props.MessageId = message.MessageId;
                    props.CorrelationId = message.CorrelationId;
                    props.Type = message.Type;
                    var headers = ConvertHeaders(message.Headers);

                    if (TryExtractDelay(headers, out var expirationMs))
                    {
                        props.Expiration = expirationMs;
                    }

                    props.Headers = headers.Count == 0 ? null : headers;

                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message.Payload, _serializerOptions));
                    channel.BasicPublish(message.Exchange, message.RoutingKey, props, body);
                    await repository.MarkPublishedAsync(message.Id, stoppingToken);
                }

                if (messages.Count > 0)
                {
                    channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // chiusura servizio
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore in pubblicazione outbox");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
    private static IDictionary<string, object?> ConvertHeaders(JsonObject headers)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in headers)
        {
            dictionary[kvp.Key] = kvp.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue(out string? s) => s,
                JsonValue value when value.TryGetValue(out int i) => i,
                JsonValue value when value.TryGetValue(out long l) => l,
                JsonValue value when value.TryGetValue(out Guid g) => g.ToString(),
                JsonValue value when value.TryGetValue(out bool b) => b,
                _ => kvp.Value?.ToJsonString()
            };
        }

        return dictionary;
    }

    private static bool TryExtractDelay(IDictionary<string, object?> headers, out string expirationMs)
    {
        expirationMs = string.Empty;

        if (!headers.TryGetValue("x-delay", out var value))
            return false;

        headers.Remove("x-delay");

        if (value is null)
            return false;

        if (value is int delayInt)
        {
            return SetExpiration(delayInt, out expirationMs);
        }

        if (value is long delayLong)
        {
            return SetExpiration((int)Math.Min(delayLong, int.MaxValue), out expirationMs);
        }

        if (value is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return SetExpiration(parsed, out expirationMs);
        }

        return false;
    }

    private static bool SetExpiration(int delaySeconds, out string expirationMs)
    {
        if (delaySeconds <= 0)
        {
            expirationMs = string.Empty;
            return false;
        }

        var milliseconds = (long)delaySeconds * 1000;
        expirationMs = milliseconds.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
