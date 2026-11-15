# Tabrilo current status

Updated 2026-11-15. Windows development build. Stable acceptance remains open.

## Environment and Git

Windows 10 Home 22H2 x64, build 19045.6466, PowerShell 7.6.9 and project-local
.NET SDK 10.0.404. WPF and Windows.Data.Pdf ran natively. This is evidence for
this host. Windows 11 and clean supported installations still need validation.

Local Git is initialized on unborn branch `codex/windows-v1`. Origin returned
no refs. Neither the sandbox nor desktop account has a configured Git author.
There are no local commits or remote publications. The files are in the working tree.

## Implementation and evidence

| Area | Status | Evidence |
| --- | --- | --- |
| Windows build and automated operations | Verified with evidence | Release integration log and generated synthetic files |
| Image engines | Verified with evidence | Real HEIC, PNG, JPEG, WebP, BMP and TIFF cases; external clipboard and multi-image HEIF acceptance pending |
| PDF engines and QR | Verified with evidence | Page rendering and assembly, normal and incremental Info rewrites, both compression paths, multi-result image and PDF QR |
| Archive engines | Verified with evidence | ZIP, TAR, GZIP, TAR.GZ and single-volume RAR; limited safety/cleanup suite |
| WPF desktop | Implemented/unverified | UI Automation observed Home, Settings, native file picker and Ctrl+Space wheel with Convert focused |
| Explorer gesture and visual acceptance | Blocked | Desktop capture returned `FrameArrived timed out`; coordinate input geometry unavailable |
| Windows installer and updater | Verified with evidence | Per-user 0.1.0 → 0.2.0 using strict NetSparkle verification and ephemeral keys; uninstall preserved local state and export |
| Shared contracts and Mac shell | Implemented/unverified | AppKit source and bundle script consume the shared catalog; Mac build/session unavailable |
| GitHub Actions and draft release | Implemented/unverified | Local workflow definitions only |
| Production distribution | Blocked | Acceptance, binary redistribution review, publisher certificate, approved update key and feed required |

## Evidence and launch

Run from the checkout with native PowerShell 7.

- `pwsh -NoProfile -File scripts/doctor.ps1` returned exit 0. See `artifacts/verification/doctor.log`.
- `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release` runs the budget
  checker and build before the integration tests. Exit 0, 11/11 groups.
  See `artifacts/verification/test-release.log`.
- `pwsh -NoProfile -File scripts/package.ps1 -Configuration Release` prints the
  package and executable paths in `artifacts/verification/package-release.log`. Exit 0.
  `artifacts/verification/package-smoke.log` records the self-contained startup check and installer digest.
- `scripts/test.ps1 -Configuration Release -OldInstaller $oldSetup -NewInstaller $newSetup`
  returned exit 0 with 12/12 groups. See `artifacts/verification/installed-update.log`
  and the `package-isolated-old.log` and `package-isolated-new.log` files for both paths.
- `artifacts/verification/desktop-uia.txt` records the native interaction and capture limits.
- Use `scripts/run.ps1 -Configuration Release` to run the framework-dependent
  development build or launch the self-contained `payload/Tabrilo.exe` printed by packaging.

## Open Windows acceptance

- Verify actual external Explorer OLE drag/drop, normal inactive drag, keyboard
  navigation, clipboard interoperability, tray/startup, mixed DPI and light/dark screenshots.
- Image region selection, adjustment undo and PDF thumbnail grids compile.
  Exercise the editor controls and previews on the native desktop. Redaction offers PNG.
- Protected/signed and complex PDF cases need fixtures. The isolated update test
  uses the production verification code with an in-process test transport and a real installer.
  The installed app's update UI and interruption during installation remain unverified.
- The unsigned release gate returned exit 1 with the production environment disabled.
- Expand the archive race, on-disk reparse, malformed-format, solid RAR,
  multi-image HEIF and resource-limit cases. Verify packaged offline runs on Windows 11.
- The draft pipeline remains unexercised. Review bundled Magick.NET LGPL
  corresponding source/relinking and qpdf native redistributable terms before distribution.

## Next actions

1. Complete external-drag, editor, clipboard and native visual acceptance on a physical or capturable desktop.
2. Extend PDF structure, format and filesystem safety fixtures.
3. Exercise installer recovery during interrupted installation and rerun clean-profile/offline validation.
4. Review redistribution conditions and configure production signing and hosting only after owner approval.
5. Build AppKit on a Mac, implement operations and Finder integration using the shared contracts, then integrate Sparkle 2 and validate Mac installation and signed updates.
