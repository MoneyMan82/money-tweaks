# Implementation log

Append-only. Newest at the bottom. Each entry: date, agent(s), what was done, branch/commit, verification, open issues.

## 2026-11-06 – Team set up (owner's main session)
- Created the agent team in `.claude/agents/` (chief-coordinator, innovation-scout, researcher, architect, developer,
  ui-designer, tester, security-reviewer), shared rules `team/RULES.md`, guard hook `.claude/hooks/agent_guard.py`,
  and the team files (ROADMAP, IDEAS, TASKS, LOG).
- State at setup: version 1.79.0 released, 401 tweaks, main at the commit that added the team.
- Seeded IDEAS.md from PROGRESS.md "Next ideas" and the 1.79 report; seeded TASKS.md with a first scouting run and
  one research task. No agent work has been run yet.

## 2026-11-06 – Autonomous cycle scheduled (owner's main session)
- Cloud routine "Money Tweaks autonomous dev cycle" (trig_011EDjzhjQx5ruWi2X7kMUs9), Sonnet 5.5, cron `56 */6 * * *` UTC
  (02:56, 08:56, 14:56, 20:56 Helsinki). Each run: scout -> research -> architect -> developer -> tester + security,
  then pushes one `claude/cycle-<timestamp>` branch and opens a draft PR. Never merges, tags, releases or deploys.
- Each run carries the backlog forward from the newest unmerged `claude/cycle-*` branch's team/ files.
- Known limits: the cloud is Linux, so the WPF build and main tests run only in GitHub Actions on the draft PR; every
  pushed branch gets a Cloudflare preview build (production untouched).

## 2026-11-07 – First real agent runs (owner's main session)
- Added `code-reviewer` (read-only) to `.claude/agents/`.
- innovation-scout ran on the real repo (read-only, no files changed): 5 ideas, added as I-006..I-010. Registry keys and
  event IDs it cited are unverified (no web search in that run).
- Added owner commands in `.claude/skills/`: /team, /ideas, /check, /build, /merge.
- Code review of Game Autopilot / Gaming Profiles restore (code-reviewer instructions, run as a stand-in because the agent
  wasn't registered yet): 11 findings. Owner's session confirmed in the code: End doesn't wait for an in-flight Start
  (AutopilotService.EndAsync has no _busy check), failed rollback drops the record (ProfileService.ActivateAsync),
  ProfileStore.Save not atomic, Recover trusts the PID alone. Queued as T-003..T-005. No application files changed.
- code-reviewer (real agent, read-only) reviewed the tweak apply/undo engine: 10 findings. Owner's session confirmed in the
  code: undo matches backups by position (TweakEngine.cs:191); op-count change replaces the original backup and a failed
  re-apply deletes it (:113, :146); the failing step itself is never rolled back (:131-147); RegStringFlagOp.Undo
  restores the whole string (RegStringFlagOp.cs:68); BatchAsync has no try/finally (TweakItem.cs:399-408).
  Queued as T-006..T-008. No application files changed.

## 2026-11-07 – Cycle 1 (run with /cycle in the owner's session; agent team 2.0)
- **Start:** marker written 13:51; no game running; guard confirmed active (a dry-run push was blocked).
- **Health:** ci-investigator BLOCKED - the readonly guard refused its GitHub API curl; it stopped as instructed (CI not
  checked; guard fix -> INBOX owner approval). tester on main 6af8301: build 0 errors / 14 warnings (12 ImageSharp
  NU1902/NU1903 advisories, CA2014, CS0649), tests 543/543, 27/27, 21/21. New tasks T-009 (ImageSharp advisories), T-010
  (code warnings).
- **"What is the next most valuable thing?"** Main green locally; no owner notes; no stale or waiting branches; fewer than
  3 in INBOX; confirmed bugs exist -> highest severity: T-006 and T-007 (tweak engine Undo/rollback; every tweak depends
  on it). Combined into one design because they share Apply/rollback.
- **Design:** architect -> team/designs/T-006-007-backup-identity.md (op identity, matcher, merge rule, pre-click
  snapshot rollback, safe DynamicRegOp undo; refuses to write file-supplied registry locations). idea-critic: BUILD 38/50
  with 6 required fixes (incl. Fortnite value names containing backslashes, Capture swallowing discovery failures, Appx
  NotApplicable counted as inactive) and 2 scope cuts; split into branch A (this cycle) and branch B (T-007b, next).
- **Build:** developer on `agent/T-006-backup-identity` (tests first: 12 failed on main). docs-writer wrote the CHANGELOG
  on a copy branch cut before the fixes (`agent/T-006-backup-identity-docs`, superseded); the developer applied its text
  to the real branch (a247fd1).
- **Verify round 1:** tester PASS (577/27/21, no flakes); code-reviewer no blocking defects (3 low); security-reviewer
  BLOCK - ArchiveOrphans built the archive path from the backup file's TweakId (path traversal / elevated write anywhere),
  plus junction and malformed-file findings. Coordinator confirmed the BLOCK in the code (TweakEngine.cs:67).
- **Fix round 1:** developer fixed all (catalog-id names + IsSafeId + containment check, reparse-point refusal, damaged
  shapes, dynamic-step rollback only for recorded targets, added-steps note); 10 new tests failed before the fixes.
- **Verify round 2:** tester PASS (587/27/21, warnings identical to main: 14); security-reviewer APPROVE WITH NOTES.
  Follow-ups T-011 (app-data folder junction hardening, pre-existing) and T-012 (IsSafeId \z, rescue defaults path).
- **Result:** INBOX -> `/merge agent/T-006-backup-identity` (6 commits on main 6af8301, 20 files, +1298/-76).
- **Agents used:** ci-investigator, tester (x3), architect, idea-critic, developer (3 rounds), code-reviewer,
  security-reviewer (x2), docs-writer. All ran as their real agent types.
- **Process issues (for process-improver, cycle 3):** guard false positives on heredoc text and on read-only
  `git merge-tree`/`git merge-base`; PowerShell game check blocked in worktrees; ci-investigator can't run; the
  docs-writer can't add to a branch checked out in another worktree (agents can't merge branches) - have the developer
  include the docs-writer's text, or run the docs-writer after the last fix round.

## 2026-11-08 – Cycle 2 (started from the Agent Command Center)
- **Start:** marker written 14:16; no game running. Owner note in INBOX (14.01): run a brainstorming competition.
- **Health:** ci-investigator (via WebFetch; curl still blocked) - CI green on the last 8 main commits incl. 696c2f8
  (build, linux, worker, Cloudflare); release v1.80.0 has all 6 assets (hashes not checked). tester on main 696c2f8:
  build 0 errors / 15 warnings (new: xUnit1031 AgentCenter PipelineTests.cs:186 -> added to T-010); tests 556/556,
  27/27, 21/21, AgentCenter 32/32. Main healthy.
- **"What is the next most valuable thing?"** 1) main broken? No. 2) owner feedback? Yes - the brainstorm request ->
  do it. Also 3) applied in parallel (independent, cheap): the INBOX branch was 5 main commits behind and both sides had
  edited CHANGELOG.md -> refresh it. The read-only brainstorm was started alongside the health check because it doesn't
  touch builds or tests.
- **Branch refresh:** developer created `agent/T-006-backup-identity-r2` from the INBOX branch and merged main 696c2f8
  into it (7703d9e; only conflict CHANGELOG.md - entry kept under Unreleased above 1.80). Tests 600/27/21/32, 15
  warnings (same as main). Coordinator check: the merge only touched files main changed, and the src/tests diff vs main
  is byte-identical to the reviewed branch, so cycle 1's code-review and security verdicts still apply (not re-run).
  INBOX now says `/merge agent/T-006-backup-identity-r2`.
- **Brainstorm (discovery, area: tweaks):** innovation-scout 5 (Wi-Fi Doctor, Power Mode Guard, Spike Culprit, Session
  Recap, Known Issues Watch), competitor-analyst 5 (Leftover Policy Finder, Inherited Tweaks Review, Setup Restore,
  Telemetry Proof, Game Compatibility Components), windows-tweak-specialist 5 (edition gating, AI-tweak update, placebo
  pass, OneDrive KFM block, present-mode readout) + a catalog audit. idea-critic scored all 15 -> IDEAS.md I-011..I-025:
  BUILD I-011 42, I-012 40, I-013 36, I-014 35 (merged into I-011), I-015 35, I-016 34 (needs-evidence); 8 REWORK;
  REJECT I-025 Telemetry Proof 18 (fails Honest gate). Critic corrected proposers: 3 duplicate existing systems
  (net.adapter-power, TweakAudit notes, BottleneckEngine, Autopilot summary, Presets export).
- **New tasks:** T-013 (dbl.onedrive-presignin wrong key/undo), T-014 (verify 4 unconfirmed catalog doubts), T-015
  (edition gating, I-011+I-014), T-016 (placebo pass, I-012). Owner decision in INBOX: `adv.vbs-hvci` (already blocked
  from Apply; only wording/visibility). Knowledge: WINDOWS-TWEAKS.md (CSP facts), COMPETITORS.md (5 tools checked).
- **Not done this cycle:** no new feature built (the owner's request was the cycle's unit of work; the next build needs a
  Step 4 design). `.claude/autonomous-run` was left in place - this run's instructions forbid deleting files; it expires
  after 4 hours.
- **Agents used:** ci-investigator, tester, developer, innovation-scout, competitor-analyst, windows-tweak-specialist,
  idea-critic - all as their real agent types.
- **Process issues (for process-improver, cycle 3):** read-only agents' progress notes are refused when chained with
  `&&` (two agents published nothing); `git merge-base` still blocked for the coordinator; ci-investigator brief used
  the wrong repo slug (it's `money-tweaks`); the PowerShell game check is still blocked in worktrees (`tasklist` used).
