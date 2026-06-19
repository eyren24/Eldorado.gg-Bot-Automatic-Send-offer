namespace EldoradoApp.ViewModels;

/// <summary>An entry in the game dropdown. <see cref="Id"/> null = all games.</summary>
public sealed record GameOption(string? Id, string Name)
{
    public static readonly GameOption All = new(null, "Tutti i giochi");

    public override string ToString() => Name;
}
