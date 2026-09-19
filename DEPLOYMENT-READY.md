# EasyWin deployment readiness

EasyWin is published without a Windows ISO, Windows ADK files, exported drivers, third-party application installers, machine manifests, logs, disk identifiers, or product keys.

Before a real deployment, the operator must provide:

- an official Windows 11 ISO containing `install.wim` or `install.esd`;
- Windows ADK and the matching WinPE add-on;
- trusted application and driver payloads matching the SHA-256 catalog entries;
- a verified backup of every selected disk;
- administrator access, UEFI boot, AC power, sufficient staging space, and a safe BitLocker state;
- a legitimate Windows digital entitlement, matching OEM license, or purchased product key for activation.

The UI independently selects the Windows target and at most one optional second internal disk. Every selected disk is identified again in WinPE before destructive work. The staging partition is protected until the first successful boot, and Windows Boot Manager is used without a custom bootloader.

Release validation for version 1.1.0: Release build completed with zero warnings and errors, 35/35 automated tests passed, and the end-to-end two-disk DryRun completed without executing destructive commands. Atomic checkpoints cover Desktop preparation, WinPE deployment, PostInstall, completion, and structured failure state.

Activation uses only the built-in Windows licensing service. EasyWin does not include Windows media, product keys, activation bypasses, KMS emulators, or other license-circumvention components.
