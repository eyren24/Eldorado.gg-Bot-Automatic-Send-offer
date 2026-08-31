# Generatore di chiavi — Eldorado Bot

Fabbrica di licenze offline. Non parla con l'app, non parla con un server, non ha bisogno
di un database: firma pochi byte con una chiave privata che sta **solo sul tuo PC**, e tu
consegni il risultato a mano su Discord.

L'app ha dentro solo la metà **pubblica**: sa verificare una chiave, non sa crearne.

```bash
dotnet build tools/EldoradoKeygen/EldoradoKeygen.csproj -c Release
```

L'eseguibile esce in `tools/EldoradoKeygen/bin/Release/net10.0/keygen.exe`.

## Una volta sola, all'inizio

```bash
keygen init
```

Crea la coppia di chiavi, ti chiede una passphrase e stampa la riga da incollare in
`EldoradoApp/Services/Licensing/LicenseOptions.cs`:

```csharp
public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZ...";
```

Poi ricompila l'app. **Finché quella costante è vuota l'app rifiuta ogni chiave**, la tua
compresa: è voluto, così una build distribuita per sbaglio non accetta niente.

Nello stesso file imposta anche il tuo contatto:

```csharp
public const string DiscordContact = "@iltuonome";
public const string DiscordInvite  = "https://discord.gg/....";   // opzionale
```

### La chiave privata

Vive in `%AppData%\EldoradoKeygen\private.key`, cifrata con la tua passphrase
(PBKDF2-SHA256 + AES-GCM). **Fanne una copia** su una chiavetta o nel password manager.

Se la perdi non puoi più emettere né rinnovare chiavi per le build già in mano ai clienti:
l'unica uscita sarebbe pubblicare una nuova versione dell'app con una nuova chiave
pubblica, e rifare tutte le licenze. Se te la rubano, chiunque può stampare chiavi gratis.

## Vendere una chiave

1. Il cliente apre l'app, che gli chiede l'attivazione e gli mostra l'**ID di questo PC**
   (`XXXX-XXXX-XXXX-XXXX`). Te lo manda su Discord insieme al pagamento.
2. Tu emetti:

   ```bash
   keygen new --pc YEBR-1BSV-C38X-XMMW --days 30 --note "Discord: tizio#1234"
   ```

3. Il comando stampa un blocco già pronto da incollare in chat. Lui la incolla nell'app e
   preme Attiva.
4. Al rinnovo ripeti il punto 2: la chiave nuova sostituisce la vecchia dalla scheda
   «Licenza», senza reinstallare niente.

Opzioni utili:

| | |
|---|---|
| `--floating` | chiave senza vincolo di PC — usala per te (`--days 3650`) |
| `--start 2026-09-01` | decorrenza diversa da oggi, per un periodo già pagato |
| `keygen list [--all]` | chi ha cosa e quando scade |
| `keygen verify ELDO-…` | dice se una chiave è tua, quando un cliente non riesce ad attivare |
| `keygen pubkey` | ristampa la riga per `LicenseOptions` |

Tutto quello che emetti finisce in `%AppData%\EldoradoKeygen\ledger.csv`, apribile in Excel.
Quello è il tuo «database»: l'app non lo legge mai.

## Annullare una chiave già consegnata (opzionale)

Serve solo se vuoi poter spegnere una chiave prima della scadenza (chargeback, uno che la
rivende). Senza, una chiave venduta vive fino alla sua data e basta.

```bash
keygen revoke BY4F45EQ
```

Riscrive `%AppData%\EldoradoKeygen\revoked.json`, **firmato**. Pubblicalo a un URL
raggiungibile — un gist pubblico va benissimo — e mettilo in `LicenseOptions`:

```csharp
public const string RevocationListUrl = "https://gist.githubusercontent.com/.../revoked.json";
```

L'app lo scarica all'avvio e ogni 6 ore, e ne verifica la firma con la stessa chiave
pubblica: una lista non firmata da te viene ignorata. Se il file è irraggiungibile l'app
tiene buona l'ultima copia e va avanti — un cliente pagante non deve restare fuori perché
GitHub è giù.

## Formato della chiave

```
payload   21 B : versione | flag | codice chiave | emissione | scadenza | impronta PC
firma     64 B : ECDSA P-256 / SHA-256
                 -> 85 byte -> 136 caratteri base32 -> ELDO-XXXXXXXX-... (17 gruppi)
```

Il formato sta in `EldoradoApp/Services/Licensing/LicenseCodec.cs`, che questo progetto
**compila direttamente** (vedi gli `<Compile Include>` nel `.csproj`): app e generatore non
possono divergere di un byte.

## Cosa regge e cosa no

Vale la pena essere espliciti, perche' «protezione anti-copia» e' un termine che promette
sempre piu' di quanto mantenga.

### Regge

**Passare la chiave a un amico.** Ogni chiave e' firmata sopra l'impronta di *quel* PC.
Su un altro computer non parte, punto.

**Falsificare l'ID macchina.** L'impronta e' lo SHA-256 di cinque sorgenti indipendenti:
`MachineGuid`, identita' dell'installazione di Windows, numero di serie del volume di
sistema, CPU e scheda madre. Le ultime due stanno in `HKLM\HARDWARE`, che e' un ramo
*volatile*: Windows lo ricostruisce dal firmware a ogni avvio, quindi non basta un
`regedit` e comunque non sopravvive a un riavvio. E dal codice a 16 caratteri che il
cliente ti manda non si torna indietro alle cinque sorgenti: e' un hash, non un valore
da impostare.

**Copiare `license.bin` su un altro PC.** E' cifrato con DPAPI legato all'utente Windows:
su un'altra macchina non si apre nemmeno.

**Modificare la scadenza dentro la chiave.** Sposti un carattere e la firma ECDSA non
torna. Per forgiarne una servirebbe la chiave privata.

**Spostare indietro l'orologio.** L'app tiene un segnaposto del tempo che ha gia' visto
passare, e ricorda le chiavi che ha visto finire. Il segnaposto vive in **due** posti
scollegati — una voce di registro sotto HKCU e un file sotto `%LocalAppData%`, nessuno dei
due accanto a `license.bin` — e ognuno dei due ricostruisce l'altro. Cancellare la licenza
e riappiccicare la vecchia chiave con la data indietro non funziona.

**Aggirare la UI.** Il controllo non e' solo sul pulsante: il motore delle offerte lo
rifa da capo, con codice suo, a ogni ciclo e prima di ogni singolo invio.

### Non regge

**Chi decompila e ricompila l'exe.** E' codice .NET: con abbastanza competenza si apre, si
toglie il controllo e si rimette insieme. I controlli in piu' punti e il bundle single-file
compresso rendono la cosa un lavoro vero, non un doppio clic su un tool scaricato — ma un
reverser capace vince. L'unico rimedio robusto sarebbe far dipendere il funzionamento da un
server, che e' esattamente cio' che questo impianto evita.
Se vuoi alzare ancora l'asticella, la mossa e' un offuscatore (Obfuscar, .NET Reactor) nel
passo di publish; va provato bene, perche' WPF risolve parecchie cose per nome.

**Chi trova e cancella entrambe le copie del segnaposto E sposta l'orologio.** A quel punto
una chiave scaduta ritorna utilizzabile su quel PC. Senza server non si chiude: la lista di
revoca e' la contromossa, ma richiede che l'app riesca a raggiungere l'URL.

**Un cliente che ti manda l'ID di un PC e poi lo usa in una VM clonata.** Lo snapshot ha le
stesse cinque sorgenti.

### Se un giorno vuoi chiuderla davvero

Serve un server che tenga il conto delle attivazioni: la stessa chiave che chiama da tre PC
diventa visibile e bloccabile. E' l'unica differenza sostanziale, e costa un piccolo backend
piu' il fatto che senza rete i clienti non lavorano. Il formato delle chiavi non cambierebbe:
si aggiungerebbe una chiamata di attivazione accanto alla verifica offline che c'e' gia'.
