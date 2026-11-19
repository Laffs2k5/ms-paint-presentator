# Installing Paint Presentator

`presenter.exe` is one self-contained file: nothing else to install for
PowerPoint and PDF decks or the built-in help deck. Marp and Slidev decks
also need Node.js. No admin rights are needed for any of it.

## Needs

- Windows 11, with **Paint from the Microsoft Store** (the one that comes with
  Windows 11).
- For PowerPoint decks: PowerPoint.
- For Marp decks: Node.js and Marp's command line (`install.ps1 -WithNode`
  sets up both).
- For Slidev decks: Node.js, and the deck project's own `npm install`.

## Install

In PowerShell (Windows PowerShell 5.1 or PowerShell 7):

```powershell
# the repository is private for now: download through the GitHub CLI, signed in
gh release download --repo Laffs2k5/ms-paint-presentator --pattern install.ps1 --dir .
# (once it is public, this works without gh:
#  irm https://github.com/Laffs2k5/ms-paint-presentator/releases/latest/download/install.ps1 -OutFile install.ps1)

powershell -ExecutionPolicy Bypass -File .\install.ps1            # presenter.exe only
powershell -ExecutionPolicy Bypass -File .\install.ps1 -WithNode  # and Node.js + Marp
```

What it does:

1. Downloads the latest release for this computer (x64 or ARM64) and unpacks
   it to `%LOCALAPPDATA%\Programs\PaintPresentator`.
2. Adds that folder to your user PATH (new terminals see it).
3. With `-WithNode`: a portable Node.js LTS (a zip, no installer) in
   `%LOCALAPPDATA%\Programs\nodejs`, on your PATH, then `presenter setup`
   (Marp's command line).

Already have the zip? `.\install.ps1 -Zip .\presenter-win-x64.zip`.
`-NoPath` leaves your PATH alone.

Then:

```powershell
presenter          # the built-in help deck, in Paint
presenter doctor   # checks this computer and Paint
```

## Uninstall

Delete `%LOCALAPPDATA%\Programs\PaintPresentator` (and `...\nodejs` if the
installer put Node.js there), remove them from your user PATH (Settings,
search "environment variables", "Edit environment variables for your
account"), and delete `%LOCALAPPDATA%\PaintPresentator` (cache, logs,
settings).

## On a managed work computer

Things that can get in the way, depending on the organisation's policies:

- **Running scripts:** the execution policy, or PowerShell's Constrained
  Language Mode, can block `install.ps1`. The manual way: unzip the release
  into a folder of your own and run `presenter.exe` from there.
- **Unsigned programs:** `presenter.exe` is not code-signed. SmartScreen may
  ask once; application control (AppLocker, WDAC) may block it outright.
- **Downloads:** a proxy may block nodejs.org or GitHub downloads.
