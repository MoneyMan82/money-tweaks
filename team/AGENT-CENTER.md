# Agent Command Center

A separate Windows app that shows the Money Tweaks agent team working, live: every agent, what it is doing, what it
found and decided, the files it changes, who it works with, research, statistics and full history.

**Open it:** double-click **Agent Command Center** on the desktop (installed in
`%LOCALAPPDATA%\MoneyTweaksAgentCenter\app`). Source: `src/MoneyTweaks.AgentCenter` (WPF) and
`src/MoneyTweaks.AgentCenter.Core` (pipeline); tests: `tests/MoneyTweaks.AgentCenter.Tests`.
To update the installed copy after code changes:
`dotnet publish src/MoneyTweaks.AgentCenter -c Release -o "%LOCALAPPDATA%\MoneyTweaksAgentCenter\app"`.

## How it is connected (supported Claude Code interfaces only)
1. **Hooks** (`.claude/settings.json`): `PreToolUse`, `PostToolUse`, `SubagentStart`, `SubagentStop`, `Stop`,
   `UserPromptSubmit`, `SessionStart`, `SessionEnd`, `Notification` run `.claude/hooks/command_center.py`. Claude Code
   gives each hook a JSON payload with the session, the subagent's `agent_id` / `agent_type`, the tool and its input.
   The publisher turns it into one event (schema `mt.agent-event/1`: agent, session, time, type, status, task, phase,
   summary, files, tool, findings, decision + why, next, needs-help, errors/blockers, tests).
2. **Progress notes:** agents run `python .claude/hooks/command_center.py publish --status '...' --summary '...' ...`
   at milestones (team/RULES.md). The hook sees the command, so the note carries the agent's real identity.
3. **Transport:** every event is appended to `%LOCALAPPDATA%\MoneyTweaksAgentCenter\spool\events-YYYYMMDD.jsonl`
   (durable) and sent to the app over the local named pipe `\\.\pipe\MoneyTweaksAgentCenter` (instant). The app imports
   the spool at start and every 2 s, so nothing is lost while it's closed; duplicates are dropped by event id.
4. **Storage:** `%LOCALAPPDATA%\MoneyTweaksAgentCenter\history.db` (SQLite) keeps every event.
5. **File monitor:** the app watches the workspace (main checkout and `.claude\worktrees`, without bin/obj/.git) and
   only attributes a change to an agent when an agent event or the agent's worktree links them.

## Controls (what is and isn't possible)
- **Pause all / Resume:** the publisher waits at each agent's next tool call while `control\paused` exists (up to 14
  minutes, then continues). Long-running tool calls finish first.
- **Stop agent:** that agent's next tool calls are refused with an instruction to report and stop (it can still report).
- **Restart agent:** not available - Claude Code has no supported way for another program to restart an agent.
- **Run cycle now / Brainstorm now / Stop run (RUNS tab and header):** the app starts a real Claude Code session with
  the official CLI in non-interactive mode - `claude -p "<prompt>" --permission-mode dontAsk --allowedTools ...` - in
  the workspace. It writes `.claude/autonomous-run` first (guard active from the first tool call) and removes it when
  the run ends. One run at a time; refused while a game is running. The log of each run is kept in
  `%LOCALAPPDATA%\MoneyTweaksAgentCenter\runs\`.
- **Automatic cycles:** off / every 1-24 hours / daily at a time, chosen in the RUNS tab. Only while the app is open;
  a due cycle waits while a game is running.
- **Sign-in:** the CLI has its own sign-in (the desktop app's isn't shared with other programs). "Sign in to Claude
  Code" opens `claude auth login`; the owner finishes it in the browser once. `claude auth status` is checked without
  using any plan usage. The CLI is the copy bundled with the Claude desktop app (newest version), `~/.local/bin`, PATH,
  or `claudePath` in the app's settings.json.
- Connection indicator: LIVE (events in the last 2 minutes), LISTENING (pipe open, quiet), OFFLINE (pipe not open),
  HOOKS MISSING (settings.json doesn't call the publisher). Agents silent for 15+ minutes are marked QUIET.

## Security
Local only: the pipe accepts the current Windows user and denies network logons; no ports are opened. Events are
sanitized twice (publisher and app): control characters and terminal escapes removed, tokens/keys/passwords redacted,
lengths capped, only http(s) links clickable. File paths outside the workspace are ignored; diffs come from read-only
git commands inside the workspace. Controls are files that can only make agents wait or stop - nothing can run commands.

## Known limits
- Claude Code's hook JSON garbles the non-ASCII project path ("Työpöytä") on this PC; the app repairs paths against the
  workspace root.
- "Thinking" is not shown: only what agents publish as notes and what Claude Code reports about tool use.
- Built-in agent types (Explore, general-purpose) appear under "Other agents".
