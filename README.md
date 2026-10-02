# Portable PDF OCR Renamer — C#

A single-executable Windows tool that watches a folder, OCRs new PDFs with the
IONOS AI Model Hub (`lightonai/LightOnOCR-2-1B`), renames files to
`date_filetype[_company]_oldname.pdf` (the LLM classifier also extracts the
sender company name from the letterhead), embeds the Markdown output as a PDF
attachment, and shows progress in a browser UI. **No Tesseract / OCRmyPDF.**

This is a complete C# / .NET 8 port of the Python original
([aipotheke/Portable-PDF-OCR-Renamer](https://github.com/aipotheke/Portable-PDF-OCR-Renamer)).

## Feature parity with the Python original

| Python module | C# counterpart | Notes |
|---|---|---|
| `app/config.py` | `AppConfig.cs` | `config.json` next to the exe, same defaults, atomic save, `IONOS_API_TOKEN` env fallback |
| `app/rules.py` | `Rules.cs` | creation-time date extraction, `processed.json` registry, Windows-safe sanitization |
| `app/ocr.py` | `Ocr.cs` | one chat-completion request per page (base64 PNG), classification + sender extraction, 429/5xx retry with exponential backoff |
| `app/pdfops.py` | `PdfOps.cs` + `PdfAttach.cs` | target name `YYYY-MM-DD_type[_sender]_oldname.pdf`, `processed/` + `md/` subfolders, `_1`/`_2` suffixes, 250-char path cap, atomic writes, never overwrites |
| `app/watcher.py` | `FolderWatcher.cs` | `FileSystemWatcher` (non-recursive), stability check (size+mtime), single worker, pause/resume, job stages (`queued` → `waiting_stable` → `waiting_for_key` → `processing` → `done`/`skipped`/`error`), per-file error isolation |
| `app/webui/server.py` | `WebUI.cs` | `HttpListener` on **127.0.0.1:8765 only**: `GET /`, `/api/status`, `/api/config`, `POST /api/config`, `/api/scan`; API key masked to last 4 chars; validated config updates applied live |
| `app/webui/index.html` | `WebUI/index.html` | identical single-page UI |
| `app/singleton.py` | `Program.cs` | single-instance lockfile (`app.lock`), `app.log` file logging |
| `app/tray.py` | — | no tray icon; the C# port runs as a console app / Windows service-style process (the tray menu's Pause/Resume and Quit are available via the web UI and Ctrl+C) |

## CLI

```
PdfOcrRenamer --once FILE   process a single PDF and exit
PdfOcrRenamer --watch       watch the configured folder (Ctrl+C to stop)
PdfOcrRenamer --serve       watcher + web UI at http://127.0.0.1:8765
PdfOcrRenamer               full app: single instance + web UI
PdfOcrRenamer -v            verbose logging
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
dotnet publish src/PdfOcrRenamer -c Release
```

## Test suite

25 xUnit tests cover: config defaults and typed getters, sanitization, the
processed registry, target-name building (date/type/sender/stem), no-overwrite
`_1` suffixes, attachment embedding (file exists, `/EmbeddedFiles` present,
attachment readable by an independent PDF reader), LLM answer parsing
(type/sender/unknown/punctuation), watcher enqueue dedup, stability waiting,
end-to-end worker processing, error recording, pause/resume, manual scans, and
every web UI endpoint (status, masked config, config validation, scan, index).

## Differences from the Python original

- Tray icon omitted (see table above); everything else is ported.
- `st_ctime` on POSIX maps to creation time where the filesystem provides it,
  falling back to last-write time — same behavior as the Python code.
- The web UI is served from `WebUI/index.html` next to the executable.
