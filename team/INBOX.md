# Owner inbox

Everything here is finished, tested on this PC and reviewed, and waits only for the owner. Approve with the `/merge`
line. To give feedback instead, write under "Owner notes" (the next cycle reads it first).

## Ready for approval

### Undo works correctly after app updates (T-006 + T-007a) - cycle 1, 2026-11-07
**For users:** every tweak's Undo and Apply. If an app update changed a tweak's steps, Undo could put a saved value
on the wrong setting, and applying again could overwrite your true originals or delete the backup. A failed Apply also
left the failing step half-done. Now each setting gets its own original back, steps added by an update are left alone,
backups are never deleted on failure, a failed Apply puts everything back exactly as before the click, and old backups
that can't be matched are refused instead of guessed. No new page: it sits behind every Apply/Undo, Tweak X-Ray,
Verify and Emergency Rescue (in-app and the Rescue exe).
- **Branch (refreshed in cycle 2, 2026-11-08):** `agent/T-006-backup-identity-r2` = the original 6 commits + merge of
  main 696c2f8 (merge commit 7703d9e; only conflict CHANGELOG.md - the entry now sits under "Unreleased" above 1.80,
  main's 1.80 section unchanged). Its src/tests diff against main is byte-identical to the reviewed branch, so the
  review verdicts below still apply. 20 files, +1298/-76. Tests on the refreshed branch (this PC, cycle 2): build 0
  errors / 15 warnings (same as main), MoneyTweaks.Tests 600/600 (main 556 + 44), Community 27/27, Linux 21/21,
  AgentCenter 32/32. The original `agent/T-006-backup-identity` (cut from 6af8301) is superseded by -r2.
- **Tests (this PC):** build 0 errors, 14 warnings (identical to main); MoneyTweaks.Tests 587/587 (main 543 + 44 new),
  Community 27/27, Linux 21/21. New identity/matcher/catalog tests ran 3x with no flakes; 22 new tests failed on the
  old code before the fix (they reproduce the bugs).
- **Reviews:** code-reviewer: no blocking defects (3 low findings fixed). security-reviewer: BLOCK in round 1 (a tampered
  backup file could make the elevated app write a file anywhere) -> fixed -> APPROVE WITH NOTES. idea-critic: BUILD 38/50.
- **Not verified:** real elevated runs of admin tweaks, real backup files from released versions (simulated in tests),
  Windows 10, screenshots (no UI change).
- **Known limits:** old backups where two steps of the same kind swapped places still match by position; tweaks whose
  old backups can't be matched need the backup file deleted to start over (the message says how). Follow-ups queued:
  T-007b (dynamic network/GPU tweaks undo, branch B), T-011 (app-data folder junction hardening), T-012 (small polish).
- Docs: CHANGELOG "Unreleased" entry and PROGRESS notes are on the branch. (`agent/T-006-backup-identity-docs` is an
  earlier docs-only copy, superseded - not needed.)

`/merge agent/T-006-backup-identity-r2`

## Needs the owner (decisions, approvals for .claude/ changes, environment problems)
- One-time setup: the scheduled task "Money Tweaks cycle" (every 4 hours, prompt: run /cycle) is not created yet -
  creating it was blocked by the app's permission check. Approve it when asked, or create it in the app's
  scheduled tasks. Until then cycles run when you type `/cycle`.
- **OWNER APPROVAL - guard fixes** (`.claude/hooks/agent_guard.py`, agents may not change it): (1) the ci-investigator's
  read-only GitHub API `curl` was blocked, so CI could not be checked this cycle (likely quoted URLs not accepted);
  (2) false positives: command names inside heredoc text (e.g. "powercfg", "format") and read-only `git merge-tree` /
  `git merge-base` are blocked; (3) the RULES.md PowerShell game check is blocked inside worktrees (agents fall back to
  `tasklist`). Say "fix the guard" in a normal session.
- **Decision (cycle 2): `adv.vbs-hvci`** (TweakCatalog.NetAdv.cs:189-195, turns off Memory Integrity). The
  windows-tweak-specialist flagged it as conflicting with the "never weaken security" rule. The idea-critic checked:
  it is already blocked from Apply (`TweakAudit.Disabled`, TweakAudit.cs:29, enforced at TweakEngine.cs:91 and
  Compatibility.cs:129) and kept only so Undo can turn the protection back on. Nothing is unsafe today; the only
  question for you is whether to hide it from users who never applied it or reword its description. No action needed
  otherwise.
- Cloud routine "Money Tweaks autonomous dev cycle" can't work as set up (no GitHub App access, Microsoft docs blocked,
  no .NET SDK). The local scheduled cycle replaces it.

## Owner notes
- 2026-11-08 14.01 (from the Agent Command Center): run a brainstorming competition now - innovation-scout, competitor-analyst and one domain specialist each propose 5 ideas in parallel; idea-critic scores all of them with SCORING.md and ranks them; add every idea to IDEAS.md with its score.
  - **Done in cycle 2:** 15 ideas (innovation-scout, competitor-analyst, windows-tweak-specialist), all scored by the
    idea-critic -> IDEAS.md I-011..I-025. Top: I-011 edition gating 42 (BUILD, a real bug: `dbl.spotlight` and
    `dbl.settings-365-ads` show "Applied" on Home/Pro while Windows ignores them), I-012 placebo pass 40 (BUILD),
    I-013 Leftover Policy Finder 36 (BUILD), I-014 AI-tweak fixes 35 (merged into I-011), I-015 Wi-Fi Doctor 35
    (BUILD, detection only), I-016 Power Mode Guard 34 (needs evidence). 8 REWORK, 1 REJECT (I-025 Telemetry Proof -
    can't be measured honestly). Queued as T-015 / T-016.
(write here)
