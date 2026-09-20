# EasyWin Beta 3 — 1.4.0-beta.3

> BETA — experimental destructive deployment software. Automated tests and simulated deployment pass, but a complete real reboot/install/first-boot cycle has not been verified. Test first in a UEFI virtual machine with disposable disks. Do not use on your main PC without verified external backups and recovery media.

Download [EasyWin Beta 3](https://github.com/sxxdwed/EasyWin/releases/tag/v1.4.0-beta.3). **Do not erase your main PC: real UEFI VM E2E is NOT RUN and EFI power-loss recovery is incomplete.** Read the [Beta 3 limitations](docs/RELEASE-BETA-3.md) and [remaining validation gates](docs/VALIDATION-GATES.md) before testing.

## Two-disk beta

Select the Windows target (erased), deployment storage (preserved), and optionally an additional erase disk independently. Separate staging uses an existing unencrypted NTFS volume and never shrinks the Windows target. WinPE validates both stable identities and staged hashes before fully repartitioning only the target. After first boot, cleanup removes only the plan-specific deployment folder on the storage disk. Selecting the target itself as storage uses the existing protected-partition mode.

With only two disks, you cannot both preserve the second disk as staging and erase it in the same run. Use same-disk protected staging if the optional second disk must also be erased.

EasyWin is a fail-closed Windows 11 deployment orchestrator for a local, USB-free reinstall. It stages an official Windows image and a self-contained WinPE worker on a temporary NTFS partition, asks Windows Boot Manager to start that WinPE image once, reapplies Windows, and finishes drivers, applications, profile settings, boot cleanup, and partition cleanup after the new system starts.

It uses Microsoft deployment primitives only: Windows ADK/WinPE, DISM, DiskPart, BCD/BCDBoot, unattend, PnPUtil, ReAgentC, and the standard UEFI boot chain. It does not implement a bootloader and does not bypass Secure Boot, TPM, BitLocker, or Windows servicing protections.

After first boot, EasyWin asks the built-in Windows licensing service to activate and checks the result without recording or shipping any product key. Activation succeeds only when the machine already has a matching digital entitlement or compatible OEM license. EasyWin does not include activation bypasses, KMS emulators, generic keys, or a Windows license.

## Safety model

Real deployment is deliberately fail-closed. Before staging and again in WinPE, EasyWin verifies the target by device identifier, serial number, model, byte size, and bus type. The deployment partition is recorded by GPT partition GUID, offset, size, and label and is excluded from every destructive plan. The manifest and every staged payload are SHA-256 verified before disk changes.

The Windows target disk is selected independently from the currently running system disk. Separate staging avoids target shrink; same-disk staging requires sufficient supported NTFS shrink capacity for `EASYWIN_DEPLOY`. The optional additional erase disk must differ from both target and separate staging. Identities are recorded in a hash-sealed manifest (not a digital signature) and resolved again before reboot and in WinPE. USB and other removable targets are rejected.

Same-disk WinPE preparation deletes explicitly enumerated partitions and forbids DiskPart `clean`. Separate-disk preparation allows `clean` only through the dedicated target path after identity and staging guards, and creates EFI/MSR/Windows/Recovery on the target. DISM applies the selected image index, BCDBoot prepares boot files, and PostInstall is copied. The optional erase disk is processed after image application. Same-disk cleanup removes protected staging, expands Windows and recreates Recovery; separate-disk cleanup does not change staging disk geometry.

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
- Staging storage fully decrypted and accessible; suspending BitLocker protection alone does not decrypt it. EasyWin never attempts to bypass or recover BitLocker.

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

The desktop application reads its application choices from the catalog, including NVIDIA App, AMD Software and MSI Center. Public downloads do not include third-party installers or exported drivers. Missing or incompatible packages are disabled; selected packages require locally supplied payloads matching the catalog hashes. Network bootstrappers also require an Internet connection after first boot.

Logs are split by responsibility (`installer.log`, `boot.log`, `disk.log`, `dism.log`, and `postinstall.log`) and each failure includes its deployment stage, tool exit code when available, and a user-facing reason.

## Checkpoints and diagnostics

EasyWin atomically updates the sealed deployment manifest before and after every important Desktop, WinPE, and PostInstall stage. The state records the attempt number, current and last successful stages, completion timestamps, whether recovery is required, and the last structured error. A partial JSON write cannot replace the previous valid manifest. Re-entering a phase preserves completed-stage history and starts a new numbered attempt; it does not silently treat an interrupted destructive step as completed.

## Standard and Lite profiles

`Standard` keeps the standard Windows application set and applies only privacy and Explorer preferences. `Lite` additionally deprovisions an explicit safety-allowlisted set of consumer applications such as Clipchamp, News, Weather, Office Hub, Solitaire, Feedback Hub, Maps, Phone Link, Teams, Xbox, and Game Bar packages. It does not remove or disable Microsoft Store, Edge, Windows Update, Defender, WinRE, the servicing stack, drivers, PowerShell, Terminal, Calculator, Photos, Paint, Notepad, or Snipping Tool. Removed Store applications can be installed again from Microsoft Store.

Lite user preferences are written to the default user registry hive during PostInstall so they apply to the account created after setup, rather than only to the temporary SYSTEM account running SetupComplete.
