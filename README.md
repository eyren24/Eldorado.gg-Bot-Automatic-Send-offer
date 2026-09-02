# 🤖 Eldorado.gg Auto-Offer Bot

Un bot di automazione avanzato e fluido per Eldorado.gg, progettato per gestire le vendite, intercettare la chat in tempo reale e inviare offerte automatiche su tutte le categorie senza blocchi o rallentamenti.

## 🏗️ Architettura

Il sistema è suddiviso in tre componenti principali per massimizzare stabilità, scalabilità e reattività:

*   **Worker (Node.js + TypeScript + Playwright):** Il motore di automazione. Gestisce sessioni browser isolate in background e intercetta direttamente il traffico di rete (API/WebSocket) per leggere i messaggi in tempo reale, eliminando i bug legati allo scraping del DOM.
*   **Backend (.NET 9 WebAPI):** Il core logico. Gestisce il database, memorizza le configurazioni dinamiche per ogni categoria e orchestra i messaggi e gli eventi in tempo reale tramite SignalR.
*   **Dashboard (React + TypeScript):** L'interfaccia utente. Un pannello di controllo reattivo per monitorare il bot, le chat attive e configurare dinamicamente i prezzi e le categorie.

## ✨ Funzionalità Principali

*   **Zero UI Freezes:** Nessun blocco dell'interfaccia grazie all'architettura a eventi di Node e all'intercettazione di rete.
*   **Supporto Multi-Categoria Universale:** Parametri e logiche di offerta iniettati dinamicamente, rendendo il bot compatibile con qualsiasi gioco o servizio sulla piattaforma.
*   **Isolamento del Contesto (Context Isolation):** Esecuzione di operazioni parallele in `BrowserContext` separati per evitare collisioni.
*   **Gestione Sessione Avanzata:** Salvataggio automatico di cookie e local storage per mantenere il login attivo e ridurre al minimo l'intervento dei sistemi antibot.

## 🚀 Setup e Installazione

### Prerequisiti
*   Node.js (v18+)
*   .NET 9 SDK
*   Manager pacchetti (npm/yarn)

### 1. Avvio API Backend (.NET 9)
```bash
cd src/Backend
dotnet restore
dotnet run
