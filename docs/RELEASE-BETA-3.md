# EasyWin Beta 3 — experimental

**Не используйте эту сборку для очистки основного ПК. Реальный полный цикл установки в UEFI VM НЕ ПРОВЕРЕН. Сбой питания при изменении EFI может оставить ПК без загрузки Windows и среды восстановления.**

**Do not erase your main PC with this build. Real UEFI VM E2E: NOT RUN. Power loss during EFI recreation can leave the PC without a bootable recovery environment.**

This is an unsigned development prerelease, not a stable release. One successful physical-PC test does not establish general deployment safety. Keep independent recovery media and verified backups outside every disk selected for erasure.

Changes:
- Official providers for Chrome, Firefox, Visual C++, DirectX, NVIDIA App, AMD Auto-Detect and MSI Center, preserving Steam/Discord.
- Actual downloads and publisher checks passed for all nine enabled providers. 7-Zip remains disabled because the inspected official installer is unsigned.
- Verified download cache, bounded ZIP extraction, local DirectX extraction, PE and Authenticode checks, RU/EN availability states.
- PostInstall uses staged files only, revalidates each installer and records optional failures without stopping remaining apps. Core file checks remain critical.

Validation gate before upload: Release build; 159 unit tests; DryRun (49 commands recorded, none executed). Actual offline application installation and full Windows VM installation: NOT RUN.

Chrome, Firefox, Visual C++ and DirectX have full locally prepared payloads, not proven offline installation outcomes. Steam/AMD need Internet; Discord/NVIDIA/MSI are conservatively marked Internet-dependent. Protected staging is populated after confirmation but before reboot/erase; moving it before confirmation is still outstanding.

No Windows ISO, ADK payload, downloaded vendor installers, exported drivers, personal manifests/logs, keys or tokens are bundled. Programs download from reviewed official sources during preflight. Do not disable safety checks to proceed past an error.

See docs/APP-PROVIDERS.md, docs/VALIDATION-GATES.md and docs/VM-E2E-PROTOCOL.md in the source or ZIP for evidence and remaining blockers.
