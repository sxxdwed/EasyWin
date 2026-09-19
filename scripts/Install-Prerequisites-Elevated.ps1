[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\EasyWin\ADK',
    [string]$PrerequisitesRoot = 'D:\EasyWin\Prerequisites'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This installer must run from an elevated PowerShell process.'
}

$logRoot = Join-Path $PrerequisitesRoot 'Logs'
New-Item -ItemType Directory -Path $InstallRoot,$logRoot -Force | Out-Null

function Invoke-Setup {
    param([string]$FilePath, [string[]]$Arguments, [string]$Name, [int[]]$AcceptableExitCodes = @(0, 3010))
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin $AcceptableExitCodes) {
        throw "$Name failed with exit code $($process.ExitCode)."
    }
    Add-Content -LiteralPath (Join-Path $logRoot 'installation-status.log') -Value "$([DateTimeOffset]::UtcNow.ToString('O')) $Name exit=$($process.ExitCode)"
}

$adkSetup = Join-Path $PrerequisitesRoot 'ADK-10.1.26100.2454\adksetup.exe'
$winPeSetup = Join-Path $PrerequisitesRoot 'WinPE-10.1.26100.2454\adkwinpesetup.exe'
Invoke-Setup $adkSetup @('/quiet','/norestart','/ceip','off','/features','OptionId.DeploymentTools','/installpath',$InstallRoot) 'Windows ADK Deployment Tools'
Invoke-Setup $winPeSetup @('/quiet','/norestart','/ceip','off','/features','OptionId.WindowsPreinstallationEnvironment','/installpath',$InstallRoot) 'Windows PE add-on'

$patchZip = Join-Path $PrerequisitesRoot 'Windows_ADK_10.1.26100.2454_Update_KB5101684.zip'
$patchRoot = Join-Path $PrerequisitesRoot 'ADK-Patch-KB5101684'
if (-not (Test-Path -LiteralPath $patchRoot)) {
    Expand-Archive -LiteralPath $patchZip -DestinationPath $patchRoot
}

foreach ($patch in Get-ChildItem -LiteralPath $patchRoot -Filter '*.msp' -File -Recurse) {
    $log = Join-Path $logRoot ("msiexec-{0}.log" -f $patch.BaseName)
    Invoke-Setup 'msiexec.exe' @('/p',('"{0}"' -f $patch.FullName),'/qn','/norestart','/l*v',('"{0}"' -f $log)) "ADK patch $($patch.Name)" @(0, 1642, 3010)
}

$winPeRoot = Join-Path $InstallRoot 'Assessment and Deployment Kit\Windows Preinstallation Environment'
[Environment]::SetEnvironmentVariable('EASYWIN_ADK_WINPE_ROOT', $winPeRoot, 'Machine')
[Environment]::SetEnvironmentVariable('EASYWIN_ADK_ROOT', $InstallRoot, 'Machine')
Set-Content -LiteralPath (Join-Path $PrerequisitesRoot 'installation-complete.txt') -Value @(
    "CompletedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
    "InstallRoot=$InstallRoot"
    "WinPeRoot=$winPeRoot"
) -Encoding UTF8
