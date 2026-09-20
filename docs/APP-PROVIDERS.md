# Application acquisition — Beta 3

Included in Beta 3. This work does not close the separate EFI/recovery/VM installation blockers.

## Trust and local preparation

Provider metadata is reviewed in `AppProviders.cs`: exact HTTPS URLs and redirect hostnames, actual certificate publisher, installer/container type, silent arguments, x64 target, maximum bytes and an optional pinned SHA-256. All enabled providers use signed executable installers. Generic catalog URLs and unsigned local-only payloads cannot bypass provider policy.

The cache is `%ProgramData%/EasyWin/Cache/Apps`. Builder acquisition uses a cache inside its explicitly supplied workspace. Each source has an isolated cache key and exclusive file lease. A cached receipt is not trusted alone: size/hash, PE structure and Authenticode publisher are revalidated. Invalid owned cache files are discarded and downloaded again. Partial downloads are not usable; finalization uses an atomic rename. An unavailable provider is unselectable.

Windows Authenticode verification must return Valid and the exact reviewed signer. This uses Windows certificate-chain policy, not merely the presence of a signature. Revocation/trust availability may affect validation on a fresh offline Windows installation; no trust bypass is implemented.

All selected packages are downloaded and inventoried before the current ERASE confirmation. The protected deployment partition/folder is still created and populated by the existing staging workflow **after that confirmation but before reboot and any disk erase**. Thus the stronger requested ordering, protected staging before confirmation, is not yet implemented. Reordering disk preparation needs a separate transaction/confirmation redesign; this change does not silently repartition a physical disk during ordinary app preflight.

The preflight prepared inventory covers installers, configuration and companions. Staging copies them into the deployment manifest and WinPE verifies the full inventory before destructive work. PostInstall revalidates the manifest/critical files, then validates each app's manifest entry, folder inventory, SHA-256, PE and Authenticode publisher before execution. A failed optional app records a warning and does not prevent remaining apps. Critical configuration/image/driver/worker failures remain fatal. No provider/downloader is called by PostInstall.

DirectX downloads the signed complete June 2010 redistributable, runs its extraction-only `/Q /T:` operation before confirmation, verifies DXSETUP and inventories all CAB companions. MSI downloads the official ZIP, requires exactly one EXE, bounds extraction size, and verifies the extracted executable. ZIP member paths are never used as destination paths.

## Actual acquisition evidence (2026-09-20)

All nine enabled providers were downloaded from the URLs in source and passed PE, size, Authenticode publisher and SHA-256 checks. No application installation was executed. Unit tests use an actual PE test-host file and test HTTP/signature responses; these are distinct from the real download checks.

| Package | Verified signer | SHA-256 of prepared installer | Offline classification |
| --- | --- | --- | --- |
| Chrome | Google LLC | 671a06cdd149ce1867d5485a4749be2f4f95f3f863fc8e4313c0cacf8a2285d2 | Full installer; actual offline installation NOT RUN |
| Firefox | Mozilla Corporation | 65b1fb9e5b5df21d5bee61c919d5a3c85fd4f9ba96996a3d6319e49274fbe0f9 | Full RU installer; actual offline installation NOT RUN |
| Visual C++ x64 | Microsoft Corporation | cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b | Full installer; actual offline installation NOT RUN |
| DirectX DXSETUP | Microsoft Corporation | 8f47d7121ef6532ad9ad9901e44e237f5c30448b752028c58a9d19521414e40d | Extracted with CAB files; actual offline installation NOT RUN |
| Steam | Valve Corp. | 7d3654531c32d941b8cae81c4137fc542172bfa9635f169cb392f245a0a12bcb | Internet required for client bootstrap/updates and service |
| Discord | Discord Inc. | a6c2a0cc74ce2d70c5b6e37be94b25edf07758ad30cc7376ff466358de676498 | Conservatively marked Internet required; update/bootstrap behavior not validated offline |
| NVIDIA App | NVIDIA Corporation | 0a43d4bd1b676b85816f893d345fa130bc7c00453d09654317799147f3b61d9c | Conservatively marked Internet required; driver downloads are not bundled |
| AMD Auto-Detect | Advanced Micro Devices | 3eb3229dc3b8d5e193b5cfe5ceea6e67c83d69d04df2037445bf8be236fdc937 | Online bootstrap, not a full driver package |
| MSI Center | MICRO-STAR INTERNATIONAL CO., LTD. | 95e6be38ed8f1a0ad5055c9831dab057f2b0bba0eb33a25eb60a9486fd0d3ebd | Internet for additional modules; no offline-completeness claim |

7-Zip remains unavailable: the inspected official installer is unsigned. A reviewed pinned release/digest policy or an officially signed package is needed; do not weaken Authenticode for generic unsigned downloads. No test is falsely named as a successful 7-Zip download.

Official references: [Chrome](https://chromeenterprise.google/download/), [Firefox full installer](https://firefox-source-docs.mozilla.org/browser/installer/windows/installer/FullConfig.html), [7-Zip](https://www.7-zip.org/download.html), [NVIDIA](https://www.nvidia.com/en-us/software/nvidia-app/), [AMD](https://www.amd.com/en/support/download/drivers.html), [MSI Center](https://www.msi.com/Landing/MSI-Center), [Visual C++](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170), [DirectX full redistributable](https://www.microsoft.com/en-us/download/details.aspx?id=8109), [Steam](https://store.steampowered.com/about/), [Discord](https://discord.com/download).

## Remaining validation

Pre-release local gate: restore PASS; full Release solution build PASS (zero warnings/errors); unit tests 159/159 PASS, zero skipped; standalone DryRun PASS (49 commands recorded, none executed). Git whitespace check PASS. Repeat the gate from the Beta 3 tag before publishing; the older Beta 2 ZIP does not contain these changes.

- Actual offline installs, first-user visibility of per-user installers when PostInstall runs as SYSTEM, vendor silent-mode behavior and exit codes need disposable VM testing.
- Windows installation/EFI recovery remains unverified; this is not ready for a physical PC.
- Installer acquisition and signature validation do not guarantee successful offline installation.
