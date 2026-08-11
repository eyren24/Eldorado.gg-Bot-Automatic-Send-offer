namespace EldoradoApp.Models;

public enum ExtraKind
{
    /// <summary>A flat amount added to the total (e.g. DuoQ +5 €).</summary>
    Fixed,

    /// <summary>A percentage of the rank price (base + divisions), e.g. +20%.</summary>
    Percent
}

/// <summary>
/// A surcharge for a buyer-requested option. The bot applies it automatically when any
/// of its <see cref="Keywords"/> shows up in the request (title or details), so
/// "DuoQ" or "with stream" in a request is priced without touching the code.
/// </summary>
public sealed class ExtraOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Shown in the UI and in the price breakdown (e.g. "DuoQ").</summary>
    public string Label { get; set; } = "";

    /// <summary>Comma-separated words that trigger this extra, e.g. "duoq, duo queue, duo".</summary>
    public string Keywords { get; set; } = "";

    public ExtraKind Kind { get; set; } = ExtraKind.Fixed;

    /// <summary>Amount in currency (Fixed) or percent points (Percent).</summary>
    public decimal Amount { get; set; }

    /// <summary>Off = the extra is never applied (keeps the row for later).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Always charge it, even when no keyword matches (e.g. a fixed service fee).</summary>
    public bool AlwaysApply { get; set; }

    public IEnumerable<string> KeywordList => Keywords
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(k => k.Length > 0);

    /// <summary>True when the request text mentions this extra.</summary>
    public bool Matches(string? text)
    {
        if (AlwaysApply)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return KeywordList.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The out-of-the-box list the seller can edit right away.</summary>
    public static List<ExtraOption> CreateDefaults() =>
    [
        new() { Label = "DuoQ", Keywords = "duoq, duo q, duo queue, duo-queue, duo", Kind = ExtraKind.Fixed, Amount = 5m },
        new() { Label = "Streaming", Keywords = "stream, streaming, livestream, live stream", Kind = ExtraKind.Fixed, Amount = 10m },
        new() { Label = "Agente specifico", Keywords = "specific agent, agente, main agent, agent request", Kind = ExtraKind.Fixed, Amount = 3m },
        new() { Label = "Priorità / express", Keywords = "priority, express, urgent, asap, fast", Kind = ExtraKind.Percent, Amount = 25m },
        new() { Label = "Orario concordato", Keywords = "schedule, specific hours, orario, time slot", Kind = ExtraKind.Fixed, Amount = 4m },
    ];
}
