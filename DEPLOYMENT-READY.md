# EasyWin 1.3.0-beta.1 — experimental readiness

NOT production-validated. Release build and 67 automated tests pass, including separate-staging WinPE DryRun and stop-before-format failures. A complete real reboot/deployment/first-boot cycle remains unverified. Test in a disposable UEFI VM first; keep external backups and independent recovery media.

EasyWin is published without a Windows ISO, Windows ADK files, exported drivers, third-party application installers, machine manifests, logs, disk identifiers, or product keys.

Before a real deployment, the operator must provide:

- an official Windows 11 ISO containing `install.wim` or `install.esd`;
- Windows ADK and the matching WinPE add-on;
- trusted application and driver payloads matching the SHA-256 catalog entries;
- a verified backup of every selected disk;
- administrator access, UEFI boot, AC power, sufficient staging space, and a safe BitLocker state;
- a legitimate Windows digital entitlement, matching OEM license, or purchased product key for activation.

The UI independently selects the Windows target and at most one optional second internal disk. Every selected disk is identified again in WinPE before destructive work. The staging partition is protected until the first successful boot, and Windows Boot Manager is used without a custom bootloader.

This beta separates the preserved StagingDisk from the erased Windows target and optional additional erase disk. Separate staging uses a plan-specific folder on an existing NTFS volume; same-disk protected staging remains available. No live disk or boot changes were performed during automated validation.

Activation uses only the built-in Windows licensing service. EasyWin does not include Windows media, product keys, activation bypasses, KMS emulators, or other license-circumvention components.
