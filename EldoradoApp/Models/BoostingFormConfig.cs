namespace EldoradoApp.Models;

/// <summary>One field of a boosting request form, with its selectable options (id → label).</summary>
public sealed record FormInput(int Id, string Title, string Type, IReadOnlyDictionary<int, string> Options)
{
    /// <summary>
    /// The label for a raw answer: option ids are resolved through <see cref="Options"/>,
    /// anything else (free text, numbers, "Yes"/"No" toggles) comes back untouched.
    /// </summary>
    public string Resolve(string raw)
    {
        if (Options.Count > 0 && int.TryParse(raw, out var optionId) &&
            Options.TryGetValue(optionId, out var label))
        {
            return label;
        }

        return raw;
    }
}

/// <summary>
/// The schema of one game+category request form, from the public
/// <c>GET /api/boosting/formConfig/{gameId}/{boostingCategoryId}</c> endpoint.
/// </summary>
/// <remarks>
/// The buyer's answers arrive as bare <c>(inputId, value)</c> pairs, so the schema is
/// what turns id 26 into "Current Rank" and id 53 into "Desired Rank". Matching happens
/// on the field <b>titles</b> rather than hard-coded ids, so a new game (or a reshuffled
/// form) keeps working as long as Eldorado keeps calling the fields what it calls them.
/// </remarks>
public sealed record BoostingFormConfig(
    string GameId,
    string CategoryId,
    IReadOnlyList<FormInput> Inputs)
{
    public static readonly BoostingFormConfig Empty = new("", "", []);

    public FormInput? ById(int id) => Inputs.FirstOrDefault(i => i.Id == id);

    /// <summary>The rank the booster starts from ("Current Rank", "Previous season rank", …).</summary>
    public FormInput? CurrentRankInput =>
        FirstWithTitle("current rank", "current season rank", "previous season rank", "starting rank");

    /// <summary>The rank to reach. Absent on per-game categories (placements, net wins).</summary>
    public FormInput? DesiredRankInput => FirstWithTitle("desired rank", "target rank", "desired");

    public FormInput? ServerInput => FirstWithTitle("server", "region");

    public FormInput? QuantityInput => FirstWithTitle("number of games", "number of wins", "games");

    public FormInput? NotesInput => FirstWithTitle("additional information", "request description", "description");

    private FormInput? FirstWithTitle(params string[] needles)
    {
        // Longest needle first: "current season rank" must win over "current rank".
        foreach (var needle in needles.OrderByDescending(n => n.Length))
        {
            var hit = Inputs.FirstOrDefault(i =>
                i.Title.Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }
}
