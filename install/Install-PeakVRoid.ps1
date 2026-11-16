<#
    PeakVRoid installer for the Steam version of PEAK (Windows).

    What it does:
      1. Finds your PEAK install through Steam (or asks you to pick PEAK.exe).
      2. Installs BepInEx (BepInExPack_PEAK from Thunderstore) if it is missing.
      3. Copies PeakVRoid.dll into BepInEx\plugins\PeakVRoid.
      4. Lets you pick a .vrm avatar and copies it into BepInEx\config\PeakVRoid.

    Run it by double-clicking "Install PeakVRoid.bat" next to this file.
    Pass -Uninstall to remove PeakVRoid again (BepInEx and your avatars are kept).
#>
param(
    [string]$GameDir = "",
    [switch]$Uninstall,
    [switch]$NoPrompt
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Windows.Forms | Out-Null

$PeakAppId = "3527290"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say($msg, $color = "White") { Write-Host $msg -ForegroundColor $color }

function Join-Paths([string]$a, [string]$b) {
    # Plain string join: unlike Join-Path it doesn't fail for drives that don't exist (e.g. an unplugged Steam library).
    return [System.IO.Path]::Combine($a, $b)
}

function Test-PathSafe([string]$p) {
    try { return [bool](Test-Path -LiteralPath $p -ErrorAction Stop) } catch { return $false }
}

function Get-SteamPath {
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in "SteamPath", "InstallPath") {
                if ($p.$name -and (Test-Path $p.$name)) { return ($p.$name -replace "/", "\") }
            }
        } catch { }
    }
    foreach ($d in "${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam") { if (Test-Path $d) { return $d } }
    return $null
}

function Find-Peak {
    $steam = Get-SteamPath
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Paths $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($m.Groups[1].Value -replace "\\\\", "\")
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        # Skip libraries on drives or folders that aren't there any more.
        if (-not (Test-PathSafe $lib)) { continue }
        $manifest = Join-Paths $lib "steamapps\appmanifest_$PeakAppId.acf"
        $dir = $null
        if (Test-PathSafe $manifest) {
            $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $dir = Join-Paths $lib ("steamapps\common\" + $m.Groups[1].Value) }
        }
        if (-not $dir) { $dir = Join-Paths $lib "steamapps\common\PEAK" }
        if (Test-PathSafe (Join-Paths $dir "PEAK.exe")) { return $dir }
    }
    return $null
}

function Pick-Peak {
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = "Couldn't find PEAK automatically - please select PEAK.exe"
    $dlg.Filter = "PEAK|PEAK.exe|Programs (*.exe)|*.exe"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return (Split-Path -Parent $dlg.FileName) }
    return $null
}

# ---------------------------------------------------------------- locate the game
Say "PeakVRoid installer" Cyan
Say "-------------------" Cyan

if (-not $GameDir) { $GameDir = Find-Peak }
if (-not $GameDir -and -not $NoPrompt) { $GameDir = Pick-Peak }
if (-not $GameDir -or -not (Test-Path (Join-Paths $GameDir "PEAK.exe"))) {
    Say "PEAK was not found. Run again with -GameDir 'C:\path\to\PEAK'." Red
    exit 1
}
Say "PEAK found: $GameDir" Green

if (Get-Process -Name "PEAK" -ErrorAction SilentlyContinue) {
    Say "PEAK is running. Please close the game first, then run the installer again." Yellow
    exit 1
}

$plugins = Join-Paths $GameDir "BepInEx\plugins\PeakVRoid"
$avatars = Join-Paths $GameDir "BepInEx\config\PeakVRoid"

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    if (Test-Path $plugins) { Remove-Item $plugins -Recurse -Force }
    Say "PeakVRoid removed. (BepInEx and your avatars in $avatars were left alone.)" Green
    exit 0
}

# ---------------------------------------------------------------- BepInEx
$bepinexCore = Join-Paths $GameDir "BepInEx\core\BepInEx.dll"
if (-not (Test-Path $bepinexCore) -or -not (Test-Path (Join-Paths $GameDir "winhttp.dll"))) {
    Say "Installing BepInEx (BepInExPack_PEAK from Thunderstore)..." Cyan
    $info = Invoke-RestMethod -UseBasicParsing "https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack_PEAK/"
    $url = $info.latest.download_url
    $tmp = Join-Paths $env:TEMP ("BepInExPack_PEAK_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Paths $tmp "pack.zip"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $packRoot = Join-Paths $tmp "BepInExPack_PEAK"
    if (-not (Test-Path $packRoot)) { $packRoot = $tmp }
    Get-ChildItem -Path $packRoot -Force | Where-Object { $_.Name -ne "pack.zip" } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $GameDir -Recurse -Force
    }
    Remove-Item $tmp -Recurse -Force
    Say "BepInEx $($info.latest.version_number) installed." Green
} else {
    Say "BepInEx is already installed." Green
}

# ---------------------------------------------------------------- PeakVRoid
$dll = @(
    (Join-Paths $Here "PeakVRoid.dll"),
    (Join-Paths $Here "BepInEx\plugins\PeakVRoid\PeakVRoid.dll"),
    (Join-Paths $Here "plugins\PeakVRoid\PeakVRoid.dll")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dll) {
    Say "PeakVRoid.dll was not found next to the installer. Extract the whole zip first." Red
    exit 1
}
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Copy-Item -Path $dll -Destination $plugins -Force
New-Item -ItemType Directory -Path (Join-Paths $avatars "players") -Force | Out-Null
Say "PeakVRoid installed to $plugins" Green

# ---------------------------------------------------------------- avatar
$bundled = Get-ChildItem -Path $Here -Filter *.vrm -File -ErrorAction SilentlyContinue
foreach ($f in $bundled) { Copy-Item $f.FullName $avatars -Force; Say "Copied avatar $($f.Name)" Green }

$existing = Get-ChildItem -Path $avatars -Filter *.vrm -File -ErrorAction SilentlyContinue
if (-not $existing -and -not $NoPrompt) {
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = "Pick your VRM avatar (you can add more later)"
    $dlg.Filter = "VRM avatars (*.vrm)|*.vrm"
    $dlg.Multiselect = $true
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        foreach ($f in $dlg.FileNames) { Copy-Item $f $avatars -Force; Say "Copied avatar $(Split-Path -Leaf $f)" Green }
    }
}

Say ""
Say "Done! Start PEAK from Steam." Cyan
Say "  Avatars folder : $avatars"
Say "  F5 = first/third person   F6 = reload avatars   F7 = avatar on/off"
Say "  F8 = next avatar          F9 = swap shoulder    PageUp/PageDown = zoom"
if (-not $NoPrompt) {
    if ((Read-Host "Open the avatars folder now? [Y/n]") -notmatch '^[nN]') { Start-Process explorer.exe $avatars }
}
