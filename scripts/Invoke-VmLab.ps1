[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Capture','PowerLoss','Start')][string]$Action,
    [Parameter(Mandatory)][string]$LabRoot,
    [string]$IsoPath,
    [ValidateSet('two-disks','one-unallocated','one-shrink')][string]$Scenario = 'two-disks',
    [switch]$ConfirmPowerLoss
)
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$root = [IO.Path]::GetFullPath($LabRoot).TrimEnd('\')
if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\') -or $root -eq [Environment]::GetFolderPath('UserProfile')) { throw 'Use a dedicated lab directory.' }
$record = Join-Path $root 'lab.json'
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $root) { throw 'Prepare requires a new lab directory.' }
    if (-not $IsoPath -or -not (Test-Path -LiteralPath $IsoPath -PathType Leaf)) { throw 'Provide official Windows ISO.' }
    $iso = (Resolve-Path -LiteralPath $IsoPath).Path
    if ([IO.Path]::GetExtension($iso) -ne '.iso') { throw 'Expected ISO.' }
    New-Item -ItemType Directory -Path $root | Out-Null
    $marker = 'EasyWinDisposableLab:' + [Guid]::NewGuid().ToString('N')
    $name = 'EasyWin-Test-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
    $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 8GB -Path $root -NewVHDPath (Join-Path $root 'target.vhdx') -NewVHDSizeBytes 160GB
    Set-VM -VM $vm -Notes $marker -AutomaticStartAction Nothing -AutomaticStopAction ShutDown
    Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    if ($Scenario -eq 'two-disks') {
        New-VHD -Path (Join-Path $root 'staging.vhdx') -Dynamic -SizeBytes 100GB | Out-Null
        Add-VMHardDiskDrive -VM $vm -Path (Join-Path $root 'staging.vhdx')
    }
    Add-VMDvdDrive -VM $vm -Path $iso
    Set-VMFirmware -VM $vm -FirstBootDevice (Get-VMDvdDrive -VM $vm)
    [ordered]@{ vmId=$vm.Id.ToString(); marker=$marker; scenario=$Scenario; isoSha256=(Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash; createdUtc=[DateTime]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath $record -Encoding utf8
    Write-Output "Prepared $name. Network disconnected. Install baseline Windows manually; no VM is started automatically."
    return
}
$lab = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
$vm = Get-VM -Id ([Guid]$lab.vmId)
if ($vm.Notes -ne $lab.marker -or $vm.Name -notlike 'EasyWin-Test-*') { throw 'Lab ownership mismatch.' }
foreach ($disk in Get-VMHardDiskDrive -VM $vm) {
    if (-not $disk.Path -or -not [IO.Path]::GetFullPath($disk.Path).StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Untrusted or physical passthrough disk. Stop.' }
}
switch ($Action) {
    'Start' { Start-VM -VM $vm }
    'PowerLoss' {
        if (-not $ConfirmPowerLoss) { throw 'Explicit -ConfirmPowerLoss is required for the disposable guest.' }
        Stop-VM -VM $vm -TurnOff -Force
    }
    'Capture' {
        $evidence = Join-Path $root ('host-evidence-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $evidence | Out-Null
        Get-VM -Id $vm.Id | Select-Object Name,Id,State,Generation,Notes | ConvertTo-Json | Set-Content (Join-Path $evidence 'vm.json') -Encoding utf8
        Get-VMHardDiskDrive -VM $vm | Select-Object Path,ControllerType,ControllerNumber,ControllerLocation | ConvertTo-Json | Set-Content (Join-Path $evidence 'attachments.json') -Encoding utf8
        Get-VMFirmware -VM $vm | Format-List * | Out-File (Join-Path $evidence 'firmware.txt') -Encoding utf8
        Copy-Item -LiteralPath $record -Destination $evidence
    }
}
