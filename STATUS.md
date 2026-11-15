# Tabrilo current status

Updated 2026-11-15. Windows utility repair is in development. Owner acceptance is open.

| Area | Status | Evidence |
| --- | --- | --- |
| Baseline on main | Verified with evidence | Native Release suite, 11/11 groups |
| Routing, destinations, wheel sectors and image coordinates | Verified with evidence | Native Release suite, 13/13 groups |
| Quick operations and focused editors | Implemented/unverified | Isolated WPF payload opened. Initial UI Automation observed a selected synthetic image and the keyboard wheel |
| Explorer drag, visual and external clipboard | Blocked | Windows Graphics Capture timed out and coordinate input geometry was unavailable |
| Installed update | Verified with evidence on baseline | Isolated 0.1.0 → 0.2.0 regression. The utility repair reruns signed feed and payload trust tests |
| macOS | Implemented/unverified | AppKit shell and shared catalog. Mac compilation and desktop validation pending |
| Production publication | Blocked | Acceptance, redistribution, publisher and update signing, approved feed |

## Local evidence

Native PowerShell 7 and repository-local .NET SDK 10.0.404 on Windows 10 x64,
build 19045. Clean Windows 11 validation is pending.

- `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release` exited 0.
  See `artifacts/verification/utility-test.log` and the synthetic evidence
  directory it prints. The existing archive guards cover streamed bytes, free
  space, collisions, corrupt GZIP, pre-cancellation and staging cleanup.
- `pwsh -NoProfile -File scripts/package.ps1 -Configuration Release -PayloadOnly -IsolatedTest -Version 0.1.1`
  builds the isolated development payload. See
  `artifacts/verification/utility-payload-final.log` for the current executable.
  Its development title shows the version and checked-out commit.
- Startup and UI Automation notes are under `artifacts/verification/utility-desktop`.
  Windows input-idle timing is a startup observation. Explorer gesture timing,
  capture, recording, mixed DPI, canvas dragging and clipboard interoperability
  need separate evidence.

## Archive alert and dependency maintenance

[CodeQL alert #1](https://github.com/KarabulutUmut/tabrilo/security/code-scanning/1)
is open. Authenticated metadata reports `cs/zipslip` at main revision
`48df81ebf0c03e817f05c20b1877cedf9c043399`. The original analysis traces ZIP
entry names through `Writer.Resolve` to the file write in `Writer.FileAsync`.
`OutputFiles.ValidateLeaf` rejects unsafe components.
`DirectoryPin` checks ancestor identities and attributes during writing.
Staging publication and cleanup need their own ownership evaluation. The
location trace is in `artifacts/verification/utility-codeql-trace.txt`.

[Maintenance PR #7](https://github.com/KarabulutUmut/tabrilo/pull/7) adds an
explicit solution-wide lockfile refresh command. The UX branch starts from main
independently. In a detached PR #3 worktree, base lockfiles with updated central
versions reproduced NU1004. Refresh restored the PR's committed package
versions and hashes. Forced locked restore exited 0. See
`artifacts/verification/utility-dependency-pr3-*.log`.
[PR #3's hosted failure](https://github.com/KarabulutUmut/tabrilo/actions/runs/43760287197/job/132953339492)
occurred in PDF and QR processing tests after restore. Dependency PRs #1–#5
remain open and unmerged.

## Next actions

1. Review archive containment and staging ownership, then verify the outcome with CodeQL and native filesystem evidence.
2. Exercise Explorer-to-wheel, tray lifetime, light/dark, mixed DPI, editor gestures, copy/save and cancellation on a capturable Windows desktop.
3. Review the PDF/QR failures in Dependabot PR #3 and verify dependency updates separately.
4. Validate clean Windows 11 packaging, installer interruption and redistribution before publisher signing or hosting.
5. Build the AppKit counterpart on a Mac and validate Finder integration, installation and signed updates.
