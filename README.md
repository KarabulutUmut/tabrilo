# Tabrilo

**Your files. Ready to share.**

Tabrilo is a native desktop toolkit for working locally with images, PDFs, QR
codes and archives. The Windows development build uses WPF and .NET 10. The
macOS app has an AppKit wheel, native image/PDF export, image compression and metadata tools, and file actions.

Tabrilo is in development and has no approved public release. See
[STATUS.md](STATUS.md) for verification and remaining acceptance work.

## Windows development

Run from the repository root in native PowerShell 7. Install the Windows x64
.NET SDK version in [global.json](global.json) on PATH, or extract the
matching SDK archive into `.tools/dotnet`. The Microsoft archive URL and
SHA-512 digest are in [shared/dependencies.json](shared/dependencies.json).

```powershell
.\scripts\restore-tools.ps1 -Validation
.\scripts\doctor.ps1
.\scripts\check-manifests.ps1
.\scripts\test.ps1 -Configuration Release
.\scripts\run.ps1 -Configuration Release
```

The test script runs the documentation budget self-test and builds in locked
NuGet mode. Tool restore verifies qpdf and actionlint before extraction. The
archive copies and NuGet packages are cached under `.tools` and `.nuget`.
For a build without tests, run `.\scripts\build.ps1 -Configuration Release`
after restoring tools.

After reviewing a dependency bump in a clean worktree, run
`.\scripts\refresh-locks.ps1` using the pinned SDK. Review the versions and
content hashes in every changed `packages.lock.json`, then verify the
committed changes with a locked restore in a clean checkout. CI uses locked
restore for the Windows build.

Build an unsigned local development payload with
`.\scripts\package.ps1 -Configuration Release -PayloadOnly`. To compile
the local installer, first run `.\scripts\restore-tools.ps1 -Installer`
and then run packaging without `-PayloadOnly`. The scripts print absolute
paths under `artifacts/package/local`. This output is for local verification.

The test script writes synthetic fixtures and results below `artifacts/tests`.
After the Release build, run `.\scripts\test-desktop.ps1` on a Windows desktop for native controls and captures.
For an installed rehearsal, restore installer tools and run the selected edition pair.
The script embeds an ephemeral public key in a distinct test installation and signs its local fixture feed.

```powershell
.\scripts\rehearse-update.ps1 -OldFlavor core -NewFlavor core
.\scripts\rehearse-update.ps1 -OldFlavor office -NewFlavor office
pwsh -NoProfile -STA -File .\scripts\export-icon.ps1
```

Set `TABRILO_TEST_UPDATE_UI=1` on an interactive test desktop to drive both update consent dialogs and verify relaunch.
Use `-OldFlavor office -NewFlavor core` for the separate explicit installer transition.
The icon export regenerates the Windows ICO from the editable SVG.

## First run

With Tabrilo running, drag files in Explorer and tap a mode key. Shift shows
formats, Ctrl shows relevant tools, and Alt shows Share, Float, Copy, Move,
Copy Path and Terminal. Release the key and drop on a slice. Releasing over
the center, a gap, or outside cancels the wheel. Escape cancels the held drag.
Ctrl+Space opens the keyboard wheel for Home’s selection. Use arrows and Enter.
Closing Home keeps the app in the notification area when tray mode is enabled.
Choose **Quit Tabrilo** from its tray menu before launching another build.

Conversions write beside each source. JPG uses a white background and quality
88 by default. PDF pages render at 150 DPI. Multiple pages or split parts go
into a new sibling folder. Combined images use full-path order and ask for
a destination when their parents differ. Jobs is available on request.

DOCX and PPTX render with a local LibreOffice engine. Configure its
`soffice.com` in Settings or package with `-IncludeOffice` using the pinned
MSI in `.tools`. This optional development payload includes the engine.
Editable PDF-to-Office reconstruction and public distribution remain open.

In Redact, drag to draw a mask, drag the mask to move it, or drag a corner to
resize it. Use Delete to remove the selected region. Copy result processes the
current edits. Paste into another program and compare it with the lossless PNG
from Save copy. Development builds show their version and commit in the title.

## Distribution and updates

The Windows installer stages each version under the current user's Programs
folder. Settings and history live in LocalAppData outside that folder.
Rerun the identical installer to verify the payload and restore missing files. Setup reports a conflict
if that displayed version has different bits or a different component selection. To switch between
core and Office, uninstall first or install a newer version. Newer versions use separate folders.
Each version's receipt controls uninstall. Modified or unrelated files are preserved.
Run `./scripts/test-installer.ps1` after restoring installer tools to exercise these transitions
with a small synthetic component and a fresh per-user test identity.
Review builds identify their version, edition, channel and source revision in the title.
Setup basenames identify the edition. Run `.\scripts\inspect-package.ps1 -Installer $setup` with the
printed installer path to verify the payload receipt, self-check and checksum and emit a neighboring `review.json`.
Review builds have an unconfigured feed. Migrate from 0.1.3 manually with the newer installer of the desired edition.

Windows CI builds and tests PRs. A manual dispatch exercises installed core → core, Office → Office
and an explicit Office → core switch on separate Windows runners. See STATUS for run results.
Packages stay local pending the component-specific gates in `packaging/materials.json`.
Pass the two absolute Setup paths to `.\scripts\prepare-release.ps1 -CoreInstaller $core -OfficeInstaller $office`
to inspect both packages and produce a local promotion manifest. No credentials are required for this step.
Run `.\scripts\restore-office-materials.ps1 -External` for the pinned LibreOffice source and external build inputs.
Use `.\scripts\inspect-office-materials.ps1 -OfficeDirectory $officeDirectory` on the payload's `tools/office` folder
to reconcile its files with the upstream MSI and index the bundled license sections.
On a VS 2026 x64 C++ build machine, `.\scripts\relink-magick.ps1` rebuilds the HEIC delegate and verifies a decode.

## macOS build

`sh apps/macos/build.sh` builds the native bundle on a Mac with Xcode Command
Line Tools. The bundle uses AppKit, Core Graphics and Image I/O. Finder acceptance requires a Mac desktop. See
[STATUS.md](STATUS.md) for the Mac handoff.

## Licensing and specification

Tabrilo's original code is [MIT-licensed](LICENSE). Bundled components retain
their individual terms. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)
and the package's licenses directory.

[PRODUCT.md](PRODUCT.md) defines scope and safety behavior.
[ARCHITECTURE.md](ARCHITECTURE.md) defines implementation and release boundaries.
[UI_SYSTEM.md](UI_SYSTEM.md) describes the native experience.
[AGENTS.md](AGENTS.md) governs repository work and documentation limits.
