[CmdletBinding()]
param(
    [string]$DestinationRoot = 'D:\EasyWin\Payloads\Drivers',
    [switch]$SkipExport
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Driver export must run from an elevated PowerShell process.'
}

$exportRoot = Join-Path $DestinationRoot 'Exported'
$logRoot = 'D:\EasyWin\Logs'
New-Item -ItemType Directory -Path $exportRoot,$logRoot -Force | Out-Null

$exportLog = Join-Path $logRoot 'driver-export.log'
if (-not $SkipExport) {
    & pnputil.exe /export-driver '*' $exportRoot 2>&1 | Tee-Object -FilePath $exportLog
    if ($LASTEXITCODE -ne 0) {
        throw "Driver export failed with exit code $LASTEXITCODE. See $exportLog."
    }
}

$packages = foreach ($inf in Get-ChildItem -LiteralPath $exportRoot -Filter '*.inf' -File -Recurse) {
    $text = Get-Content -LiteralPath $inf.FullName -Raw
    $hardwareIds = [regex]::Matches(
        $text,
        '(?im)^[^;\r\n=]+\s*=\s*[^,\r\n]+,\s*(?<id>(?:PCI|USB|HDAUDIO|ACPI|SWD|ROOT)\\[^,;\r\n]+)') |
        ForEach-Object { $_.Groups['id'].Value.Trim().Trim('"').ToUpperInvariant() } |
        Where-Object { $_ } |
        Sort-Object -Unique
    if (-not $hardwareIds) {
        continue
    }

    $classMatch = [regex]::Match($text, '(?im)^\s*Class\s*=\s*(?<value>[^;\r\n]+)')
    $providerMatch = [regex]::Match($text, '(?im)^\s*Provider\s*=\s*(?<value>[^;\r\n]+)')
    $class = if ($classMatch.Success) { $classMatch.Groups['value'].Value.Trim().Trim('"') } else { '' }
    $category = switch -Regex ($class) {
        '^System$' { 'chipset'; break }
        '^Display$' { 'gpu'; break }
        '^Net$' { if ($text -match '(?i)wireless|wi-?fi|wlan|802\.11') { 'wifi' } else { 'lan' }; break }
        '^Media$' { 'audio'; break }
        default { 'other' }
    }
    $relative = $inf.FullName.Substring($exportRoot.TrimEnd('\').Length).TrimStart('\').Replace('\', '/')
    $stableId = [IO.Path]::GetFileNameWithoutExtension($inf.Name) + '-' + (Get-FileHash -Algorithm SHA256 -LiteralPath $inf.FullName).Hash.Substring(0, 12).ToLowerInvariant()
    [ordered]@{
        id = $stableId
        name = if ($providerMatch.Success) { $providerMatch.Groups['value'].Value.Trim().Trim('"') } else { $inf.Name }
        category = $category
        infPath = "Drivers/Exported/$relative"
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $inf.FullName).Hash
        sizeBytes = $inf.Length
        architecture = 'x64'
        hardwareIds = @($hardwareIds)
        compatibleIds = @()
    }
}

$catalog = [ordered]@{ version = 1; packages = @($packages) }
$catalogPath = Join-Path $DestinationRoot 'catalog.json'
$catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $catalogPath -Encoding UTF8
[Environment]::SetEnvironmentVariable('EASYWIN_PAYLOAD_ROOT', 'D:\EasyWin\Payloads', 'Machine')
Set-Content -LiteralPath (Join-Path $DestinationRoot 'export-complete.txt') -Encoding UTF8 -Value @(
    "CompletedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
    "Packages=$(@($packages).Count)"
    "Catalog=$catalogPath"
)
