---
name: team
description: Show the Money Tweaks agent team status - ideas backlog, task queue, branches waiting for approval, and the scheduled cycle. Use when the owner types /team or asks "what is the team doing".
disable-model-invocation: true
---
Give the owner a short, plain-language status of the Money Tweaks agent team. Read only; change nothing.

1. Read `team/IDEAS.md`, `team/TASKS.md` and the last 3 entries of `team/LOG.md`.
2. Run `git fetch origin` and list remote branches matching `origin/claude/cycle-*` and local worktree branches
   (`git worktree list`), newest first, with their last commit date and subject. For each, say whether it is already
   merged into main (`git branch -r --merged origin/main`).
3. Also check the newest `claude/cycle-*` branch's `team/IDEAS.md` and `team/TASKS.md` (`git show`), since cloud runs
   record their backlog there until merged.

Reply in this shape (keep it short, no file dumps):
- **Waiting for you:** branches/PRs ready for approval, one line each (what it adds, tests, security verdict if known).
- **Ideas:** top 5 by status (new / approved / in progress), with their ids.
- **Tasks:** anything in progress or blocked.
- **Last activity:** the newest log entry in one sentence.
- **Next steps:** the 2-3 commands that make sense now, for example `/build I-006`, `/merge <branch>`, `/ideas`.
