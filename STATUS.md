# Tabrilo current status

Windows development build 0.1.3 is available for owner review on [draft PR #8](https://github.com/KarabulutUmut/tabrilo/pull/8).
The local core and Office packages were built from `84d05c33d3f17c368b9e553293b70bd0f5892ec0`.
On Windows 10 x64 build 19045 with .NET SDK 10.0.404, both packaged self-checks passed.
The final locked Release build had zero warnings and errors. Its 20/20 test groups included the actual Office engine and owner PDF.

## Explorer and Move — verified with evidence

On 0.1.2, releasing over a second Explorer folder moved the PNG. On repaired 0.1.3, the owner
released Shift before dropping over real folders on the same display and across two displays.
The original hash stayed in Source and the destination received no file. Center, gap and physical
Esc cancelled. Leaving while held, returning to JPG and releasing produced one neighboring JPEG.
An immediate new drag worked. Earlier native evidence covers Shift/Ctrl/Alt switching and external Copy/Copy Path.
The owner tried Share with Home closed, cancelled the sheet, dragged a file from Float to Explorer,
and opened Terminal in the source directory. A second Terminal check confirmed the publisher prompt was gone.
A real Move collision preserved both files. The prior picker-cancel check is in `three-wheel-move-cancel.log`.
The 0.1.2 binary threw on a cyclic Move. Native tests now cover self/child rejection, collisions, Unicode,
missing paths, successful file/folder moves and a cancelled batch that reports its completed item.
Physical checks used the 0.1.3 core builds with their respective UI fixes. On the final archive-only revision,
a fresh Home → JPG smoke test produced a 10,214-byte JPEG (1275 × 1650) and preserved the PNG hash.

## Archive ownership — verified with evidence

Extraction holds the stage root through publication. Rollback checks identity and preserves entries with
uncertain ownership. Native tests cover staging replacement, collisions, open readers, interrupted publication
and unrelated markers. A further 160 ZIP, TAR, GZIP and TGZ publications succeeded.
The authenticated main alert #1 remains open at `984ea4b3`. Its 20-step trace follows a ZIP entry to
a pathname-based stream. The PR merge analysis for application commit `84d05c33` reported zero results.
Earlier PR analyses also reported zero. Native regressions provide the evidence for the ownership repair.

## PDFs and Office — verified with evidence

Lossless final compression reduced the screenshot PDF from 1,039,492 to 5,464 bytes and the photo PDF
from 6,914,695 to 3,938,235 bytes. PDFium rendered both pixel-identically to the old outputs.
The runs took 137 ms and 2,085 ms respectively. Test-process peak working sets were 156 MB and 244 MB.
Direct and indirect destination arrays and allowed links passed. Cycles, active actions and protected
rewrites remain guarded. All nine owner PDF Split outputs matched source text, page geometry and pixels.
PDFsharp remains at MIT 6.2.4. Page assembly discloses omitted tags/names and flattened bookmark hierarchy.
The Office tests covered external relationship rejection, private profiles, queued cancellation and
distinct text and rendered content on page two of both DOCX and PPTX. Complex layout remains unverified.

## Per-user installation — verified with evidence

The old installer accepted a same-version switch while omitting Office and left old Office files on uninstall.
Setup now refuses changed bits or components at one version. An identical package verifies installed files,
restores missing entries and reports modified files. Each version's receipt governs removal.
Real isolated NSIS tests cover both component directions, interrupted registration, repair, upgrade and uninstall.

| Local 0.1.3 package | Installer bytes | Unpacked payload bytes |
| --- | ---: | ---: |
| Core | 97,382,338 | 247,815,372 |
| With Office | 612,178,409 | 1,854,330,921 |
| Office increment | 514,796,071 | 1,606,515,549 |

A fresh Windows runner installed the Office-equipped 0.1.2 test identity, rendered both Office fixtures
and verified an isolated signed upgrade to core 0.1.3. Damaged and interrupted downloads preserved the
prior install. Uninstall removed both versions while preserving settings and the export. All 19 groups passed.
Installed file bytes including each version's uninstaller were 1,854,390,054 (Office) and 247,874,498 (core),
a difference of 1,606,515,556 bytes. [Clean Windows run](https://github.com/KarabulutUmut/tabrilo/actions/runs/46005520619)

## Reproduce and inspect

From native PowerShell 7 at the checkout root, set `TABRILO_TEST_OFFICE` to the full package's
`payload/tools/office/program/soffice.com`, `TABRILO_TEST_PHOTO` to the test JPEG and `TABRILO_TEST_SPLIT_PDF` to the owner PDF.
Run `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release` and `pwsh -NoProfile -File scripts/check-manifests.ps1`.
After restoring installer tools, run `pwsh -NoProfile -File scripts/test-installer.ps1` for the isolated component matrix.
Use README for packaging commands. The local test and packaging commands exited 0.

Evidence under `artifacts/verification` includes `accepted-release.log`, `accepted-independent.json`,
`accepted-core.json` and `accepted-office.json` (absolute executable/installer paths and hashes).
`acceptance-probe/` holds native before/after results. `archive-stress.log`, `final-main-trace.json`,
`final-installer-matrix.log` and `accepted-ci.log` cover publication, analysis and installation.
The final test outputs are in `artifacts/tests/20261127-132301-4636`.

## Next actions

1. Owner review of draft PR #8. The local owner guide includes the launch command and four short checks.
2. Windows implemented/unverified on a second volume: outside drop and interrupted cross-volume directory Move. Only C: was mounted. Mixed-DPI, accessibility and sustained-load interaction also need native checks.
3. Mac implementation: busy feedback or concurrent lightweight actions, result-specific Reveal, partial batches, background Move and metadata-format parity. Most contextual tools, Office, packaging and signed updates remain to implement.
4. Mac validation: hosted macOS 15 AppKit build and PDF self-check passed. Use Finder to check valid destinations, Control/Option, permissions, external clipboard and multiple monitors, then test an installed old-to-new update.
5. Public distribution is blocked pending source availability for the bundled LibreOffice MPL build, corresponding-source/relink review for bundled LGPL libraries, tested signed packages and owner-approved feeds.
