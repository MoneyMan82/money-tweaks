---
name: build
description: Have the Money Tweaks agent team build one idea from the backlog end to end on an isolated branch (research, design, code, tests, security review) and stop for the owner's approval. Use when the owner types /build <idea id or description>.
argument-hint: "<idea id like I-006, or a short description>"
disable-model-invocation: true
---
Build this with the agent team: $ARGUMENTS
(If it is an id, look it up in `team/IDEAS.md`; if empty, ask which idea, showing the top 3 "new" ideas.)

Run the flow, delegating each step to the named agent and recording progress in `team/TASKS.md` (new T- id) and
`team/LOG.md`:
1. `researcher` - verify the facts (settings, Windows builds, licenses) with sources.
2. `architect` - design using existing systems; include sidebar + Ctrl+K entry, tests, docs. If the design says it
   can't be done safely, stop and report why.
3. `developer` (or `ui-designer` for mainly visual work) - implement in its own worktree on its own branch, with tests.
4. `tester` and `security-reviewer` in parallel on that branch. If tests fail or security blocks, send the findings
   back to the developer and repeat (at most 3 rounds), then report.

Never push, merge, tag, release or deploy. Finish with a short report for the owner:
- what was built and where to find it in the app
- branch name and commits
- test results (exact counts) and the security verdict
- anything not verified
- "Say `/merge <branch>` to merge and release it, or tell me what to change."
