# Opencode Portable

<img src="assets/logo.png" width="128" alt="Logo Opencode Portable">

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-blue)
[![Release](https://img.shields.io/github/v/release/terzastella/opencode-portable)](https://github.com/terzastella/opencode-portable/releases)

> Pensato per i modelli gratuiti integrati di opencode — niente login, niente
> provider da collegare. I provider esterni (NVIDIA, Anthropic, OpenAI, …)
> **non sono stati testati** con questo progetto: le API key si possono dare
> al volo ma le configurazioni provider non sono verificate.
> Leggi in [English](README.md).

Avvia [opencode](https://github.com/anomalyco/opencode) come **app portatile su
Windows 10/11**: config, dati, cache, stato e temp restano dentro questa cartella. Niente in `%APPDATA%`, `%LOCALAPPDATA%`, `~/.config` o `~/.local/share` — e ciò che opencode crea fuori viene pulito solo se nato durante quella esecuzione.

Testato con opencode `1.18.31` (versione snapshot; gli script di setup usano l'ultima upstream).

## Download

Niente installazione, niente setup: scarica **`OpencodePortable.exe`** dall'[ultima release](https://github.com/terzastella/opencode-portable/releases), mettilo dove vuoi (Desktop, chiavetta), doppio-click. Fine dell'installazione. (I launcher `.cmd`/`.ps1`/`.sh` e il supporto Linux/macOS sono nei sorgenti qui sotto.)

## Avvio rapido

```bash
git clone https://github.com/terzastella/opencode-portable.git
cd opencode-portable
```

(Utenti finali: non serve — vedi [Download](#download) sopra.)

### Opzione A — exe singolo (Windows, consigliato)

Compila una volta (serve .NET 10 SDK), poi distribuisci solo l'exe — 100%
offline a runtime, non scarica mai nulla:

```powershell
.\scripts\publish-fat.ps1
# -> dist/OpencodePortable.exe (~137 MB: runtime + opencode incorporato)
```

Doppio-click: scegli la cartella di lavoro nel dialog, opencode parte lì
dentro. Al primo avvio estrae da solo il binario incorporato (senza rete).
Tutto ciò che l'app scrive — config, cache, stato, tmp, binario — sta in una
cartella per-run `<workspace>/.opencode-portable-<data>-<pid>-<rand>/`,
**cancellata all'uscita, sempre**: non devi cancellare nulla, sparisce da sé.
Anche chiudere la finestra (X / Alt+F4) è gestito: prima il rename atomico in
trash (istantaneo), poi kill dell'albero figlio e delete col tempo rimasto —
gli eventuali resti li spazza il giro dopo. Ogni istanza concorrente ha la sua cartella; i resti da crash vengono spazzati
al giro dopo (i processi vivi mai toccati). Niente viene mai scritto accanto
all'exe, e il dialog riparte sempre da zero (nessuna memoria). Nessun login
salvato: i modelli free integrati funzionano subito, le API key si danno al volo.
Flag: `--workspace <dir>` salta il dialog, `--console` va diretto al
drag-and-drop (per macchine dove il dialog nativo non funziona), `--from-path` riusa opencode
da PATH (stesse regole fail-closed degli script), `--self-test` verifica
il dialog nativo senza mostrarlo. Se il dialog shell non riesce a restituire
la selezione, l'exe ripiega sul drag-and-drop nella console. (Il picker gira
in un processo figlio: anche un crash nativo degrada al fallback invece di
uccidere l'app.)
La cancellazione riprova per secondi contro i file bloccati (immagine in uso,
antivirus) e i resti vengono segnalati, mai silenziati; `--clean <workspace>`
spazza le root stale su richiesta (es. dopo kill da Task Manager).
Un processo guardiano copre anche i kill brutali: qualunque cosa termini il
padre — uscita, crash, X, Task Manager — lo sveglia e cancella la cartella
per-run. Gira come copia rinominata (`opwatch-<pid>-<rand>.exe`) staccata dalla
console, quindi chiudere il gruppo-app da Task Manager non lo uccide; dopo aver
pulito, la copia si autoelimina (quelle stale le spazza l'avvio dopo). Prima
termina eventuali processi ancora in esecuzione da dentro la cartella, e scrive
`%TEMP%\opencode-portable-watchdog-<pid>.log` (diagnostico; rimosso a successo,
lasciato come prova in caso di fallimento).

### Opzione B — script

Scarica il binario per il tuo OS in `bin/` (i binari **non** sono committati su git):

```powershell
# Windows
.\scripts\setup.ps1
# oppure: .\scripts\setup.ps1 -Version 1.18.31
```

```bash
# Linux / macOS
./scripts/setup.sh
# oppure: ./scripts/setup.sh --version 1.18.31
```

Esegui:

```cmd
:: Windows (funziona anche il doppio-click)
opencode-portable.cmd --help
```

```powershell
# Windows PowerShell (supporta pipeline)
.\opencode-portable.ps1 --help
```

```bash
# Linux / macOS
./opencode-portable.sh --help
```

Al primo avvio `config/opencode.json` viene creato da `config/opencode.example.json`.

## Struttura

```
.
├── dist/OpencodePortable.exe  # app Windows single-file (build: scripts/publish-fat.ps1)
├── opencode-portable.cmd   # launcher Windows (doppio-click)
├── opencode-portable.ps1   # launcher PowerShell (mirror)
├── opencode-portable.sh    # launcher Linux / macOS (mirror)
├── bin/                    # qui i binari (ignorata da git, vedi scripts/setup.*)
├── assets/                 # logo.svg, logo.png, opencode-portable.ico
├── config/
│   ├── opencode.example.json  # template versionato
│   └── opencode.json          # tua config locale (ignorata, auto-creata)
├── data/                   # tutti i file runtime (ignorata)
│   ├── config/ data/ cache/ state/ tmp/
├── scripts/
│   ├── setup.ps1           # scarica binario windows-x64/arm64
│   ├── setup.sh            # scarica binario linux/darwin x64/arm64
│   ├── publish-fat.ps1     # compila l'exe all-in-one (incorpora lo zip)
│   ├── pack-release.ps1/.sh  # archivi release puliti con secret scan
│   └── install-shortcuts.ps1  # collegamento Desktop + Start Menu con icona
├── src/OpencodePortable/   # sorgenti C# dell'exe single-file
├── docs/HOW-IT-WORKS.md    # dettagli architettura (in inglese)
├── LICENSE                 # MIT
├── README.md               # versione inglese
└── README.it.md            # questo file
```
(`bin/` contiene `opencode.exe` + `.gitkeep`; `config/` il template versionato;
`dist/`, `data/`, `src/**/payload|bin|obj` sono output ignorati da git.)

## Collegamenti desktop (Windows)

```powershell
.\scripts\install-shortcuts.ps1            # crea collegamenti Desktop + Start Menu
.\scripts\install-shortcuts.ps1 -Uninstall # li rimuove
```

I collegamenti avviano `opencode-portable.cmd` con l'icona di `assets/opencode-portable.ico`. (Di proposito lo script, non l'exe: i collegamenti servono per l'uso veloce da terminale; l'exe è l'app da doppio-click.) Working directory: la root portable.

## Come funziona

| Tema | Cosa fanno i launcher |
|---|---|
| Isolamento | `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME` → `data/...`, e `OPENCODE_CONFIG` → `config/opencode.json` |
| Avvio veloce | `OPENCODE_DISABLE_MODELS_FETCH=1` (salta il fetch bloccante di 10–30s da `models.dev`) |
| Temp | Tmp isolata per run (`TMP/TEMP/TMPDIR` dentro, cancellata all'uscita). Nomi diversi per launcher: `run-HHMMSS-RAND` (cmd), `run-aaaammgg-HHmmss-PID-rand` (ps1), `mktemp run-...-PID-XXX` (sh); l'exe non ha sottolivello (la root è già per-run) |
| Residui da crash | Spazzati al giro dopo (exe + ps1/sh: solo PID morti, mai i vivi; cmd: età >7 giorni, senza check PID — divergenza documentata) |
| Guard anti-sporco | Foto di `%TEMP%\opencode`, `%LOCALAPPDATA%\opencode`, `%APPDATA%\opencode` (Win) o `~/.config/opencode`, `~/.local/share/opencode`, `~/.cache/opencode` (Unix): rimosse solo se create da quella run |
| Igiene env | `.ps1`/`.sh` ripristinano le variabili (`try/finally`, `trap`); istanze concorrenti sicure |

Dettagli in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) (in inglese).

## Policy sui binari

`bin/`, `data/`, `dist/`, `src/**/payload|bin|obj` e `config/opencode.json` locale sono ignorati da git. La repo contiene **solo sorgenti**:

- `scripts/setup.ps1` → `opencode-windows-x64.zip` (o `-arm64`, auto-detect, WOW64-aware) dalle release upstream
- `scripts/setup.sh` → `opencode-linux-x64.tar.gz` / `opencode-darwin-arm64.zip`, ecc. (errore subito su OS/arch non supportati)
- Se `bin/` è vuota i launcher **si rifiutano di partire** (fail-closed): riusare `opencode` da `PATH` richiede opt-in esplicito (`--from-path` — ovunque per exe/cmd, solo primo arg per ps1/sh — o `OPENCODE_ALLOW_PATH_FALLBACK=1`), e il percorso risolto viene sempre stampato. L'exe preferisce il binario incorporato e guarda il PATH solo se manca.

Per distribuire: `pack-release.ps1` fa `opencode-portable-win-x64.zip`, `pack-release.sh` fa `opencode-portable-linux-macos.tar.gz`. Entrambi scansionano i segreti e non includono mai `data/`, `bin/*`, `dist/` o config locali. L'exe all-in-one (`dist/`) non si committa mai (~137 MB).

## Note di sicurezza

- **I binari sono artefatti upstream fidati**: `setup.*` scarica e `publish-fat.ps1` incorpora via HTTPS dalle GitHub releases, senza checksum. Se upstream pubblicherà hash/firme, verificali prima di eseguire.
- **Le credenziali stanno in `data/` (script) o `<workspace>/.opencode-portable-*/` (exe)** (token auth, API key): non condividere né pubblicare quelle cartelle, e non distribuire archivi che le contengono — `pack-release` le esclude di proposito. Chiavetta persa = credenziali perse.
- **Gli zip scaricati hanno il Mark-of-the-Web**: su altri PC Windows può bloccare i launcher `.ps1` (execution policy). Usa `opencode-portable.cmd`, o `Unblock-File`, dopo aver ispezionato il contenuto.
- Questo wrapper isola i file, **non fa sandbox** di opencode: un AI coding agent esegue comandi shell nel tuo workspace — controlla cosa fa, come con qualsiasi installazione upstream.
- **Modello di trust**: il workspace NON è un confine di sicurezza — usa solo cartelle fidate (niente share di rete, cartelle sincronizzate scrivibili da altri, o repo non attendibili); chi può scrivere il workspace può influenzare config, cache e binari estratti. Stesso discorso per `--from-path` / `OPENCODE_ALLOW_PATH_FALLBACK=1`: bypassa il trust del payload incorporato ed esegue il primo `opencode` nel PATH — il percorso viene sempre stampato, verificane l'hash se hai dubbi.
- **Aggiornamenti**: nessun auto-update (`autoupdate: false`); ogni release fissa la sua versione di opencode al build (`publish-fat` risolve latest lì, incorpora + hash). Per aggiornare, scarica la nuova release.
- **Niente Windows Error Reporting**: l'exe silenzia il WER per i propri crash (compresi gli AV supervisionati del picker) così nulla finisce in `ReportArchive`; la diagnostica resta nei nostri `crash-*.log`. `--clean-host` rimuove le nostre tracce host-side (vecchi archivi WER, telemetria watchdog, dir crash TEMP) — per i preesistenti può servire admin.

## Flag exe e exit code

| Flag | Effetto |
|---|---|
| `--workspace <dir>` / `--workspace=<dir>` | Salta il picker, parte diretto lì |
| `--console` | Salta il dialog, drag-and-drop diretto |
| `--from-path` | Opt-in a riusare `opencode` da PATH se manca il binario (ovunque prima di `--`; anche `OPENCODE_ALLOW_PATH_FALLBACK=1`) |
| `--picker-exe <path>` / `--picker-exe=<path>` | Altro eseguibile come picker figlio (diagnostica) |
| `--pick-folder [titolo] [iniziale]` | Modalità figlio: dialog nativo, protocollo `PICK-OK` (0) / `PICK-CANCEL` (1) / `PICK-ERROR` (2) |
| `--watch <pid> <ticks> <dir>` | Modalità guardiano (avviata da sola, hop orfano) |
| `--clean <dir>` | Spazza le root stale senza avviare nulla; errore se manca la dir |
| `--clean-host` | Rimuove le nostre tracce host (archivi WER, telemetria, dir crash TEMP); sempre exit 0 |
| `--self-test` | Smoke test COM non-UI (istanzia + opzioni + check WER, niente dialog) |
| `--test-fallback`, `--test-close`, `--test-robust-delete`, `--test-watchdog`, `--test-watchdog-proc`, `--test-watchdog-copy`, `--test-detached` | Self-test interni (vedi sorgenti) |
| `--report-console <file>` | Scrive l'handle console del processo (sonda distacco) |
| `--has-payload` | Info sul payload incorporato; exit 0 se presente (usato da publish-fat) |
| `--` | Tutto ciò che segue va a opencode così com'è, anche flag da launcher |

Exit code: `0` = ok / picker annullato / `--clean-host` fatto; `1` = workspace invalida, binario/config mancanti, fallback fallito, self-test fallito, `--clean` senza dir, `--watch` malformato, abort guardiano, errore fatale (MessageBox + `crash-*.log` con righe); `2` = errore interno figlio picker (il padre ripiega); altrimenti propaga quello di opencode.

## Config

Flusso script: modifica `config/opencode.json` (default sensati per portable). Il template è `config/opencode.example.json`; cancella `opencode.json` per tornare ai default.

Flusso exe: config **effimera per run** (`<root>/config/opencode.json`, dal template accanto all'exe o dal default incorporato), cancellata all'uscita con tutto il resto.

Nota: questo progetto punta ai modelli free integrati di opencode — niente login,
niente provider esterni da collegare. Login/auth non persistono mai per l'exe
(root fresca a ogni run); eventuali API key di altri modelli si danno al volo,
mai salvate (vedi [HOW-IT-WORKS](docs/HOW-IT-WORKS.md) § Auth note).

## Licenza

[MIT](LICENSE) — © 2026 Terzastella.
