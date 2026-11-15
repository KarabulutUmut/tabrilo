# Tabrilo UI system

Status: approved design with Windows assets and screens in development. See STATUS.md for native evidence.
Product name: **Tabrilo**. Tagline: **Your files. Ready to share.**

## Visual identity

Calm, compact, capable desktop utility. Avoid an enterprise dashboard, decorative clutter, and reference-app imitation.
Original logo direction: a folded paper ribbon forming a subtle T, legible inside a rounded-square application badge.
Create original editable vector artwork and generated Windows icon/Mac icon assets with a simple reproducible export path.
Create a simplified monochrome tray/menu-bar mark. Do not reuse third-party app artwork or bundle proprietary font files.
Name/repository selection is not a trademark-clearance claim.

## Color tokens

| Token | Light | Dark |
| --- | --- | --- |
| Background | #F7F8FA | #111820 |
| Surface | #FFFFFF | #1A2430 |
| Primary text | #18212F | #F2F6FA |
| Accent | #0F766E | #5EEAD4 |
| Text on accent | #FFFFFF | #111820 |
| Secondary text | #566170 | #B4C0CE |
| Error/destructive | #B42318 | #FDA29B |

Define borders, hover, pressed, disabled, selection, and keyboard focus as derived/explicit tokens.
Validate actual text/control contrast in both themes; these tokens are not an accessibility certification.
Use color with text/icons, never color alone for success, warning, destructive actions, or selection.
A dark neutral radial surface with teal/mint selection is the default in both appearances, with its own tested text tokens.
System appearance is the default; users can select Light or Dark.

## Typography and geometry

Use native system fonts: Segoe UI on Windows and the system font on macOS; no external font installation required.
Keep comparable hierarchy and density rather than demanding identical glyph rendering across operating systems.
Initial scale: 13-14 logical-unit body, 12 secondary labels, 20-24 page headings, 16 section headings.
Use an 8-unit spacing rhythm with 4-unit fine spacing, 8-12-unit control corners, and consistent icon sizing.
Controls should have comfortable pointer targets; respect text scaling and platform accessibility preferences.
Use original coherent line icons with visible labels/tooltips; do not replace core actions with ambiguous icons alone.
Native title bars, minimize/maximize/close behavior, file pickers, and permission dialogs remain platform-native.

## Application structure

Home, Recent results, and Settings form the primary navigation.
Home has a drop/paste target, selected files with removal controls, contextual action groups, and restrained recent results.
Do not create empty analytics cards, marketing banners, dummy statistics, or unnecessary sidebars.
The selected file list supports multi-select, clear filenames/types, unsupported reasons, and removing items without deleting originals.
Recent results show outputs, operation labels, missing-file states, open/reveal actions, and clear/disable controls.
Do not store unredacted previews in history. Keep the history limit at 50 references.
Settings groups: Appearance, Shortcuts/Wheel, Files/Exports, Privacy/History, Startup/Tray, Updates/About.
Only offer controls that actually affect behavior; an unimplemented preference is not a functioning feature.

## Editors

Shared layout: contextual title/selected files, primary preview canvas, operation controls, bottom Cancel and Export actions.
Use a responsive layout and sensible minimum sizes; controls must remain reachable without fixed-position clipping.
Provide zoom-to-fit/actual-size where useful, keyboard navigation, and clear original/result comparison.
Image edit/crop/annotation/frame/redaction share a consistent canvas model and undo/redo patterns.
Draw, move and resize crop and mask regions on the canvas. Numeric fields provide precision.
Draw actual annotations in the editing view and use handles and outlines for selection.
Keep the result view separate from the editing canvas and process current edits for Copy and Save.
PDF organization uses a page-thumbnail grid with multi-select, order, rotation, duplication, removal, and insertion.
Long documents use virtualization/bounded thumbnails, not eager full-document rendering.
Show source/output formats, destination, naming, dimensions, quality, and estimated/actual size where meaningful.
Do not display fabricated compression percentages or progress. Recalculate results after option changes where feasible.

## Radial action wheel

Gesture: drag real files -> hold the configured activation modifier -> wheel appears near pointer -> deliberately select an action.
Default drag modifier: Shift; make it configurable and avoid interfering with ordinary dragging when inactive.
Use a bounded 6-8-slot arrangement, clear labels, and intentional secondary navigation for more actions.
Use the visible sectors as drop targets. Leave the center neutral and show the active sector immediately.
Default wheel diameter is approximately 320 logical units; keep it within the usable monitor area.
Users can pin/reorder favorites. Context filtering may disable irrelevant commands but must not unpredictably reshuffle favorites.
No command executes solely because of hover. Make drop/release/click semantics explicit and consistent, with Escape to cancel.
Provide an in-app keyboard invocation for selected files and discoverable equivalents outside the wheel.
For quick actions, execute from the wheel with defaults and show compact progress and result feedback.
Use a small format or preset chooser where a decision is required. Leave Home and the foreground application undisturbed.
Open the editor only when the action needs selection, configuration, or preview.
Wheel navigation supports arrows, Tab, Enter, and Escape. Label all actions for accessibility.
Test edge positioning, multi-monitor coordinates, mixed DPI, focus restoration, interrupted drags, and repeat invocations.
Native drag-and-drop payload acquisition is required; no clipboard polling, process injection, or fake inside-only global behavior.

## Shortcut model

Bindings refer to semantic command IDs with explicit app/editor/global scopes.
Use Ctrl on Windows and Command on macOS for equivalent primary commands; users may customize supported bindings.
Initial suggestions: Open Ctrl/Cmd+O; Paste Ctrl/Cmd+V; Undo Ctrl/Cmd+Z; Home Ctrl/Cmd+1; Recent Ctrl/Cmd+2.
Use a consistent app-scoped wheel command and display the actual configured binding; do not silently steal OS-reserved shortcuts.
Detect conflicts and explain unavailable global keys. Normal text editing takes precedence while editing a text field.
Publish platform-default bindings from shared command data rather than maintaining inconsistent manual lists.

## Privacy-first workflows

Annotate and Redact have different labels, controls, and explanatory text.
Redact uses opaque solid masks and defaults to fresh PNG export; no blur or pixelation modes under the Redact name.
Show a short factual note: the exported copy is processed, while the original and backups remain unchanged.
Copy Result copies only the processed image. Sharing does not require an intermediate cloud upload.
Share copy combines user-selected edits, metadata removal, and optional resize/compression with explicit results.
Metadata UI uses File information and Remove personal metadata; explain supported fields rather than promising anonymity.
PDF metadata UI explains that comments, attachments, hidden content, and page redaction are different operations.

## QR results

Show payload text, type if recognized, source/page, and a location/preview when feasible.
Make multiple results easy to select/copy. A URL's real destination must remain visible before Open.
No automatic navigation, link preview network request, executable scheme, or clipboard replacement.
No-code-found, unsupported input, and cancelled scan are distinct outcomes.

## Jobs, errors, and updates

Keep long operations off the UI thread. Allow cancellation and show which inputs succeeded or failed.
Use determinate progress only when measurable; otherwise a labeled busy state with cancellation.
Export succeeds only after the output exists and validates; show its real location with Open/Reveal actions.
Keep job progress and outcomes in one nonmodal Jobs window. Preserve errors and partial results until dismissal.
Provide concise actionable errors and optional sanitized technical detail, not raw stack traces in primary UI.
Updates show availability, version, and consent; do not restart mid-operation or hide a failed signature as a network success.
An unconfigured update channel is labeled as such. A missing feed is not equivalent to an up-to-date application.
Startup/tray behavior is opt-in and visible; Close and Quit must have clear, predictable meanings.

## Accessibility and visual verification

Support keyboard-only operation, screen-reader labels, visible focus, reduced motion, system scale, and high-contrast needs.
Do not rely on hover-only actions or color alone. Animate briefly and respect reduced-motion settings.
Check both light/dark themes, empty/loading/error states, long filenames, narrow windows, and 100/125/150/200% scale.
Review screenshots from the actual native Windows app; source layouts or mock images are not runtime evidence.
Later repeat the same scenarios on macOS. Shared design does not remove the need for platform-specific accessibility testing.
