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

.\install.ps1             # presenter.exe only
.\install.ps1 -WithNode   # and Node.js + Marp
```

If PowerShell refuses to run scripts at all (execution policy `Restricted`),
`powershell -ExecutionPolicy Bypass -File .\install.ps1` instead. A script
saved by a browser carries a "downloaded from the internet" mark that
`RemoteSigned` refuses: `Unblock-File .\install.ps1` first. (`gh` and `irm`
do not set that mark.)

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
  Language Mode, can block `install.ps1`. When Group Policy sets the policy,
  `-ExecutionPolicy Bypass` is ignored; `RemoteSigned` from Group Policy
  still runs `.\install.ps1` in your own session. The manual way: unzip the
  release into a folder of your own and run `presenter.exe` from there.
- **Unsigned programs:** `presenter.exe` is not code-signed. SmartScreen may
  ask once; application control (AppLocker, WDAC) may block it outright. See
  the next section for Microsoft Defender's rule for new programs.
- **Downloads:** a proxy may block nodejs.org or GitHub downloads.

### "Risky action blocked": Defender's rule for new programs

Microsoft Defender's attack surface reduction rule *Block executable files
from running unless they meet a prevalence, age, or trusted list criterion*
stops `presenter.exe` as it starts: a new, unsigned program has no
reputation yet. Nothing visible happens. The command hangs or ends without
output, and Windows Security's Protection history shows **Risky action
blocked**. Its details need admin rights; the same event can be read
without them:

```powershell
Get-WinEvent -LogName 'Microsoft-Windows-Windows Defender/Operational' -MaxEvents 200 |
    Where-Object Id -eq 1121 | Select-Object -First 3 TimeCreated, Message | Format-List
```

The rule's ID is `01443614-cd74-433a-b99e-2ecdc07bfc25`; `Path` names the
file. After a block, Defender refuses any access to that file, even reading
it. Leave it, and work with a fresh copy from the zip.

What works:

- **A code-signing certificate your organisation trusts.** Some
  organisations give staff one, and let Defender run what it signs. Sign a
  fresh copy *before its first run*, then run that copy:

  ```powershell
  gh release download --repo Laffs2k5/ms-paint-presentator --pattern presenter-win-x64.zip --dir .
  $dir  = "$env:LOCALAPPDATA\Programs\PaintPresentator-signed"  # not the folder of a blocked copy
  Expand-Archive .\presenter-win-x64.zip $dir -Force
  $exe  = "$dir\presenter.exe"
  $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert   # pick yours if there are several
  Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
  & $exe --version
  ```

  Then put `$dir` on your user PATH instead of the installer's folder.

  Tested on a managed Windows 11 Enterprise machine without admin rights:
  the unsigned copy was blocked, the signed one ran. Check first that your
  organisation's rules for its certificate cover a tool like this.
- **Otherwise, ask IT** to allow the program (by file, or by a certificate
  it trusts).
