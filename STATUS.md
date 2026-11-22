# Tabrilo current status

Windows three-wheel development is on utility PR #8. Owner gesture acceptance is open.

| Area | Status | Evidence |
| --- | --- | --- |
| Native image/PDF, QR, archives and updater trust | Verified with evidence | Native Release suite, Windows 10 x64, .NET SDK 10.0.404 |
| PDF Split | Verified with evidence | Nine one-page parts from the owner's tagged MDPI guide. Source SHA-256 preserved. Fixtures check links, annotations, bookmarks, order and rotation |
| DOCX/PPTX layout | Verified with evidence | Both two-page fixtures render through local LibreOffice 26.8.3 into independently decoded PDFs and page images |
| Windows wheel and six file actions | Implemented/unverified | Owner verified packaged Explorer PNG → JPG and cancellation on 2c5077ec. One decoded JPEG, unchanged PNG. Desktop, separate edge cases, external actions and mixed DPI need acceptance |
| macOS | Implemented/unverified | Native hosted build and three-page raster/combined PDF self-check passed. Finder, remaining tools, Office and updates need native acceptance or implementation |
| Production publication | Blocked | Native acceptance, redistribution review, signing credentials and approved feeds |

## Reproduction

From native PowerShell 7 at the repository root, set `TABRILO_TEST_OFFICE` to the absolute path of
the extracted engine's `program/soffice.com` and optionally `TABRILO_TEST_SPLIT_PDF` to a selected PDF.
Run `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release`. The log prints its evidence directory.
The native run with both optional inputs passed 16/16 checks. Results and independent inspections are in
`artifacts/verification/three-wheel*` and `artifacts/tests`. Physical Explorer timing is a separate check.
Build an Office-equipped local payload with `scripts/package.ps1 -Configuration Release -PayloadOnly -IncludeOffice`.
The pinned MSI is 376,311,808 bytes. The measured administrative image is 1,603,968,965 bytes.
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

1. Complete remaining packaged Desktop/Explorer interaction, outside-release, visual and external clipboard acceptance.
2. Verify clean Windows installation, real installed upgrades and licensing before public distribution.
3. Complete archive staging ownership and rerun CodeQL against the fix.
4. Validate Finder gestures, Mac rendering, contextual tools, permissions, packaging and updates on macOS.
5. Evaluate editable Office reconstruction and richer tagged PDF hierarchy preservation.
