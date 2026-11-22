# Tabrilo

**Your files. Ready to share.**

Tabrilo is a native desktop toolkit for working locally with images, PDFs, QR
codes and archives. The Windows development build uses WPF and .NET 10. The
macOS has an AppKit wheel, native image/PDF export, image compression and metadata tools, and file actions.

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
For an installed update test, use two installers built with `-IsolatedTest`
and distinct versions on a fresh test profile. The test uses an ephemeral
Ed25519 key in memory and an isolated per-user installation identity.

```powershell
.\scripts\package.ps1 -Version 0.1.0 -IsolatedTest
.\scripts\package.ps1 -Version 0.2.0 -IsolatedTest
.\scripts\test.ps1 -OldInstaller $oldSetup -NewInstaller $newSetup
pwsh -NoProfile -STA -File .\scripts\export-icon.ps1
```

Set `$oldSetup` and `$newSetup` to the installer paths printed by the first
two commands. The icon export regenerates the Windows ICO from the original
editable SVG.

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
Applications built from the current checkout display an unconfigured update
channel. Windows x64 updates require signed appcasts and installers.

GitHub Actions builds and tests Windows from a clean checkout. On main it also
checks an unsigned self-contained payload on the runner. Desktop interaction
and installed-updater acceptance require separate native sessions. Release
publication is inactive. Production signing, feed configuration and binary
redistribution still require review and owner approval.

## macOS foundation

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
