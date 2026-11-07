---
name: team
description: Show the Money Tweaks agent team status - what is waiting for approval, main health, ideas, tasks, last cycle and what's next. Use when the owner types /team or asks "what is the team doing".
disable-model-invocation: true
---
Give the owner a short, plain-language status of the Money Tweaks agent team. Read only; change nothing.

1. Read `team/INBOX.md`, `team/STATE.md`, `team/TASKS.md`, `team/IDEAS.md` and the last 2 entries of `team/LOG.md`.
2. List agent branches: `git branch --list "agent/*" "claude/*" --sort=-committerdate --format="%(refname:short) | %(committerdate:relative) | %(subject)"`
   and `git worktree list`; mark which are already merged into main (`git branch --merged main`).
3. If the scheduled task tool is available, check the "money-tweaks-cycle" task's last and next run.

Reply in this shape (short, no file dumps):
- **Waiting for you:** each INBOX item in one line (what it adds, tests, verdicts) with its `/merge` line; or "nothing".
- **Main health:** from STATE.md (CI and local tests).
- **Last cycle:** number, when, and what it did, in one sentence. **Next:** the intended next step.
- **Top ideas:** the 5 highest-scored with verdicts. **Tasks:** in progress / blocked / needs-owner.
- **Commands:** `/merge <branch>`, `/cycle` (run one now), and anything in INBOX "Needs the owner".
