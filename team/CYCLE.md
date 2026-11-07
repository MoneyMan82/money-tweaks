# The autonomous development cycle

One cycle = one complete unit of valuable, verified work, left ready for the owner's approval. It is run by the Chief
Coordinator (the main session acting as coordinator via `/cycle`, a scheduled task, or `claude --agent chief-coordinator`).
Agents are listed in `team/AGENTS.md`. Hard limits are in `team/RULES.md` and enforced by `.claude/hooks/agent_guard.py`.

## Step 0 - Start
1. Write the current time into `.claude/autonomous-run` (this turns on the guard for this session; it expires after
   4 hours). Read `team/STATE.md` and increase the cycle number.
2. Game check (RULES.md). If a game is running: this is a **quiet cycle** - do only read-only work (Step 1 part a,
   discovery and scoring in Step 4, reviews), no builds or tests, and say so in the log.
3. Read INBOX, TASKS, IDEAS (scores), FINDINGS, the last 3 LOG entries.

## Step 1 - Health check (always, even if CI is red; collect every failure, don't stop at the first)
a. `ci-investigator`: CI on the latest main commits, failing annotations and causes, latest release assets.
b. `tester` (skip in a quiet cycle): full build + all test projects on current main in its worktree.
Every failure that is real (not a game-running refusal, not a runner outage) becomes a task in TASKS.md with type
`bug`, severity `critical` if main is red. Record results in STATE.md ("main health").

## Step 2 - Ask: "what is the next most valuable thing?"
Answer these in order and take the FIRST that applies. Write the question, the answer and why into the LOG entry.
1. Is main broken (CI or local tests red)? -> fix it (Step 5 with the bug task).
2. Did the owner leave feedback in INBOX.md ("Owner notes") or reject something? -> act on it.
3. Is an INBOX branch stale (main moved and it no longer merges cleanly, or its tests fail now)? -> developer merges
   main into that branch and fixes it; re-run Step 6 on it.
4. Are there 3 or more branches waiting in INBOX? -> don't add more unapproved work. Do review/quality work instead:
   code-reviewer on the least-reviewed subsystem, windows-tweak-specialist catalog audit, ux-reviewer pass, or
   license-auditor audit; turn confirmed findings into tasks. Then go to Step 7.
5. Is there a confirmed bug task (TASKS.md, type `bug`, status `queued`)? -> fix the highest severity one.
6. Are there fewer than 8 scored candidate ideas in IDEAS.md? -> run discovery (Step 3) first.
7. Otherwise -> build the highest-scoring idea with verdict BUILD that is not blocked (Step 4 then 5).
If nothing safe can be done (environment broken, everything blocked), log why and stop - an honest empty cycle is fine.

## Step 3 - Discovery (when the backlog is short, or every 4th cycle anyway)
Run in parallel: `innovation-scout` (inside-out), `competitor-analyst` (outside-in, rotate the area each time: tweaks,
gaming/FPS, network, hardware/tuning, UX/onboarding, recovery/safety, laptops), and one domain specialist matching that
area (`windows-tweak-specialist`, `gaming-performance-analyst` or `hardware-specialist`). Merge their ideas, drop
duplicates (against IDEAS.md and the app), then send every new idea to `idea-critic` for a score (SCORING.md).
Add all of them to IDEAS.md with score, verdict and the critic's reason - rejected ones too, so they aren't proposed again.

## Step 4 - Verify and design the chosen item
1. Facts: the domain specialist for the item (tweak -> windows-tweak-specialist, game/FPS -> gaming-performance-analyst,
   hardware -> hardware-specialist, technical/library -> researcher). Missing primary sources = no build; mark the idea
   `needs-evidence`.
2. `architect`: design with the smallest scope that delivers the value; must name UI placement (sidebar + Ctrl+K),
   reused systems, undo/recovery, tests, docs.
3. `idea-critic` reviews the design (not just the idea). REWORK -> back to architect once; REJECT -> record and pick the
   next item (back to Step 2, at most once per cycle).
4. Create the task in TASKS.md (T-id, acceptance criteria from the design, status `in progress`).

## Step 5 - Build
- `developer` (C#), `ui-designer` (mainly visual), or `web-developer` (worker/ or website) implements on its own branch
  `agent/<T-id>-<short-name>` in a worktree, with tests. For bugs: first a failing test that reproduces it, then the fix.
- `docs-writer` adds the CHANGELOG "Unreleased" entry, README/website text and counts, PROGRESS notes on the same branch.

## Step 6 - Verify (all must pass; run the independent ones in parallel)
- `tester`: full build, all test projects, the diff reviewed for regressions; screenshots of changed pages.
- `code-reviewer`: the diff and the code paths it touches.
- `security-reviewer`: verdict APPROVE / APPROVE WITH NOTES / BLOCK.
- `license-auditor` if dependencies, assets or outside sources changed; `ux-reviewer` if UI changed.
Any failure, BLOCK or confirmed finding -> back to the builder with the exact findings; then re-verify only what is
affected plus the full test run. At most 3 rounds; after that, park the task as `needs-owner` with the evidence.

## Step 7 - Record and hand over
1. INBOX.md: for each ready branch - what it adds for users and where to find it, tests (exact counts), reviewer
   verdicts, what is NOT verified, risks, and the line `/merge <branch>`.
2. Update TASKS.md, IDEAS.md (status), FINDINGS.md (durable findings), `team/knowledge/` (KNOWLEDGE UPDATE lines),
   ROADMAP.md if a milestone moved, STATE.md (cycle number, time, main health, next intended step).
3. Every 3rd cycle: `process-improver`; apply its `team/` proposals; put "OWNER APPROVAL" proposals into INBOX.
4. Append the LOG entry: cycle number, the "next thing" answer, agents used, results, branch/commits, verification,
   open issues. Never edit earlier entries.
5. Delete `.claude/autonomous-run`. Finish with a short summary for the owner (what is ready, what changed, what's next).
