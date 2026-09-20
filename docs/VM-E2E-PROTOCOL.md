# VM E2E — NOT RUN

Host Hyper-V commands are unavailable on the current workstation. No VM test, physical disk mutation or reboot was performed.

## Important recovery limit

Resuming from a sealed completed PrepareDisk checkpoint is implemented only after a valid EasyWin WinPE session is running. The current disk preparation still recreates EFI. Power loss during EFI replacement can make the staged WinPE unreachable through normal firmware boot. This is a release blocker, not solved by checkpoint logic. Never claim unattended power-loss recovery until a persistent EFI recovery handoff has been designed and tested.

## Disposable lab

Use `scripts/Invoke-VmLab.ps1 -Action Prepare -LabRoot D:\EasyWin\VmTests\unique-test -IsoPath <official.iso> -Scenario two-disks` on a Hyper-V host. Other scenarios: `one-unallocated`, `one-shrink`. It creates new VHDX files only, leaves networking disconnected and does not start the VM. No physical passthrough disks are allowed for subsequent actions.

Install baseline Windows manually in this throwaway VM. For `one-unallocated`, leave at least 40 GiB unallocated at the end. For `one-shrink`, use an NTFS Windows partition with at least 40 GiB shrinkable space. For `two-disks`, format only the second virtual disk NTFS for deployment storage. Take a baseline checkpoint before each case. Copy the beta and official ISO to the guest; supply supported ADK/WinPE and SignTool. Use an isolated NAT switch only when explicitly enabling the app-download scenarios.

Capture evidence before staging, before the first reboot, after every interrupted stage, after PostInstall and after the final reboot with `Collect-VmGuestEvidence.ps1`. Run the collector inside the VM, not on the host. Keep the ISO accessible on an unmodified test volume for hashing. Collect manifest, logs/timestamps, BCD, disk layout, ISO hash and ADK version. If WinPE cannot boot after power loss, record FAIL and the firmware screen; attaching recovery media for diagnosis does not turn the test into PASS.

## Matrix — each case starts from a restored baseline

1. Two disks, separate storage, Standard.
2. One disk, existing unallocated extent.
3. One disk, safe shrink.
4. SetupComplete renamed in the disposable guest image: SYSTEM task must finish PostInstall.
5. Reboot interruption before destructive work: original Windows or current staged WinPE remains reachable.
6. Power loss after persisted PrepareDisk completion: use lab PowerLoss with `-ConfirmPowerLoss`; restart; no clean/format replay.
7. Power loss during and after ApplyImage: restart; matching layout only; incomplete apply may repeat DISM, never erase.
8. Substitute a different throwaway VHDX after staging: identity validation stops before any destructive command.
9. BitLocker-encrypted staging: preflight fails even if protection is suspended.
10. Russian language, Standard and Lite.
11. English language, Standard and Lite.
12. No optional apps, Internet unavailable: core deployment independent of networking.
13. Steam and Discord selected: verified acquisition before confirmation; simulate network loss after reboot; core Windows still completes.
14. Automatic drivers: capture INF/catalog verification, storage-only WinPE injection, full export byte accounting and installed-device status after first boot.

PASS requires the complete chain: current Windows → preflight → confirmation → staging → one-time boot → WinPE validation → target preparation → DISM → BCDBoot/unattend/WinRE → new Windows → PostInstall → drivers/apps → cleanup → another reboot → normal Windows boot. Booting once, unit tests, or a DryRun is not a PASS.

Driver signature and storage accessibility checks still need real hardware/VM validation. Existing unpublished source changes do not update the installed beta EXE or GitHub release.
