# EasyWin Beta 2 — recovery hardening

## Beta 2 follow-up work

The current working tree adds opt-in checkpoint recovery with exact persisted partition GUID/type/geometry validation and no repeated erase; verified official Steam/Discord acquisition before confirmation; pre-confirmation driver export/signature checks; BCD capability probing on an exported store; conservative WinPE/DISM version and letter checks; localized entry-point strings/categories; release provenance/signing tooling; and a disposable VM lab/evidence harness.

Real Steam/Discord downloads and Authenticode checks succeeded without executing installers. Unit tests and DryRun are separate evidence from that network test. No live driver export/signature validation, VM installation or signed release has been validated. The WinPE recovery menu uses keyboard choices; the Desktop recovery dialog and safe-cleanup workflow are still incomplete.

**Critical:** checkpoints do not preserve bootability through EFI deletion. Power loss during EFI recreation can leave WinPE unreachable. See [VM protocol and remaining boot-handoff blocker](VM-E2E-PROTOCOL.md). Resume code alone does not close this P0 issue. Downloads currently cover Steam and Discord; other vendor providers and full service-layer RU/EN localization remain incomplete. The entry-point localization guard covers Cyrillic source text and literal exception sinks, not arbitrary whole-program English text/data flow. Full ADK/component version compatibility, VMD/inbox-driver coverage, actual live-BCD write permissions and atomic cleanup recovery need further validation.

These changes belong to Beta 2; an existing installed Beta 1 executable is not updated automatically.

Validation of the working tree on 2026-09-20 (repeat from the release tag before packaging):
- Restore and Release solution build: PASS, zero warnings/errors.
- Unit tests: 135/135 PASS, zero skipped; includes prepared app configuration mutation/addition/removal rejection.
- Standalone DryRun: PASS, 49 recorded commands, none executed.
- Real UEFI VM E2E: NOT RUN. Physical-PC testing readiness: NO.
- Prepared app inventory now covers configuration and companion files, not only installer binaries. Work-volume free space is rechecked after acquisition/export against the full payload budget.

## Published Beta 1 baseline (before the Beta 2 changes above)

Still BETA — real end-to-end deployment not yet verified.

The installed user executable is not updated automatically by downloading this release.
Do not use passing unit tests as authorization to erase a physical computer.

## Automated coverage added

- Numeric BitLocker status and exact staging volume identity/geometry.
- Existing unallocated extent before safe shrink; forged overlapping extent rejection.
- SYSTEM scheduled-task bootstrap independent of SetupComplete; mutual-exclusion lock.
- First-boot target identity gate and nested log archival outside staging.
- Actual PostInstall orchestrator DryRun, optional installer failure, interruption after app checkpoint, repeat launch without reinstall.
- Required WinPE files individually detected; missing component blocks preflight.
- RU/EN resource parity and required resource build validation.
- Repeated WinPE entry after destructive work has started is blocked before any disk command, including older stage checkpoints.

DryRun uses recorded processes and synthetic disk identities. It cannot prove firmware behavior, image compatibility, boot-driver support or successful first boot.

## Remaining release blockers

- No real UEFI VM install/reboot/first-boot evidence yet.
- Stale deployment is detected and blocked, but Resume / safe-cleanup / Cancel recovery UI is not complete. Do not manually remove staging when Windows has been erased.
- Destructive WinPE stages are not automatically resumable. They fail closed on re-entry. An interruption during cleanup may need manual recovery; no transactional rollback of disk changes is claimed.
- Generic third-party installers cannot guarantee exactly-once execution after power loss between installer exit and durable checkpoint. Successful per-app checkpoints prevent normal replay; cancelled/uncommitted installers may rerun.
- Complete RU/EN coverage of technical service errors/catalog descriptions and visual UI testing are pending.
- Driver export/injection needs VM/hardware validation, explicit signature-chain/network-driver diagnostics and size accounting before final confirmation. DISM/PnPUtil are used without ForceUnsigned.
- Additional preflight coverage of BCD capability, all drive-letter collisions and version compatibility is still required.
- This package is an experimental beta, not a finished or production-validated build.

## Disposable VM test protocol — not executed

Use a throwaway UEFI VM with Secure Boot and only disposable virtual disks. Keep the host's physical drives, shared folders and USB storage disconnected from the guest. Take a full VM checkpoint before each scenario. Provide official matching x64 Windows media and licensed activation where available; never include keys in evidence.

| Scenario | Required evidence |
| --- | --- |
| One disk with trailing unallocated space | No shrink; stage preserved; Windows and recovery boot after cleanup |
| One disk requiring shrink | Supported shrink selected; protected stage survives every destructive step |
| Separate staging volume, including multi-volume disk | Only confirmed GUID used; staging disk data preserved |
| Optional second erase disk | Only target and explicitly confirmed additional disk altered |
| Encrypted/locked staging and changed identity | Block before confirmation/destruction; no disk layout changes |
| Missing ADK, payload, image index or hash mismatch | Action disabled/preflight failed; readable diagnostics |
| Interrupted WinPE after formatting begins | Re-entry blocked, staging and logs preserved; no repeated erase |
| SetupComplete intentionally skipped | Specialize-registered SYSTEM task starts PostInstall after setup |
| Concurrent and interrupted PostInstall | One owner; completed app checkpoints not replayed; critical failure retains staging |
| Optional app fails / no apps selected | Windows remains usable; warning recorded; remaining steps finish |
| Standard and Lite; RU and EN | Update, Defender, Store, servicing and recovery preserved; readable UI |
| Successful first boot and cleanup, both staging modes | Current Windows matches target, logs at ProgramData/EasyWin/Logs/PlanId, temporary task/BCD cleaned |

Record source commit/hash, ISO SHA-256 and edition, ADK/WinPE versions, virtual disk identifiers, initial/final layouts, stage logs, BCD before/after, first-boot evidence and exact failure-injection point. Redact identifiers and secrets before publishing.

Do not mark the completion gate passed until these scenarios have evidence and the remaining software blockers above are closed.

Official dependency guidance: [Microsoft ADK and WinPE downloads](https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install). Choose a supported x64 ADK and matching WinPE add-on/components and apply the applicable servicing updates; presence checks alone do not verify servicing level.
