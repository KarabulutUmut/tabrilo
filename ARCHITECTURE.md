# Tabrilo architecture

Status: approved design and Windows implementation decisions. See STATUS.md for acceptance evidence.
Changes that affect product scope, trust boundaries, paid services, or the dual-native approach require explicit approval.

## Core decisions

| Concern | Decision |
| --- | --- |
| Windows | C#, .NET 10 LTS, WPF, MVVM, native Windows APIs; initial x64 package |
| macOS | Swift + AppKit; native ImageIO/CoreGraphics/PDFKit where suitable; validate behavior rather than assuming parity |
| Sharing | Versioned data contracts, design tokens, synthetic fixtures, and acceptance expectations |
| Processing | Vetted libraries/tool adapters; no custom codec/parser/cryptographic implementation |
| Persistence | Small versioned local JSON stores; no server/database/account requirement |
| Windows updates | NetSparkleUpdater core with strict Ed25519 verification and WPF-integrated UI |
| Windows packaging | Per-user self-contained installer; NSIS is the initial packaging choice, subject to integration verification |
| Mac updates | Sparkle 2 with supported signature verification; independent platform feed |
| Release | GitHub-hosted artifacts and platform/architecture/channel-specific feeds; explicit release approval |
| Original code license | Standard MIT; independent Tabrilo copyright and third-party notices |

Do not add React, Electron, Tauri, Avalonia, a webview editor, or a custom Rust/C++ bridge.
If a selected dependency cannot satisfy a requirement, record evidence and choose a compatible vetted replacement without weakening safety.
Data contracts do not share executable C#/Swift implementations. Platform-specific changes may require corresponding work in both apps.

## Repository shape

```text
apps/windows/               # WPF application, processing/core modules, Windows adapters
apps/macos/                 # AppKit application and Mac adapters; separately verified
shared/contracts/           # commands, formats, settings schemas, errors, version policy
shared/design/              # canonical visual tokens and original vector assets
shared/dependencies.json    # actual pinned dependencies, origins, licenses, runtime assets
shared/acceptance.json      # expected feature/format cases; not a hand-maintained list of fake passes
tests/fixtures/             # small synthetic fixtures and deterministic generators
tests/windows/              # unit/integration/adversarial/native tests
tests/macos/                # equivalent Mac tests as implemented
scripts/                    # doctor, build, test, run, package, documentation and release checks
packaging/                  # installer definitions and platform packaging inputs
.github/workflows/          # low-privilege Windows validation and C# CodeQL
```

The six root narrative files are enough. Do not create a docs tree for duplicated plans or run logs.
Legal LICENSE, THIRD_PARTY_NOTICES.txt, upstream license files, and machine-readable configuration are separate requirements.
Ignore build output, dependency caches, temporary exports, local settings, keys, diagnostics, and large test artifacts.
Commit small necessary original icons/fixtures; do not commit downloaded runtimes or opaque generated app packages.

## Windows module boundaries

Use a modest solution with clear dependencies; projects can be combined where their responsibilities remain clear:
- Core: operation requests/results, capability rules, jobs, settings models, and export transaction logic; no WPF types.
- Processing: image/PDF/archive/QR adapters and external-tool orchestration, behind testable interfaces.
- Windows: WPF views/view-models, clipboard/file dialogs, OLE drag/drop, native overlay, tray, and shortcut registration.
- Tests: independent unit, integration, adversarial, and interactive desktop suites.

Prefer CommunityToolkit.Mvvm or equivalent small reviewed helpers instead of inventing a large application framework.
Views invoke semantic commands. Engines receive typed requests and cancellation, not UI controls or user shell commands.
No giant MainWindow; no service-per-button abstraction or speculative plugin marketplace.
Mac modules mirror responsibilities and semantic outcomes without translating Windows UI code line by line.

## Contracts and parity

Version JSON schemas for commands, accepted file selections, supported formats, settings, output naming, errors, and design tokens.
Generate/load platform-specific representations and validate against schemas in CI; do not hand-copy divergent constants.
Keep requested capabilities separate from observed support/test evidence. Missing implementations stay visible as blockers.
Shared acceptance cases compare decoded content, page geometry, expected pixels, and safety properties, not byte-identical exports.
Shortcuts reference semantic command IDs and scopes. Translate primary modifiers to Ctrl/Command at the platform boundary.
Use independent platform build counters and explicitly mapped display/package versions; do not conflate GitHub's latest release with a feed.

## Dependency selection

Inspect primary documentation and exact license terms before implementation; pin versions, lockfiles, and asset checksums.
Record purpose, platform/architecture, source, version, license, notices, redistribution obligations, and known capability limits.
Prefer maintained permissive/native dependencies; do not silently choose paid SDKs or AGPL/GPL application obligations.
Assess transitive/native codec terms too; do not assume a wrapper's license covers bundled binaries or patent questions.
Potential processing choices are libraries behind adapters, not permission to rewrite parsers.
PDF manipulation/rendering, HEIC/WebP support, QR decoding, RAR, and metadata inspection need explicit acceptance fixtures.
Windows uses qpdf 12.4.2 for inspection and full rewrites, Windows.Data.Pdf for bounded page rendering,
and MIT-licensed PDFsharp 6.2.4 for image PDFs. PDFsharp 6.5.0 required licensing at runtime.
Magick.NET runs with a raster coder allowlist and denies external delegates.
The bundled native codecs' LGPL redistribution requirements remain a release dependency.
Do not assume any one PDF library preserves every document structure or provides every image-compression mode.
Bundle required redistributable runtimes in release packages. Downloads, if genuinely unavoidable, need explicit reviewed setup and integrity checks.
Use absolute trusted executable paths, argument arrays, timeouts, output limits, and process-tree cancellation; no shell interpolation.
Separate external parser workers where practical. Do not claim an in-process decoder or ordinary child process is a sandbox.

## Job and export pipeline

Input validation -> capability check -> queued operation -> private working data -> output validation -> final publication.
Use bounded concurrency, memory/pixel/page limits, timeouts where appropriate, and cancellable progress.
Read large inputs incrementally; do not load every PDF page or image into memory at once.
Reserve unique output names. For file publication, stage on the destination volume and rename without overwriting a user file.
A batch may publish successful individual items, but must identify failures/cancellations; archive extraction is all-or-nothing.
Temporary directories need app ownership markers and bounded cleanup that never traverses links or deletes arbitrary user paths.
Crashes/cancellation must not masquerade as completed output. Originals remain read-only to normal operations.

## Privacy and parser trust boundaries

All inputs and embedded metadata are untrusted. File extensions are hints, not sufficient validation.
Image redaction creates new pixels and an allowlisted output; JPEG/PNG and clipboard paths must share the verified result.
Preview, orientation, crop, redaction, and export use one coordinate model; test transforms explicitly.
Normalize color deliberately; metadata privacy must not silently corrupt normal visual interpretation.
PDF rendering disables scripts/actions/network resources. Rewriting properties must remove old metadata revisions.
For PDF preservation limits, reject or clearly request consent rather than silently flattening or losing content.
Archive writers enforce containment at filesystem access time, reject links/special paths, and measure streamed output bytes.
Treat QR results as text until an explicit allowed-link action is confirmed.
Never log passwords, raw document contents, QR secrets, images, or private signing material.
Diagnostics should use redacted/relative paths where possible, bounded rotation, and no automatic upload.

## Local state

Windows settings/history: user-scoped LocalApplicationData/Tabrilo; keep this separate from the install directory.
macOS settings/history: user-scoped Application Support/Tabrilo or native equivalent; separate caches from durable preferences.
Keep versioned JSON preferences and at most 50 recent result references; use atomic writes and validated migration defaults.
Malformed settings must not crash startup; preserve an app-owned recovery copy and reset invalid fields safely.
Do not persist passwords, unredacted editor snapshots, automatic clipboard captures, or telemetry identifiers.
Uninstall preserves preferences by default unless the user explicitly requests deletion. Never remove exported user files.

## Native integrations

The wheel requires real global file-drag activation plus normal in-app drag/drop and a keyboard equivalent.
Windows: use native/OLE drag payloads and a suitable overlay; do not infer dragged files by stealing the clipboard.
Do not inject DLLs into Explorer, install privileged drivers, or require administrator rights for normal interaction.
Hook only the input needed for the explicit gesture; release resources on exit, failure, or permission denial.
Windows observes button edges from Explorer and scopes key observation to the held gesture.
A provisional OLE target obtains real CF_HDROP before revealing the wheel. Its physical release and
OLE Drop must agree on a sector. Cancelled sessions reject late notifications. AppKit uses dragging
sequence numbers and native pasteboard file URLs. Both consume the versioned shared wheel contract.
Test screen bounds, negative coordinates, mixed DPI, monitors, interrupted drags, focus, and normal drag behavior.
macOS: request only permissions actually needed and explain their purpose; denial keeps basic in-app functionality usable.
Graceful fallback does not count as verification of an unavailable global wheel feature.

## Windows build and support policy

Inspect the actual Windows version, SDK, shell, Git state, and available interactive desktop before work.
Use .NET 10 LTS with an exact SDK pin and supported patch update policy; validate current OS/library requirements.
Do not claim all Windows 10 editions are vendor-supported merely because a build runs on one machine.
If Codex runs in WSL, invoke native Windows tools for WPF and record any inability to execute interactive tests.
Avoid trimming/AOT for WPF or native wrappers unless explicitly validated. Use a self-contained x64 release payload.
Keep the app functional without the developer SDK or ad hoc dependencies inherited from the developer's PATH.
The Windows installer is per-user with no routine elevation, supports upgrades/uninstall, and keeps data outside installation.
Test interrupted/failed upgrades and recovery; do not invent a separate custom update cryptosystem or advertise untested rollback.

## Update trust and release pipeline

Windows uses NetSparkle strict verification for feed and payload; macOS uses supported Sparkle feed/archive signature settings.
Windows bounds transport and parses the signed appcast locally using NetSparkle's strict Ed25519 verifier.
Use platform-specific signing keys and a pinned public trust anchor. Production private keys never enter source, logs, or packages.
Development tests use ephemeral keys and isolated feeds. A test key must never become the production identity.
Without production credentials, show a clear update-configuration state and fail the production publication gate.
Do not disable verification or treat a SHA-256 checksum as publisher authenticity.
Use HTTPS, constrained download origins/redirects, bounded downloads, version/OS/architecture checks, and anti-downgrade rules.
Create separate feeds for Windows x64 and Mac architectures/channels, e.g. a GitHub Pages update directory.
Do not use the repository-wide releases/latest asset as the sole feed: a release for one OS must not break another OS.
Feed URLs remain unconfigured until hosting exists; do not present a missing feed as proof there are no updates.
Automatic checks need a visible preference/disclosure. Installation requires a safe idle point and clear user consent.
Update settings/history survive replacement. Validate successful replacement, restart, refusal, interruption, and failure recovery.

PRs and pushes to main run Windows build/test and the manual-build C# CodeQL analysis.
Main can also build and self-check an unsigned payload in the runner workspace.
Future release automation requires a reviewed commit, validated packages, trusted signing and explicit promotion.
Keep the signing script inactive in CI until the release workflow is approved.
Use minimal job permissions, verified full-SHA action pins and no PR-accessible signing secrets.
Never run untrusted contributor code in a privileged pull_request_target/signing job. Do not attach secrets to ordinary CI artifacts.
Dependabot checks the root solution and central NuGet versions, plus workflows.
Review qpdf, NSIS, SDK/runtime and actionlint updates separately using their
pinned origins, digests and license terms in the dependency inventory.

## Apple and publisher signing

No paid Apple membership initially. Build/sign ad hoc as required without representing the app as notarized or Developer ID-signed.
Use Sparkle's documented setup for the selected signing/build configuration, including framework loading and helper requirements.
Do not weaken OS security globally or remove quarantine programmatically as an installation strategy.
Document truthful first-launch limitations after testing on a Mac; managed devices may not permit an exception.
Apple/Windows publisher signing and cryptographic updater authenticity are separate concerns.
No guarantee of frictionless ad-hoc-signed updates before a real old-to-new installed Mac test.

## Windows invocation and jobs

The command coordinator snapshots semantic ID, input paths and clipboard origin,
invocation origin, options, destination policy, and cancellation on dispatch.
Home, the keyboard wheel, and Explorer use the same coordinator. Only explicit
choices, editor interactions, and OS clipboard work use the WPF dispatcher.
The quick operation implementations and image processing run on background tasks.

Compact nonactivating feedback exposes cancellation and completion. A bounded Jobs window
opens on request. History persists published paths only. An explicit Move uses native file operations.
Input editors own cancellable, debounced result previews. A newer request
cancels older work, and Save and Copy consume current validated output.

Separate exports default to their source directories. Combined output across
different source folders and first-use clipboard exports require an explicit
destination. A configured destination is checked before the job writes.
Destination checks do not replace validation or atomic publication in processing.

## Page assembly and Office layout

Page assembly holds read leases and caches qpdf inspection per source for grouped Split.
qpdf copies page content and annotations to a new document. A second structural pass maps local
destinations onto retained page objects, disables removed targets, and rebuilds flat page bookmarks.
The page editor discloses omitted document-level structures. Rewrites refuse signed and protected input.
Forms, active actions, embedded files and unsupported layers have separate page-assembly guards.

Windows renders static DOCX/PPTX through a local LibreOffice profile with disabled macros and
active content. The bounded worker rejects linked external resources, validates qpdf output and
opens it with Windows.Data.Pdf. The optional MSI administrative image includes upstream notices.
Editable PDF-to-DOCX/PPTX needs a reconstruction engine and layout acceptance.

## Validation and operations

Local scripts cover environment checks, builds, tests, packaging, documentation
budgets, shared manifests and workflow validation. Real failures return nonzero.
Keep unit/integration, adversarial, native-UI, and installed-update evidence distinguishable.
Use synthetic fixtures and independent inspectors. Track unavailable tests as skipped/blocked, never passed.
CI should compile/test Windows. Add Mac compilation/tests only when actual Mac targets exist; never use echo-success placeholders.
Do not let green shared-contract checks imply that unimplemented Mac features work.
Build artifacts, screenshots, and logs are ignored locally or bounded CI artifacts; STATUS.md links the current evidence.
Report the actual launch/package paths, last passing commands, remaining blockers, and exact next run.

## Primary implementation references

- Codex repository guidance: https://developers.openai.com/codex/guides/agents-md
- Windows/.NET: https://learn.microsoft.com/en-us/dotnet/core/install/windows
- Archive safety: https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices
- NetSparkle: https://github.com/NetSparkleUpdater/NetSparkle
- Sparkle: https://sparkle-project.org/documentation/
- qpdf: https://qpdf.readthedocs.io/en/stable/cli.html
- GitHub workflow security: https://docs.github.com/en/actions/reference/security/secure-use
- Apple first launch: https://support.apple.com/en-us/102445

These references are implementation guidance, not evidence that Tabrilo has been tested or audited.
