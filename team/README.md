# Money Tweaks agent team

A self-running team of 20 Claude Code agents that improves Money Tweaks on its own. It checks the app's health,
asks itself what the next most valuable thing is, researches and challenges it, builds it on a separate branch, tests
and reviews it, and leaves it ready for your approval. **Your only job: approve with `/merge <branch>`.**

## What you do
| When | Type | What happens |
|---|---|---|
| Whenever you're curious | `/team` | What's waiting for you, main health, last cycle, top ideas |
| Something is ready | `/merge <branch>` | Your approval: merge, test, release (the line is in `team/INBOX.md`) |
| You want it now | `/cycle` | Runs one full cycle immediately instead of waiting for the schedule |
| You have a wish | write under "Owner notes" in `team/INBOX.md`, or `/build <idea or description>` | The next cycle acts on it first |
| Optional | `/ideas [topic]`, `/check [area]` | Extra idea discovery or a code review on demand |

Nothing is merged, pushed, released or deployed until you type `/merge`.

## How it runs by itself
- A scheduled task in the Claude desktop app (**Money Tweaks cycle**) runs `/cycle` every 4 hours on this PC, so it
  can use the full Windows build and all tests. It runs while the Claude app is open; if the app was closed, it runs
  on next launch. Manage it in the app's scheduled tasks list (pause, run now, change the time).
- If a game is running when a cycle starts, it does a quiet cycle (research and reviews only, no builds or tests).
- Each cycle follows `team/CYCLE.md`:
  1. **Health check:** CI on GitHub (even if red: every failure is collected) and a full local build + all tests.
  2. **"What's next?":** broken main > your notes > stale approved work > (if 3+ items wait for you: reviews only) >
     confirmed bugs > discovery when fewer than 8 scored ideas > highest-scoring idea.
  3. **Discovery** (when needed or every 4th cycle): scout + competitor analyst + a domain specialist find ideas; the
     idea critic scores every one with `team/SCORING.md`.
  4. **Verify and design:** domain specialist checks the facts, architect designs, critic attacks the design.
  5. **Build:** developer / UI designer / web developer on `agent/<task>` in a worktree; docs writer updates docs.
  6. **Verify:** tester (full build + all tests), code reviewer, security reviewer, plus license auditor and UX
     reviewer when relevant. Failures go back to the builder (up to 3 rounds).
  7. **Hand over:** `team/INBOX.md` entry with tests, verdicts and the `/merge` line; log, state and knowledge updated.
     Every 3rd cycle the process improver reviews how the team is doing.

## The team (`team/AGENTS.md` has the full registry)
- Lead: **chief-coordinator**
- Discovery/research (read-only): innovation-scout, competitor-analyst, researcher, windows-tweak-specialist,
  gaming-performance-analyst, hardware-specialist, idea-critic, architect
- Building (own worktree, never push): developer, ui-designer, web-developer, docs-writer
- Verification (read-only; the tester runs builds): tester, code-reviewer, security-reviewer, license-auditor,
  ux-reviewer, ci-investigator
- Improvement (read-only): process-improver

## Files
| File | Purpose |
|---|---|
| `team/INBOX.md` | Ready for your approval, things that need you, and your notes |
| `team/STATE.md` | Cycle number, main health, next intended step |
| `team/CYCLE.md` | The procedure every cycle follows |
| `team/SCORING.md` | How ideas are scored and the "what's next" questions |
| `team/IDEAS.md` / `team/TASKS.md` | Ideas backlog with scores / task queue with types and severities |
| `team/ROADMAP.md` | Milestones |
| `team/FINDINGS.md` | Shared findings agents pass to each other |
| `team/LOG.md` | Append-only log of every cycle |
| `team/RULES.md` | Your rules; every agent reads them first |
| `team/knowledge/` | Architecture, product, quality bar, Windows tweaks, gaming, hardware, licenses, competitors |
| `.claude/agents/` | Agent definitions · `.claude/skills/` your commands · `.claude/hooks/agent_guard.py` the guard |

## Safety
The guard (`.claude/hooks/agent_guard.py`) checks every agent command and edit, and during a cycle the main session's
too. It blocks pushes, merges into main, tags, releases, deploys, release scripts, destructive git/file commands,
registry/service/network/power changes outside the app's tests, elevation, downloaded scripts, uploads and secrets,
and edits outside the agent's own worktree. It fails closed on errors. It is a safety net that matches commands and
paths, not a sandbox, and it needs `python` on PATH.

## Housekeeping
Agent worktrees collect in `.claude/worktrees/` (each has its own build output). After merging or rejecting work you
can ask your normal Claude session to clean up old ones.

## Adding an agent
See "Adding a new agent" in `team/AGENTS.md`. New agent files load when a new Claude Code session starts.
