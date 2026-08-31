using System.Globalization;
using System.Text;

namespace EldoradoKeygen;

/// <summary>One sold key, as recorded on your own machine.</summary>
/// <remarks>
/// This file is the closest thing to a database in the whole design, and it is deliberately
/// a CSV you can open in Excel: it exists so you can answer "who bought what, and when does
/// it run out" - the app never reads it and never needs to.
/// </remarks>
public sealed record LedgerEntry(
    string KeyId,
    DateOnly Issued,
    DateOnly Expires,
    string MachineId,
    string Note,
    bool Revoked,
    string Key)
{
    public bool IsFloating => MachineId.Length == 0;

    /// <summary>Where the key stands today, in one word for the listing.</summary>
    public string Status(DateOnly today) =>
        Revoked ? "ANNULLATA"
        : today > Expires ? "scaduta"
        : "attiva";

    public int DaysLeft(DateOnly today) => Math.Max(0, Expires.DayNumber - today.DayNumber + 1);
}

/// <summary>Append-only ledger of everything the generator has ever minted.</summary>
public static class Ledger
{
    private const string Header = "keyId,issued,expires,machineId,note,revoked,key";

    public static List<LedgerEntry> Load(string path)
    {
        var entries = new List<LedgerEntry>();

        if (!File.Exists(path))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var f = SplitCsv(line);
            if (f.Count < 7)
            {
                continue;   // hand-edited row: skip rather than lose the whole file
            }

            entries.Add(new LedgerEntry(
                f[0],
                ParseDate(f[1]),
                ParseDate(f[2]),
                f[3],
                f[4],
                bool.TryParse(f[5], out var revoked) && revoked,
                f[6]));
        }

        return entries;
    }

    public static void Save(string path, IEnumerable<LedgerEntry> entries)
    {
        var sb = new StringBuilder(Header).AppendLine();

        foreach (var e in entries)
        {
            sb.Append(Quote(e.KeyId)).Append(',')
              .Append(e.Issued.ToString("yyyy-MM-dd")).Append(',')
              .Append(e.Expires.ToString("yyyy-MM-dd")).Append(',')
              .Append(Quote(e.MachineId)).Append(',')
              .Append(Quote(e.Note)).Append(',')
              .Append(e.Revoked ? "true" : "false").Append(',')
              .Append(Quote(e.Key))
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static void Append(string path, LedgerEntry entry)
    {
        var all = Load(path);
        all.Add(entry);
        Save(path, all);
    }

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : DateOnly.MinValue;

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    /// <summary>Minimal RFC 4180 reader - enough for the fields this tool writes.</summary>
    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"')
                {
                    sb.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
