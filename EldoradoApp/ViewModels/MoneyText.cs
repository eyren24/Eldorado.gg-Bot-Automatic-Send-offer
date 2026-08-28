using System.Globalization;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Text ⇆ amount for the editable price cells.
/// <para>
/// The price grids bind a <b>string</b> with <c>UpdateSourceTrigger=PropertyChanged</c>, not a
/// decimal, so the price applies while you type. A decimal-typed binding can't do that in WPF:
/// updating the source raises <c>PropertyChanged</c>, the binding writes the re-formatted number
/// straight back into the box, and the edit is destroyed mid-word — with <c>StringFormat=N2</c>
/// typing «1,25» leaves «1,25,00» in the cell, and without it the decimal separator is swallowed
/// the moment you press it («1,25» → «125»). Round-tripping the raw text avoids the echo
/// entirely, because what goes back into the box is exactly what was typed.
/// </para>
/// </summary>
internal static class MoneyText
{
    /// <summary>Same styles the default decimal binding uses, so parsing didn't change.</summary>
    private const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands;

    public static string Format(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>
    /// True when <paramref name="text"/> is a complete amount. Half-typed cells ("", "1,")
    /// fail here on purpose: the last good price stays until the number makes sense again.
    /// </summary>
    public static bool TryParse(string? text, out decimal amount) =>
        decimal.TryParse(text, Styles, CultureInfo.CurrentCulture, out amount);
}
