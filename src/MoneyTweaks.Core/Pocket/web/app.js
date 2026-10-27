// Money Tweaks Pocket Panel: live second-screen dashboard. Talks only to the Money Tweaks app that served this page.
"use strict";
(() => {
  const $ = (id) => document.getElementById(id);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* private mode: layout isn't remembered */ } },
  };

  // ---- tiles ------------------------------------------------------------------------------------------------------------
  const TILES = [
    { id: "fps", label: "FPS", unit: "", graph: true, max: null },
    { id: "cpu", label: "CPU usage", unit: "%", graph: true, max: 100 },
    { id: "gpu", label: "GPU usage", unit: "%", graph: true, max: 100 },
    { id: "cputemp", label: "CPU temperature", unit: "°C", graph: true, max: 110 },
    { id: "gputemp", label: "GPU temperature", unit: "°C", graph: true, max: 110 },
    { id: "ram", label: "RAM", unit: "GB", graph: true, max: null },
    { id: "vram", label: "VRAM", unit: "GB", graph: true, max: null },
    { id: "overlay", label: "Overlay", graph: false },
  ];
  const DEFAULT_LAYOUT = TILES.map((t) => ({ id: t.id, on: true, size: t.id === "fps" ? "large" : "normal" }));
  let layout = store.get("mt.layout", DEFAULT_LAYOUT);
  for (const t of TILES) if (!layout.some((l) => l.id === t.id)) layout.push({ id: t.id, on: true, size: "normal" });
  layout = layout.filter((l) => TILES.some((t) => t.id === l.id));
  const opts = store.get("mt.opts", { vibrate: true, dense: false });

  const hist = {};          // id -> recent values (graph)
  const shown = {};         // id -> number currently displayed (animated towards the target)
  const target = {};        // id -> latest value
  const els = {};
  let controls = [];      // the PC's allowlisted controls with their current state
  const pending = {};     // id -> { active, until }: a tap the PC hasn't confirmed yet
  let session = store.get("mt.session", null);
  let unpairing = false;   // this device asked to unpair: the stream's "revoked" notice is expected

  function buildTiles() {
    const host = $("tiles");
    host.textContent = "";
    document.body.classList.toggle("dense", !!opts.dense);
    for (const l of layout) {
      if (!l.on) continue;
      const def = TILES.find((t) => t.id === l.id);
      const tile = document.createElement("div");
      tile.className = "tile " + (l.size || "normal");
      const lbl = document.createElement("div"); lbl.className = "lbl"; lbl.textContent = def.label;
      const val = document.createElement("div"); val.className = "val"; val.textContent = "…";
      const sub = document.createElement("div"); sub.className = "sub";
      tile.append(lbl, val, sub);
      let canvas = null, ctl = null;
      if (def.graph) { canvas = document.createElement("canvas"); tile.append(canvas); }
      if (def.id === "overlay") { ctl = document.createElement("div"); tile.append(ctl); }
      host.append(tile);
      els[def.id] = { tile, val, sub, canvas, ctl, def };
    }
    renderControls();
  }

  // ---- telemetry --------------------------------------------------------------------------------------------------------
  const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? null : v.toFixed(d));

  function apply(f) {
    set("fps", f.fps?.fps, 0, f.fps?.fps != null ? `1% low ${fmt(f.fps.low1) ?? "–"} · ${fmt(f.fps.frameMs, 1) ?? "–"} ms${f.fps.target ? " · " + f.fps.target : ""}` : f.fps?.note);
    set("cpu", f.cpu?.load, 0, f.cpu?.name || "");
    set("gpu", f.gpu?.load, 0, f.gpu?.name || "");
    set("cputemp", f.cpu?.temp, 0, f.cpu?.temp == null ? f.cpu?.tempNote : f.cpu?.tempSource, f.cpu?.temp >= (f.cpu?.warnC ?? 90) ? "warn" : "");
    set("gputemp", f.gpu?.temp, 0, f.gpu?.temp == null ? f.gpu?.tempNote : "", f.gpu?.temp >= (f.gpu?.warnC ?? 85) ? "warn" : "");
    set("ram", f.ram?.usedGb, 1, f.ram?.totalGb ? `of ${fmt(f.ram.totalGb, 0)} GB` : "", f.ram?.usedGb / f.ram?.totalGb > 0.9 ? "warn" : "");
    set("vram", f.gpu?.vramUsedMb != null ? f.gpu.vramUsedMb / 1024 : null, 1, f.gpu?.vramTotalMb ? `of ${fmt(f.gpu.vramTotalMb / 1024, 0)} GB` : f.gpu?.vramNote || "");
    if (els.overlay) {
      const o = f.overlay || {};
      els.overlay.val.textContent = o.visible ? "On" : "Off";
      els.overlay.sub.textContent = o.visible ? `${o.style} · ${o.theme}${o.measuring ? " · measuring " + o.measuring : ""}` : "Turn it on to measure FPS in your game. More in Controls.";
    }
    alertsUi(f.alerts || []);
    if (f.controls) { syncControls(f.controls); }
  }

  function set(id, v, digits, sub, state) {
    const e = els[id];
    const h = (hist[id] ||= []);
    if (v != null && !Number.isNaN(v)) { h.push(v); if (h.length > 120) h.shift(); target[id] = v; }
    else { h.push(null); if (h.length > 120) h.shift(); target[id] = null; }
    if (!e) return;
    e.digits = digits;
    e.sub.textContent = sub || "";
    e.tile.classList.toggle("na", v == null);
    e.tile.classList.toggle("warn", state === "warn");
    if (v == null) { e.val.textContent = "not available"; shown[id] = null; }
  }

  // Numbers glide to the new value and graphs redraw smoothly, at the screen's refresh rate.
  function frame() {
    for (const id in els) {
      const e = els[id];
      if (e.def.id === "overlay") continue;
      const t = target[id];
      if (t != null) {
        const cur = shown[id] ?? t;
        const next = Math.abs(t - cur) < 0.05 ? t : cur + (t - cur) * 0.18;
        shown[id] = next;
        e.val.textContent = "";
        e.val.append(next.toFixed(e.digits ?? 0));
        if (e.def.unit) { const u = document.createElement("small"); u.textContent = e.def.unit; e.val.append(u); }
      }
      if (e.canvas) draw(e, hist[id] || []);
    }
    requestAnimationFrame(frame);
  }

  function draw(e, data) {
    const c = e.canvas, dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    if (!w || !h) return;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const g = c.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const vals = data.filter((v) => v != null);
    if (vals.length < 2) return;
    const max = e.def.max ?? (Math.max(...vals) * 1.15 || 1);
    const min = e.def.max ? 0 : Math.min(...vals) * 0.85;
    const n = 120, step = w / (n - 1), off = n - data.length;
    const y = (v) => h - 2 - ((v - min) / (max - min || 1)) * (h - 4);
    const accent = getComputedStyle(document.documentElement).getPropertyValue(e.tile.classList.contains("warn") ? "--gold" : "--accent").trim();
    g.beginPath();
    let started = false, lastX = 0;
    data.forEach((v, i) => {
      if (v == null) { started = false; return; }
      const x = (off + i) * step;
      if (!started) { g.moveTo(x, y(v)); started = true; } else g.lineTo(x, y(v));
      lastX = x;
    });
    g.strokeStyle = accent; g.lineWidth = 2; g.lineJoin = "round"; g.stroke();
    g.lineTo(lastX, h); g.lineTo(off * step, h); g.closePath();
    const grad = g.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, accent + "55"); grad.addColorStop(1, accent + "00");
    g.fillStyle = grad; g.fill();
  }

  // ---- alerts -----------------------------------------------------------------------------------------------------------
  const seenAlerts = new Set();
  function alertsUi(list) {
    const host = $("alerts");
    host.textContent = "";
    for (const a of list) {
      const d = document.createElement("div");
      d.className = "alert " + (a.level === "Critical" ? "critical" : "");
      const b = document.createElement("b"); b.textContent = a.title;
      d.append(b, document.createTextNode(a.detail || ""));
      host.append(d);
      if (!seenAlerts.has(a.key)) {
        seenAlerts.add(a.key);
        if (a.level === "Critical" && opts.vibrate && navigator.vibrate) navigator.vibrate([200, 100, 200]);
      }
    }
    for (const k of [...seenAlerts]) if (!list.some((a) => a.key === k)) seenAlerts.delete(k);
  }

  // ---- controls (only the actions the PC allows) -----------------------------------------------------------------------
  const isOn = (c) => (pending[c.id] ? pending[c.id].active : c.active);

  // The PC's state wins, except for a tap it hasn't confirmed yet (then the switch keeps showing what you chose).
  function syncControls(list) {
    const now = Date.now();
    for (const c of list) {
      const p = pending[c.id];
      if (p && (p.active === c.active || now > p.until)) delete pending[c.id];
    }
    controls = list;
    // Only redraw when something changed (frames arrive every second; a redraw mid-tap would lose the tap).
    const key = JSON.stringify(list.map((c) => [c.id, c.active])) + JSON.stringify(pending);
    if (key === lastControlsKey) return;
    lastControlsKey = key;
    renderControls();
  }
  let lastControlsKey = "";

  function switchRow(c, extraClass) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "sw" + (isOn(c) ? " on" : "") + (pending[c.id] ? " busy" : "") + (extraClass ? " " + extraClass : "");
    b.setAttribute("role", "switch");
    b.setAttribute("aria-checked", String(isOn(c)));
    const txt = document.createElement("span"); txt.className = "txt";
    const name = document.createElement("b"); name.textContent = c.label;
    txt.append(name);
    if (c.hint) { const h = document.createElement("small"); h.textContent = c.hint; txt.append(h); }
    const knob = document.createElement("span"); knob.className = "knob";
    b.append(txt, knob);
    b.addEventListener("click", () => run(c, !isOn(c)));
    return b;
  }

  function renderControls() {
    // Quick switch on the Live tab's overlay tile.
    const e = els.overlay;
    if (e) {
      e.ctl.textContent = "";
      const vis = controls.find((c) => c.id === "overlay.visible");
      if (vis) e.ctl.append(switchRow(vis, "quick"));
    }
    const host = $("controls");
    host.textContent = "";
    const groups = [];
    for (const c of controls) {
      let g = groups.find((x) => x.name === c.group);
      if (!g) groups.push(g = { name: c.group, items: [] });
      g.items.push(c);
    }
    for (const g of groups) {
      const box = document.createElement("div"); box.className = "group";
      const h = document.createElement("h3"); h.textContent = g.name; box.append(h);
      const choices = g.items.filter((c) => c.kind === "choice");
      for (const c of g.items.filter((c) => c.kind !== "choice")) box.append(switchRow(c));
      if (choices.length) {
        const seg = document.createElement("div"); seg.className = "seg";
        for (const c of choices) {
          const b = document.createElement("button");
          b.type = "button"; b.textContent = c.label;
          b.className = (isOn(c) ? "on" : "") + (pending[c.id] ? " busy" : "");
          b.setAttribute("aria-pressed", String(isOn(c)));
          b.addEventListener("click", () => { if (!isOn(c)) run(c, true, choices); });
          seg.append(b);
        }
        box.append(seg);
      }
      host.append(box);
    }
  }

  async function run(c, want, siblings) {
    if (pending[c.id]) return;
    if (opts.vibrate && navigator.vibrate) navigator.vibrate(10);
    pending[c.id] = { active: want, until: Date.now() + 5000 };
    if (siblings) for (const s of siblings) if (s.id !== c.id && isOn(s)) pending[s.id] = { active: false, until: Date.now() + 5000 };
    renderControls();
    try {
      const r = await api("POST", "/api/action", { id: c.id });
      const j = await r.json();
      if (!j.ok) {
        delete pending[c.id];
        if (siblings) for (const s of siblings) delete pending[s.id];
        toast(j.message || "That didn't work.");
      }
      if (j.actions) syncControls(j.actions); else renderControls();
    } catch {
      delete pending[c.id];
      if (siblings) for (const s of siblings) delete pending[s.id];
      renderControls();
      toast("The PC didn't respond.");
    }
  }

  // ---- tabs -------------------------------------------------------------------------------------------------------------
  function tab(name) {
    const live = name !== "controls";
    $("viewLive").hidden = !live; $("viewControls").hidden = live;
    $("tabLive").className = live ? "on" : ""; $("tabControls").className = live ? "" : "on";
    $("tabLive").setAttribute("aria-pressed", String(live)); $("tabControls").setAttribute("aria-pressed", String(!live));
    if (!live) $("editor").hidden = true;
    store.set("mt.tab", live ? "live" : "controls");
    window.scrollTo({ top: 0 });
  }
  $("tabLive").addEventListener("click", () => tab("live"));
  $("tabControls").addEventListener("click", () => tab("controls"));

  // ---- connection -------------------------------------------------------------------------------------------------------
  function api(method, path, body) {
    return fetch(path, {
      method, cache: "no-store",
      headers: { Authorization: "Bearer " + session, ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
  }

  function status(text, cls) { const c = $("conn"); c.className = "conn " + (cls || ""); c.querySelector("span").textContent = text; }

  function needPairing(text) {
    session = null;
    store.set("mt.session", null);
    $("dash").hidden = true; $("editor").hidden = true; $("tabbar").hidden = true; $("pair").hidden = false;
    if (text) $("pairText").textContent = text;
    status("Not paired", "down");
  }

  async function pair(code) {
    history.replaceState(null, "", "/");   // the code isn't kept in the address bar or history
    const name = /iPhone|iPad/.test(navigator.userAgent) ? "iPhone / iPad" : /Android/.test(navigator.userAgent) ? "Android device" : "Browser";
    try {
      const r = await fetch("/api/pair", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code, name }) });
      const j = await r.json();
      if (!r.ok || !j.session) { needPairing(j.error); return false; }
      session = j.session;
      store.set("mt.session", session);
      return true;
    } catch { needPairing("Couldn't reach Money Tweaks. Make sure this device is on the same network as the PC."); return false; }
  }

  let backoff = 1000;
  async function stream() {
    while (session) {
      try {
        status("Connecting…");
        const r = await api("GET", "/api/events");
        if (r.status === 401) { needPairing("This device isn't paired anymore. Scan a new QR code in Money Tweaks › Pocket Panel."); return; }
        if (!r.ok || !r.body) throw new Error("bad response");
        status("Live", "live");
        backoff = 1000;
        const reader = r.body.getReader(), dec = new TextDecoder();
        let buf = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += dec.decode(value, { stream: true });
          let i;
          while ((i = buf.indexOf("\n\n")) >= 0) {
            const chunk = buf.slice(0, i); buf = buf.slice(i + 2);
            const ev = /^event: (.*)$/m.exec(chunk)?.[1], data = /^data: (.*)$/m.exec(chunk)?.[1];
            if (ev === "frame" && data) apply(JSON.parse(data));
            if (ev === "revoked") { if (!unpairing) needPairing("This device was unpaired on the PC."); return; }
          }
        }
      } catch { /* fall through to reconnect */ }
      status("Reconnecting…", "down");
      await new Promise((res) => setTimeout(res, backoff));
      backoff = Math.min(backoff * 2, 15000);
    }
  }

  function toast(text) {
    const t = document.createElement("div"); t.className = "toast"; t.textContent = text;
    document.body.append(t); setTimeout(() => t.remove(), 3500);
  }

  // ---- layout editor ----------------------------------------------------------------------------------------------------
  function editor() {
    const list = $("editList");
    list.textContent = "";
    layout.forEach((l, i) => {
      const def = TILES.find((t) => t.id === l.id);
      const li = document.createElement("li"); li.className = l.on ? "" : "off";
      const name = document.createElement("span"); name.textContent = def.label;
      const mk = (txt, fn, title) => { const b = document.createElement("button"); b.type = "button"; b.textContent = txt; b.title = title; b.addEventListener("click", fn); return b; };
      li.append(name,
        mk("↑", () => { if (i > 0) { [layout[i - 1], layout[i]] = [layout[i], layout[i - 1]]; save(); } }, "Move up"),
        mk("↓", () => { if (i < layout.length - 1) { [layout[i + 1], layout[i]] = [layout[i], layout[i + 1]]; save(); } }, "Move down"),
        mk(l.size === "large" ? "L" : l.size === "small" ? "S" : "M", () => { l.size = l.size === "small" ? "normal" : l.size === "normal" ? "large" : "small"; save(); }, "Size"),
        mk(l.on ? "Hide" : "Show", () => { l.on = !l.on; save(); }, "Show or hide"));
      list.append(li);
    });
    $("optVibrate").checked = !!opts.vibrate;
    $("optDense").checked = !!opts.dense;
  }
  function save() { store.set("mt.layout", layout); store.set("mt.opts", opts); buildTiles(); editor(); }

  $("editBtn").addEventListener("click", () => { $("editor").hidden = false; editor(); $("editor").scrollIntoView({ behavior: "smooth" }); });
  $("doneBtn").addEventListener("click", () => { $("editor").hidden = true; });
  $("resetBtn").addEventListener("click", () => { layout = DEFAULT_LAYOUT.map((l) => ({ ...l })); save(); });
  $("optVibrate").addEventListener("change", (e) => { opts.vibrate = e.target.checked; save(); });
  $("optDense").addEventListener("change", (e) => { opts.dense = e.target.checked; save(); });
  $("unpairBtn").addEventListener("click", async () => {
    unpairing = true;
    try { await api("POST", "/api/unpair"); } catch { /* unpaired locally anyway */ }
    needPairing("Unpaired. Scan a new QR code in Money Tweaks › Pocket Panel to connect again.");
  });

  // ---- start ------------------------------------------------------------------------------------------------------------
  (async () => {
    const m = /[#&]pair=([A-Za-z0-9_-]{20,100})/.exec(location.hash);
    if (m && !(await pair(m[1]))) return;
    if (!session) { needPairing(); return; }
    try {
      const r = await api("GET", "/api/hello");
      if (r.status === 401) { needPairing("This device isn't paired anymore. Scan a new QR code in Money Tweaks › Pocket Panel."); return; }
      controls = (await r.json()).actions || [];
    } catch { /* stream() keeps retrying */ }
    $("pair").hidden = true; $("dash").hidden = false; $("tabbar").hidden = false;
    buildTiles();
    tab(store.get("mt.tab", "live"));
    requestAnimationFrame(frame);
    stream();
  })();
})();
