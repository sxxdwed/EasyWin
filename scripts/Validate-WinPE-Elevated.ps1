[CmdletBinding()]
param(
    [string]$BootWim = 'D:\EasyWin\WinPE-Media\sources\boot.wim',
    [string]$MarkerPath = 'D:\EasyWin\Build\winpe-validation-complete.txt'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'WinPE validation must run from an elevated PowerShell process.'
}

$mountRoot = Join-Path 'D:\EasyWin\Build' ("ValidationMount-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mountRoot -Force | Out-Null
$mounted = $false
try {
    & dism.exe /English /Mount-Image "/ImageFile:$BootWim" /Index:1 "/MountDir:$mountRoot" /ReadOnly | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "DISM mount failed with exit code $LASTEXITCODE." }
    $mounted = $true

    $startup = Join-Path $mountRoot 'Windows\System32\winpeshl.ini'
    $worker = Join-Path $mountRoot 'Windows\System32\EasyWin\EasyWin.WinPE.exe'
    if (-not (Test-Path -LiteralPath $startup) -or -not (Test-Path -LiteralPath $worker)) {
        throw 'EasyWin WinPE startup payload is incomplete.'
    }
    if ((Get-Content -LiteralPath $startup -Raw) -notmatch '(?i)EasyWin\\EasyWin\.WinPE\.exe') {
        throw 'winpeshl.ini does not autostart EasyWin.WinPE.exe.'
    }

    Set-Content -LiteralPath $MarkerPath -Encoding UTF8 -Value @(
        "CompletedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
        "BootWim=$BootWim"
        'WorkerPresent=True'
        'AutostartConfigured=True'
    )
}
finally {
    if ($mounted) {
        & dism.exe /English /Unmount-Image "/MountDir:$mountRoot" /Discard | Out-Null
    }
    if (Test-Path -LiteralPath $mountRoot) {
        Remove-Item -LiteralPath $mountRoot -Recurse -Force
    }
}
