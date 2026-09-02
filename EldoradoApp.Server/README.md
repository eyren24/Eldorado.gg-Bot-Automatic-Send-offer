# EldoradoApp.Server

Backend ASP.NET Core per licenze, abbonamenti, dispositivi, configurazioni e audit del bot.
Non contiene mai la chiave privata di firma: continua a emettere le chiavi con
`tools/EldoradoKeygen` e configura qui solo la chiave pubblica.

## Avvio locale

1. Avvia PostgreSQL:

   ```powershell
   docker compose up -d postgres
   ```

2. Imposta una chiave amministrativa che non sia quella dell'esempio:

   ```powershell
   $env:Admin__ApiKey = "genera-qui-una-stringa-lunga-casuale"
   ```

3. Avvia l'API:

   ```powershell
   dotnet run --project .\EldoradoApp.Server\EldoradoApp.Server.csproj --urls http://localhost:8080
   ```

Alla prima partenza `Database:AutoCreate` crea le tabelle PostgreSQL. Per un deploy
di produzione, mantieni `AutoCreate` solo durante il bootstrap e genera migrazioni EF
versionate prima degli aggiornamenti successivi.

## Collegare il client

Nella scheda **Licenza** dell'app inserisci `http://localhost:8080` come endpoint
server, salvalo e poi abilita «richiedi verifica server». Il client scambia una
licenza locale valida per un token legato a quel PC. Il token viene protetto da DPAPI
ed è revocabile dal database.

## Endpoint principali

- `POST /api/v1/activations` — verifica la chiave firmata e crea/rinnova il token del PC.
- `GET /api/v1/entitlements/current` — licenza, abbonamento e kill switch effettivi.
- `GET|PUT /api/v1/configuration` — backup JSON della configurazione non sensibile.
- `POST /api/v1/automation-events` — audit delle azioni del bot.
- `/api/v1/admin/*` — gestione licenze, ordini e policy; richiede `X-Eldorado-Admin-Key`.

Non esporre l'API in HTTP pubblico: davanti a Internet usa HTTPS e imposta una chiave
amministrativa lunga tramite variabile d'ambiente o secret manager.
