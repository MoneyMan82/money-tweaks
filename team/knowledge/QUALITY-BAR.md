# Definition of done

A branch may go into `team/INBOX.md` only when every line below is true (or explicitly marked "not applicable" with a
reason). The tester and reviewers check against this list.

## Function
- Does what the task's acceptance criteria say, on this PC, verified by a test or an observed run - not assumed.
- Real data only: no mock data, placeholder controls, fake results or invented numbers in the UI.
- Reachable: sidebar or button entry and a Ctrl+K entry with useful keywords; the summary says where it is.
- Honest UI: says what changes, if it's reversible, admin/restart needs; shows "inconclusive"/"not available" plainly.

## Safety and reversibility
- Every system change has a backup taken first, read-back after, and an undo that restores the exact original
  (including "was absent"). Crash/partial-failure recovery considered. Irreversible parts stated in the text.
- No weakening of Windows security, no anti-cheat interaction, no downloaded scripts, nothing sent off the PC.
- security-reviewer verdict APPROVE or APPROVE WITH NOTES (notes listed in INBOX).

## Code
- Reuses existing systems; no duplicate feature. Core logic in MoneyTweaks.Core (no WPF), UI in MoneyTweaks.
- No blocking work on the UI thread; async code awaited; exceptions handled where users would otherwise get stuck.
- code-reviewer has no unresolved confirmed findings of medium severity or higher.

## Tests
- New behaviour and every fixed bug have xUnit tests (a bug fix starts with a failing test).
- Full local run: `dotnet build MoneyTweaks.slnx -c Release` without new warnings, and all of
  `tests/MoneyTweaks.Tests`, `tests/MoneyTweaks.Community.Tests`, `tests/MoneyTweaks.Linux.Tests` pass. Exact counts
  recorded. Known environment refusals (FortniteIni tests while Fortnite runs) rerun after the game closes.
- Changed pages rendered with `--screenshots` and looked at (no personal data in screenshots).

## Docs and licenses
- CHANGELOG "Unreleased" entry; README/website text and counts updated (PublishedCountsTests passes); PROGRESS notes.
- New dependencies/assets/outside ideas checked by license-auditor; Source + TWEAK-SOURCES row for outside tweak ideas.

## Hand-over
- Branch committed (not pushed), INBOX entry with `/merge <branch>`, task and idea statuses updated, LOG entry written.
