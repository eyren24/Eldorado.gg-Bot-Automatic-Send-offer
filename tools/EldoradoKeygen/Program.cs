using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EldoradoApp.Services.Licensing;
using EldoradoKeygen;

// The offline key factory. It never talks to the app, to a server or to the network: it
// signs a few bytes with a private key that lives only on this machine, and you deliver
// the result over Discord by hand.

Console.OutputEncoding = Encoding.UTF8;

var home = Environment.GetEnvironmentVariable("ELDORADO_KEYGEN_HOME") is { Length: > 0 } custom
    ? custom
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoKeygen");

Directory.CreateDirectory(home);

var vaultPath = Path.Combine(home, "private.key");
var publicPath = Path.Combine(home, "public.txt");
var ledgerPath = Path.Combine(home, "ledger.csv");
var revokedPath = Path.Combine(home, "revoked.json");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

try
{
    return command switch
    {
        "init" => Init(),
        "new" => Mint(),
        "list" => List(),
        "revoke" => SetRevoked(true),
        "unrevoke" => SetRevoked(false),
        "verify" => Verify(),
        "pubkey" => PublicKey(),
        _ => Help()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERRORE: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------- init

int Init()
{
    if (File.Exists(vaultPath) && !Flag("--force"))
    {
        Console.WriteLine($"Esiste gia' una chiave privata in {vaultPath}.");
        Console.WriteLine("Rigenerarla invaliderebbe TUTTE le chiavi gia' vendute.");
        Console.WriteLine("Se e' davvero quello che vuoi: keygen init --force");
        return 1;
    }

    Console.WriteLine("Creo la coppia di chiavi di firma (ECDSA P-256).");
    Console.WriteLine("La passphrase protegge la chiave privata: senza non potrai piu' emettere chiavi.");
    Console.WriteLine();

    var passphrase = ReadPassphrase("Passphrase          : ");
    if (passphrase.Length < 8)
    {
        Console.Error.WriteLine("Servono almeno 8 caratteri.");
        return 1;
    }

    if (ReadPassphrase("Ripeti la passphrase: ") != passphrase)
    {
        Console.Error.WriteLine("Le due passphrase non coincidono.");
        return 1;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    KeyVault.Save(vaultPath, ecdsa, passphrase);
    File.WriteAllText(publicPath, publicKey);

    Console.WriteLine();
    Console.WriteLine("Fatto.");
    Console.WriteLine($"  chiave privata : {vaultPath}   <-- NON condividerla, NON perderla");
    Console.WriteLine($"  chiave pubblica: {publicPath}");
    Console.WriteLine();
    Console.WriteLine("1) Incolla questa riga in EldoradoApp/Services/Licensing/LicenseOptions.cs:");
    Console.WriteLine();
    Console.WriteLine($"    public const string PublicKey = \"{publicKey}\";");
    Console.WriteLine();
    Console.WriteLine("2) Ricompila l'app. Da quel momento accetta solo le chiavi firmate da qui.");
    Console.WriteLine();
    Console.WriteLine("Fai una copia di private.key in un posto sicuro (password manager, chiavetta).");
    Console.WriteLine("Se la perdi non potrai piu' rinnovare le licenze dei clienti gia' serviti:");
    Console.WriteLine("l'unica via d'uscita sarebbe una nuova build con una nuova chiave pubblica.");

    return 0;
}

// ---------------------------------------------------------------- new

int Mint()
{
    var days = int.TryParse(Option("--days"), out var d) ? d : 30;
    var machineId = Option("--pc") ?? "";
    var note = Option("--note") ?? "";
    var floating = Flag("--floating");

    if (days is < 1 or > 3650)
    {
        Console.Error.WriteLine("--days deve stare fra 1 e 3650.");
        return 1;
    }

    byte[] deviceTag = [];

    if (!floating)
    {
        if (machineId.Length == 0)
        {
            Console.Error.WriteLine("Serve l'ID macchina del cliente: --pc XXXX-XXXX-XXXX-XXXX");
            Console.Error.WriteLine("Lo legge dalla schermata di attivazione dell'app (\"ID DI QUESTO PC\").");
            Console.Error.WriteLine("Per una chiave che gira su qualsiasi PC (la tua): --floating");
            return 1;
        }

        if (!LicenseCodec.TryParseMachineId(machineId, out deviceTag))
        {
            Console.Error.WriteLine($"ID macchina non valido: '{machineId}'.");
            Console.Error.WriteLine("Devono essere 16 caratteri, tipo K3M9-XR2T-8QF5-1WBZ.");
            return 1;
        }
    }

    // Decorrenza: normally today, but a paid-for period can have started earlier.
    var issued = DateOnly.FromDateTime(DateTime.UtcNow);

    if (Option("--start") is { Length: > 0 } start)
    {
        if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", null, DateTimeStyles.None, out issued))
        {
            Console.Error.WriteLine($"--start deve essere una data yyyy-MM-dd, non '{start}'.");
            return 1;
        }
    }

    using var ecdsa = KeyVault.Load(vaultPath, ReadPassphrase("Passphrase: "));

    var expires = issued.AddDays(days - 1);
    var keyId = LicenseCodec.NewKeyId();

    var payload = LicenseCodec.BuildPayload(keyId, issued, expires, deviceTag);
    var key = LicenseCodec.Format(payload, LicenseCodec.Sign(payload, ecdsa));

    Ledger.Append(ledgerPath, new LedgerEntry(
        keyId, issued, expires, floating ? "" : machineId.ToUpperInvariant(), note, false, key));

    Console.WriteLine();
    Console.WriteLine($"Codice chiave : {keyId}");
    Console.WriteLine($"Durata        : {days} giorni  ({issued:dd/MM/yyyy} -> {expires:dd/MM/yyyy})");
    Console.WriteLine($"Vincolo       : {(floating ? "nessuno (gira su qualsiasi PC)" : machineId.ToUpperInvariant())}");
    if (note.Length > 0)
    {
        Console.WriteLine($"Nota          : {note}");
    }

    Console.WriteLine();
    Console.WriteLine("---- da incollare al cliente su Discord ----");
    Console.WriteLine();
    Console.WriteLine("Ecco la tua chiave per Eldorado Bot. Aprila l'app, incollala nel riquadro");
    Console.WriteLine($"e premi Attiva. Scade il {expires:dd/MM/yyyy}, poi mi riscrivi per il rinnovo.");
    Console.WriteLine();
    Console.WriteLine("```");
    Console.WriteLine(key);
    Console.WriteLine("```");
    Console.WriteLine();
    Console.WriteLine("-------------------------------------------");
    Console.WriteLine($"Registrata in {ledgerPath}");

    return 0;
}

// ---------------------------------------------------------------- list

int List()
{
    var entries = Ledger.Load(ledgerPath);
    if (entries.Count == 0)
    {
        Console.WriteLine($"Nessuna chiave emessa ({ledgerPath} non esiste ancora).");
        return 0;
    }

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var all = Flag("--all");

    Console.WriteLine($"{"CODICE",-10} {"STATO",-10} {"SCADE",-11} {"GG",-4} {"PC",-20} NOTA");
    Console.WriteLine(new string('-', 92));

    var shown = 0;
    foreach (var e in entries.OrderBy(x => x.Expires))
    {
        var status = e.Status(today);
        if (!all && status != "attiva")
        {
            continue;
        }

        shown++;
        Console.WriteLine($"{e.KeyId,-10} {status,-10} {e.Expires:dd/MM/yyyy}  {e.DaysLeft(today),-4} " +
                          $"{(e.IsFloating ? "(qualsiasi)" : e.MachineId),-20} {e.Note}");
    }

    Console.WriteLine();
    Console.WriteLine(all
        ? $"{entries.Count} chiavi in totale."
        : $"{shown} attive su {entries.Count} totali. Con --all vedi anche scadute e annullate.");

    return 0;
}

// ---------------------------------------------------------------- revoke / unrevoke

int SetRevoked(bool revoked)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"Uso: keygen {(revoked ? "revoke" : "unrevoke")} CODICE");
        return 1;
    }

    var keyId = args[1].Trim().ToUpperInvariant();
    var entries = Ledger.Load(ledgerPath);
    var index = entries.FindIndex(e => e.KeyId.Equals(keyId, StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        Console.Error.WriteLine($"Nessuna chiave con codice {keyId} nel registro.");
        return 1;
    }

    entries[index] = entries[index] with { Revoked = revoked };
    Ledger.Save(ledgerPath, entries);

    // The app only learns about this from the published file, and only if it is signed.
    using var ecdsa = KeyVault.Load(vaultPath, ReadPassphrase("Passphrase: "));

    var ids = entries.Where(e => e.Revoked).Select(e => e.KeyId).ToList();
    var digest = LicenseCodec.RevocationDigest(ids);

    var document = new
    {
        revoked = ids.Select(id => id.ToUpperInvariant()).Distinct().Order().ToList(),
        signature = Convert.ToBase64String(LicenseCodec.Sign(digest, ecdsa)),
        updated = DateTimeOffset.UtcNow
    };

    File.WriteAllText(revokedPath,
        JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine($"{keyId}: {(revoked ? "annullata" : "riabilitata")}.");
    Console.WriteLine($"Lista aggiornata: {revokedPath} ({ids.Count} chiavi annullate)");
    Console.WriteLine();
    Console.WriteLine("Perche' abbia effetto sui PC dei clienti, pubblica quel file a un URL");
    Console.WriteLine("raggiungibile (basta un gist pubblico) e mettilo in LicenseOptions.RevocationListUrl.");

    return 0;
}

// ---------------------------------------------------------------- verify

int Verify()
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Uso: keygen verify ELDO-....");
        return 1;
    }

    var text = string.Join(' ', args.Skip(1));

    if (!LicenseCodec.TryDecode(text, out var payload, out var signature, out var error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    if (!File.Exists(publicPath))
    {
        Console.Error.WriteLine($"Manca {publicPath}: esegui 'keygen init'.");
        return 1;
    }

    var info = LicenseCodec.Read(payload);
    var genuine = LicenseCodec.Verify(payload, signature, File.ReadAllText(publicPath));
    var today = DateOnly.FromDateTime(DateTime.UtcNow);

    Console.WriteLine($"Firma      : {(genuine ? "VALIDA (emessa da te)" : "NON VALIDA")}");
    Console.WriteLine($"Codice     : {info.KeyId}");
    Console.WriteLine($"Emessa     : {info.Issued:dd/MM/yyyy}");
    Console.WriteLine($"Scade      : {info.Expires:dd/MM/yyyy}  ({(today > info.Expires ? "scaduta" : $"{info.Expires.DayNumber - today.DayNumber + 1} giorni")})");
    Console.WriteLine($"Vincolo PC : {(info.IsDeviceLocked ? "si'" : "no (gira ovunque)")}");

    var entry = Ledger.Load(ledgerPath).FirstOrDefault(e => e.KeyId == info.KeyId);
    if (entry is not null)
    {
        Console.WriteLine($"Registro   : {entry.MachineId} {entry.Note} {(entry.Revoked ? "[ANNULLATA]" : "")}".TrimEnd());
    }

    return genuine ? 0 : 1;
}

// ---------------------------------------------------------------- pubkey / help

int PublicKey()
{
    if (!File.Exists(publicPath))
    {
        Console.Error.WriteLine($"Manca {publicPath}: esegui 'keygen init'.");
        return 1;
    }

    Console.WriteLine($"    public const string PublicKey = \"{File.ReadAllText(publicPath).Trim()}\";");
    return 0;
}

int Help()
{
    Console.WriteLine($"""
        Eldorado Bot - generatore di chiavi (offline)

        Cartella dati: {home}
          private.key  chiave privata cifrata con passphrase - l'unico segreto
          public.txt   chiave pubblica da incollare in LicenseOptions.PublicKey
          ledger.csv   registro di tutte le chiavi emesse
          revoked.json lista firmata delle chiavi annullate

        COMANDI

          keygen init [--force]
              Crea la coppia di chiavi. Una volta sola, all'inizio.

          keygen new --pc XXXX-XXXX-XXXX-XXXX [--days 30] [--note "Discord: tizio"]
          keygen new --floating [--days 3650] [--note "la mia copia"]
              Emette una chiave. Con --pc e' legata a quel computer; con --floating
              gira ovunque (usala per te, o per un cliente che cambia PC spesso).
              Con --start 2026-09-01 la decorrenza parte da quella data invece che
              da oggi, per un periodo gia' pagato che e' cominciato prima.

          keygen list [--all]
              Chi ha cosa e quando scade.

          keygen revoke CODICE  |  keygen unrevoke CODICE
              Annulla (o riabilita) una chiave gia' consegnata e riscrive revoked.json.

          keygen verify ELDO-...
              Dice se una chiave e' tua, per chi ha problemi ad attivare.

          keygen pubkey
              Ristampa la riga da incollare in LicenseOptions.

        FLUSSO TIPICO
          1. Il cliente ti scrive su Discord e ti manda l'ID del suo PC.
          2. keygen new --pc <quell'ID> --days 30 --note "Discord: nome"
          3. Gli incolli il blocco che stampa il comando.
          4. Fra 30 giorni paga e ripeti il passo 2: incolla la nuova chiave e riparte.
        """);

    return 0;
}

// ---------------------------------------------------------------- argument helpers

string? Option(string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static string ReadPassphrase(string prompt)
{
    // Set it in the environment to script the tool; otherwise it is typed and masked.
    if (Environment.GetEnvironmentVariable("ELDORADO_KEYGEN_PASSPHRASE") is { Length: > 0 } fromEnv)
    {
        return fromEnv;
    }

    Console.Write(prompt);

    // Console.ReadKey throws outright with no console attached or with stdin on a pipe
    // (an IDE terminal, a script), which would turn a scripted run into a crash.
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    var sb = new StringBuilder();

    while (true)
    {
        ConsoleKeyInfo key;

        try
        {
            key = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            return Console.ReadLine() ?? "";
        }


        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
}
