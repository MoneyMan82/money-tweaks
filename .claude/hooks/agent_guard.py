"""PreToolUse guard for the Money Tweaks agent team (.claude/agents/*.md).

Usage (from an agent's frontmatter hook):  python agent_guard.py <mode>
  readonly     Bash limited to read-only git/dotnet inspection commands. No file writes.
  coordinator  Read-only Bash; Edit/Write only inside team/ of the main checkout.
  developer    Full tools inside its own worktree; no push, merge, tag, release, deploy or destructive commands.
  tester       Like developer (builds and runs tests in its own worktree) but may not edit source files.

Reads the hook JSON on stdin. Exit 0 = allow, exit 2 = block (stderr is shown to the agent).
Any error inside the guard blocks the call (fail closed).
"""
import json
import os
import re
import sys


def block(msg):
    sys.stderr.write("Blocked by Money Tweaks agent guard: " + msg +
                     " Ask the owner (through the Chief Coordinator) to do this, or to approve it.\n")
    sys.exit(2)


def norm(p):
    return os.path.normcase(os.path.abspath(p)).replace("\\", "/")


# Never allowed for any agent: these need the owner's approval.
FORBIDDEN = [
    (r"\bgit\s+push\b", "pushing (any push can deploy: main deploys the website, other branches create Cloudflare previews)."),
    (r"\bgit\s+merge\b", "merging branches."),
    (r"\bgit\s+(rebase|cherry-pick|am)\b", "rewriting or replaying history."),
    (r"\bgit\s+tag\b(?!\s+(-l|--list|--contains|--points-at|-n)\b)(?=\s+\S)", "creating or deleting tags (tags publish releases)."),
    (r"\bgit\s+reset\b[^|;&]*--hard", "git reset --hard."),
    (r"\bgit\s+clean\b", "git clean."),
    (r"\bgit\s+branch\b[^|;&]*(\s-D\b|--delete|\s-d\b|\s-M\b|--force)", "deleting or force-moving branches."),
    (r"\bgit\s+(checkout|switch)\s+(-f\b|--force|--\s|\.\s*$)", "discarding changes with checkout."),
    (r"\bgit\s+restore\b(?![^|;&]*--staged)", "discarding changes with git restore."),
    (r"\bgit\s+stash\s+(drop|clear)\b", "dropping stashes."),
    (r"\bgit\s+(filter-branch|filter-repo|replace|update-ref|reflog\s+expire|gc\b[^|;&]*--prune)", "rewriting or pruning history."),
    (r"\bgit\s+worktree\s+(remove|prune)\b", "removing worktrees."),
    (r"\bgit\s+(config|remote)\b(?!\s+(-l|--list|--get|get-url|-v|show)\b)", "changing git configuration or remotes."),
    (r"\bgh\s+(release|pr\s+merge|repo|secret|workflow\s+run|api)\b", "GitHub releases, merges or API writes."),
    (r"\bwrangler\b", "Cloudflare deploys and data changes."),
    (r"\bnpm\s+(run\s+)?(deploy|publish)\b|\bnpm\s+publish\b", "publishing or deploying."),
    (r"bump-version\.ps1|build-release\.ps1|release-notes\.ps1", "release scripts (the owner decides versions and releases)."),
    (r"\brm\s+(-[a-zA-Z]*[rRf]|--recursive|--force)", "recursive or forced deletes."),
    (r"\b(rmdir|rd)\s+/s\b|\bdel\s+/[sfq]\b|\bRemove-Item\b|\bri\s+-r", "deleting files or folders."),
    (r"\b(format|diskpart|bcdedit|vssadmin|cipher\s+/w|fsutil|bootrec|sfc|dism)\b", "disk, boot or system repair commands."),
    (r"\breg(\.exe)?\s+(add|delete|import|restore|load|unload|copy)\b|\b(Set|New|Remove)-ItemProperty\b|\bNew-Item\b[^|;&]*HK(LM|CU|CR|U)", "changing the registry of this PC."),
    (r"\bsc(\.exe)?\s+(config|delete|stop|start|create|failure)\b|\b(Set|Stop|Start|New|Remove)-Service\b|\bnet\s+(stop|start|user|localgroup)\b", "changing Windows services or accounts."),
    (r"\b(taskkill|Stop-Process|shutdown|Restart-Computer|Stop-Computer|logoff)\b", "killing processes or shutting down."),
    (r"\b(netsh|powercfg|bcdedit|Set-MpPreference|Add-MpPreference|Set-NetFirewall|New-NetFirewallRule|Disable-NetAdapter|Set-ExecutionPolicy|schtasks|Register-ScheduledTask|Unregister-ScheduledTask)\b", "changing network, power, security or scheduled-task settings."),
    (r"-Verb\s+RunAs|\brunas\b|\bgsudo\b|\bsudo\b", "elevating to administrator."),
    (r"\b(iex|Invoke-Expression)\b|(curl|wget|irm|iwr|Invoke-WebRequest|Invoke-RestMethod)\b[^\n]*\|\s*(ba|z)?sh\b|(curl|wget|irm|iwr|Invoke-RestMethod)\b[^\n]*\|\s*(iex|python|pwsh|powershell|node|cmd)\b", "running downloaded or dynamic scripts."),
    (r"\b(curl|wget)\b[^|;&]*(\s-X\s*(POST|PUT|PATCH|DELETE)|\s(-d|--data\S*|-F|--form|-T|--upload-file)\s)|\b(Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b[^|;&]*-Method\s+(Post|Put|Patch|Delete)|-Body\b", "sending data to the internet."),
    (r"\.dev\.vars|\.secret\b|\bsecrets?\.json\b|GITHUB_TOKEN|CLOUDFLARE_API_TOKEN|\bGH_TOKEN\b|\bprintenv\b|\benv\s*$|Get-ChildItem\s+env:|\bset\s*$", "reading secrets or credentials."),
]

READONLY_BASH = re.compile(
    r"^\s*(git\s+(status|log|show|diff|branch|rev-parse|ls-files|ls-tree|grep|blame|describe|merge-base|shortlog|worktree\s+list|tag\s+(-l|--list|--contains|--points-at))\b"
    r"|dotnet\s+(--info|--version|--list-sdks|list\b)"
    r"|(ls|pwd|wc|head|tail|cat|grep|find|file|stat|du|sort|uniq|diff)\b)")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "readonly"
    data = json.loads(sys.stdin.buffer.read().decode("utf-8-sig"))  # hook JSON is UTF-8, not the Windows code page
    tool = data.get("tool_name", "")
    ti = data.get("tool_input") or {}
    cwd = data.get("cwd") or os.getcwd()
    # Project root from this script's own location (.claude/hooks/agent_guard.py). Not CLAUDE_PROJECT_DIR: Git Bash
    # mangles its non-ASCII characters (the project lives under "Työpöytä").
    project = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    in_worktree = "/.claude/worktrees/" in norm(cwd) + "/"

    if tool == "Bash":
        cmd = ti.get("command", "")
        for pattern, why in FORBIDDEN:
            if re.search(pattern, cmd, re.IGNORECASE | re.MULTILINE):
                block(why)
        if mode in ("readonly", "coordinator"):
            parts = [p for p in re.split(r"\|\||&&|;|\|", cmd) if p.strip()]
            if not parts or not all(READONLY_BASH.match(p) for p in parts):
                block("this agent only runs read-only inspection commands (git status/log/diff/show, ls, grep, cat...).")
            if re.search(r"(^|[^<>&0-9])>{1,2}\s*[^&\s]|\btee\b|\bsed\s+-i|\s-delete\b|\s-exec(dir)?\b|\s-ok\b", cmd):
                block("this agent may not write files from the shell.")
        else:
            if not in_worktree:
                block("coding and testing agents must run in their own worktree under .claude/worktrees/, not in the main checkout.")
            if re.search(r"\bgit\s+(commit|checkout|switch)\b", cmd, re.IGNORECASE):
                head = os.popen('git -C "%s" rev-parse --abbrev-ref HEAD' % cwd).read().strip()
                if re.search(r"\bgit\s+commit\b", cmd, re.IGNORECASE) and head in ("main", "master"):
                    block("committing on main.")
                if re.search(r"\bgit\s+(checkout|switch)\s+(main|master)\b", cmd, re.IGNORECASE):
                    block("switching to main.")
            if mode == "tester" and re.search(r"\bgit\s+commit\b", cmd, re.IGNORECASE):
                block("the Tester reports bugs; the Developer commits fixes.")
        sys.exit(0)

    if tool in ("Edit", "Write", "NotebookEdit", "MultiEdit"):
        path = ti.get("file_path") or ti.get("notebook_path") or ""
        target = norm(path if os.path.isabs(path) else os.path.join(cwd, path))
        if mode == "coordinator":
            team = norm(os.path.join(project, "team")) + "/"
            if not target.startswith(team):
                block("the Chief Coordinator only edits the team files in team/ (roadmap, ideas, tasks, log).")
            sys.exit(0)
        if mode in ("readonly", "tester"):
            block("this agent is read-only.")
        if not in_worktree:
            block("coding agents may only edit files inside their own worktree.")
        root = norm(cwd).split("/.claude/worktrees/")[0] + "/.claude/worktrees/"
        worktree = root + norm(cwd)[len(root):].split("/")[0] + "/"
        if not target.startswith(worktree):
            block("this file is outside your worktree (%s)." % worktree)
        rel = target[len(worktree):]
        if rel.startswith(".claude/") or rel.startswith(".github/") or rel.startswith("team/"):
            block("agents may not change agent definitions, guard hooks, CI/release workflows or the team files.")
        sys.exit(0)

    sys.exit(0)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception as e:  # fail closed
        block("guard error (%s)." % e)
