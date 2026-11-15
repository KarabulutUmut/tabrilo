# Tabrilo agent instructions

## Mission and boundaries

Build the complete scoped Windows v1, then the native macOS counterpart. This is not a throwaway prototype.
Use C#/.NET 10/WPF on Windows and Swift/AppKit on macOS. Do not substitute a web UI or cross-platform UI framework.
Write original application code and assets from this specification and primary dependency/platform documentation.
Do not fetch, clone, translate, rebrand, or copy a reference application's source, tests, assets, docs, or update identity.
Independent library dependencies are allowed with documented licenses and redistribution compliance.
Never weaken a safety requirement or quietly reduce scope to make a test or implementation appear complete.

## Read and update the right sources

Read this file, STATUS.md, and the relevant sections of PRODUCT.md, ARCHITECTURE.md, and UI_SYSTEM.md before work.
README.md owns verified setup/use commands. PRODUCT.md owns intended behavior. ARCHITECTURE.md owns structural decisions.
UI_SYSTEM.md owns design. STATUS.md owns current evidence, blockers, and the next actionable handoff.
A planned feature is not implemented merely because it appears in PRODUCT.md.
If evidence contradicts a document, fix the document or implementation; do not repeat a known false claim.
Honor explicit current user decisions. Record intentional departures from the approved design with their reasons.

## Documentation budgets

This table is authoritative for the first-party narrative files. Caps include blank lines and fenced blocks.

| File | Maximum lines |
| --- | ---: |
| README.md | 150 |
| AGENTS.md | 120 |
| PRODUCT.md | 220 |
| ARCHITECTURE.md | 240 |
| UI_SYSTEM.md | 180 |
| STATUS.md | 100 |

Implement scripts/check-docs.ps1 to parse this table, warn at 80%, and fail above a cap or for missing required files.
Run the same check locally and in CI. Include a self-test for over-budget/missing-file cases.
Update sections in place; remove obsolete text rather than appending run logs, dated summaries, or duplicate roadmaps.
Do not add narrative Markdown files, archive directories, saved prompt collections, or nested AGENTS files without approval.
Do not evade caps through giant lines, dense semicolon paragraphs, or renamed prose dumps. Keep paragraphs readable.
Legal notices, upstream license texts, and generated machine reports are exempt; do not truncate required notices.
Store test evidence as bounded ignored artifacts or CI artifacts. Git history, PRs, and release notes keep historical detail.
Keep STATUS.md at no more than five prioritized next actions. Replace its old handoff rather than accumulating handoffs.

## Engineering rules

Use typed, cancellable operations and a shared semantic command registry; keep processing out of UI event handlers.
Prefer cohesive modules and focused tests over a giant MainWindow or unnecessary abstraction layers.
Use current stable, explicitly pinned dependency versions after inspecting their primary documentation and licenses.
Do not add a third implementation language, custom FFI engine, paid service, or new product domain without justification/approval.
Never invent cryptography, image codecs, archive parsers, or PDF parsers.
Do not assume an optional OS codec exists. Validate packaged functionality on a clean installation.
Use native Windows tooling for WPF builds/tests; a Linux/WSL build is not desktop validation.
Do not change global Git settings or install/elevate system-wide tools without the required approval.

## Safety requirements

Preserve inputs, write to app-owned temporary destinations, validate output, then publish without overwriting originals.
Treat file contents, filenames, QR payloads, clipboard data, and external metadata as untrusted data.
Never execute document scripts, shell-interpolate filenames, auto-open QR links, or run extracted files.
Redaction must replace pixels and emit a fresh allowlisted output; overlays/blur do not qualify.
Archive extraction must enforce on-disk containment and streamed resource limits, not just string checks.
PDF metadata removal must not leave previous incremental metadata revisions in the output.
Keep passwords and signing keys out of source, logs, process arguments, app bundles, and public CI artifacts.
Never bypass updater verification, even when production signing credentials are absent.
Use ephemeral test keys only in an isolated test channel; no production key generation or credential upload without approval.

## Git and release discipline

Inspect the working tree and remote before initializing or changing branches. Preserve pre-existing user work.
Do not force-push, reset/delete user changes, publish releases/tags, or change repository settings unless explicitly authorized.
Use local, reviewable commits for the first build task. Do not invent Git author addresses.
CI must give untrusted PR jobs no signing credentials and minimal token permissions.
Public stable releases require tested packages and authentic signatures, not merely a successful build or checksum.
Unconfigured signing must block trusted release publication, not block ordinary local development or non-production tests.

## Verification and reporting

Inspect generated files with independent decoders/inspectors where feasible; mocks alone do not validate real exports.
Run unit, integration, adversarial, native-interaction, packaging, and updater tests appropriate to the touched behavior.
Keep input-hash preservation assertions and destructive/cancellation failure tests.
Never relabel a skipped test as passed or disable a failing gate to get green CI.
A GUI test needs an actual interactive desktop and evidence; creating its test code is not executing it.
Windows-only work cannot establish Mac usability, permissions, Finder integration, or Mac update success.
Record exact commands, environment, exit codes, and artifact paths in the current handoff/evidence.
Use statuses: not started, implemented/unverified, verified with evidence, blocked.
If a capability/tool is unavailable, keep progressing elsewhere and report the concrete blocker rather than guessing success.
Before finishing, reconcile the six docs, run their budget check, and leave the repository in a resumable state.
