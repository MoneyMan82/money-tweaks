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
