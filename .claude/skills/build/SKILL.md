---
name: build
description: Have the Money Tweaks agent team build one specific idea or task now, end to end on an isolated branch (verify, design, critic, code, docs, tests, code/security/license/UX review), and leave it in team/INBOX.md for approval. Use when the owner types /build <idea or task id, or a description>.
argument-hint: "<I-006, T-006, or a short description>"
disable-model-invocation: true
---
Build this with the agent team: $ARGUMENTS
(An I- id is looked up in `team/IDEAS.md`, a T- id in `team/TASKS.md`. If empty, show the top 3 scored ideas and the
top queued bug task and ask which one.)

Act as the Chief Coordinator (`.claude/agents/chief-coordinator.md`). Write the current time into
`.claude/autonomous-run` first, then follow `team/CYCLE.md` Steps 4 to 7 for this one item (skip Steps 1-3; for an idea
the owner named directly, the critic still scores it, but the owner's choice wins unless a safety gate fails - then
stop and explain). Delegate each step to the agent named in `team/AGENTS.md`.

Never push, merge, tag, release or deploy. Finish with: what was built and where it is in the app, branch and commits,
exact test counts, reviewer verdicts, what is not verified, and the `/merge <branch>` line.
