"""Money Tweaks Agent Command Center - event publisher (Claude Code hook + progress CLI).

As a hook (registered in .claude/settings.json for PreToolUse, PostToolUse, SubagentStart, SubagentStop, Stop,
UserPromptSubmit, SessionStart, SessionEnd, Notification): reads Claude Code's hook JSON on stdin, turns it into one
structured, sanitized event (schema "mt.agent-event/1") and
  1. appends it to the spool  %LOCALAPPDATA%\\MoneyTweaksAgentCenter\\spool\\events-YYYYMMDD.jsonl  (durable; the app
     imports it on start, so nothing is lost while the app is closed), and
  2. sends it to the running app over the local named pipe  \\\\.\\pipe\\MoneyTweaksAgentCenter  (instant updates).
It never blocks or fails a tool call (always exits 0) except for the owner's controls: while
  ...\\control\\paused exists, PreToolUse waits (agents pause at their next tool call), and
  ...\\control\\stop-<agent id or type> makes PreToolUse refuse further tools so the agent finishes and reports.

As a CLI: agents publish progress notes at milestones (the hook sees the command, so the event carries the real agent
identity; running it only prints "published"):
  python .claude/hooks/command_center.py publish --status RESEARCHING --phase 'Competitor review' \
      --summary 'Comparing WinUtil and Sophia network tweaks' --finding '...' --decision '...' --why '...' \
      --next '...' --needs 'researcher' --error '...' --file 'src/x.cs' --source 'https://...'
Use single quotes for every value.
"""
import json, os, re, shlex, sys, time, uuid

SCHEMA = "mt.agent-event/1"
PIPE = "\\\\.\\pipe\\" + re.sub(r"[^A-Za-z0-9_-]", "", os.environ.get("MT_AGENT_CENTER_PIPE") or "MoneyTweaksAgentCenter")
STATUSES = {"IDLE", "QUEUED", "PLANNING", "RESEARCHING", "ANALYZING", "CODING", "TESTING", "REVIEWING", "BLOCKED",
            "COMPLETED", "FAILED", "PAUSED"}
# Agents write it as `python .claude/hooks/command_center.py publish ...`, often after `cd "<folder>" && `, and
# sometimes with the script's absolute path (seen in the first live test), so accept all three forms.
PUBLISH_RE = re.compile(r"""^\s*(?:cd\s+(?:"[^"]*"|\S+)\s*&&\s*)?python3?\s+("?)(?:[^"\s]*[/\\])?\.claude[/\\]hooks[/\\]command_center\.py\1\s+publish\b""")
ROOT = os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "MoneyTweaksAgentCenter")

SECRET_PATTERNS = [
    re.compile(r"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})"),
    re.compile(r"\b(sk-[A-Za-z0-9_-]{16,}|sk-ant-[A-Za-z0-9_-]{16,})"),
    re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}"),            # JWT
    re.compile(r"(?i)\b(bearer|token|authorization)\s*[:=]?\s*[A-Za-z0-9._~+/=-]{16,}"),
    re.compile(r"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret)\s*[:=]\s*\S+"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----"),
]
CONTROL_CHARS = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")   # whole escape sequences first


def clean(text, limit=600):
    if text is None:
        return None
    s = str(text)
    s = CONTROL_CHARS.sub("", s)
    for p in SECRET_PATTERNS:
        s = p.sub("[redacted]", s)
    s = s.strip()
    return s if len(s) <= limit else s[: limit - 1] + "…"


def status_for_tool(tool, ti):
    if tool in ("WebSearch", "WebFetch"):
        return "RESEARCHING"
    if tool in ("Read", "Grep", "Glob", "LS", "NotebookRead"):
        return "ANALYZING"
    if tool in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
        return "CODING"
    if tool in ("Agent", "Task", "TodoWrite", "SendMessage"):
        return "PLANNING"
    if tool == "Bash":
        c = (ti.get("command") or "").lower()
        if re.search(r"\bdotnet\s+(test|build)\b|\bnpm\s+(test|run\s+test)\b|\bpytest\b", c):
            return "TESTING"
        if re.search(r"\bgit\s+(diff|log|show|blame)\b", c):
            return "REVIEWING"
        return "ANALYZING"
    return None


def tool_target(tool, ti):
    for k in ("file_path", "notebook_path", "path", "url", "query", "pattern", "command", "description", "to"):
        if ti.get(k):
            return clean(ti[k], 240)
    return None


def files_of(tool, ti):
    out = []
    for k in ("file_path", "notebook_path"):
        if ti.get(k):
            out.append(str(ti[k]))
    if tool == "MultiEdit" and ti.get("file_path"):
        pass
    return out


def line_counts(tool, ti):
    def n(s):
        return 0 if not s else str(s).count("\n") + 1
    if tool == "Edit":
        return n(ti.get("new_string")), n(ti.get("old_string"))
    if tool == "MultiEdit":
        edits = ti.get("edits") or []
        return sum(n(e.get("new_string")) for e in edits), sum(n(e.get("old_string")) for e in edits)
    if tool == "Write":
        return n(ti.get("content")), None   # removed lines unknown for a full rewrite
    return None, None


TEST_RE = re.compile(r"(Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)(?:.*?-\s*([\w.]+)\.dll)?")


def test_results(text):
    res = []
    for m in TEST_RE.finditer(text or ""):
        res.append({"failed": int(m.group(2)), "passed": int(m.group(3)), "skipped": int(m.group(4)),
                    "total": int(m.group(5)), "suite": m.group(6)})
    return res


def response_text(tr):
    if tr is None:
        return ""
    if isinstance(tr, str):
        return tr
    if isinstance(tr, dict):
        parts = [str(tr.get(k)) for k in ("stdout", "stderr", "output", "result", "content", "error") if tr.get(k)]
        return "\n".join(parts) if parts else json.dumps(tr)[:4000]
    return str(tr)


def response_failed(tr):
    if isinstance(tr, dict):
        if tr.get("is_error") or tr.get("interrupted"):
            return True
        if isinstance(tr.get("exitCode"), int) and tr["exitCode"] != 0:
            return True
    return False


def parse_publish(cmd):
    try:
        parts = shlex.split(cmd, posix=True)
    except ValueError:
        return None
    try:
        i = parts.index("publish")
    except ValueError:
        return None
    args, key = {}, None
    for p in parts[i + 1:]:
        if p.startswith("--"):
            key = p[2:].lower()
            args.setdefault(key, [])
        elif key:
            args[key].append(p)
    return {k: " ".join(v) if k not in ("finding", "file", "source") else v for k, v in args.items()}


def base_event(d, etype):
    agent_id = d.get("agent_id") or "main"
    return {
        "schema": SCHEMA,
        "id": uuid.uuid4().hex,
        "ts": int(time.time() * 1000),
        "session": d.get("session_id") or "unknown",
        "agent": agent_id,
        "agentType": d.get("agent_type") or ("main" if agent_id == "main" else None),
        "type": etype,
        "source": "hook",
        "cwd": clean(d.get("cwd"), 400),
        "promptId": d.get("prompt_id"),
    }


def build(d):
    """Hook payload -> list of events."""
    ev = d.get("hook_event_name")
    ti = d.get("tool_input") if isinstance(d.get("tool_input"), dict) else {}
    tool = d.get("tool_name")
    out = []
    if ev == "SessionStart":
        e = base_event(d, "session_start"); e["summary"] = clean("Session started (" + str(d.get("source") or "startup") + ")"); out.append(e)
    elif ev == "SessionEnd":
        e = base_event(d, "session_end"); e["summary"] = clean("Session ended (" + str(d.get("reason") or "") + ")"); out.append(e)
    elif ev == "UserPromptSubmit":
        prompt = str(d.get("prompt") or "")
        if re.match(r"\s*<(task-notification|agent-message|system-reminder|local-command|command-)", prompt):
            # Claude Code delivers background-task results and agent hand-backs as prompts: not a new task.
            e = base_event(d, "notification"); e["summary"] = "Background work reported back"; out.append(e)
        else:
            e = base_event(d, "task"); e["status"] = "PLANNING"; e["task"] = clean(prompt, 400)
            e["summary"] = "New instruction received"; out.append(e)
    elif ev == "Stop":
        e = base_event(d, "turn_end"); e["status"] = "IDLE"; e["summary"] = "Finished the current turn"; out.append(e)
    elif ev == "Notification":
        e = base_event(d, "notification"); e["summary"] = clean(d.get("message"), 300)
        if "permission" in (d.get("message") or "").lower() or "waiting" in (d.get("message") or "").lower():
            e["status"] = "BLOCKED"; e["blocker"] = clean(d.get("message"), 300)
        out.append(e)
    elif ev == "SubagentStart":
        e = base_event(d, "agent_start"); e["status"] = "PLANNING"; e["summary"] = f"{d.get('agent_type') or 'Agent'} started"; out.append(e)
    elif ev == "SubagentStop":
        e = base_event(d, "agent_stop"); e["status"] = "COMPLETED"; e["summary"] = f"{d.get('agent_type') or 'Agent'} finished"; out.append(e)
    elif ev == "PreToolUse" and tool:
        if tool == "Bash" and PUBLISH_RE.match(ti.get("command") or ""):
            a = parse_publish(ti.get("command") or "") or {}
            e = base_event(d, "progress"); e["source"] = "agent"
            st = (a.get("status") or "").upper()
            if st in STATUSES:
                e["status"] = st
            for k, field, lim in (("phase", "phase", 120), ("task", "task", 400), ("summary", "summary", 600),
                                  ("decision", "decision", 400), ("why", "why", 400), ("next", "next", 300),
                                  ("needs", "needsHelp", 80), ("error", "error", 400), ("blocker", "blocker", 400)):
                if a.get(k):
                    e[field] = clean(a[k], lim)
            if a.get("finding"):
                e["findings"] = [clean(x, 500) for x in a["finding"] if x][:10]
            if a.get("file"):
                e["files"] = [clean(x, 400) for x in a["file"] if x][:20]
            if a.get("source"):
                e["sources"] = [clean(x, 400) for x in a["source"] if x][:20]
            if not e.get("summary"):
                e["summary"] = e.get("decision") or (e.get("findings") or [None])[0] or e.get("phase") or "Progress update"
            out.append(e)
            return out
        e = base_event(d, "tool_start")
        e["tool"] = {"name": tool, "target": tool_target(tool, ti), "useId": d.get("tool_use_id")}
        st = status_for_tool(tool, ti)
        if st:
            e["status"] = st
        f = files_of(tool, ti)
        if f:
            e["files"] = [clean(x, 400) for x in f]
        if tool in ("Agent", "Task"):
            e["type"] = "delegate"
            e["to"] = ti.get("subagent_type") or "general-purpose"
            e["summary"] = clean(ti.get("description") or "Delegated a task", 200)
            e["task"] = clean(ti.get("prompt"), 400)
        elif tool == "SendMessage":
            e["type"] = "message"; e["to"] = clean(ti.get("to"), 80); e["summary"] = clean(ti.get("summary") or ti.get("message"), 300)
        elif tool == "SubagentHandback":
            e["type"] = "report"; e["status"] = "COMPLETED"; e["summary"] = clean(ti.get("message"), 1500)
        elif tool == "TodoWrite":
            todos = ti.get("todos") or []
            e["type"] = "todos"
            e["todos"] = [{"content": clean(t.get("content"), 200), "status": t.get("status"), "active": clean(t.get("activeForm"), 200)} for t in todos][:40]
            cur = next((t for t in todos if t.get("status") == "in_progress"), None)
            if cur:
                e["phase"] = clean(cur.get("activeForm") or cur.get("content"), 200); e["summary"] = e["phase"]
        elif tool == "WebSearch":
            e["type"] = "research"; e["summary"] = clean("Searching: " + str(ti.get("query")), 300); e["query"] = clean(ti.get("query"), 300)
        elif tool == "WebFetch":
            e["type"] = "research"; e["summary"] = clean("Reading " + str(ti.get("url")), 300)
            e["sources"] = [clean(ti.get("url"), 400)]; e["query"] = clean(ti.get("prompt"), 300)
        else:
            e["summary"] = clean(f"{tool}: {e['tool']['target'] or ''}", 300)
        out.append(e)
    elif ev == "PostToolUse" and tool:
        if tool == "Bash" and PUBLISH_RE.match(ti.get("command") or ""):
            return out
        tr = d.get("tool_response")
        e = base_event(d, "tool_end")
        failed = response_failed(tr)
        e["tool"] = {"name": tool, "target": tool_target(tool, ti), "useId": d.get("tool_use_id"),
                     "ok": not failed, "ms": d.get("duration_ms")}
        f = files_of(tool, ti)
        if f:
            e["files"] = [clean(x, 400) for x in f]
        if tool in ("Edit", "Write", "MultiEdit", "NotebookEdit") and not failed:
            e["type"] = "file_change"
            add, rem = line_counts(tool, ti)
            e["lines"] = {"added": add, "removed": rem}
            e["summary"] = clean(f"{'Wrote' if tool == 'Write' else 'Edited'} {os.path.basename(f[0]) if f else ''}", 200)
            out.append(e)
            return out
        if tool == "Bash":
            text = response_text(tr)
            tests = test_results(text)
            if tests:
                e["type"] = "test_result"; e["tests"] = tests
                p = sum(t["passed"] for t in tests); fl = sum(t["failed"] for t in tests)
                e["status"] = "TESTING"; e["summary"] = f"Tests: {p} passed, {fl} failed"
                if fl:
                    e["error"] = clean(f"{fl} test(s) failed", 200)
            elif failed:
                e["error"] = clean(text, 400)
            if "Blocked by Money Tweaks agent guard" in text:
                e["type"] = "blocked"; e["blocker"] = clean(text, 300)
        elif failed:
            e["error"] = clean(response_text(tr), 400)
        if e.get("error") and e["type"] == "tool_end":
            e["summary"] = clean(f"{tool} failed: {e['error']}", 300)
        out.append(e)
    return out


def write_spool(events):
    folder = os.path.join(ROOT, "spool")
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, time.strftime("events-%Y%m%d.jsonl"))
    data = "".join(json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n" for e in events).encode("utf-8")
    fd = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | getattr(os, "O_BINARY", 0), 0o600)
    try:
        os.write(fd, data)
    finally:
        os.close(fd)


def send_pipe(events):
    try:
        with open(PIPE, "wb", buffering=0) as p:   # fails at once if the app isn't running or is busy
            for e in events:
                p.write((json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
    except OSError:
        pass


def control(d):
    """Owner controls from the Command Center. Returns a deny reason or None; waits while paused."""
    folder = os.path.join(ROOT, "control")
    if not os.path.isdir(folder):
        return None
    tool = d.get("tool_name")
    ti = d.get("tool_input") if isinstance(d.get("tool_input"), dict) else {}
    if tool in ("SubagentHandback", "TodoWrite") or (tool == "Bash" and PUBLISH_RE.match(ti.get("command") or "")):
        return None   # an agent may always report back
    ids = [d.get("agent_id") or "main", d.get("agent_type") or ("main" if not d.get("agent_id") else "")]
    for i in ids:
        if i and os.path.exists(os.path.join(folder, "stop-" + re.sub(r"[^A-Za-z0-9._-]", "_", i))):
            return ("Stopped by the owner from the Agent Command Center. Do not use more tools: report what you have done "
                    "so far and what is unfinished, then stop.")
    paused = os.path.join(folder, "paused")
    if os.path.exists(paused):
        e = base_event(d, "paused"); e["status"] = "PAUSED"; e["summary"] = "Paused by the owner (waiting at the next tool call)"
        emit([e])
        deadline = time.time() + 14 * 60
        while os.path.exists(paused) and time.time() < deadline:
            time.sleep(1)
        e = base_event(d, "resumed"); e["summary"] = "Resumed" if not os.path.exists(paused) else "Still paused after 14 minutes; continuing"
        emit([e])
    return None


def emit(events):
    if not events:
        return
    try:
        write_spool(events)
    except OSError:
        pass
    send_pipe(events)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "publish":
        print("published")   # the hook already recorded it with the agent's identity
        return 0
    try:
        d = json.loads(sys.stdin.buffer.read().decode("utf-8-sig") or "{}")
    except Exception:
        return 0
    try:
        emit(build(d))
    except Exception:
        pass
    if d.get("hook_event_name") == "PreToolUse":
        try:
            reason = control(d)
        except Exception:
            reason = None
        if reason:
            print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                                     "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
