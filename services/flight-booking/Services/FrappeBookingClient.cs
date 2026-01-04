using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using flight_booking.Models;

namespace flight_booking.Services;

public sealed class FrappeBookingClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<FrappeBookingClient> _logger;
    private readonly string _endpoint;

    public FrappeBookingClient(HttpClient httpClient, FrappeOptions options, ILogger<FrappeBookingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = options.Endpoint;
    }

    public async Task<BookingSyncResponse> UpsertBookingAsync(BookingPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var response = await _httpClient.PostAsJsonAsync(_endpoint, payload, SerializerOptions, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Token Frappe non valido.");

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Frappe webhook errore {Status}: {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Frappe ha risposto con stato {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payloadResponse = DeserializeBookingResponse(body);

        if (payloadResponse is null)
            throw new InvalidOperationException("Risposta Frappe vuota o non valida.");

        return payloadResponse;
    }

    private static BookingSyncResponse? DeserializeBookingResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var payload = SelectPayloadElement(document.RootElement);

            if (payload.ValueKind != JsonValueKind.Object)
                return null;

            return new BookingSyncResponse
            {
                Status = ReadString(payload, "status"),
                Name = ReadString(payload, "name"),
                Uuid = ReadGuid(payload, "uuid"),
                DocStatus = ReadInt(payload, "docstatus")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement SelectPayloadElement(JsonElement root)
    {
        if (TryGetPropertyCaseInsensitive(root, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data;

        if (TryGetPropertyCaseInsensitive(root, "message", out var message) && message.ValueKind == JsonValueKind.Object)
            return message;

        if (TryGetPropertyCaseInsensitive(root, "doc", out var doc) && doc.ValueKind == JsonValueKind.Object)
            return doc;

        return root;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string propertyName)
        => TryGetPropertyCaseInsensitive(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Guid ReadGuid(JsonElement element, string propertyName)
    {
        if (TryGetPropertyCaseInsensitive(element, propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid))
                return guid;

            if (value.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(value, "name", out var inner) && inner.ValueKind == JsonValueKind.String && Guid.TryParse(inner.GetString(), out guid))
                return guid;
        }

        return Guid.Empty;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (TryGetPropertyCaseInsensitive(element, propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer))
                return integer;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out integer))
                return integer;
        }

        return 0;
    }
}
