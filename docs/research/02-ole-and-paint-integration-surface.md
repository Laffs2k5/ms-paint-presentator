# Research: OLE in DoomPaint, and what Paint exposes over OLE/COM

> Status: research notes, 2026-11-18. Windows 11 only. Items marked **[verify]** are
> hypotheses for the POC on a real Windows 11 laptop. They could not be tested from
> here (Linux container).

## TL;DR

- DoomPaint uses OLE for exactly one thing: **being the clipboard source.** It
  publishes a single custom `IDataObject` once with `OleSetClipboard`, offers only
  `CF_DIB` on `TYMED_HGLOBAL`, and generates the bitmap *inside* `GetData` when Paint
  pastes. Paint never subscribes for change notifications, so a Ctrl+V is still sent
  for every frame. OLE fixes the **data side** (no clipboard rewrites, no race, no
  error dialog, built-in back-pressure). It does not remove the paste trigger.
- Paint's realistic OLE/COM integration surface on Windows 11:
  1. **Clipboard consumer** (`OleGetClipboard` → `GetData`): *proven* path, accepts `CF_DIB`.
     It probably also accepts the registered `"PNG"` format (with alpha) **[verify]**.
  2. **Clipboard source** (Paint's own Copy puts `CF_DIB` + `"PNG"` on the clipboard):
     lets us *read back* what the presenter draws live on a slide.
  3. **UI Automation** (COM, `IUIAutomation`): proven for menu Paste, zoom, canvas
     geometry and dialog dismissal. Could drive layers **[verify]**.
  4. **OLE drag-and-drop target** (`IDropTarget`): Paint accepts dropped files.
     It is useless for streaming, but a fallback for "open this image".
  5. **Classic OLE embedding server** (`Paint.Picture` / "Bitmap Image"): legacy.
     Reports suggest it is broken or missing on current Windows 11 Paint **[verify]**.
     Not a streaming path anyway.
  6. **Not OLE but adjacent:** `WM_COMMAND` with MFC standard command IDs
     (`ID_EDIT_PASTE = 0xE125`) posted to Paint's main window could paste **without
     focus and without synthetic keystrokes** **[verify, high value]**.
- **Recommendation for the POC:** reuse the DoomPaint pattern (live `IDataObject` +
  paste trigger), and spend experiment time on three questions:
  (a) can the paste be triggered *without* foreground focus (`WM_COMMAND`, UIA
  `Invoke`), (b) can `"PNG"`/`CF_DIBV5` give alpha/overlay effects, and
  (c) how fast is a 1080p paste.

---

## 1. How DoomPaint uses OLE

Source: [`mspaintdoom/clipserve.py`](https://github.com/markrussinovich/DoomPaint/blob/main/mspaintdoom/clipserve.py),
[`exp_ole_clip.py`](https://github.com/markrussinovich/DoomPaint/blob/main/exp_ole_clip.py),
[`exp_verify_game_path.py`](https://github.com/markrussinovich/DoomPaint/blob/main/exp_verify_game_path.py),
commits `b1c1a30`, `d41b772` (PR #2, co-authored with GitHub Copilot) and `fcaf888`.
Implemented in Python with `pythoncom` / `win32com.server` (pywin32).

### 1.1 Three generations of clipboard feeding

| Gen | Commit | Mechanism | Problem |
|---|---|---|---|
| 1 | before the first public commit (described in the `clipserve.py` docstring of `64e8769`) | `OpenClipboard` / `EmptyClipboard` / `SetClipboardData(CF_DIB)` every frame, then Ctrl+V | Rewriting the clipboard while Paint's async paste is mid-read → **"Can't complete operation"** modal, and the paste is lost. "Scale 2 blew through a 90 ms guard" |
| 2 | `64e8769`…`d8693a0` | **Win32 delayed rendering:** `SetClipboardData(CF_DIB, NULL)`, render on `WM_RENDERFORMAT` from a hidden message-only window. Plus a post-render grace period and a dialog-killer watchdog | `WM_RENDERFORMAT` says when a read *starts*, but nothing says when it *finishes*. The race shrinks but never closes (~3 dialogs per session → 0 over 900 frames with timers, still fragile) |
| 3 | `b1c1a30` + `d41b772` | **OLE: one live `IDataObject` published once with `OleSetClipboard`**, `GetData` renders the newest frame on demand | Race impossible by construction. `publish()` no longer blocks, so roughly **3× throughput**. PR #2 reports 640×400 sustained at ~25 pastes/s, later ~33 |

### 1.2 The data object

```text
_DibDataObject (implements IDataObject, registered via win32com.server.util.wrap)
  QueryGetData(fe)   -> S_OK only for cfFormat == CF_DIB (8) && tymed & TYMED_HGLOBAL
  GetData(fe)        -> STGMEDIUM(TYMED_HGLOBAL, <BITMAPINFOHEADER + 24-bit BGR pixels>)
                        bytes come from provider() → render_fn() (render-on-demand)
  EnumFormatEtc(GET) -> IEnumFORMATETC with one entry: (CF_DIB, NULL, DVASPECT_CONTENT, -1, TYMED_HGLOBAL)
  DAdvise/DUnadvise/EnumDAdvise -> OLE_E_ADVISENOTSUPPORTED
  GetDataHere/GetCanonicalFormatEtc/SetData -> E_NOTIMPL
```

- The payload is a **`CF_DIB`**: a BMP file minus its 14-byte `BITMAPFILEHEADER`
  (Pillow `img.save(..., "BMP")[14:]`). That is 24-bit, bottom-up, with no alpha.
- **Never return empty data.** A failed `GetData` brings back the Paint error dialog.
  On cold start (Ctrl+V before the first frame), the last good frame or a black
  640×400 DIB is served instead.
- The "consumed" event is set inside `GetData` under the same lock that swaps the
  frame, so producer and consumer stay edge-correct.

### 1.3 Threading and lifetime

- A dedicated **STA thread** calls `OleInitialize()`, creates a **message-only
  window** (`HWND_MESSAGE`) and runs a `GetMessage` pump. Paint's cross-process
  `GetData` calls are COM RPC calls marshalled to that STA, so the pump must stay
  responsive. **Never block the STA on rendering work longer than one frame.**
- `OleSetClipboard` is retried 10× at 50 ms, because the clipboard may be open by
  someone else.
- **Theft detection:** `AddClipboardFormatListener(hwnd)` → on `WM_CLIPBOARDUPDATE`,
  `OleIsCurrentClipboard(dataobj)`. If another app took the clipboard, a flag is set
  and the object is re-published on the next `publish()`, not on a timer. That
  respects a user's copy made while the game is paused.
- **Shutdown:** `OleFlushClipboard` runs on the STA through a synchronous `SendMessage`.
  This renders real `HGLOBAL` bytes so the last frame stays pasteable and Paint
  never calls into a dead object (which would show the dialog on quit). Per
  [OleFlushClipboard docs](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-oleflushclipboard),
  all `TYMED_HGLOBAL` formats are rendered at flush.

### 1.4 Pacing loop

```text
publish(render_fn):  wait(consumed, ≤250 ms) → arm(not consumed) → swap render_fn
paste():             SendInput Ctrl+V (Shift released, existing Ctrl respected)
Paint:               OleGetClipboard → GetData(CF_DIB) → our render_fn() → consumed.set()
```

- 8 consecutive missed `GetData` signals → switch to the UIA **Edit > Paste** paster.
- `exp_ole_clip.py` verifies the key assumption: Windows does **not** cache the first
  render. Every paste produces a fresh `GetData` call (it checks for "distinct canvas
  colours across 6 pastes" and "≥35 GetData calls for 40 fast pastes"). It also logs
  the "requests during setup (history broker etc.)" that arrive right after
  `OleSetClipboard`. These come from Windows **clipboard history** (`cbdhsvc`)
  reading our object. That matters for us (see 3.4).

### 1.5 What the podcast called "Paint listens to this"

On the podcast Mark says: "Paint actually listens to this, so we can avoid the
clunky paste operation and just render right into the clipboard and let Paint know
it's there." In the code, **Paint does not listen**: `DAdvise` is refused, and Ctrl+V
still drives every frame. What "listening" really means is that **Paint pulls
through OLE (`OleGetClipboard` → our live object) instead of copying a static
`HGLOBAL`**, so the bytes are always fresh and never torn. Treat "paste-free
streaming" as unproven. Section 3.2 has the experiment that would settle it.

---

## 2. Background: the OLE pieces involved

| API / concept | Role for us | Docs |
|---|---|---|
| `OleSetClipboard(IDataObject*)` | Puts a *pointer* to our object on the clipboard. All formats are delayed-rendered. OLE's internal window answers `WM_RENDERFORMAT` by calling our `GetData`. AddRefs the object | [learn.microsoft.com](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-olesetclipboard) |
| `OleIsCurrentClipboard` | "Do we still own it?" (theft detection) | [docs](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-oleiscurrentclipboard) |
| `OleFlushClipboard` | Render everything to real `HGLOBAL`s and release our object (on exit) | [docs](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-oleflushclipboard) |
| `OleGetClipboard` | What Paint calls. While we run, it **forwards via RPC** to our object | [docs](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-olegetclipboard) |
| `IDataObject::DAdvise` / `IAdviseSink::OnDataChange` | Push notifications to consumers that *subscribe*. Clipboard consumers like Paint don't subscribe. Used for OLE links/embeddings | [Data Notification](https://learn.microsoft.com/windows/win32/com/data-notification) |
| Synthesized formats | Windows converts `CF_DIB` ↔ `CF_BITMAP` ↔ `CF_DIBV5` automatically, so offering one is enough | [Clipboard formats](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats) |
| Clipboard-history opt-out formats | `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` (DWORD 0), `CanUploadToCloudClipboard` (DWORD 0) | [Cloud clipboard formats](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats) |

**.NET shortcut [verify]:** WinForms `Clipboard.SetDataObject(obj, copy: false)`
places a `DataObject` (or a wrapper around your object) through
`ClipboardCore.SetData`, which uses `OleSetClipboard` and flushes only when
`copy == true`. A `DataObject` subclass that overrides `GetData(string format)` to
return the newest frame as a `MemoryStream` for `DataFormats.Dib`, or as a
`Bitmap`, would give the same live-object behaviour with ~30 lines of C# (STA
thread + `Application.Run` pump). If the WinForms conversion layer gets in the way,
implement `System.Runtime.InteropServices.ComTypes.IDataObject` directly, which is
a 1:1 port of `_DibDataObject`.
Docs: [Clipboard.SetDataObject](https://learn.microsoft.com/dotnet/api/system.windows.forms.clipboard.setdataobject),
[source](https://source.dot.net/System.Windows.Forms/System/Windows/Forms/OLE/Clipboard.cs.html).

---

## 3. What Paint (Windows 11) exposes or might expose

Modern Paint (`Microsoft.Paint` MSIX package, `mspaint.exe`) is still a classic
Win32 app underneath, with a modern XAML UI layered on top. Evidence from
DoomPaint: the canvas is a Win32 child window of class **`MSPaintView`**, the main
window title is `"<doc> - Paint"`, and UIA AutomationIds exist for `CropButton`,
`ZoomSliderControl`, `ZoomValuesComboBox`, `scrollViewer`, `image` (the canvas
group), `CanvasSizeTextBlock`, `CloseButton` (on the error dialog), and the `Edit` →
`Paste` menu items. Paint can be captured while occluded with
`PrintWindow(PW_RENDERFULLCONTENT)`.

### 3.1 Paint as clipboard consumer (proven)

| Format | Status | Notes |
|---|---|---|
| `CF_DIB` (8), `TYMED_HGLOBAL` | **Proven** (DoomPaint) | 24-bit BGR. Fastest to produce: raw pixels, no encoding |
| `CF_DIBV5` (17) | Synthesized from `CF_DIB` by Windows | A [Windows 11 bug report](https://techcommunity.microsoft.com/discussions/windows11/serious-bug-in-windows-clipboard---images-get-corrupted/4466584) (23H2–25H2) mentions corrupted DIB→DIBV5 conversion when pasting into Paint. **Offer `CF_DIB` explicitly** |
| `"PNG"` (registered) | **[verify]** | Paint *copies* `CF_DIB` + `"PNG"` ([getpaint.net forum](https://forums.getpaint.net/topic/122848-pasting-dibv5-image/)). Since Paint gained transparency and layers in 2023, it very likely *pastes* `"PNG"` with alpha too. Costs a PNG encode on our side and a decode in Paint (slower than DIB), but enables **transparent overlays** such as pasting a callout on top of a slide |
| `CF_HDROP` (file list) | **[verify]** | Paste of a copied file. Might insert the image |

**Experiment (POC):** port `exp_ole_clip.py`. Offer `"PNG"`, `CF_DIBV5` and `CF_DIB` in
`EnumFormatEtc` and **log which `FORMATETC`s Paint asks for, in what order**. Paint's
preference order tells us what it actually consumes.

### 3.2 Can Paint consume without a paste gesture? (the "listens" question)

Options, from most to least promising:

1. **`WM_COMMAND` → paste command ID [verify, high value].** Paint is MFC-heritage.
   If its paste command still routes through the main window's `WM_COMMAND` with the
   standard MFC ID `ID_EDIT_PASTE` (`0xE125`, from MFC's public `afxres.h`), then
   `PostMessage(hwndPaint, WM_COMMAND, 0xE125, 0)` pastes **without focus and
   without SendInput**. That frees the presenter view to own the keyboard, and the
   clicker no longer fights Paint. Related IDs to probe: `ID_EDIT_UNDO 0xE12B`,
   `ID_EDIT_REDO 0xE12C`, `ID_EDIT_COPY 0xE122`, `ID_EDIT_SELECT_ALL 0xE12A`,
   `ID_FILE_OPEN 0xE101`. How to find the real ID: log `WM_COMMAND` reaching Paint's
   main window while clicking Edit > Paste (Spy++ from VS, or a `WH_CALLWNDPROC`
   hook), or post candidate IDs and watch for `GetData` calls on our object. Note:
   `WM_PASTE` sent to the main window reportedly does *not* work
   ([CodeProject thread](https://forum.codeproject.com/post/2953923)).
2. **UIA `Invoke` on the Paste menu item / a toolbar Paste button.** It works today,
   but opens the menu. Check whether there is an always-visible **Paste button in
   the toolbar** (Clipboard group) whose `InvokePattern` doesn't need the menu to
   expand, and whether `Invoke` works when Paint is *not* foreground.
3. **`DAdvise` sniffing [verify, likely negative].** Implement `DAdvise` with
   `CreateDataAdviseHolder` and log whether Paint (or anything) ever calls it. If
   Paint subscribed, `SendOnDataChange` would give true paste-free streaming. The
   expected answer is "never called", but it's a cheap check.
4. **Clipboard-change auto-refresh:** Paint does not monitor the clipboard. No known
   setting does this.

### 3.3 Paint as clipboard source (read-back)

When the presenter scribbles on a slide with Paint's pen, Ctrl+A, Ctrl+C (or
`WM_COMMAND` copy) puts `CF_DIB` + `"PNG"` on the clipboard. `OleGetClipboard` gets
it back. Uses: "save annotated slide", "undo my scribbles = re-paste the clean
slide", exporting the hand-annotated deck afterwards. An alternative read-back with
no clipboard is `PrintWindow` of the canvas.

### 3.4 Clipboard hygiene (side effects on the presenter's machine)

- Windows **clipboard history (Win+V)** and **cloud clipboard** read every new
  clipboard item. DoomPaint's log shows the "history broker" calling our object
  right after `OleSetClipboard`. We should add
  `ExcludeClipboardContentFromMonitorProcessing` (or `CanIncludeInClipboardHistory = 0`
  and `CanUploadToCloudClipboard = 0`) to our `EnumFormatEtc`, so we don't fill the
  presenter's history with slides or sync them to other devices. Corporate DLP and
  clipboard managers may also read it. **[verify]**
- Restore the user's original clipboard content at exit. Snapshot it with
  `OleGetClipboard` at start and `OleSetClipboard` it back, instead of
  `OleFlushClipboard`-ing our last slide.
- During a talk, a copy in another app steals the clipboard. DoomPaint's reclaim-on-
  next-publish policy works for us too.

### 3.5 OLE drag and drop

Paint registers an `IDropTarget` (you can drop image files on it, which opens them).
Driving it programmatically means a real drag loop (`DoDragDrop` + synthetic mouse).
That is slower and more fragile than paste. **Not pursued**, except as a possible
"open .png" fallback. Also, `IDropTarget` pointers cannot be obtained cross-process
(the `OleDropTargetInterface` window property is only valid in-process).

### 3.6 Classic OLE embedding server (`Paint.Picture`, "Bitmap Image")

Classic Paint was an OLE 2 *server*: Word/WordPad could **Insert Object → Bitmap
Image** and edit it in place. In theory, a server lets a client `CoCreateInstance`
the object, `IPersistStorage::Load` a bitmap, `DoVerb(OLEIVERB_SHOW)` it (Paint
opens with our image), and later read it back through `IDataObject`. That would be
image-into-Paint with no clipboard at all. Current status on Windows 11 Paint is
doubtful: users report Word refusing to insert "Bitmap Image" on Windows 11
([MS Q&A](https://learn.microsoft.com/en-us/answers/questions/727520/running-word-on-windows-11-insert-object-command-d)),
WordPad OLE objects failing on 24H2
([Tech Community](https://techcommunity.microsoft.com/discussions/windows11/accessing-wordpad-ole-objects-in-windows-11-2h24/4450434)),
and pasting from modern Paint into Word/Outlook needing a registry fix
([winhelponline](https://www.winhelponline.com/blog/word-encountered-problem-paste-from-paint/)).
Even if it works, each `DoVerb` reloads the document. That is a slide *loader*,
not a frame pump. **[verify]** with:

```powershell
reg query "HKCR\Paint.Picture" /s
reg query "HKCR\CLSID\{D3E34B21-9D75-101A-8C3D-00AA001A1652}" /s   # classic Paint.Picture CLSID
(Get-AppxPackage Microsoft.Paint).InstallLocation |
  ForEach-Object { Select-String -Path "$_\AppxManifest.xml" -Pattern 'com:|ComServer|protocol|fileTypeAssociation|AppExecutionAlias' }
```

The manifest dump also lists any **packaged COM servers, protocols (`ms-paint:`) and
file associations** Paint declares. This is the authoritative list of
"theoretically available" activation surfaces.

### 3.7 Other control surfaces (non-OLE, for completeness)

| Surface | Use | Status |
|---|---|---|
| UI Automation (`IUIAutomation`, pywinauto `uia` backend, FlaUI in .NET) | Zoom, fit-to-window, canvas size, dismiss dialogs, menu paste, possibly **layers** (add, show/hide, select) and **View > Full screen** | Proven for zoom, paste and dialogs. Layers and full screen **[verify]** with Accessibility Insights or `inspect.exe` |
| `SendInput` keystrokes (Ctrl+V/Z/Y, Ctrl+PgUp/PgDn zoom, Ctrl+0) | Works only when Paint is foreground. Some builds drop synthetic Ctrl+V | Proven, with a self-test |
| `PrintWindow(PW_RENDERFULLCONTENT)` | Verify a paste landed, thumbnails, read-back | Proven |
| Command line `mspaint "C:\path\slide.png"` | Open a file (new document) | Works. `/p` and `/pt` are legacy and unverified on Win11 |
| `ms-paint:` URI | Launches Paint. Does not reliably take a file argument | Low value |
| Low-level keyboard hook (`WH_KEYBOARD_LL`) | Capture clicker keys globally without Paint seeing them | Proven |
| DLL injection / hooking Paint's renderer | Fastest, but fragile across MSIX updates and an EDR red flag on a corporate laptop | **Rejected** |

---

## 4. Implications for the presenter design

1. **Display core = DoomPaint's pattern**, re-implemented in our stack of choice.
   C# can do it natively (`ComTypes.IDataObject` or a WinForms `DataObject`
   subclass). Python can reuse DoomPaint's MIT code.
2. **Offer formats:** `CF_DIB` (always) + `"PNG"` (only for transparent overlays) +
   the three clipboard-history opt-out formats.
3. **Trigger priority:** `WM_COMMAND` paste (if 3.2.1 works) → toolbar Paste through
   UIA `Invoke` → SendInput Ctrl+V → UIA menu Paste.
4. **Slides are small:** a slide change is one paste of a pre-rendered DIB, so
   latency ≈ Paint's per-paste composite time at slide resolution. Measure this
   at 960×540, 1280×720 and 1920×1080.
5. **Undo stack as slide history:** each paste is an undo step, so `WM_COMMAND
   ID_EDIT_UNDO/REDO` (or Ctrl+Z/Y) could give instant back/forward **[verify]**
   undo depth and memory use).

## 5. POC experiments derived from this doc

| # | Experiment | Pass criterion |
|---|---|---|
| E1 | Port `exp_ole_clip.py`: offer `PNG`+`CF_DIBV5`+`CF_DIB`, log Paint's `QueryGetData`/`GetData` order | We know which format Paint prefers, and whether alpha works |
| E2 | `PostMessage(WM_COMMAND, 0xE125)` to Paint's main window, with Paint **in the background** | `GetData` is called and the canvas changes, with no focus needed |
| E3 | Find the real paste/undo/redo IDs by logging `WM_COMMAND` | ID table recorded in docs |
| E4 | UIA: list Paint's full tree (toolbar Paste, Layers panel, View > Full screen) | AutomationIds recorded |
| E5 | `DAdvise` sniffing | Documented yes or no |
| E6 | Throughput: 960×540 / 1280×720 / 1920×1080 DIB pastes per second | Numbers recorded, target ≤100 ms per slide at 1080p |
| E7 | Undo depth: paste 50 slides, then Ctrl+Z ×50 | Max depth and RAM use recorded |
| E8 | Clipboard-history opt-out formats work (Win+V stays clean) | Win+V shows no slides |
| E9 | Manifest/registry dump (3.6) | List of COM servers, protocols and associations |

## Sources

- DoomPaint: <https://github.com/markrussinovich/DoomPaint> (clipserve.py, paint_out.py, exp_ole_clip.py, commit history)
- OleSetClipboard: <https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-olesetclipboard>
- OleFlushClipboard: <https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-oleflushclipboard>
- OleGetClipboard: <https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-olegetclipboard>
- OleIsCurrentClipboard: <https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-oleiscurrentclipboard>
- Data notification / DAdvise: <https://learn.microsoft.com/windows/win32/com/data-notification>, <https://learn.microsoft.com/windows/win32/api/objidl/nf-objidl-idataobject-dadvise>
- Clipboard formats (synthesized, history/cloud opt-out): <https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats>
- WinForms Clipboard.SetDataObject: <https://learn.microsoft.com/dotnet/api/system.windows.forms.clipboard.setdataobject>
- Paint copies CF_DIB + PNG: <https://forums.getpaint.net/topic/122848-pasting-dibv5-image/>
- Win11 DIB→DIBV5 corruption report: <https://techcommunity.microsoft.com/discussions/windows11/serious-bug-in-windows-clipboard---images-get-corrupted/4466584>
- WM_PASTE to Paint doesn't work: <https://forum.codeproject.com/post/2953923>
- Word "Bitmap Image" insert on Win11: <https://learn.microsoft.com/en-us/answers/questions/727520/running-word-on-windows-11-insert-object-command-d>
- WordPad OLE objects on 24H2: <https://techcommunity.microsoft.com/discussions/windows11/accessing-wordpad-ole-objects-in-windows-11-2h24/4450434>
- Paste from Paint into Word error: <https://www.winhelponline.com/blog/word-encountered-problem-paste-from-paint/>
- Paint command line / ms-paint protocol: <https://digitalcitizen.life/ways-start-paint-windows-mspaint>, <https://www.techwalla.com/articles/microsoft-paint-command-line-options>
