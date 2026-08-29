using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Fills in what the received-requests feed leaves out: the buyer's actual form answers.
/// </summary>
/// <remarks>
/// <para>
/// The feed (<c>boostingOffers/me/boostingRequests/received</c>) returns eight fields and
/// no rank at all — the job description lives on the request itself, under
/// <c>GET boostingOffers/boostingRequests/{id}/details</c> as a list of
/// <c>(inputId, value)</c> answers. Those ids are named by the category's public form
/// schema, <c>GET boosting/formConfig/{gameId}/{categoryId}</c>: for Valorant rank boosts
/// 26 is "Current Rank", 53 "Desired Rank", 60 "Server".
/// </para>
/// <para>
/// Both lookups are cached — details never change once posted, and a form schema is per
/// category — so a poll only pays for requests it has not seen before.
/// </para>
/// </remarks>
public sealed class BoostingRequestHydrator(HttpClient http)
{
    private const int MaxParallelFetches = 6;

    /// <summary>Cached requests kept before the cache is dropped (a poll sees at most a page).</summary>
    private const int MaxCachedDetails = 500;

    private readonly ConcurrentDictionary<string, (BoostingRequestFacts Facts, string Json)> _details = new();
    private readonly ConcurrentDictionary<string, BoostingFormConfig> _forms = new();

    /// <summary>Requests whose details could not be read, so the UI can say so honestly.</summary>
    public int FailedCount { get; private set; }

    /// <summary>Returns the same requests with <see cref="BoostingRequest.Facts"/> filled in.</summary>
    public async Task<IReadOnlyList<BoostingRequest>> HydrateAsync(
        IReadOnlyList<BoostingRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return requests;
        }

        var hydrated = new BoostingRequest[requests.Count];
        var failures = 0;

        using var gate = new SemaphoreSlim(MaxParallelFetches);

        var work = requests.Select(async (request, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var facts = await FactsForAsync(request, cancellationToken).ConfigureAwait(false);
                if (facts is null)
                {
                    Interlocked.Increment(ref failures);
                    hydrated[index] = request;
                    return;
                }

                hydrated[index] = request with
                {
                    Facts = facts.Value.Facts,
                    DetailsJson = facts.Value.Json
                };
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        FailedCount = failures;
        return hydrated;
    }

    private async Task<(BoostingRequestFacts Facts, string Json)?> FactsForAsync(
        BoostingRequest request, CancellationToken cancellationToken)
    {
        if (_details.TryGetValue(request.Id, out var cached))
        {
            return cached;
        }

        var json = await GetStringOrNullAsync(
            $"api/boostingOffers/boostingRequests/{Uri.EscapeDataString(request.Id)}/details",
            cancellationToken).ConfigureAwait(false);

        // Some requests only answer on the request itself; the details live under the same key.
        json ??= await GetStringOrNullAsync(
            $"api/boostingOffers/boostingRequests/{Uri.EscapeDataString(request.Id)}",
            cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return null;
        }

        var form = await FormConfigAsync(request.GameId, request.BoostingCategoryId, cancellationToken)
            .ConfigureAwait(false);

        var facts = ReadFacts(json, form);
        var entry = (facts, json);

        // A request's answers never change, so caching is safe; just don't grow forever
        // in a bot that runs for days.
        if (_details.Count >= MaxCachedDetails)
        {
            _details.Clear();
        }

        _details[request.Id] = entry;
        return entry;
    }

    /// <summary>The category's form schema, fetched once per game+category.</summary>
    public async Task<BoostingFormConfig> FormConfigAsync(
        string? gameId, string? categoryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(categoryId))
        {
            return BoostingFormConfig.Empty;
        }

        var key = $"{gameId}/{categoryId}";
        if (_forms.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var json = await GetStringOrNullAsync($"api/boosting/formConfig/{key}", cancellationToken)
            .ConfigureAwait(false);

        var config = json is null ? BoostingFormConfig.Empty : ReadFormConfig(json, gameId, categoryId);
        _forms[key] = config;
        return config;
    }

    private async Task<string?> GetStringOrNullAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (Exception ex)
        {
            ApiLog.Write($"[hydrate] {path} fallita: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---- Payload readers (defensive: the server may nest or rename the envelope) ----

    /// <summary>Reads the buyer's answers out of a details payload and names them via the schema.</summary>
    internal static BoostingRequestFacts ReadFacts(string json, BoostingFormConfig form)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return BoostingRequestFacts.Empty;
        }

        using (document)
        {
            if (!TryFindArray(document.RootElement, "descriptionValues", out var values))
            {
                return BoostingRequestFacts.Empty;
            }

            var answers = new Dictionary<int, string>();
            var toggles = new List<string>();

            // The payload names its own fields ("label": "Current Rank"), which also covers
            // the customization entries the schema keeps outside its "inputs" lists. The
            // fetched schema still contributes the option tables, so both survive the merge.
            var fields = new List<FormInput>();

            foreach (var entry in values.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryReadInt(entry, "id", out var id))
                {
                    continue;
                }

                var raw = ReadText(entry, "value");
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var schema = form.ById(id);
                var title = ReadText(entry, "label") ?? schema?.Title ?? "";
                var field = new FormInput(id, title, schema?.Type ?? "", schema?.Options ?? EmptyOptions);
                fields.Add(field);

                var label = field.Resolve(raw);

                // Toggles come back as "Yes"/"No"; the "Yes" ones are the buyer's options.
                if (string.Equals(label, "Yes", StringComparison.OrdinalIgnoreCase))
                {
                    toggles.Add(title.Length > 0 ? title : $"#{id}");
                    continue;
                }

                answers[id] = label;
            }

            return BuildFacts(answers, toggles, new BoostingFormConfig(form.GameId, form.CategoryId, fields));
        }
    }

    private static readonly IReadOnlyDictionary<int, string> EmptyOptions =
        new Dictionary<int, string>();

    private static BoostingRequestFacts BuildFacts(
        Dictionary<int, string> answers, List<string> toggles, BoostingFormConfig form)
    {
        string? Pick(FormInput? input) =>
            input is not null && answers.TryGetValue(input.Id, out var value) ? value : null;

        var quantityText = Pick(form.QuantityInput);
        int? quantity = int.TryParse(quantityText, out var parsed) && parsed is > 0 and <= 200
            ? parsed
            : null;

        var roles = new[]
            {
                form.CurrentRankInput, form.DesiredRankInput, form.ServerInput,
                form.QuantityInput, form.NotesInput
            }
            .Where(i => i is not null)
            .Select(i => i!.Id)
            .ToHashSet();

        // Everything else the buyer picked — "Completion Method: Duo", "Specific agents: Jett" —
        // stays searchable so the seller's extras can key off it. Bare numbers are noise.
        var leftovers = answers
            .Where(a => !roles.Contains(a.Key) && !a.Value.All(char.IsDigit))
            .Select(a => form.ById(a.Key)?.Title is { Length: > 0 } title
                ? $"{title}: {a.Value}"
                : a.Value);

        return new BoostingRequestFacts(
            CurrentRank: Pick(form.CurrentRankInput),
            DesiredRank: Pick(form.DesiredRankInput),
            Server: Pick(form.ServerInput),
            Quantity: quantity,
            Notes: Pick(form.NotesInput),
            Toggles: [.. toggles, .. leftovers]);
    }

    internal static BoostingFormConfig ReadFormConfig(string json, string gameId, string categoryId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return BoostingFormConfig.Empty;
        }

        using (document)
        {
            var inputs = new List<FormInput>();
            CollectInputs(document.RootElement, inputs);
            return new BoostingFormConfig(gameId, categoryId, inputs);
        }
    }

    /// <summary>Walks the schema and picks up every "inputs" entry, cards and customizations alike.</summary>
    private static void CollectInputs(JsonElement element, List<FormInput> sink)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("inputs") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var input in property.Value.EnumerateArray())
                        {
                            if (ReadInput(input) is { } parsed)
                            {
                                sink.Add(parsed);
                            }
                        }
                    }

                    CollectInputs(property.Value, sink);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectInputs(item, sink);
                }

                break;
        }
    }

    private static FormInput? ReadInput(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryReadInt(element, "id", out var id))
        {
            return null;
        }

        var options = new Dictionary<int, string>();
        if (element.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (TryReadInt(value, "id", out var optionId) && ReadText(value, "name") is { } name)
                {
                    options[optionId] = name;
                }
            }
        }

        return new FormInput(id, ReadText(element, "title") ?? "", ReadText(element, "type") ?? "", options);
    }

    // ---- Small JSON helpers ----

    /// <summary>Finds the first array with the given name anywhere in the payload.</summary>
    private static bool TryFindArray(JsonElement element, string name, out JsonElement found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        found = property.Value;
                        return true;
                    }

                    if (TryFindArray(property.Value, name, out found))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindArray(item, name, out found))
                    {
                        return true;
                    }
                }

                break;
        }

        found = default;
        return false;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string? ReadText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            _ => null
        };
    }
}
