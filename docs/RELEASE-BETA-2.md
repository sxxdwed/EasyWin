# EasyWin Beta 2 — experimental, not ready for a physical PC

Do not use this build to erase your main computer. Real UEFI VM end-to-end installation has NOT been verified. Unsigned development build.

Changes:
- Explicit checkpoint recovery with partition identity/layout/hash checks; no repeated disk erase.
- Official Steam/Discord downloads with publisher verification before confirmation, plus complete prepared-file inventory validation.
- Pre-confirmation driver export/signature checks and payload size accounting.
- Additional BCD-copy, drive-letter and version preflight checks; RU/EN entry-point improvements.
- Tagged clean-build provenance, optional real certificate signing, disposable VM test/evidence scripts.

Verified before packaging: Release build, 135 unit tests and DryRun. These do not prove a real installation works. DryRun executes no disk or boot commands.

Remaining blockers include bootability after power loss during EFI recreation, real driver and complete UEFI VM validation, full localization, remaining app providers, and complete recovery/cleanup UI. See VALIDATION-GATES.md and VM-E2E-PROTOCOL.md.

No Windows image, activation keys, private logs, exported drivers or downloaded third-party installers are included.
