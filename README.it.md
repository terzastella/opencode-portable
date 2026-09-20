<p align="center">
  <img src="assets/logo.png" width="160" alt="Logo Opencode Portable">
</p>

<h1 align="center">Opencode Portable</h1>

<p align="center">Esegui <a href="https://github.com/anomalyco/opencode">opencode</a> come vera app portatile su <b>Windows 10 / 11</b>: doppio-click, scegli la cartella, lavora.<br>Chiudila — uscita, X, Ctrl+C, persino kill da Task Manager — e l'app rimuove i suoi file (verificabile con la checklist sotto; <a href="#verifica-zero-tracce">nota sui limiti OS</a>).</p>

<p align="center">
  <a href="README.md"><img src="https://img.shields.io/badge/English-read-lightgrey" alt="English"></a>
  <a href="README.it.md"><img src="https://img.shields.io/badge/Italiano-selezionato-blue" alt="Italiano"></a>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-blue" alt="Windows 10 | 11">
  <a href="https://github.com/terzastella/opencode-portable/releases"><img src="https://img.shields.io/github/v/release/terzastella/opencode-portable" alt="Release"></a>
  <a href="https://github.com/terzastella/opencode-portable/actions"><img src="https://github.com/terzastella/opencode-portable/actions/workflows/build.yml/badge.svg" alt="CI build"></a>
</p>

> Pensato per i modelli gratuiti integrati di opencode — niente login, niente
> provider da collegare. I provider esterni (NVIDIA, Anthropic, OpenAI, …)
> **non sono stati testati** con questo progetto: le API key si possono dare
> al volo ma le configurazioni provider non sono verificate.
>
> Testato con opencode `1.18.31` (versione snapshot; gli script di setup usano `UPSTREAM_VERSION` pinnata).

## Download

Niente installazione, niente setup: scarica **`OpencodePortable.exe`** dall'[ultima release](https://github.com/terzastella/opencode-portable/releases),
mettilo dove vuoi (Desktop, chiavetta), doppio-click. Fine dell'installazione.

Verifica il download con lo `.sha256` pubblicato (allegato all'exe in ogni release, insieme alla SBOM CycloneDX `OpencodePortable.exe.cdx.json`):

```powershell
if ((Get-FileHash OpencodePortable.exe -Algorithm SHA256).Hash.ToLower() -ne (Get-Content OpencodePortable.exe.sha256)) { throw 'hash mismatch' }
```

## Avvio rapido

1. **Doppio-click** su `OpencodePortable.exe`.
2. **Scegli la cartella di lavoro** (dialog esplora-file, o trascinala nella console).
3. **Lavora.** Alla chiusura, tutto ciò che l'app ha scritto — config, cache,
   stato, tmp, binario — viene cancellato con la cartella per-run. Niente viene
   mai scritto accanto all'exe.

## Perché portatile

| Aspetto | Cosa ottieni |
|---|---|
| Isolamento | Config, dati, cache, stato e temp in `<workspace>/.opencode-portable-<data-ora>-<pid>-<rand>/`, cancellata all'uscita — normale, X/Alt+F4, Ctrl+C e kill Task Manager inclusi |
| Offline | Binario opencode incorporato, estratto al primo avvio — mai rete |
| Avvio veloce | Salta il fetch bloccante di 10–30 s da `models.dev` |
| Niente login salvato | I modelli free integrati funzionano subito; le API key si danno al volo, mai salvate |
| Istanze concorrenti | Ognuna ha la sua cartella — niente condivisioni, niente lock |

## Verifica zero tracce

Dopo un run, controlla che non resti nulla:

```cmd
:: 1. workspace pulita (mostra anche nascosti/system):
dir "C:\percorso\workspace" /a
:: 2. residui noti su C: (il solo exe è atteso):
dir C:\*opencode* /s /b /a
dir C:\opwatch-*.exe /s /b /a
:: 3. nessun processo rimasto (tasklist non accetta *):
tasklist | findstr /I "opencode opwatch"
```

Atteso: solo i tuoi file, solo l'exe, nessun processo.

<details>
<summary><b>Build dai sorgenti</b></summary>

```bash
git clone https://github.com/terzastella/opencode-portable.git
cd opencode-portable
```

```powershell
# Exe all-in-one (serve .NET 10 SDK) — risolve l'ultimo opencode,
# lo incorpora + hash, pubblica in dist/:
.\scripts\publish-fat.ps1
```

Architettura completa in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) (in inglese).

</details>

<details>
<summary><b>Opzione B — script (Windows .cmd/.ps1, Linux/macOS .sh)</b></summary>

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
(`--help` è il flag di opencode: serve il binario scaricato, altrimenti i launcher falliscono chiusi.)

</details>

<details>
<summary><b>Struttura progetto</b></summary>

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
├── .github/workflows/      # CI: smoke a ogni push, payload completo sui tag
├── UPSTREAM_VERSION        # versione upstream pinnata
├── global.json             # SDK .NET pinnato
├── THIRD-PARTY-NOTICES.md  # componenti, versioni, licenze
├── SECURITY.md             # policy, ambito, disclosure
├── CHANGELOG.md            # storia release
├── LICENSE                 # MIT
├── README.md               # versione inglese
└── README.it.md            # questo file
```

(`bin/` contiene `opencode.exe` + `.gitkeep`; `config/` il template versionato;
`dist/`, `data/`, `src/**/payload|bin|obj` sono output ignorati da git.)

Collegamenti desktop (Windows):

```powershell
.\scripts\install-shortcuts.ps1            # crea collegamenti Desktop + Start Menu
.\scripts\install-shortcuts.ps1 -Uninstall # li rimuove
```

I collegamenti avviano `opencode-portable.cmd` con l'icona di `assets/opencode-portable.ico`. (Di proposito lo script, non l'exe: i collegamenti servono per l'uso veloce da terminale; l'exe è l'app da doppio-click. Solo Windows; valida le cartelle prima.) Working directory: la root portable.

</details>

<details>
<summary><b>Come funziona</b></summary>

| Tema | Cosa fanno i launcher |
|---|---|
| Isolamento | `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME` → `data/...`, e `OPENCODE_CONFIG` → `config/opencode.json` |
| Avvio veloce | `OPENCODE_DISABLE_MODELS_FETCH=1` (salta il fetch bloccante di 10–30 s da `models.dev`) |
| Temp | Tmp isolata per run (`TMP/TEMP/TMPDIR` dentro, cancellata all'uscita). Nomi diversi per launcher: `run-HHMMSScc-RAND` (cmd, con centesimi e sanitizzazione locale), `run-aaaammgg-HHmmss-PID-rand` (ps1), `mktemp run-AAAAMMGG-HHMMSS-PID-XXX` (sh); l'exe non ha sottolivello (la root è già per-run) |
| Residui da crash | Spazzati al giro dopo (exe + ps1 + sh: solo PID morti + nome stamp esatto, mai i vivi né le dir utente; cmd: età >7 giorni, senza check PID — divergenza documentata) |
| Guard anti-sporco | Foto di `%TEMP%\opencode`, `%LOCALAPPDATA%\opencode`, `%APPDATA%\opencode` (Win) o `~/.config/opencode`, `~/.local/share/opencode`, `~/.cache/opencode` (Unix): rimosse solo se create da quella run (l'exe copre anche `%USERPROFILE%\.config\opencode` + `.local\share\opencode`) |
| Igiene env | `.ps1`/`.sh` ripristinano le variabili (`try/finally`, `trap`); istanze concorrenti sicure |

L'exe aggiunge: picker supervisionato in processo figlio (il crash nativo degrada
al drag-and-drop), cancellazione con retry contro i lock (resti segnalati, mai
silenziosi), `--clean <workspace>`, e guardiano orfano (`opwatch-<pid>-<rand>.exe`
autoeliminante) anche per i kill di gruppo da Task Manager. Telemetria scritta in `%TEMP%`
solo in caso di fallimento/abort/resti, mai a successo.

Dettagli in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) (in inglese).

</details>

<details>
<summary><b>Policy sui binari</b></summary>

`bin/`, `data/`, `dist/`, `src/**/payload|bin|obj` e `config/opencode.json` locale sono ignorati da git. La repo contiene **solo sorgenti**:

- `scripts/setup.ps1` → `opencode-windows-x64.zip` (o `-arm64`, auto-detect, WOW64-aware) dalle release upstream
- `scripts/setup.sh` → `opencode-linux-x64.tar.gz` / `opencode-darwin-arm64.zip`, ecc. (errore subito su OS/arch non supportati)
- Se `bin/` è vuota i launcher **si rifiutano di partire** (fail-closed): riusare `opencode` da `PATH` richiede opt-in esplicito (`--from-path` — ovunque per exe/cmd, solo primo arg per ps1/sh — o `OPENCODE_ALLOW_PATH_FALLBACK=1`), e il percorso risolto viene sempre stampato. L'exe preferisce il binario incorporato e guarda il PATH solo se manca.

Per distribuire: `pack-release.ps1` fa `opencode-portable-win-x64.zip`, `pack-release.sh` fa `opencode-portable-linux-macos.tar.gz`. Entrambi scansionano i segreti e non includono mai `data/`, `bin/*` (tranne `.gitkeep`), `dist/` o config locali. L'exe all-in-one (`dist/`) non si committa mai (~137 MiB / ~144 MB).

</details>

<details>
<summary><b>Note di sicurezza</b></summary>

- **I binari sono artefatti upstream fidati**: `setup.*` scarica e `publish-fat.ps1` incorpora via HTTPS dalle GitHub releases. L'hash SHA-256 del payload è incorporato al build e riverificato a ogni estrazione; manca solo l'anchor indipendente (upstream non pubblica checksum/firme — se mai lo farà, verificateli voi stessi).
- **Le credenziali stanno in `data/` (script) o `<workspace>/.opencode-portable-*/` (exe)** (token auth, API key): non condividere né pubblicare quelle cartelle, e non distribuire archivi che le contengono — `pack-release` le esclude di proposito. Chiavetta persa = credenziali perse.
- **Gli zip scaricati hanno il Mark-of-the-Web**: su altri PC Windows può bloccare i launcher `.ps1` (execution policy). Usa `opencode-portable.cmd`, o `Unblock-File`, dopo aver ispezionato il contenuto.
- Questo wrapper isola i file, **non fa sandbox** di opencode: un AI coding agent esegue comandi shell nel tuo workspace — controlla cosa fa, come con qualsiasi installazione upstream.
- **Modello di trust**: il workspace NON è un confine di sicurezza — usa solo cartelle fidate (niente share di rete, cartelle sincronizzate scrivibili da altri, o repo non attendibili); chi può scrivere il workspace può influenzare config, cache e binari estratti. Stesso discorso per `--from-path` / `OPENCODE_ALLOW_PATH_FALLBACK=1`: bypassa il trust del payload incorporato ed esegue il primo `opencode` nel PATH — il percorso viene sempre stampato, verificane l'hash se hai dubbi.
- **Aggiornamenti**: nessun auto-update (`autoupdate: false`); ogni release fissa la sua versione di opencode nel file `UPSTREAM_VERSION` al build (`publish-fat` lo usa salvo `-Version` esplicita, poi incorpora + hash). Per aggiornare, cambia il file e scarica la nuova release.
- **CI**: ogni push/PR compila su Windows e lancia la suite di self-test (badge stato qui sopra; i tag eseguono un job `fat` completo in più).
- **Niente Windows Error Reporting**: l'exe silenzia il WER per i propri crash (compresi gli AV supervisionati del picker) così nulla finisce in `ReportArchive`; la diagnostica resta nei nostri `crash-*.log` (`<root>/state/` di norma, `%TEMP%\opencode-portable-crash-*/` solo se il crash avviene prima che un workspace sia noto; ultimi 5 conservati, username oscurati). `--clean-host` rimuove le nostre tracce host-side (vecchi archivi WER, telemetria watchdog, dir crash TEMP) — per i preesistenti può servire admin.
- **Limiti di "zero tracce"**: rimuove i file applicativi e gli artefatti runtime noti (verificabile con la checklist sotto). **Non** è anti-tracciamento forense: telemetria OS/sicurezza fuori dal nostro controllo (Prefetch, Defender/SmartScreen, USN Journal, pagefile, cronologia shell, log antivirus) non è rimovibile da nessuna app portable.

</details>

<details>
<summary><b>Flag exe e exit code</b></summary>

| Flag | Effetto |
|---|---|
| `--workspace <dir>` / `--workspace=<dir>` | Salta il picker, parte diretto lì |
| `--console` | Salta il dialog, drag-and-drop diretto |
| `--from-path` | Opt-in a riusare `opencode` da PATH se manca il binario (ovunque prima di `--`; anche `OPENCODE_ALLOW_PATH_FALLBACK=1`) |
| `--picker-exe <path>` / `--picker-exe=<path>` | Altro eseguibile come picker figlio (diagnostica) |
| `--pick-folder [titolo] [iniziale]` | Modalità figlio: dialog nativo, protocollo `PICK-OK` (0) / `PICK-CANCEL` (1) / `PICK-ERROR` (2) |
| `--watch <pid> <ticks> <dir>` | Modalità guardiano (avviata da sola, hop orfano) |
| `--watch-hop <pid> <ticks> <dir>` | Hop interno: genera il nipote `--watch` staccato ed esce (non chiamare direttamente) |
| `--clean <dir>` | Spazza le root stale senza avviare nulla; errore se manca la dir |
| `--clean-host` | Rimuove le nostre tracce host (archivi WER, telemetria, dir crash TEMP); sempre exit 0 |
| `--self-test` | Smoke test COM solo non-UI (istanzia + opzioni + check WER, mai `Show` — niente click umani) |
| `--test-fallback`, `--test-close`, `--test-robust-delete`, `--test-watchdog`, `--test-watchdog-proc`, `--test-watchdog-copy`, `--test-watchdog-hop`, `--test-detached`, `--test-junction`, `--test-midrun-delete`, `--test-race`, `--test-longpath` | Self-test interni, attivi solo con `OPENCODE_ENABLE_TESTFLAGS=1` (vedi sorgenti) |
| `--report-console <file>` | Sonda distacco, con gate come i test sopra |
| `--has-payload` | Info sul payload incorporato; exit 0 se presente (usato da publish-fat) |
| `--` | Tutto ciò che segue va a opencode così com'è, anche flag da launcher |

Exit code: `0` = ok / picker annullato / `--clean-host` fatto / `--has-payload` presente; `1` = workspace invalida (inclusa drive root), binario/config mancanti, fallback fallito, self-test fallito, `--clean` senza dir, `--watch`/`--watch-hop`/`--picker-exe`/`--report-console` malformati, abort guardiano, errore fatale (MessageBox + `crash-*.log` con righe); `2` = solo errore interno figlio `--pick-folder` (il padre ripiega da solo, mai exit 2 in un run normale); altrimenti propaga quello di opencode.

</details>

<details>
<summary><b>Config</b></summary>

Flusso script: modifica `config/opencode.json` (default: `autoupdate: false`, `share: "disabled"` — sensati per un'installazione portable). Il template è `config/opencode.example.json`; cancella `opencode.json` per tornare ai default.

Flusso exe: config **effimera per run** (`<root>/config/opencode.json`, dal template accanto all'exe o dal default incorporato), cancellata all'uscita con tutto il resto.

Nota: questo progetto punta ai modelli free integrati di opencode — niente login,
niente provider esterni da collegare. Login/auth non persistono mai per l'exe
(root fresca a ogni run); eventuali API key di altri modelli si danno al volo,
mai salvate (vedi [HOW-IT-WORKS](docs/HOW-IT-WORKS.md) § Auth note).

</details>

## Documentazione

Architettura completa: [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) (in inglese).

## Licenza

[MIT](LICENSE) — © 2026 Terzastella.
