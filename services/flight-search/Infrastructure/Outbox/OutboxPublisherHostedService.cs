using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using flight_search.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace flight_search.Infrastructure.Outbox;

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
                var batch = await repository.GetUnpublishedAsync(100, stoppingToken);

                foreach (var message in batch)
                {
                    var props = channel.CreateBasicProperties();
                    props.Persistent = true;
                    props.MessageId = message.MessageId;
                    props.CorrelationId = message.CorrelationId;
                    props.Type = message.Type;
                    props.Headers = ConvertHeaders(message.Headers);

                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message.Payload, _serializerOptions));
                    channel.BasicPublish(message.Exchange, message.RoutingKey, props, body);
                    await repository.MarkPublishedAsync(message.Id, stoppingToken);
                }

                if (batch.Count > 0)
                {
                    channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante la pubblicazione outbox (flight-search)");
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
}
