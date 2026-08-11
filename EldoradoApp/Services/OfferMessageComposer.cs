using System.Text;
using System.Text.RegularExpressions;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Fills the seller's message template with the details of the offer that was just sent.
/// Unknown placeholders are left untouched so a typo is visible instead of silently
/// blanking part of the message.
/// </summary>
public static partial class OfferMessageComposer
{
    /// <summary>Placeholder name → what it means; shown as clickable chips in the UI.</summary>
    public static readonly IReadOnlyList<(string Token, string Description)> Placeholders =
    [
        ("{buyer}", "Nome del compratore"),
        ("{from}", "Rank di partenza"),
        ("{to}", "Rank di destinazione"),
        ("{divisions}", "Numero di divisioni"),
        ("{price}", "Prezzo offerto con valuta"),
        ("{amount}", "Prezzo senza valuta"),
        ("{currency}", "Valuta"),
        ("{eta}", "Tempo di consegna promesso"),
        ("{category}", "Titolo della categoria"),
        ("{extras}", "Elenco degli extra applicati"),
        ("{breakdown}", "Dettaglio completo del prezzo"),
        ("{date}", "Data di oggi"),
        ("{time}", "Ora corrente"),
    ];

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    public static string Compose(
        string template,
        BoostingRequest request,
        PriceQuote quote,
        BoostingDeliveryTime deliveryTime)
    {
        var now = DateTimeOffset.Now;

        var text = (template ?? "")
            .Replace("{buyer}", request.BuyerUsername ?? "")
            .Replace("{from}", quote.FromRank ?? "?")
            .Replace("{to}", quote.ToRank ?? "?")
            .Replace("{divisions}", quote.DivisionCount.ToString())
            .Replace("{price}", quote.TotalText)
            .Replace("{amount}", quote.Total.ToString("N2"))
            .Replace("{currency}", quote.Currency)
            .Replace("{eta}", DeliveryText(deliveryTime))
            .Replace("{category}", request.BoostingCategoryTitle ?? "")
            .Replace("{extras}", ExtrasText(quote))
            .Replace("{breakdown}", BreakdownText(quote))
            .Replace("{date}", now.ToString("dd/MM/yyyy"))
            .Replace("{time}", now.ToString("HH:mm"));

        // A template line like "{extras}" collapses to nothing when there are none.
        return ExtraBlankLines().Replace(text, "\n\n").Trim();
    }

    private static string ExtrasText(PriceQuote quote)
    {
        var extras = quote.Lines
            .Where(l => l.Kind == PriceLineKind.Extra)
            .Select(l => $"✅ {l.Label.Replace("Extra · ", "")}")
            .ToList();

        return extras.Count == 0 ? "" : string.Join("\n", extras);
    }

    private static string BreakdownText(PriceQuote quote)
    {
        var builder = new StringBuilder();
        foreach (var line in quote.Lines)
        {
            builder.AppendLine($"• {line.Label}: {line.Amount:N2} {quote.Currency}");
        }

        builder.Append($"= {quote.TotalText}");
        return builder.ToString();
    }

    /// <summary>Human wording for the API's delivery-time enum ("Hour3" → "3 ore").</summary>
    public static string DeliveryText(BoostingDeliveryTime time) => time switch
    {
        BoostingDeliveryTime.Automated => "tempo automatico",
        BoostingDeliveryTime.Instant => "immediata",
        BoostingDeliveryTime.Minute5 => "5 minuti",
        BoostingDeliveryTime.Minute20 => "20 minuti",
        BoostingDeliveryTime.Hour1 => "1 ora",
        BoostingDeliveryTime.Hour2 => "2 ore",
        BoostingDeliveryTime.Hour3 => "3 ore",
        BoostingDeliveryTime.Hour5 => "5 ore",
        BoostingDeliveryTime.Hour8 => "8 ore",
        BoostingDeliveryTime.Hour12 => "12 ore",
        BoostingDeliveryTime.Day1 => "1 giorno",
        BoostingDeliveryTime.Day2 => "2 giorni",
        BoostingDeliveryTime.Day3 => "3 giorni",
        BoostingDeliveryTime.Day7 => "7 giorni",
        BoostingDeliveryTime.Day14 => "14 giorni",
        BoostingDeliveryTime.Day28 => "28 giorni",
        BoostingDeliveryTime.Day45 => "45 giorni",
        BoostingDeliveryTime.Day60 => "60 giorni",
        _ => time.ToString()
    };
}
