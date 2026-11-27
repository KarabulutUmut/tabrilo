# Tabrilo Windows review status

Windows 0.2.0 review packages come from `ff2c490391384158a15bdd1a9694541c8728a1bb` on [draft PR #8](https://github.com/KarabulutUmut/tabrilo/pull/8).
The later `59f3942` commit changes the test harness, component-audit tooling and receipts. The final handoff commit changes only the six project documents.
Both are per-user, self-contained Windows x64 builds for OS build 19041 or newer. The local channel has no production feed.
Testing used Windows 10 Home 22H2 build 19045.6466, PowerShell 7.6.9, SDK 10.0.404 and separate fresh hosted Windows runners.

## Review packages

| Edition | Local build ID | Installer bytes | Unpacked file bytes | Isolated installed file bytes including uninstaller |
| --- | --- | ---: | ---: | ---: |
| Core | `20261127-181131-8186927f` | 100,492,297 | 254,261,762 | 254,324,726 |
| Office | `20261127-181214-62588332` | 615,288,263 | 1,860,777,313 | 1,860,840,276 |

Start `payload/Tabrilo.exe` in the corresponding directory under `artifacts/package/local/0.2.0`.
Both receipts, installer checksums and packaged self-checks passed. The paired manifest is
`artifacts/promotion/ff2c490391384158a15bdd1a9694541c8728a1bb/windows-review.json`.
It gives exact executable and Setup paths, SHA-256 digests, identities, sizes and release-note inputs.

## Windows verification matrix

| Area | Status and executed evidence |
| --- | --- |
| Routing and publication | Verified with evidence. Typed command registry, three modes, compatible formats, source-adjacent exports, collision-safe siblings, original hashes, cancellation and partial batches passed. |
| Images | Verified with evidence. Required still-image codecs, HEIF fixture, JPEG alpha, orientation, edits/marks/framing, secure PNG masks and transformed coordinates passed. WIC and Pillow independently decoded outputs. |
| PDF | Verified with evidence. Native page render, metadata full rewrite, guarded page assembly, links/destinations and both compression modes passed. PDFium matched all nine owner split pages in text, geometry and pixels. |
| QR and archives | Verified with evidence. Multiple QR locations, ZIP/TAR/GZIP/TGZ/RAR, streamed limits, stage/rollback ownership, collision and input preservation passed. Additional native archive stress covered 160 publications. |
| Office | Verified with evidence. Private offline LibreOffice 26.8.3.1 rendered two-page DOCX/PPTX with tables, embedded imagery and a missing title font. Page two had distinct text and pixels. External links failed safely. |
| Real Explorer | Verified with evidence. Owner completed outside/center/gap/Esc and mode switching on 0.2.0. Automated Explorer on the packaged revision repeated five drags. Source SHA-256 stayed fixed, Receiving stayed empty and returning to JPG wrote one adjacent 100 × 70 JPEG. |
| Universal actions | Verified with evidence. Earlier physical Share/cancel, Float drag-out, Explorer file-reference paste, exact Unicode paths in Notepad and Terminal working directory checks are retained. |
| Move | Verified with evidence. Distinct volume GUIDs, file/folder success, collision, pre-cancel, in-flight file cancel, partial counts and safe retry. Cancelling the real Windows folder transfer left 3,748 source and 252 destination files in one run. All 4,000 hashes passed after retry. |
| Native UI and load | Verified with evidence. Real wheel captures, feedback, both Settings themes, 150% text, Edit/Redact, PDF page previews, keyboard focus, monitor-edge containment and hosted system high contrast. Three fresh hosted processes preserved document colors and tile edges in high contrast and exited cleanly. |
| External redaction paste | Verified with evidence. On `a9dd5f2`, Paint and Save copy produced 7,000 identical RGBA pixels including 425 opaque mask pixels. Independent PNG inspection found no metadata. The harness could not confirm restoration of the previous clipboard. |
| Packages and upgrades | Verified with evidence. Both installed 0.1.99 apps accepted signed 0.2.0 upgrades of the same edition. A newer manually selected Office → core Setup also passed. Corrupt/truncated input preserved the old install. |
| Update UI | Verified with evidence. Actual installed apps handled download/install consent and relaunched. History, settings and exports survived uninstall. The upgraded Office copy rendered both DOCX and PPTX offline. |
| Security | Verified with evidence. Signed feed and installer, SHA-256, edition, origin, version/OS and PE identity checks. Main archive alert #1 is still open at `984ea4b3`. PR analysis and native rollback regressions are separate evidence. |
| Hardware | Implemented/unverified on mixed-DPI or negative-coordinate layouts. Both attached displays reported 96 DPI and nonnegative origins. Edge placement passed on both. |
| Editable PDF → DOCX/PPTX | Not started. Editable reconstruction remains explicit future scope. |

The packaged core reached input-idle in 835 ms with an 82,321,408-byte working set.
During 8 real encodes, 24 wheel openings took 46.9 ms median and 69.6 ms maximum. Cancellation took 29.5 ms.
The desktop driver’s working set went from 272,613,376 to 264,306,688 bytes. The installed Office coordinator
verified a 615,290,304-byte signed Setup. Both local installed suites, including consent, relaunch and uninstall, passed 21/21.

## Reproduce and inspect

From native PowerShell 7 at the root, run `./scripts/check-manifests.ps1`, `./scripts/check-docs.ps1 -SelfTest`
and `./scripts/test.ps1 -Configuration Release`. The configured Office/owner-fixture run passed 20/20 with zero build warnings.
`./scripts/test-desktop.ps1` exercises visible WPF windows. README gives both installed rehearsal commands.
External Windows UI Automation verified all six Home buttons, the feedback actions and four named, keyboard-focusable Settings selectors in the packaged core. See `verification/rc-desktop/final-accessibility.json`.
The following evidence paths are relative to `artifacts/`. Private inputs and document contents stay off CI.

- `verification/surface-candidate-tests.log`, `verification/final-independent.json` and `verification/archive-stress.log` cover processing, source preservation and archive ownership.
- `verification/windows-capabilities.json` maps all 37 command IDs to their formats, Windows dispatch paths, acceptance criteria and evidence.
- `verification/rc-desktop/final-explorer.json`, `final-wheel-*.png`, `final-clipboard.json` and `independent-clipboard.log` in that directory record the packaged UI. `tests/20261127-182531-24768/` contains controls, themes, editors and load measurements.
- `verification/final-office-update-ui.log` and `verification/final-core-update-ui.log` cover the isolated interactive Office and core upgrades. `verification/final-hosted-*.log` records clean-runner results.
- [Installed package jobs](https://github.com/KarabulutUmut/tabrilo/actions/runs/46047580810) include the actual upgrade sizes. [Hosted accessibility](https://github.com/KarabulutUmut/tabrilo/actions/runs/46047563114) includes native captures.

## Redistribution and external release gates

Each payload includes 21 locked package/runtime records, native file hashes, 38 codec entries, upstream notices and exact source manifests.
The Office cache holds all four matching release archives and 153 upstream external inputs, verified at 1,549,304,918 bytes total.
The MSI inventory matched 19,423 Office files across 5,985 components, plus its administrative MSI. Ten alternative ARM64 CRT rows share x64 target names. The report indexes 236 headings in the shipped license at `materials/libreoffice-26.8.3.1/windows-components.json`.
`scripts/restore-office-materials.ps1 -External` rebuilds the cache. The upstream build-input superset includes optional components.
For Magick, every PE section matched the vendor DLL. The hosted replacement build loaded its re-linked DLL by verified SHA-256 and decoded HEIC. See `verification/final-relink-result/verification.json`.
Both editions' eight qpdf CRT DLLs matched Microsoft's signed 14.51.36247.0 x64 redistributable byte-for-byte. See `materials/vc145/verification.json`. Distributor entitlement remains an owner decision.
Office's nine x86 and ten x64 CRT DLLs also matched Microsoft's signed 14.44.35211.0 bundles byte-for-byte. Their MSI, CAB and individual file hashes are in `packaging/verification/windows-0.2.0.json`.
Public promotion requires approved corresponding-source delivery, material clearance, a clean approved commit, publisher credentials, timestamping and signed edition-specific feeds.
The 0.1.3 development channel needs manual migration. Local build, merge and production publication are independent.

## Next actions

1. Owner accepts the Windows review on open PR #8. A mixed-DPI/negative-origin station remains a hardware coverage gap.
2. For distribution, complete recipient-access and publisher decisions against the material manifests. Keep signing and publication gated.
3. After merge, branch from the accepted `main` on the MacBook. Start in `apps/macos/Sources/main.swift`, `DragWheel.swift`, `MacExports.swift`, `FileShelf.swift` and `SelfCheck.swift`.
4. Implement remaining AppKit editors, PDF/QR/archive/Office tools, background jobs/results, Move, packaging and edition-preserving Sparkle 2 updates against the shared contracts, tokens and fixtures.
5. Run native Finder release/cancellation, Control/Option, VoiceOver, clipboard, multi-display, first launch and installed-update acceptance on the MacBook. The [hosted AppKit check](https://github.com/KarabulutUmut/tabrilo/actions/runs/46047563160) covered compilation and fixture exports.
