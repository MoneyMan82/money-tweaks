# Money Tweaks agent team

Eight Claude Code subagents, defined in `.claude/agents/`, with shared rules in `team/RULES.md` and a guard hook in
`.claude/hooks/agent_guard.py`.

| Agent | Job | Access | Model |
|---|---|---|---|
| `chief-coordinator` | Plans, delegates, keeps the team files current, asks the owner for approvals | Read-only shell; edits only `team/` | opus |
| `innovation-scout` | Finds evidence-backed feature and improvement ideas | Read-only + web | sonnet |
| `researcher` | Investigates software, GitHub projects, Windows settings, licenses | Read-only + web | sonnet |
| `architect` | Feasibility and implementation designs that reuse existing systems | Read-only + web | opus |
| `developer` | Writes C#/.NET 10 and WPF code with tests | Edits and commits in its **own worktree** | opus |
| `ui-designer` | WPF layout, theme, animations, accessibility | Edits and commits in its **own worktree** | opus |
| `tester` | Builds, runs all test suites, reviews diffs, reports bugs | Runs builds/tests in its **own worktree**; no edits or commits | sonnet |
| `security-reviewer` | Safety and vulnerability verdict for every branch | Read-only | opus |

## How to use it
- **Run the whole team:** start Claude Code with the coordinator as the main agent:
  `claude --agent chief-coordinator`, then say what you want ("find the next 5 ideas", "build idea I-003").
  The coordinator delegates to the others (subagents can't start other subagents, so the coordinator has to be the
  main agent to manage the team).
- **Automatic delegation:** in a normal Claude Code session the specialists are picked automatically from their
  descriptions (for example the Security Reviewer before a merge, the Tester after code changes).
- **Ask for one directly:** "use the researcher to check …", or `@agent-researcher` in the prompt.
- Check `/agents` in an interactive `claude` terminal to see them loaded.

## Workflow
idea (`IDEAS.md`) → research → design → task (`TASKS.md`) → developer/ui-designer on a branch in
`.claude/worktrees/` → tester + security-reviewer → fixes → **owner approval** → owner merges/releases → `LOG.md`.

## What agents can never do without you
Merge into main, push any branch, tag, release, deploy, run the release scripts, delete files or branches, rewrite
history, change this PC's system settings, elevate, send data to the internet, or read secrets. The guard hook blocks
these commands for every team agent (it fails closed if it errors). When work is ready, the coordinator presents the
branch and asks you. To merge an approved branch, tell your main session (not an agent), e.g. "merge branch X".

Limits of the guard: it pattern-matches shell commands and file paths, so it is a safety net, not a sandbox. It needs
`python` on PATH (if Python is missing the hook can't run, and Claude Code treats that as a non-blocking error).
