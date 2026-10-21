# How Dropover was analysed

Dropshelf was designed from what Dropover ships, not from its source code. The analysis looked at `/Applications/Dropover.app` (version 5.3.1, build 920, bundle id `me.damir.dropover-mac`). Nothing was copied from the app: no artwork, no videos, no code. The Windows app is an independent reimplementation.

## Method

| Source | What it showed |
|---|---|
| `Contents/Info.plist` | Menu-bar-only app (`LSUIElement`). A "Add to Dropover" Services entry. A `dropover://` URL scheme. URL schemes for Dropbox, Google Drive and OneDrive. An iCloud container. App Intents (Add to Shelf, Get Files from Shelf, Open Recent Shelf, Create Cloud Session). |
| `Contents/Frameworks` | Separate modules for core logic (`DropoverCore`), file actions (`DropoverFileOperations`), cloud upload (`DropoverCloud`, `DropoverAWS`, `GoogleDrive`, `MicrosoftOneDrive`), and crash reporting (`Sentry`). |
| `Contents/PlugIns` | A Share extension, a Control Center widget and a Recent Shelves widget. |
| `en.lproj/Localizable.strings` (655 strings) and the frameworks' `.strings` files (≈300 more) | The full list of features, settings, menu items, action names, options, defaults and error messages. This was the main source for the feature map below. |
| Symbol and class names in the Mach-O binaries | Architecture names: `DragRecognizer`, `DragSessionWindow` (the shelf), `DragSessionCoordinator`, `ActionDockWindow` (Instant Actions), `DragSessionWindowRetractor`, `EdgeDockDistributionController`, `DraggableFile` / `DraggableText` / `DraggablePromise` / `DraggableWebImage` / `DraggableWeblocation`. Settings keys: `ShakeGestureSensitivity`, `AutomaticWindowSnappingEnabled`, `DefaultShelfLocation`, `CopiesContentOnDrag`, `AlwaysCloseShelf`, `DraggableTextForcesPlainText`. |
| Frames from the bundled intro videos | What the shelf looks like and how it behaves (see below). |

Decompiling the binary itself (the shake detection code) timed out in the analysis engine, so that algorithm was not observed. Dropshelf's shake detection is its own design. Everything else in the map below comes from the shipped strings, plists and videos.

## What a shelf looks like (from the intro videos)

- A small dark, translucent, rounded window, about 120×130 pt.
- A close (✕) button top left, a drag handle top centre, and a chevron menu button top right.
- A fanned stack of thumbnails, with a pill below it ("5 Images ›") that opens the detail view.
- While dragging, it shows "Drop files here" with a blue highlight ring.
- The chevron menu holds: Open With ›, Show in Finder, Quick Look, then AirDrop, Mail, Messages, Add to Photos, More ›, then Copy Dropover Link…, then Add From Clipboard, Copy 5 Images, Resize…, then All Actions ›.
- Actions with options open inside the shelf. The panel has a back button, a title, a subtitle ("5 Images") and a blue Continue button, followed by rows such as Dimension, Max. width/height, Show output in, Create copy and Save in.

## Feature map: Dropover → Dropshelf

| Dropover (macOS) | Dropshelf (Windows) |
|---|---|
| Shake cursor while dragging (3 sensitivities) | ✅ Global low-level mouse hook with the same 3 sensitivity levels |
| Activate with modifier key (⌘ ⌃ ⇧) | ✅ Alt / Ctrl / Shift |
| Drop to notch | ➖ Windows has no notch |
| Ignored applications | ✅ |
| Menu bar item and menu (New Shelf, From Clipboard, Pinned, Recent, All Shelves, Close All…) | ✅ Notification-area icon and the same menu |
| Shelf: stack, detail grid/list, close (⌥ = clear), pin, rename, indicator color | ✅ Alt+click on close clears the shelf |
| Drag out everything or single items; close-on-drag-out (moved / always / never); always copy | ✅ |
| Dock to edge, retract after first drop, double-click edge, snap into place (⌘ disables) | ✅ Ctrl disables snapping |
| Default shelf location (cursor, centre, corners) | ✅ |
| Instant Actions under a new shelf | ✅ |
| Favorite actions (up to 5) and All Actions | ✅ |
| Command bar "Perform action…" with recent actions | ✅ Ctrl+K |
| Custom Actions (presets from templates) | ✅ Same templates |
| Custom Scripts (UNIX / Automator) | ✅ PowerShell, batch, Python, JScript/VBScript, .exe |
| Folder Monitoring with rules (name, extension, kind, size; and/or; case sensitivity; delay; per-file shelf; subfolders) | ✅ |
| Screenshot shelf | ✅ Watches the Windows Screenshots folder |
| Recent shelves, restore previous shelf, pinned shelves | ✅ |
| Global and shelf keyboard shortcuts | ✅ |
| Adding-files behavior (new / visible / last active shelf) | ✅ Applies to Send To, the Explorer context menu and the command line |
| "Add to Dropover" Services menu and Share extension | ✅ Explorer context menu and Send To |
| Quick Look | ✅ Built-in preview: images, PDFs, video/audio, text |
| AirDrop / Mail / Messages / More | ✅ Windows share sheet (Nearby Sharing and others) and Email via the default mail client |
| Resize, Compress, Convert format, Stitch, Images → PDF, Remove metadata, Extract text (OCR), Set desktop picture | ✅ OCR uses Windows' built-in recognizer |
| Batch rename (find/replace, #counter #date #original), Rename, ZIP, Move to, Copy to, Move to Trash, Get Info, Copy path, Copy text content, Open with | ✅ |
| ZIP password, PDF password, stitch PDFs | ❌ Not yet (needs a ZIP/PDF encryption library) |
| Cloud sharing (Dropover Cloud, iCloud, Dropbox, Google Drive, OneDrive, S3, Imgur), upload history | ❌ Not ported (needs API keys and accounts) |
| Intelligent shelf names (on-device AI) | ➖ Uses rule-based names ("4 Images") |
| Licensing, trial, App Store purchases, Sentry, Siri/Shortcuts, widgets, Alfred/Raycast | ➖ Not applicable |
