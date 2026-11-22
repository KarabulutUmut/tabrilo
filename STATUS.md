# Tabrilo current status

Windows development build 0.1.2 at `5d2c4be5` is the owner-tested payload on draft PR #8.

| Area | Status | Evidence |
| --- | --- | --- |
| Native image/PDF, QR, archives and updater trust | Verified with evidence | Native Release suite, Windows 10 x64, .NET SDK 10.0.404 |
| PDF Split | Verified with evidence | Nine one-page parts from the owner's tagged MDPI guide. Source SHA-256 preserved. Fixtures check links, annotations, bookmarks, order and rotation |
| DOCX/PPTX layout | Verified with evidence | Both two-page fixtures render through local LibreOffice 26.8.3 into independently decoded PDFs and page images |
| Explorer and Desktop gesture | Verified with evidence | Owner tested Explorer JPG and cancellation on 2c5077ec. On 5d2c4be5, Home closed, center and outside releases cancelled separately, switching Shift/Ctrl/Alt/Shift and leaving/returning gave one neighboring JPG |
| Clipboard | Verified with evidence | Explorer pasted Tabrilo file references byte-for-byte. Notepad pasted and saved the exact Unicode absolute path |
| Remaining desktop checks | Implemented/unverified | Physical Esc under load, mixed DPI, visual accessibility, native share targets, Float drag-out, Move collisions and terminal interaction |
| macOS | Implemented/unverified | Native hosted build and three-page raster/combined PDF self-check passed. Finder, remaining tools, Office and updates need native acceptance or implementation |
| Production publication | Blocked | Native acceptance, redistribution review, signing credentials and approved feeds |

## Reproduction

From native PowerShell 7 at the repository root, set `TABRILO_TEST_OFFICE` to the absolute path of
the extracted engine's `program/soffice.com` and optionally `TABRILO_TEST_SPLIT_PDF` to a selected PDF.
Run `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release`. The log prints its evidence directory.
With both optional inputs and `-OldInstaller` / `-NewInstaller`, the final run passed 17/17 groups,
including isolated signed installation, upgrade 0.1.0 → 0.1.2 and uninstall. Previous settings were restored.
The log is `artifacts/verification/three-wheel-test.log`. Outputs are in `artifacts/tests/20261122-225908-27992`.
Eight coordinator results were inspected across PDF, Word and PowerPoint. Rendered WPF wheel samples,
clipboard checks and owner evidence are in `artifacts/verification/three-wheel*`. Forty warm Windows
wheel construction/layout samples took 0.48 ms median, 0.54 ms p95, excluding OS event delivery and display.
Build an Office-equipped local payload with `scripts/package.ps1 -Configuration Release -PayloadOnly -IncludeOffice`.
The pinned MSI is 376,311,808 bytes. The measured administrative image is 1,603,968,965 bytes.
The Office-equipped local installer is 611,727,657 bytes. The unpacked app passes its self-check.
The checked-out scripts do not install LibreOffice system-wide. Complex layout, substitute fonts
and editable PDF reconstruction remain outstanding.

## Maintenance and archive review

PR #7 added trusted lock refresh. PRs #1 and #2 update full-SHA Actions pins. PR #3 retains MIT
PDFsharp 6.2.4 and updates Magick.NET independently of the proposed AGPL-3.0-only PDFsharp 6.5.0.
PRs #1–#5 and #7 merged after hosted checks. Hashing and Pkcs 11.0.0 provide net10.0 assemblies.
Archive alert #1 remains open. The original trace reaches the streamed ZIP writer. Component validation,
ancestor pinning and create-new writes protect entry extraction. The stage pin is released before
directory rename and recursive failure cleanup. Ownership across those operations needs a handle-based
fix and native regression evidence. The trace is in `artifacts/verification/utility-codeql-trace.txt`.

## Next actions

1. Check physical Esc under load, native actions, accessibility and mixed-DPI monitors.
2. Verify Office-equipped installation on a clean Windows machine and review redistribution before publication.
3. Complete archive staging ownership and rerun CodeQL against the fix.
4. Validate Finder gestures, Mac rendering, contextual tools, permissions, packaging and updates on macOS.
5. Evaluate editable Office reconstruction and richer tagged PDF hierarchy preservation.
