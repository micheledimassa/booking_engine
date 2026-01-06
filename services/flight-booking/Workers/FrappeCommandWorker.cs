using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using flight_booking.Contracts.Messaging;
using flight_booking.Infrastructure.Messaging;
using flight_booking.Infrastructure.Outbox;
using flight_booking.Models;
using flight_booking.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using RabbitMQ.Client;

namespace flight_booking.Workers;

public sealed class FrappeCommandWorker : IdempotentConsumerHostedService
{
    private const int MaxRetryAttempts = 6;
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null
    };

    private readonly FrappeBookingClient _frappeClient;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<FrappeCommandWorker> _logger;

    public FrappeCommandWorker(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        FrappeBookingClient frappeClient,
        ILogger<FrappeCommandWorker> logger)
        : base(connectionFactory, scopeFactory, logger, queueName: "booking.frappe-commands")
    {
        _frappeClient = frappeClient;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct)
    {
        var command = payload.Deserialize<FrappeBookingUpsertRequestedMessage>(MessageSerializerOptions)
                      ?? throw new InvalidOperationException("Payload frappe.booking.upsert.requested non valido.");

        var outbox = services.GetRequiredService<IOutboxRepository>();

        if (command.Attempt >= MaxRetryAttempts)
        {
            _logger.LogError("Booking {BookingId} ha raggiunto il massimo di {MaxAttempts} tentativi verso Frappe", command.BookingId, MaxRetryAttempts);
            await PublishFailureAsync(command, new InvalidOperationException("Numero massimo di tentativi verso Frappe raggiunto."), outbox, ct);
            return;
        }

        try
        {
            var response = await _frappeClient.UpsertBookingAsync(command.Payload, ct);
            await PublishSuccessAsync(command, response, outbox, ct);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "Circuit breaker aperto per Frappe, booking {BookingId} mandato in retry", command.BookingId);
            await PublishRetryAsync(command, outbox, ct);
        }
        catch (HttpRequestException ex)
        {
            if (ShouldRetry(ex.StatusCode))
            {
                _logger.LogWarning(ex, "Errore HTTP {Status} da Frappe per booking {BookingId}: mando in retry", ex.StatusCode, command.BookingId);
                await PublishRetryAsync(command, outbox, ct);
            }
            else
            {
                await PublishFailureAsync(command, ex, outbox, ct);
            }
        }
        catch (Exception ex)
        {
            await PublishFailureAsync(command, ex, outbox, ct);
        }
    }

    private static bool ShouldRetry(HttpStatusCode? statusCode)
        => statusCode is null || (int)statusCode >= 500 || statusCode == HttpStatusCode.TooManyRequests;

    private Task PublishSuccessAsync(FrappeBookingUpsertRequestedMessage command, BookingSyncResponse response, IOutboxRepository outbox, CancellationToken ct)
    {
        var evt = new FrappeBookingUpsertSucceededMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = command.BookingId,
            CorrelationId = command.CorrelationId,
            Name = response.Name,
            DocStatus = response.DocStatus,
            SyncedAt = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.SerializeToNode(evt, MessageSerializerOptions) as JsonObject
                      ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        return outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "frappe.booking.upsert.succeeded",
            Exchange = _options.FrappeEventExchange,
            RoutingKey = "frappe.booking.upsert.succeeded",
            Payload = payload,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:frappe.success"
        }, ct);
    }

    private Task PublishFailureAsync(FrappeBookingUpsertRequestedMessage command, Exception exception, IOutboxRepository outbox, CancellationToken ct)
    {
        var evt = new FrappeBookingUpsertFailedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = command.BookingId,
            CorrelationId = command.CorrelationId,
            Reason = exception.Message,
            Attempt = command.Attempt + 1,
            FailedAt = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.SerializeToNode(evt, MessageSerializerOptions) as JsonObject
                      ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = evt.Attempt
        };

        return outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "frappe.booking.upsert.failed",
            Exchange = _options.FrappeEventExchange,
            RoutingKey = "frappe.booking.upsert.failed",
            Payload = payload,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:frappe.failed:{evt.Attempt}"
        }, ct);
    }

    private Task PublishRetryAsync(FrappeBookingUpsertRequestedMessage command, IOutboxRepository outbox, CancellationToken ct)
    {
        var nextAttempt = command.Attempt + 1;
        var retryDelaySeconds = Math.Min(300, (int)Math.Pow(2, nextAttempt) * 15);

        var retryHeaders = new JsonObject
        {
            ["x-correlation-id"] = command.CorrelationId.ToString(),
            ["x-retry-count"] = nextAttempt,
            ["x-delay"] = retryDelaySeconds
        };

        var retryPayload = JsonSerializer.SerializeToNode(command with { Attempt = nextAttempt }, MessageSerializerOptions) as JsonObject
                           ?? new JsonObject();

        return outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = command.BookingId,
            Type = "frappe.booking.upsert.retry",
            Exchange = _options.FrappeCommandExchange,
            RoutingKey = "frappe.booking.upsert.retry",
            Payload = retryPayload,
            Headers = retryHeaders,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = command.CorrelationId.ToString(),
            IdempotencyKey = $"{command.BookingId}:frappe.retry:{nextAttempt}"
        }, ct);
    }
}
