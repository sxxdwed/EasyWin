[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$IsoPath,
    [string]$OutputPath = 'D:\EasyWin\Logs\iso-editions.txt',
    [string]$MarkerPath = 'D:\EasyWin\ISO\validation-complete.txt'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'ISO inspection must run from an elevated PowerShell process.'
}

$image = Get-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue
$mountedHere = -not ($image -and $image.Attached)
if ($mountedHere) {
    $image = Mount-DiskImage -ImagePath $IsoPath -PassThru
}

try {
    $volume = $image | Get-Volume
    $root = $volume.DriveLetter + ':\'
    $wim = Join-Path $root 'sources\install.wim'
    $esd = Join-Path $root 'sources\install.esd'
    $source = if (Test-Path -LiteralPath $wim) { $wim } elseif (Test-Path -LiteralPath $esd) { $esd } else { throw 'install.wim/install.esd not found.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath),(Split-Path -Parent $MarkerPath) -Force | Out-Null
    & dism.exe /English /Get-WimInfo "/WimFile:$source" 2>&1 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw "DISM Get-WimInfo failed with exit code $LASTEXITCODE."
    }

    $content = Get-Content -LiteralPath $OutputPath -Raw
    if ($content -notmatch '(?im)^Name\s*:\s*Windows 11 Pro\s*$') {
        throw 'The official image does not contain Windows 11 Pro.'
    }

    Set-Content -LiteralPath $MarkerPath -Encoding UTF8 -Value @(
        "CompletedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
        "InstallImage=$source"
        "ContainsWindows11Pro=True"
    )
}
finally {
    Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue
}
