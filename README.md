# Dropshelf: Dropover for Windows

Dropshelf brings the Dropover workflow to Windows. Start dragging anything, shake the mouse, and a small floating **shelf** appears under the cursor. Drop your files, images, text or links on it. Then drag them out wherever you need them, or run an action on them: resize, convert, ZIP, OCR, rename, share, or your own scripts.

It is an independent reimplementation for Windows 10 and 11, built from a reverse-engineering pass over Dropover 5.3.1 for macOS. [docs/reverse-engineering.md](docs/reverse-engineering.md) has the full feature map and what was ported.

## Run it

**Prebuilt:** download from this repository's **Releases** page: `Dropshelf-win-x64.exe` for Intel/AMD PCs, or `Dropshelf-win-arm64.exe` for Snapdragon PCs. Each is a single self-contained file, so nothing else needs to be installed. Put it anywhere, for example `%LOCALAPPDATA%\Programs\Dropshelf\`, and double-click it. Windows SmartScreen may warn because the exe is unsigned: choose **More info › Run anyway**.

**From source on Windows** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet run --project src/Dropshelf
```

**Build a release exe** (works on Windows, macOS or Linux):

```bash
dotnet publish src/Dropshelf/Dropshelf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64
```

Pushing to GitHub also builds both exes via `.github/workflows/build.yml`.

## Using it

| To | Do |
|---|---|
| Make a shelf | Drag something, then shake the mouse left and right. You can also press **Ctrl+Alt+D**, double-click the tray icon, or pick *New Shelf* from the tray menu. |
| Shelf from the clipboard | **Ctrl+Alt+V** |
| Reopen the last closed shelf | **Ctrl+Alt+Z**, or tray › *Recent Shelves* |
| Drag everything out | Drag the stack of thumbnails |
| Drag single items | Click the "5 Images ›" pill to open the detail view, then drag items |
| Run an action | Click the ⌄ button on the shelf, or press **Ctrl+K** and type |
| Preview | **Space** |
| Empty a shelf | Hold **Alt** and click ✕ |
| Dock to a screen edge | Push the shelf past the edge, or double-click its top bar. Hover to bring it back. |
| Send files from Explorer | Turn on *Settings › General › Explorer context menu / Send to* |

### Actions

These actions are available:

- **General:** open, open with, show in Explorer, preview, share, email, copy, copy path, rename, batch rename, ZIP, move to, copy to, Recycle Bin, properties.
- **Image:** resize, compress, convert format, stitch, create PDF, remove metadata, extract text (OCR), set as desktop background.
- **Text:** copy text content.

There are three ways to extend them:

- **Custom Actions:** save an action with preset options so it runs in one click.
- **Custom Scripts:** drop a `.ps1`, `.bat`, `.cmd`, `.py`, `.js`, `.vbs` or `.exe` into `%APPDATA%\Dropshelf\Scripts`. Each one becomes an action. File paths arrive as arguments and in `DROPSHELF_FILES`.
- **Instant Actions:** tiles that appear under a freshly shaken shelf. Drop files on a tile to run its action immediately.

### Command line

```bash
Dropshelf.exe --add "C:\path\file1.jpg" "C:\path\file2.pdf"
```

Other switches: `--new-shelf`, `--clipboard`, `--settings`, `--background` (start without the welcome guide). Launching Dropshelf again while it is running opens Settings.

## Where things are stored

| What | Where |
|---|---|
| Settings, pinned and recent shelves, scripts | `%APPDATA%\Dropshelf` |
| Data dropped without a file (browser images, Outlook mails), text snippets, clipboard images, logs | `%LOCALAPPDATA%\Dropshelf` |

Shelves hold *references* to your files and never copy them. *Settings › General › Application data* shows and clears the folders Dropshelf created itself.

## Differences from Dropover

- **Shake detection:** Windows has no "a file drag is in progress" API, so Dropshelf watches every left-button drag. It ignores window moves, its own windows, and apps listed under *Ignored applications*. If a shelf opens while you shake something that isn't a file drag, it closes itself when you let go.
- **Elevated apps:** Dropshelf must run as a normal user. Windows blocks drops from apps running as administrator.
- **Not ported:** cloud uploads (Dropover Cloud, iCloud, Dropbox, Google Drive, OneDrive, S3, Imgur), ZIP and PDF passwords, stitching PDFs, the MacBook notch, on-device AI shelf names, and licensing.

## Project layout

```
src/Dropshelf/
  App.xaml(.cs)        startup, single instance, hotkeys, settings wiring
  Core/                settings, shelf items, drop/clipboard import, persistence, folder monitoring, shell integration
  Native/              Win32/COM interop: mouse hook, shake detector, thumbnails, share sheet, Explorer operations
  Actions/             every shelf action, plus custom scripts and presets
  Shelf/               shelf window, manager, inline action panels, Instant Actions
  UI/                  tray icon, Settings, preview, quick start, themes
```

"Dropover" is a trademark of Tappable Software. This project isn't affiliated with them.
