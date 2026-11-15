# Tabrilo

**Your files. Ready to share.**

Tabrilo is a native desktop toolkit for working locally with images, PDFs, QR
codes and archives. The Windows development build uses WPF and .NET 10. The
macOS follow-up has an AppKit shell and shared command contracts.

There is no public release. See [STATUS.md](STATUS.md) for tested capabilities,
remaining Windows acceptance work and release blockers.

## Windows development

Run from the repository root in native PowerShell 7. Use the Windows x64 .NET
SDK pinned in [global.json](global.json). The scripts also recognize a
project-local SDK at `.tools/dotnet/dotnet.exe`. Microsoft publishes the SDK
binary archive and SHA-512 digest recorded in
[shared/dependencies.json](shared/dependencies.json).

```powershell
.\scripts\restore-tools.ps1 -Installer
.\scripts\doctor.ps1
.\scripts\build.ps1 -Configuration Debug
.\scripts\test.ps1 -Configuration Release
.\scripts\run.ps1 -Configuration Release
.\scripts\package.ps1 -Configuration Release
.\scripts\check-docs.ps1 -SelfTest
```

The tool restore verifies qpdf and portable NSIS archives before extraction.
Omit `-Installer` for build and test work that only needs qpdf. Build and
package enforce the NuGet lockfiles. The local archive copy and packages
are cached under `.tools` and `.nuget`.

The package script prints the absolute paths to the executable and the
per-user installer under `artifacts/package/local`. The published payload
contains the .NET Desktop runtime and bundled processing dependencies.
`-PayloadOnly` creates a self-contained app without compiling the installer.
The local installer is for verification pending distribution review.

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

Choose files, drop them into Home, or paste an image or file selection
explicitly. Available actions depend on the input types. Ctrl+Space opens the
keyboard action wheel. The default Explorer wheel gesture is Shift while
dragging. Settings displays the current bindings and wheel preferences.

Choose an export folder when prompted. Result lists show the actual paths.
Redaction exports lossless PNG. Select opaque regions on the original, then
use Preview result before copying or exporting. To check redaction end to
end, paste the copy into another program and inspect both the pasted result
and the saved PNG.

## Distribution and updates

The Windows installer stages each version under the current user's Programs
folder. Settings and history live in LocalAppData outside that folder.
Applications built from the current checkout display an unconfigured update
channel. Windows x64 updates require signed appcasts and installers.

Once the release acceptance and redistribution work is complete, configure the
protected `windows-release` environment with required reviewers and a trusted
branch policy. Review the stable HTTPS feed and Windows trust anchor in
`shared/contracts/windows-update.json`. The draft workflow expects the reviewed
release enable variable, a timestamp server and the three signing secrets listed
in the workflow. After reviewing and explicitly promoting a draft, publish its
appcast and signature to the configured Windows x64 feed. Each Mac architecture
has a separate feed. The repository's latest-release endpoint is not used for
platform selection.

## macOS foundation

`sh apps/macos/build.sh` describes the build entry on a Mac with Xcode Command
Line Tools. The AppKit shell and catalog loading await a Mac build. See
[STATUS.md](STATUS.md) for the Mac handoff.

## Licensing and specification

Tabrilo's original code is [MIT-licensed](LICENSE). Bundled components retain
their individual terms. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)
and the package's licenses directory.

[PRODUCT.md](PRODUCT.md) defines scope and safety behavior.
[ARCHITECTURE.md](ARCHITECTURE.md) defines implementation and release boundaries.
[UI_SYSTEM.md](UI_SYSTEM.md) describes the native experience.
[AGENTS.md](AGENTS.md) governs repository work and documentation limits.
