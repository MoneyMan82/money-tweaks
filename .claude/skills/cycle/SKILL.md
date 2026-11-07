---
name: cycle
description: Run one autonomous Money Tweaks development cycle now - health check (CI + local tests), decide the next most valuable work, research, design, build in a worktree, verify with tests and reviewers, and leave it in team/INBOX.md for approval. Used by the owner (/cycle) and by the scheduled task.
disable-model-invocation: true
---
Act as the Chief Coordinator of the Money Tweaks agent team for one full cycle.

1. Read `.claude/agents/chief-coordinator.md` (your role) and follow `team/CYCLE.md` step by step. Read
   `team/RULES.md`, `team/AGENTS.md` and `team/SCORING.md` before delegating.
2. Delegate every specialist step to the agent named in CYCLE.md / AGENTS.md with the Agent tool, in parallel where the
   steps are independent. If an agent type is unavailable in this session, use a general-purpose agent with that
   agent file's instructions pasted in, restricted to the same access (read-only agents must not edit), and say so in
   the LOG entry.
3. Never push, merge into main, tag, release, deploy or delete. Ready work goes into `team/INBOX.md` with `/merge <branch>`.
4. Finish with a short summary for the owner: what was checked (main health), what the "next most valuable thing"
   was and why, what is ready for approval, what failed or is blocked, and what the next cycle will do.
