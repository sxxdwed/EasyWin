[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$DotnetPath,
    [Parameter(Mandatory)] [string]$BuilderDll,
    [Parameter(Mandatory)] [string]$AdkWinPeRoot,
    [Parameter(Mandatory)] [string]$PayloadRoot,
    [string]$OutputRoot = 'D:\EasyWin\WinPE-Media'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'WinPE creation must run from an elevated PowerShell process.'
}

$buildRoot = 'D:\EasyWin\Build'
$workRoot = Join-Path $buildRoot ("WinPE-work-{0}" -f [Guid]::NewGuid().ToString('N'))
$logRoot = 'D:\EasyWin\Logs'
New-Item -ItemType Directory -Path $buildRoot,$logRoot -Force | Out-Null
$stdout = Join-Path $logRoot 'winpe-build.stdout.log'
$stderr = Join-Path $logRoot 'winpe-build.stderr.log'

$arguments = @(
    ('"{0}"' -f $BuilderDll),
    '--build-winpe',
    '--adk', ('"{0}"' -f $AdkWinPeRoot),
    '--payload', ('"{0}"' -f $PayloadRoot),
    '--output', ('"{0}"' -f $OutputRoot),
    '--workspace', ('"{0}"' -f $workRoot)
)
$process = Start-Process -FilePath $DotnetPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if ($process.ExitCode -ne 0) {
    throw "EasyWin WinPE build failed with exit code $($process.ExitCode). See $stderr."
}

Set-Content -LiteralPath (Join-Path $buildRoot 'winpe-build-complete.txt') -Value @(
    "CompletedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
    "OutputRoot=$OutputRoot"
    "WorkRoot=$workRoot"
) -Encoding UTF8
