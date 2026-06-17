namespace EldoradoApp.Models;

public sealed record Order(
    string Id,
    string BuyerUsername,
    string ProductTitle,
    decimal Amount,
    string Currency,
    OrderStatus Status,
    DateTimeOffset CreatedAt);
