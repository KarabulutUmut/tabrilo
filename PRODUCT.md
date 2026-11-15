# Tabrilo product contract

Status: approved v1 target. Implementation evidence belongs in STATUS.md, not in this specification.
Identity: Tabrilo. Tagline: **Your files. Ready to share.**

## Product

An independent, local desktop toolkit for images, PDFs, and safe archive extraction.
Native Windows and macOS applications should have equivalent commands, outcomes, layouts, and configurable shortcuts.
Windows is the first full implementation. macOS requires a subsequent real Mac build and validation run.
All declared file operations must work offline after normal installation; no accounts or developer runtimes for end users.
Only opted-in update checks/downloads and explicitly confirmed external links may use the network.

## Exclusions

No audio/video tools, collages, RAW development, PSD editing, animated-image editing, arbitrary SVG execution, or Office conversion.
No web downloader, window tiling, floating file shelf, cloud storage/sync, analytics, advertising, subscriptions, or required AI.
No archive creation, password cracking, encrypted/multipart archive support, or PDF content redaction in v1.
No background clipboard surveillance, unsolicited startup persistence, or paid Apple membership requirement.
Do not add adjacent features merely because a dependency supports them.

## Images

| Command | Required behavior |
| --- | --- |
| Convert | Batch/single conversion with quality, transparency policy, explicit output format, and collision-safe naming |
| Compress | Lossless where supported; labeled lossy/resize options; preview and actual size comparison |
| File information | Inspect readable metadata; distinguish personal tags from required display/color information |
| Remove personal metadata | Fresh sanitized copy with documented tag policy; never a blanket anonymity guarantee |
| Edit image | Resize, rotate, flip, brightness, contrast, saturation; undo/redo and original/result comparison |
| Annotate | Text, arrows, rectangles, circles, freehand; select/move/delete and undo/redo |
| Frame | Solid/simple-gradient background, padding, corner radius, and shadow; no template marketplace |
| Crop | Free/preset aspect ratios, numeric dimensions, movable/resizable selection, undo, zoom-safe coordinates |
| Images to PDF | Arrange images; choose page size, orientation, fit mode, and margins; export a new PDF |
| Redact | Irreversible replacement of selected pixels in the exported copy; see privacy contract below |
| Read QR | Multiple codes from a file or explicitly pasted image; use the common QR workflow |
| Share copy | Combine chosen edits/redactions, personal-metadata removal, optional resize/compression, then save/copy |

Required import targets: JPEG, PNG, WebP, BMP, TIFF, HEIC/HEIF. Required export targets: JPEG, PNG, WebP.
HEIC/HEIF import needs a packaged/declared, tested implementation on each platform, not an assumed optional Windows codec.
For multipage TIFF or multi-image HEIF, require explicit image/page selection or clearly reject unsupported structures.
Never silently discard additional pages/frames. Animated inputs are outside the initial support contract.
Normalize orientation and deliberately manage color profiles. Explain alpha flattening when exporting JPEG.
Compression must report when no smaller acceptable output is achievable; do not pretend every file shrinks.

## Image redaction contract

1. Decode into a controlled pixel buffer; map orientation, cropping, scaling, and selection coordinates consistently.
2. Replace the full selected area with an opaque solid color. Do not advertise blur, pixelation, or overlays as redaction.
3. Encode a fresh single-image PNG/JPEG; never pass through original payloads, thumbnails, previews, layers, or edit history.
4. Emit only explicitly allowlisted non-personal format/color data; normalize color as needed.
5. Reopen the result and validate it; test metadata and embedded-data removal with an independent inspector.
6. Save and Copy Result must use the processed output, including when pasting into an external application.
7. Preserve the original; explain that originals, backups, surrounding context, and previously shared copies are unaffected.
8. Do not persist unredacted session thumbnails, OCR text, source pixels, or recovery documents by default.
9. Bound temporary-data lifetime and clean up after cancellation/crashes; do not claim forensic secure erasure.

For lossy JPEG, account for block-boundary effects; assert no source detail remains in the protected area.
Keep secure image redaction visibly separate from annotation. Default its output to PNG.
The initial Windows redaction implementation offers PNG while JPEG boundary validation is pending.
Provide manual region selection; automated face/OCR detection is not required and must not be implied.

## PDFs

| Command | Required behavior |
| --- | --- |
| Compress | Conservative optimization plus explicitly lossy image-quality mode; actual before/after sizes |
| File information | Inspect supported document properties and identify protection/signature/structure caveats |
| Metadata editing/removal | Edit/remove supported properties through a full rewritten document, not incremental masking |
| Split | Extract selected pages/ranges, one file per page, or groups; validate ranges and output page counts |
| Organize | Thumbnail grid; reorder, rotate, duplicate, remove, and insert pages; undo/redo |
| Merge | Arrange documents/pages, preview, and create a new output |
| Export pages as images | Current/selected/all pages to PNG/JPEG with resolution controls and bounded processing |
| Read QR | Current/selected/all pages; return payloads, page numbers, and source positions |

Default compression must not rasterize complete pages or remove searchable text/vector content.
Do not claim a guaranteed size reduction or silently replace a smaller original with a larger result.
Preserve/test page geometry, text, rotations, links, annotations, and form appearances where supported.
Detect structures an operation cannot preserve; block with a reason or obtain explicit, specific consent for a safe transformation.
Warn before modifying digitally signed PDFs; a preserved signature drawing is not a valid retained signature.
Prompt for a user-provided password only where decryption is supported. Never log/store passwords or claim password recovery.
Metadata removal must remove targeted Info/XMP properties and old incremental revisions in the new output.
Do not use reversible metadata-only edits as secure removal. Independent inspection must check the rewritten file.
Removing document properties is not sanitizing attachments, comments, hidden layers, or page content.
Do not implement PDF redaction as rectangles or relabel annotation as redaction.
The app must not execute PDF scripts, launch actions, or automatically load external resources.

## Archives

Extract ZIP, TAR, GZIP, TAR.GZ/TGZ, and single-volume unencrypted RAR. Do not imply encrypted or multipart support.
Standalone GZIP produces a decompressed file; TAR.GZ/TGZ is decompressed and extracted as an archive.
Each operation produces a newly named output folder and never overwrites an existing user's folder.
Use fresh app-owned staging under a validated destination; publish the final folder only after complete success.
Reject traversal, absolute/drive/UNC paths, alternate streams, reserved names, and normalized/case-colliding entries.
Reject symbolic/hard links, devices, FIFOs, and other special entries. Detect reparse points/junctions and link redirection.
Validate actual filesystem containment during writes, including destination ancestry; string-prefix checks are insufficient.
Enforce actual streamed byte, per-file, count, time/resource, and disk-availability limits; never trust headers alone.
Set finite documented defaults. Exceeding a limit must fail safely, not silently remove the protection.
Do not execute entries, preserve dangerous privilege metadata, or recursively expand nested archives automatically.
Cancellation/failure removes only app-owned staging. Corrupt/unsupported entries must not become a false successful extraction.
Test traversal, links, collisions, bombs, malformed entries, Unicode, cancellation, locked outputs, and low disk space.

## QR workflow

One semantic command supports images, explicitly pasted screenshots, and PDFs.
Return exact decoded content, recognized type, source page/file, and position; preserve all locations when grouping duplicates.
Show the real destination. Copy is always explicit; Open requires confirmation and an allowed HTTP(S) URL.
Do not auto-open links, execute custom schemes, fetch previews, configure Wi-Fi, or initiate payments.
Use bounded background rendering/scanning and cancellation. Never upload files to a decoding service.

## Desktop and interaction

Home, Recent results, Settings, and focused contextual editors; no dashboard clutter.
Accept file picker, normal drag/drop, and explicit clipboard file/image paste. Preserve ordinary text-field paste.
A native global drag gesture opens a contextual wheel near the pointer; keyboard invocation works for selected app files too.
Provide 6-8 visible actions, stable user-pinned positions, a deliberate More path, explicit selection, and Escape cancellation.
Quick wheel actions should execute with sensible defaults and report results in a compact surface.
Convert offers a compact format choice. Image compression offers format-aware presets and advanced options.
Archive extraction, QR reading, conservative PDF compression and image metadata removal use direct execution.
Open a focused editor when an operation needs selection, configuration, or preview.
Ordinary Explorer/Finder dragging must remain unaffected when the gesture is not activated.
Mixed selections must show valid common operations or clear unsupported-selection explanations.
Use configurable semantic shortcuts with Ctrl/Command conventions, conflict detection, and normal editing preservation.
Include light/dark/system themes, reduced motion, keyboard focus, screen-reader labels, and usable text scaling.
Tray/menu-bar mode and launch-at-login are explicit preferences; default startup persistence off.
Recent results keep at most 50 local result references, not copies of all inputs; users can disable/clear them.
No persistent unredacted thumbnails. Missing files are shown without deleting originals or fabricating results.

## Output and job behavior

Originals remain unchanged by default. Write, validate, then publish new outputs with collision-safe names.
Default separate exports to each source directory. Let a configured export directory override this.
Clipboard exports require an explicit export folder. Combined exports from different folders require one chosen destination.
Offer an intentional destination override and ask for a fallback when the original destination cannot be used.
Use cancellable bounded jobs, real progress where measurable, and indeterminate progress otherwise.
Show failures and partial batch outcomes accurately; never claim batch-wide success when items failed.
Let users preview, save, copy processed images, reveal outputs, and retry appropriate failures.
Updates must wait until active work completes or the user explicitly cancels it.
No operation needs an account, cloud API, separately installed SDK, or paid feature unlock.

## Release acceptance

All advertised operations must produce real independently readable files; no placeholder success states.
Input hashes, export validity, redaction properties, archive containment, QR behavior, and PDF preservation require evidence.
The wheel must work with real Explorer/Finder drags on the claimed platform, not solely inside the app window.
Installer/uninstaller behavior, offline processing, missing-dependency handling, and signed updates require end-to-end tests.
An update must preserve settings/history preferences, reject tampering, and handle interruption safely.
Publish only the OS/architecture/format combinations actually validated. Mac source or CI compilation alone is not Mac desktop validation.
Do not reduce scope silently. Any incomplete requirement remains an explicit blocker in STATUS.md and the final report.
