using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.Extensions.Http;
using flight_booking.Contracts;
using flight_booking.Infrastructure;
using flight_booking.Models;
using flight_booking.Repositories;
using flight_booking.Services;
using flight_booking.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var dbHost = RequireSetting(builder, "DB_HOST");
var dbPort = RequireSetting(builder, "DB_PORT");
var dbName = RequireSetting(builder, "DB_NAME");
var dbUser = RequireSetting(builder, "DB_USER");
var dbPass = RequireSetting(builder, "DB_PASSWORD");
var frappeBaseUrl = RequireSetting(builder, "FRAPPE_BASE_URL");
var frappeApiKey = RequireSetting(builder, "FRAPPE_API_KEY");
var frappeEndpoint = builder.Configuration["FRAPPE_BOOKING_ENDPOINT"] ?? FrappeOptions.DefaultEndpoint;
var flightSearchBaseUrl = RequireSetting(builder, "FLIGHT_SEARCH_BASE_URL");
var flightSearchApiKey = RequireSetting(builder, "FLIGHT_SEARCH_API_KEY");

var connString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass}";

builder.Services.AddSingleton(new FrappeOptions
{
    BaseUrl = frappeBaseUrl,
    ApiKey = frappeApiKey,
    Endpoint = frappeEndpoint
});

builder.Services.Configure<FrappeCircuitBreakerOptions>(builder.Configuration.GetSection(FrappeCircuitBreakerOptions.SectionName));

builder.Services.AddSingleton(new FlightSearchOptions
{
    BaseUrl = flightSearchBaseUrl,
    ApiKey = flightSearchApiKey
});

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connString));
builder.Services.AddScoped<IBookingRepository, BookingRepository>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddMessagingInfrastructure(builder.Configuration);
builder.Services.AddHostedService<BookingSagaWorker>();
builder.Services.AddHostedService<InventoryEventsWorker>();
builder.Services.AddHostedService<FrappeCommandWorker>();
builder.Services.AddHostedService<FrappeEventsWorker>();

builder.Services.AddHttpClient<FrappeBookingClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<FrappeOptions>();
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
    })
    .AddPolicyHandler((sp, _) =>
    {
        var breakerOptions = sp.GetRequiredService<IOptions<FrappeCircuitBreakerOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("FrappeCircuitBreaker");
        return GetCircuitBreakerPolicy(breakerOptions, logger);
    })
    .AddPolicyHandler(GetRetryPolicy());

builder.Services.AddHttpClient<IFlightInventoryClient, FlightInventoryClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<FlightSearchOptions>();
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
    })
    .AddPolicyHandler(GetRetryPolicy());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/bookings", async ([FromBody] BookingWebhookRequest request, BookingService bookingService, CancellationToken ct) =>
{
    try
    {
        var response = await bookingService.HandleAsync(request, ct);
        return Results.Accepted($"/v1/bookings/{response.BookingId}", response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Richiesta non valida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapGet("/v1/bookings/{id:guid}", async (Guid id, IBookingRepository bookingRepository, CancellationToken ct) =>
{
    var record = await bookingRepository.GetAsync(id, ct);
    return record is null ? Results.NotFound(new { error = "Booking non trovato." }) : Results.Ok(record);
});

app.Run("http://0.0.0.0:8090");

static string RequireSetting(WebApplicationBuilder builder, string key)
{
    return builder.Configuration[key] ?? throw new InvalidOperationException($"Variabile di configurazione '{key}' mancante.");
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, retryAttempt)));
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(FrappeCircuitBreakerOptions options, ILogger logger)
{
    var handledEvents = Math.Max(1, options.HandledEventsAllowedBeforeBreaking);
    var breakDuration = TimeSpan.FromSeconds(Math.Max(1, options.BreakDurationSeconds));

    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: handledEvents,
            durationOfBreak: breakDuration,
            onBreak: (outcome, timespan) =>
                logger.LogWarning("Circuit breaker Frappe aperto per {BreakSeconds}s (reason: {Reason})", timespan.TotalSeconds, outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString()),
            onReset: () => logger.LogInformation("Circuit breaker Frappe chiuso: riprendono le chiamate"),
            onHalfOpen: () => logger.LogInformation("Circuit breaker Frappe in stato HALF-OPEN"));
}
