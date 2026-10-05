# Portable PDF OCR Renamer

A small Windows tool that watches a folder for new PDFs, reads them with AI
(OCR), and automatically renames each file to something sensible like

```
2024-06-15_invoice_SignalIduna_scan001.pdf
```

The document type and sender company are recognized automatically, the
recognized text is saved as a Markdown attachment inside the PDF, and
everything happens in the background while a tray icon shows what's going on.
No Tesseract or OCRmyPDF needed — just an IONOS AI Model Hub account.

> **Hint:** This is the C# (.NET Framework 4.8) port. The original Python
> project lives at
> [aipotheke/Portable-PDF-OCR-Renamer](https://github.com/aipotheke/Portable-PDF-OCR-Renamer).

## How to use it (no technical knowledge needed)

1. **Download** — Go to the project's [Releases] page on GitHub, open the
   latest release, and download the attached **zip file** (for example
   `PdfOcrRenamer-....zip`). GitHub also uploads each successful build as a
   workflow artifact under **Actions** if you want the newest development
   build.
2. **Extract** — Right-click the zip in Windows Explorer and choose
   *Extract all…*. Put the folder anywhere you like, e.g. `C:\PdfOcrRenamer`.
   The app needs no installation.
3. **Start** — Double-click `PdfOcrRenamer.exe`. A window opens with the file
   list and settings. When you close the window, the app keeps running in the
   system tray (bottom-right, next to the clock) — use the tray icon's
   *Quit* to really stop it.
4. **Get a token** — The AI service needs an access token (see the next
   section). This is the only thing you have to organize once.
5. **Set up once** — In the app window:
   - Paste your **IONOS API key** into the API key field.
   - Choose the **watch folder** — the folder the app should monitor
     (e.g. your scanner's output folder).
   - Adjust the **document types** (e.g. invoice, contract, receipt) that may
     appear in file names.
   - Click **Save**.
6. **Drop PDFs in** — Copy or scan PDFs into the watch folder. Each new file
   appears in the list, is processed, and is then renamed and moved into the
   `processed/` subfolder (the extracted text lands in `md/`).

Requirements: **Windows 10 (1903+) or Windows 11** with the normal .NET
Framework 4.8 that ships with Windows — nothing to install in the normal case.
If it is genuinely missing, get the offline installer from
<https://dotnet.microsoft.com/download/dotnet-framework/net48>.

## How to get a token

The OCR runs on the IONOS AI Model Hub, which requires a free authentication
token:

1. Create/log in to an **IONOS Cloud** account at
   [dcd.ionos.com](https://dcd.ionos.com) (the Data Center Designer).
2. Make sure your user is in a user group with **AI Model Hub** access
   (Account > User Management; see IONOS's
   [Access Management guide](https://docs.ionos.com/cloud/ai/ai-model-hub/tutorials/access-management)).
3. In the DCD, generate an **authentication token** for that user
   (Account > User Management > select user > *Generate token*).
4. Copy the token and paste it into the app's **IONOS API key** field (step 5
   above). Treat it like a password — anyone with the token can use your
   account.

Technical users can alternatively set the `IONOS_API_TOKEN` environment
variable; the app falls back to it automatically. Full IONOS documentation:
[AI Model Hub](https://docs.ionos.com/cloud/ai/ai-model-hub).

## What it does (functions)

- **Watches one folder** for new PDFs (also scans files already in it when
  you press *Scan*).
- **Renames** each PDF to `YYYY-MM-DD_type[_sender]_oldname.pdf` using the
  file's creation date, the recognized document type, and the sender company
  extracted from the letterhead. Existing files are never overwritten
  (`_1`, `_2` suffixes instead).
- **Extracts the text** of each page as Markdown using the
  `lightonai/LightOnOCR-2-1B` vision model via the IONOS AI Model Hub
  (OpenAI-compatible API).
- **Embeds the Markdown** back into the PDF as a file attachment, so the
  recognized text travels with the document.
- **Tray icon & window**: live job list with status colors, pause/resume,
  settings dialog, and a tray menu (Open / Pause-Resume / Quit) with status
  in the tooltip.
- **Runs headless** too: `PdfOcrRenamer --cli` runs the watcher without a
  window (Ctrl+C to stop), and `PdfOcrRenamer --once FILE` processes a single
  PDF and exits.
- **Robustness**: single instance only (`app.lock`), logging to `app.log`,
  per-file error isolation, retry with exponential backoff on rate limits,
  atomic writes, and a `processed.json` registry so nothing is done twice.

## CLI

```
PdfOcrRenamer               full app: single instance + WinForms UI + tray icon
PdfOcrRenamer --cli        headless: watcher only, no window (Ctrl+C to stop)
PdfOcrRenamer --once FILE   process a single PDF and exit
```

## Feature parity with the Python original

| Python module | C# counterpart | Notes |
|---|---|---|
| `app/config.py` | `AppConfig.cs` | `config.json` next to the exe, same defaults, atomic save, `IONOS_API_TOKEN` env fallback |
| `app/rules.py` | `Rules.cs` | creation-time date extraction, `processed.json` registry, Windows-safe sanitization |
| `app/ocr.py` | `Ocr.cs` | one chat-completion request per page (base64 PNG), classification + sender extraction, 429/5xx retry with exponential backoff |
| `app/pdfops.py` | `PdfOps.cs` + `PdfAttach.cs` | target name `YYYY-MM-DD_type[_sender]_oldname.pdf`, `processed/` + `md/` subfolders, `_1`/`_2` suffixes, 250-char path cap, atomic writes, never overwrites |
| `app/watcher.py` | `FolderWatcher.cs` | `FileSystemWatcher` (non-recursive), stability check (size+mtime), single worker, pause/resume, job stages (`queued` → `waiting_stable` → `waiting_for_key` → `processing` → `done`/`skipped`/`error`), per-file error isolation |
| `app/webui/*` | `MainForm.cs` | native WinForms UI: job list with stage coloring, settings form (API key, watch folder, doc types, checkboxes), pause/resume, scan button — same validation as the original web UI |
| `app/tray.py` | `TrayIcon.cs` | `NotifyIcon` tray menu: Open / Pause-Resume / Quit, dimmed icon when paused |
| `app/singleton.py` | `Program.cs` | single-instance lockfile (`app.lock`), `app.log` file logging |

## PDF handling without AGPL dependencies

The Python original uses `pypdfium2` (rendering) and `pypdf` (attachment
embedding). The C# equivalents:

- **Rendering** — [Docnet.Core](https://www.nuget.org/packages/Docnet.Core) (MIT
  wrapper around Apache-2.0 PDFium), rendered to BGRA pixels and encoded to PNG
  by a small built-in encoder (`PngEncoder.cs`, `System.IO.Compression`).
- **Attachment embedding** — `PdfAttach.cs` appends an incremental PDF update
  (new `/Names` + `/EmbeddedFiles` entry in a rewritten catalog, `/EF`
  filespec + embedded-file stream, updated xref with `/Prev`). Verified against
  pypdf and PDFium; no external PDF writer dependency, so no AGPL/iText
  licensing concerns.

## Code base & building

The solution layout is deliberately small and mirrors the Python original
module by module:

- `src/PdfOcrRenamer/` — the app (net48, WinForms):
  `Program.cs` (entry point, single instance, logging),
  `AppConfig.cs` (config.json), `Rules.cs` (registry/sanitization),
  `Ocr.cs` (AI Model Hub client), `PdfOps.cs` + `PdfAttach.cs` (renaming,
  folders, attachment embedding), `PngEncoder.cs`, `FolderWatcher.cs`
  (queue/stability/worker), `MainForm.cs` (UI), `TrayIcon.cs`, `ILogger.cs`.
- `tests/PdfOcrRenamer.Tests/` — 25 xUnit tests covering config, sanitization,
  registry, target names, no-overwrite suffixes, attachment embedding, LLM
  answer parsing, watcher enqueue dedup, stability waiting, end-to-end
  worker processing, error recording, pause/resume, and manual scans.
- `.github/workflows/build.yml` — CI (below).

Build and test locally (Windows, with the .NET SDK installed):

```
dotnet build
dotnet test
dotnet publish src/PdfOcrRenamer -c Release -o publish
```

### CI / GitHub Actions

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs on every push to
`main` and every pull request:

1. **Build & test** on Windows (`dotnet build` + `dotnet test` on `windows-latest`,
   since the app targets WinForms).
2. **Publish** a framework-dependent build on a Windows runner and upload it as a
   workflow **artifact** (`PdfOcrRenamer-net48`).

Push a version tag (`v1.1.0`) to also create a **GitHub Release** with the zipped
build attached and auto-generated release notes.

## Differences from the Python original

- The browser UI is replaced by a native WinForms window plus a system tray
  icon (restoring full tray parity with the Python original).
  The app targets **.NET Framework 4.8** with WinForms — the lightest UI option
  available on Windows (WinForms has no additional runtime cost; WPF/WinUI/Avalonia
  are all heavier). The download is a few MB because the runtime is the one that
  ships with Windows itself.
- Tests run only on Windows (`net48` + WinForms target).
- The tray icon carries the full status: tooltip shows pause state, watch
  folder, queued/active/done counts and an API-key warning; the icon changes
  color when paused or busy; closing the window hides it to the tray
  (Quit via the tray menu ends the app).
- `st_ctime` on POSIX maps to creation time where the filesystem provides it,
  falling back to last-write time — same behavior as the Python code.

[Releases]: https://github.com/aipotheke/Portable-PDF-OCR-Renamer-CS/releases
