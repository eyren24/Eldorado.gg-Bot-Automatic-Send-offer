using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EldoradoApp.Services;

/// <summary>Durable state for post-offer chat delivery. A lost browser confirmation is never an excuse to send again.</summary>
public sealed class OfferMessageOutbox
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldoradoApp");
    private static readonly string FilePath = Path.Combine(Directory_, "message-outbox.json");
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private List<OutboxEntry>? _entries;

    public OutboxReservation Reserve(OutgoingOfferMessage message)
    {
        lock (Sync)
        {
            var entries = Load();
            var id = Fingerprint(message);
            var entry = entries.FirstOrDefault(x => x.Id == id);

            if (entry is { State: OutboxState.Sent })
            {
                return new OutboxReservation(id, OutboxDecision.AlreadySent, entry.Detail);
            }

            if (entry is { State: OutboxState.Unknown or OutboxState.Staged or OutboxState.Sending })
            {
                return new OutboxReservation(id, OutboxDecision.NeedsReview, entry.Detail);
            }

            if (entry is null)
            {
                entry = new OutboxEntry
                {
                    Id = id,
                    RequestId = message.RequestId,
                    BuyerId = message.BuyerId,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                entries.Add(entry);
            }

            entry.State = OutboxState.Sending;
            entry.Detail = "Invio in corso";
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Save(entries);
            return new OutboxReservation(id, OutboxDecision.Send, "");
        }
    }

    public void Complete(OutboxReservation reservation, OfferMessageResult result)
    {
        if (reservation.Decision != OutboxDecision.Send)
        {
            return;
        }

        lock (Sync)
        {
            var entries = Load();
            var entry = entries.FirstOrDefault(x => x.Id == reservation.Id);
            if (entry is null)
            {
                return;
            }

            entry.State = result.Outcome switch
            {
                MessageOutcome.Sent => OutboxState.Sent,
                MessageOutcome.Staged => OutboxState.Staged,
                MessageOutcome.Unknown => OutboxState.Unknown,
                _ => OutboxState.Failed
            };
            entry.Detail = result.Detail;
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Save(entries);
        }
    }

    private List<OutboxEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<OutboxEntry>>(File.ReadAllText(FilePath), Json) ?? []
                : [];
        }
        catch
        {
            _entries = [];
        }

        var oldest = DateTimeOffset.UtcNow.AddDays(-90);
        _entries.RemoveAll(x => x.UpdatedAtUtc < oldest);
        return _entries;
    }

    private static void Save(List<OutboxEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Message outbox not persisted: {ex.Message}");
        }
    }

    private static string Fingerprint(OutgoingOfferMessage message)
    {
        var banner = message.BannerPath is { Length: > 0 } path && File.Exists(path)
            ? $"{Path.GetFullPath(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}"
            : "";
        var source = string.Join("\n", message.RequestId, message.BuyerId ?? "", message.Text, banner);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private sealed class OutboxEntry
    {
        public string Id { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string? BuyerId { get; set; }
        public OutboxState State { get; set; }
        public string Detail { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record OutboxReservation(string Id, OutboxDecision Decision, string Detail);

public enum OutboxDecision
{
    Send,
    AlreadySent,
    NeedsReview
}

public enum OutboxState
{
    Sending,
    Sent,
    Staged,
    Unknown,
    Failed
}
