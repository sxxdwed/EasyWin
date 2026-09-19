# Security and destructive-operation policy

EasyWin treats all ISO paths, catalog files, manifests, removable media, installer payloads, driver packages, and process output as untrusted input.

- Commands are represented as an executable plus an argument list. User text is never evaluated by `cmd.exe`, PowerShell, or another shell.
- Relative catalog paths are canonicalized beneath an explicit root and must remain there.
- Staged files are authenticated with SHA-256 immediately before use.
- Disk selection never relies on disk number alone.
- A real run requires successful preflight, two confirmations, and a matching disk/partition recheck in WinPE.
- The same-disk staging partition is protected by stable partition identity and is never included in a generated erase plan.
- BCD is exported before mutation; handoff is one-time and rollback is attempted on any preparation failure.
- Secure Boot, TPM, BitLocker, Defender, Windows Update, Store, Recovery, DISM/SFC, and the servicing stack are not disabled or bypassed.

Application packages are intentionally metadata-only until the operator supplies an official installer and records its exact hash. EasyWin will not download or execute unknown binaries.

The Lite profile may deprovision only the consumer AppX package identifiers compiled into `ProfileSafetyPolicy`. Profiles cannot request arbitrary package removal, and the allowlist excludes Microsoft Store, Edge, Defender, Windows Update, WinRE, servicing components, shells, drivers, and core Windows utilities.
