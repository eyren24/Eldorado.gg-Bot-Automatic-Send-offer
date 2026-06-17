using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EldoradoApp.Services;

/// <summary>Shared JSON settings for Eldorado API payloads.</summary>
internal static class EldoradoApiJson
{
    /// <summary>Read options: tolerant casing for incoming responses.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Write options: camelCase, omit nulls, for outgoing request bodies.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Reads a JSON value as a string even when the server emits it as a number or
/// boolean (e.g. enums serialized numerically), so deserialization never throws
/// on an unexpected token shape.
/// </summary>
internal sealed class TolerantStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>Generic cursor-paginated envelope used across the seller API.</summary>
internal sealed class CursorPagedResult<T>
{
    public List<T>? Results { get; set; }
    public string? Cursor { get; set; }
    public string? NextPageCursor { get; set; }
    public string? PreviousPageCursor { get; set; }
    public int PageSize { get; set; }
}

// ---- Orders (OrderPrivateViewModel, trimmed to what the app needs) ----

internal sealed class OrderDto
{
    public string Id { get; set; } = "";
    public string? BuyerUsername { get; set; }
    public string? SellerUsername { get; set; }
    public MoneyDto? TotalPrice { get; set; }
    public StateDto? State { get; set; }
    public OrderOfferDetailsDto? OrderOfferDetails { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
}

internal sealed class MoneyDto
{
    public decimal Amount { get; set; }

    [JsonConverter(typeof(TolerantStringConverter))]
    public string? Currency { get; set; }
}

internal sealed class StateDto
{
    [JsonConverter(typeof(TolerantStringConverter))]
    public string? State { get; set; }
}

internal sealed class OrderOfferDetailsDto
{
    public string? OfferTitle { get; set; }
    public string? GameCategoryTitle { get; set; }
    public string? GameId { get; set; }
}

// ---- Boosting requests received (BoostingRequestListItemSellerDTO) ----

internal sealed class BoostingRequestDto
{
    public string Id { get; set; } = "";
    public string? GameId { get; set; }
    public string? BoostingCategoryId { get; set; }
    public string? BoostingCategoryTitle { get; set; }
    public string? BuyerId { get; set; }
    public string? BuyerUsername { get; set; }
    public bool IsBuyerMuted { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
}

// ---- Subscriptions (BoostingSubscriptionGetPrivateDTO) ----

internal sealed class BoostingSubscriptionDto
{
    public string? GameId { get; set; }
    public string? GameName { get; set; }
    public string? GameCategoryTitle { get; set; }
    public string? BoostingCategoryId { get; set; }
    public string? BoostingCategoryName { get; set; }
    public bool IsActive { get; set; }
    public bool IsSubscribed { get; set; }
}
