# EasyWin

EasyWin is a fail-closed Windows 11 deployment orchestrator for a local, USB-free reinstall. It stages an official Windows image and a self-contained WinPE worker on a temporary NTFS partition, asks Windows Boot Manager to start that WinPE image once, reapplies Windows, and finishes drivers, applications, profile settings, boot cleanup, and partition cleanup after the new system starts.

It uses Microsoft deployment primitives only: Windows ADK/WinPE, DISM, DiskPart, BCD/BCDBoot, unattend, PnPUtil, ReAgentC, and the standard UEFI boot chain. It does not implement a bootloader and does not bypass Secure Boot, TPM, BitLocker, or Windows servicing protections.

After first boot, EasyWin asks the built-in Windows licensing service to activate and checks the result without recording or shipping any product key. Activation succeeds only when the machine already has a matching digital entitlement or compatible OEM license. EasyWin does not include activation bypasses, KMS emulators, generic keys, or a Windows license.

## Safety model

Real deployment is deliberately fail-closed. Before staging and again in WinPE, EasyWin verifies the target by device identifier, serial number, model, byte size, and bus type. The deployment partition is recorded by GPT partition GUID, offset, size, and label and is excluded from every destructive plan. The manifest and every staged payload are SHA-256 verified before disk changes.

The Windows target disk is selected independently from the currently running system disk. EasyWin measures the supported shrink range on that exact disk and creates `EASYWIN_DEPLOY` only when at least the required protected capacity is available. The UI can optionally select one distinct second internal disk for erasure, giving a hard maximum of two affected disks per run. Both disks are recorded by stable identity in the signed manifest and revalidated in WinPE before the first destructive command. USB and other removable targets are rejected.

WinPE deletes only explicitly enumerated partitions and never issues DiskPart `clean`. It first preserves staging, prepares EFI/MSR/Windows on the selected Windows disk, applies and verifies the selected WIM/ESD index, writes the new EFI boot files and copies PostInstall. Only after the new Windows image exists does it erase the optional second disk, format it as one NTFS `Data` volume, register the new UEFI boot entry, and reboot. Recovery is created after the first successful boot: PostInstall removes the staging partition, expands Windows, reserves the final recovery area, installs WinRE, and enables it.

If elevation, UEFI, AC power, free space, BitLocker state, ADK files, image metadata, hashes, disk identity, or boot validation fails, the real pipeline stops before destructive work.

## Projects

- `EasyWin.Desktop` — WPF/MVVM selection, preflight, two destructive confirmations, and live progress.
- `EasyWin.Core` — immutable deployment contracts, manifests, hashing, path policy, typed process execution, and logs.
- `EasyWin.Deployment` — environment probes, local staging, WinPE/BCD/DiskPart/DISM/BCDBoot/unattend, post-install, cleanup, and orchestration.
- `EasyWin.WinPE` — self-contained WinPE worker and automatic deployment entry point.
- `EasyWin.PostInstall` — idempotent first-boot drivers/apps/profile/cleanup worker.
- `EasyWin.Builder` — ADK media builder and end-to-end dry-run harness.
- `EasyWin.Tests` — unit, safety, command-generation, and dry-run integration tests.

## Prerequisites for a real deployment

- Windows 11 in UEFI mode and an administrator session.
- .NET 8 SDK to build the project.
- Windows ADK plus the matching WinPE add-on.
- An official Windows ISO containing `sources\install.wim` or `sources\install.esd`.
- Local, trusted application and driver payloads whose SHA-256 values match the catalogs.
- BitLocker suspended through the supported Windows workflow before boot handoff. EasyWin never attempts to bypass or recover it.

## Build and validation

```powershell
dotnet restore EasyWin.sln
dotnet build EasyWin.sln -c Release --no-restore
dotnet test EasyWin.sln -c Release --no-build
dotnet run --project src/EasyWin.Builder -c Release -- --dry-run
```

The reproducible PowerShell entry points are `scripts\Verify-CompletionGate.ps1` for the non-destructive gate and `scripts\Build-Release.ps1` for the self-contained x64 desktop bundle with nested WinPE and PostInstall payloads.

Debug builds of the desktop default to DryRun. DryRun traverses Desktop preparation, staging, one-time boot, WinPE validation/deployment, PostInstall, and cleanup while recording—never executing—disk, BCD, DISM apply-image, BCDBoot, reboot, or partition changes.

## Catalogs

Profiles, applications, and driver packages are JSON. Installer and driver paths are relative to the catalog root; rooted paths and traversal are rejected. Arguments are stored as arrays and passed directly through `ProcessStartInfo.ArgumentList`, never through a shell. Placeholder or mismatched hashes block execution.

The desktop application reads its application choices from the catalog rather than a hardcoded list. The prepared payload includes NVIDIA App, AMD Software: Adrenalin auto-detect, and MSI Center in addition to the standard applications. Vendor packages may declare hardware ID prefixes: incompatible entries are disabled in the UI and are skipped again by PostInstall as a second safety check. NVIDIA App and MSI Center are staged as offline installers; AMD's official auto-detect bootstrap requires an Internet connection after first boot to obtain the hardware-specific package.

Logs are split by responsibility (`installer.log`, `boot.log`, `disk.log`, `dism.log`, and `postinstall.log`) and each failure includes its deployment stage, tool exit code when available, and a user-facing reason.
