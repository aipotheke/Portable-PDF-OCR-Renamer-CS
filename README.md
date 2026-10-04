# Portable PDF OCR Renamer — C#

A Windows tool that watches a folder, OCRs new PDFs with the
IONOS AI Model Hub (`lightonai/LightOnOCR-2-1B`), renames files to
`date_filetype[_company]_oldname.pdf` (the LLM classifier also extracts the
sender company name from the letterhead), embeds the Markdown output as a PDF
attachment, and shows progress in a native **WinForms UI with a system tray
icon**. **No Tesseract / OCRmyPDF.**

This is a complete C# / .NET Framework 4.8 port of the Python original
([aipotheke/Portable-PDF-OCR-Renamer](https://github.com/aipotheke/Portable-PDF-OCR-Renamer)).

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

## CLI

```
PdfOcrRenamer               full app: single instance + WinForms UI + tray icon
PdfOcrRenamer --cli        headless: watcher only, no window (Ctrl+C to stop)
PdfOcrRenamer --once FILE   process a single PDF and exit
```

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

## Build

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

## Requirements

The app targets **.NET Framework 4.8**, which is preinstalled on Windows 10
(1903+) and Windows 11 — no runtime download or installation needed in the
normal case. Only satellite resources for **German and English** are shipped.

If .NET Framework 4.8 is genuinely missing (e.g. an old Windows Server or a
pristine Windows 10 pre-1903), get the offline installer from
<https://dotnet.microsoft.com/download/dotnet-framework/net48>.

The published download is now a few MB instead of the ~70 MB .NET 8
self-contained zip, because the framework runtime ships with Windows.

## Test suite

25 xUnit tests (run on Windows in CI) cover: config defaults and typed
getters, sanitization, the processed registry, target-name building
(date/type/sender/stem), no-overwrite `_1` suffixes, attachment embedding
(file exists, `/EmbeddedFiles` present, attachment readable by an independent
PDF reader), LLM answer parsing (type/sender/unknown/punctuation), watcher
enqueue dedup, stability waiting, end-to-end worker processing, error
recording, pause/resume, and manual scans.

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
