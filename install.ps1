<#
.SYNOPSIS
    Installs Paint Presentator (presenter.exe) for the current user. No admin rights needed.

.DESCRIPTION
    Downloads the latest release for this computer (x64 or ARM64), unpacks it to
    %LOCALAPPDATA%\Programs\PaintPresentator and adds that folder to the user's PATH.
    With -WithNode it also installs a portable Node.js (only Marp and Slidev decks
    need it) into %LOCALAPPDATA%\Programs\nodejs and Marp's command line.

    The repository is private for now: a download then needs the GitHub CLI (gh),
    signed in. Once it is public, a plain web download works.

.EXAMPLE
    .\install.ps1                   # presenter.exe only (PowerPoint and PDF decks, the demo)
    .\install.ps1 -WithNode         # and Node.js plus Marp, for Marp decks
    .\install.ps1 -Zip .\presenter-win-x64.zip   # from a zip already downloaded
#>
[CmdletBinding()]
param(
    [string]$Repo = 'Laffs2k5/ms-paint-presentator',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\PaintPresentator'),
    [string]$Zip,
    [switch]$WithNode,
    [switch]$NoPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times faster without the progress bar

function Add-ToUserPath([string]$dir) {
    $path = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($path -split ';' | Where-Object { $_ })
    if ($parts -notcontains $dir) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $dir) -join ';'), 'User')
        Write-Host "  added to your PATH: $dir (new terminals see it)"
    }
    if (($env:Path -split ';') -notcontains $dir) { $env:Path = "$env:Path;$dir" }
}

# Expand-Archive in Windows PowerShell 5.1 fails on some zips (MethodCountCouldNotFindBest):
# unpack with .NET's own zip classes, the same in 5.1 and 7
function Expand-Zip([string]$zip, [string]$dest) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($dest)
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }  # a folder
            $target = [IO.Path]::GetFullPath((Join-Path $root $entry.FullName))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "unsafe path in the zip: $($entry.FullName)" }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally { $archive.Dispose() }
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$asset = "presenter-win-$arch.zip"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("presenter-install-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    # --- presenter.exe ---------------------------------------------------------------
    if (-not $Zip) {
        $Zip = Join-Path $temp $asset
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            Write-Host "Downloading $asset (latest release of $Repo, through gh)"
            gh release download --repo $Repo --pattern $asset --dir $temp --clobber
            if ($LASTEXITCODE -ne 0) { throw "gh could not download ${asset}: is gh signed in (gh auth login)?" }
        }
        else {
            Write-Host "Downloading $asset (latest release of $Repo)"
            Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$Repo/releases/latest/download/$asset" -OutFile $Zip
        }
    }
    # full Windows paths: Windows PowerShell 5.1's Expand-Archive fails on others
    $Zip = (Resolve-Path -LiteralPath $Zip).ProviderPath
    $InstallDir = [IO.Path]::GetFullPath($InstallDir)
    Unblock-File -Path $Zip  # downloaded files carry a "from the internet" mark
    Write-Host "Unpacking to $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Expand-Zip $Zip $InstallDir
    Get-ChildItem $InstallDir -File | Unblock-File
    if (-not $NoPath) { Add-ToUserPath $InstallDir }

    # --- Node.js, portable (only Marp and Slidev decks need it) --------------------------
    if ($WithNode) {
        $node = Get-Command node -ErrorAction SilentlyContinue
        if ($node) {
            Write-Host "Node.js is already there: $($node.Source)"
        }
        else {
            $index = Invoke-RestMethod -UseBasicParsing -Uri 'https://nodejs.org/dist/index.json'
            $lts = $index | Where-Object { $_.lts -and $_.files -contains "win-$arch-zip" } | Select-Object -First 1
            if (-not $lts) { throw "no Node.js LTS build for win-$arch found" }
            $name = "node-$($lts.version)-win-$arch"
            Write-Host "Downloading Node.js $($lts.version) (portable, no installer)"
            $nodeZip = Join-Path $temp "$name.zip"
            Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$($lts.version)/$name.zip" -OutFile $nodeZip
            $nodeRoot = Join-Path $env:LOCALAPPDATA 'Programs\nodejs'
            Expand-Zip $nodeZip $nodeRoot
            $nodeDir = Join-Path $nodeRoot $name
            if (-not $NoPath) { Add-ToUserPath $nodeDir } else { $env:Path = "$env:Path;$nodeDir" }
        }
        Write-Host "Installing Marp's command line for Marp decks"
        & (Join-Path $InstallDir 'presenter.exe') setup
    }

    Write-Host ""
    Write-Host "Done. Try:  presenter            (the built-in help deck)"
    Write-Host "            presenter doctor     (checks this computer and Paint)"
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
