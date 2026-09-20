[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$AdkVersion
)
$ErrorActionPreference = 'Stop'
$computer = Get-CimInstance Win32_ComputerSystem
if ($computer.Model -notin @('Virtual Machine','VMware Virtual Platform','VirtualBox','KVM','Standard PC (Q35 + ICH9, 2009)')) { throw 'Evidence collection is restricted to a disposable VM guest.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new evidence directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $OutputDirectory 'manifest.json')
Get-Disk | Select-Object Number,SerialNumber,UniqueId,Size,BusType,PartitionStyle | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'disks.json') -Encoding utf8
Get-Partition | Select-Object DiskNumber,PartitionNumber,Guid,GptType,Offset,Size,DriveLetter | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'partitions.json') -Encoding utf8
& bcdedit.exe /enum all | Out-File (Join-Path $OutputDirectory 'bcd.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'BCD evidence failed.' }
$hash = Get-FileHash -LiteralPath $IsoPath -Algorithm SHA256
[ordered]@{ isoSha256=$hash.Hash; adkVersion=$AdkVersion; captureUtc=[DateTime]::UtcNow.ToString('O'); vmModel=$computer.Model } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'environment.json') -Encoding utf8
$logRoot = Join-Path (Split-Path -Parent $ManifestPath) 'Logs'
if (Test-Path -LiteralPath $logRoot) { Copy-Item -LiteralPath $logRoot -Destination $OutputDirectory -Recurse }
Write-Output 'Evidence captured. Contains identifiers: review/redact before sharing; never commit this directory.'
