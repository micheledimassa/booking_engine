using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Polly;
using Polly.Extensions.Http;
using flight_booking.Contracts;
using flight_booking.Models;
using flight_booking.Repositories;
using flight_booking.Services;

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

builder.Services.AddSingleton(new FlightSearchOptions
{
    BaseUrl = flightSearchBaseUrl,
    ApiKey = flightSearchApiKey
});

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connString));
builder.Services.AddScoped<IBookingRepository, BookingRepository>();
builder.Services.AddScoped<BookingService>();

builder.Services.AddHttpClient<FrappeBookingClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<FrappeOptions>();
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
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
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Richiesta non valida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(title: "Errore di comunicazione con Frappe", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
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
