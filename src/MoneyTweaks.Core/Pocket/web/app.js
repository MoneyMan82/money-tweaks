// Money Tweaks Pocket Panel: a mobile control deck for this PC. Talks only to the Money Tweaks app that served this page.
"use strict";
(() => {
  const $ = (id) => document.getElementById(id);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* private mode: not remembered */ } },
  };
  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
  const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? null : v.toFixed(d));
  const opts = Object.assign({ vibrate: true, auto: true }, store.get("mt.opts", {}));
  const buzz = (p) => { if (opts.vibrate && navigator.vibrate) navigator.vibrate(p); };

  let session = store.get("mt.session", null);
  let controls = [];          // allowlisted controls with their current state (from the PC)
  const pending = {};         // id -> { active, until }: taps the PC hasn't confirmed yet
  let deck = null;            // dashboards shared by every paired device
  let games = [];             // Game Library games a page can open for
  let frame = null;           // latest telemetry
  let lastFrameAt = 0, lastMsgAt = 0;
  let curPage = store.get("mt.page", null);
  let editing = false, dragging = false;

  // ---- readings -------------------------------------------------------------------------------------------------------
  const METRICS = {
    fps: { label: "FPS", unit: "", digits: 0, get: (f) => f.fps?.fps, sub: (f) => (f.fps?.fps != null ? `1% low ${fmt(f.fps.low1) ?? "–"} · ${fmt(f.fps.frameMs, 1) ?? "–"} ms${f.fps.target ? " · " + f.fps.target : ""}` : f.fps?.note) },
    cpu: { label: "CPU", unit: "%", digits: 0, max: 100, get: (f) => f.cpu?.load, sub: (f) => f.cpu?.name || "Usage" },
    gpu: { label: "GPU", unit: "%", digits: 0, max: 100, get: (f) => f.gpu?.load, sub: (f) => f.gpu?.name || "Usage" },
    cputemp: { label: "CPU temp", unit: "°C", digits: 0, max: 110, get: (f) => f.cpu?.temp, sub: (f) => (f.cpu?.temp == null ? f.cpu?.tempNote : f.cpu?.tempSource || "") },
    gputemp: { label: "GPU temp", unit: "°C", digits: 0, max: 110, get: (f) => f.gpu?.temp, sub: (f) => (f.gpu?.temp == null ? f.gpu?.tempNote : f.gpu?.name || "") },
    ram: { label: "RAM", unit: "GB", digits: 1, get: (f) => f.ram?.usedGb, sub: (f) => (f.ram?.totalGb ? `of ${fmt(f.ram.totalGb, 0)} GB` : "") },
    vram: { label: "VRAM", unit: "GB", digits: 1, get: (f) => (f.gpu?.vramUsedMb != null ? f.gpu.vramUsedMb / 1024 : null), sub: (f) => (f.gpu?.vramTotalMb ? `of ${fmt(f.gpu.vramTotalMb / 1024, 0)} GB` : f.gpu?.vramNote || "") },
  };
  const TYPES = [
    { type: "fps", name: "FPS", desc: "Frames per second and 1% low while the overlay measures a game", size: "m" },
    { type: "frametime", name: "Frame-time graph", desc: "The overlay's real frame times, last ~2 seconds", size: "m" },
    { type: "cpu", name: "CPU usage", desc: "Load with a live graph", size: "s" },
    { type: "gpu", name: "GPU usage", desc: "Load of the GPU doing the most work", size: "s" },
    { type: "cputemp", name: "CPU temperature", desc: "With an optional warning threshold", size: "s" },
    { type: "gputemp", name: "GPU temperature", desc: "With an optional warning threshold", size: "s" },
    { type: "ram", name: "RAM", desc: "Memory in use", size: "s" },
    { type: "vram", name: "VRAM", desc: "Video memory in use", size: "s" },
    { type: "alerts", name: "Warnings", desc: "Money Tweaks' live warnings (heat, throttling, memory)", size: "m" },
    { type: "game", name: "Game", desc: "The game Money Tweaks detects right now", size: "s" },
    { type: "button", name: "Touch button", desc: "An allowed action: overlay, profiles, readings, benchmark…", size: "s" },
  ];
  const hist = {};             // metric -> recent values for sparklines
  const shown = {}, target = {};
  const alarm = {};            // widget id -> currently over its threshold

  function onFrame(f) {
    frame = f;
    lastFrameAt = Date.now();
    for (const k in METRICS) {
      const v = METRICS[k].get(f);
      const h = (hist[k] ||= []);
      h.push(v == null || Number.isNaN(v) ? null : v);
      if (h.length > 90) h.shift();
      target[k] = v == null || Number.isNaN(v) ? null : v;
    }
    alertsUi(f.alerts || []);
    if (f.controls) syncControls(f.controls);
    autoPage(f.game);
    updateWidgets();
  }

  // ---- pages and widgets ----------------------------------------------------------------------------------------------
  const page = () => deck?.pages.find((p) => p.id === curPage) || deck?.pages[0];
  const refs = {};             // widget id -> its live elements

  function renderPages() {
    const host = $("pages");
    host.textContent = "";
    for (const p of deck.pages) {
      const b = el("button", "chip" + (p.id === page()?.id ? " on" : ""), p.name);
      if (p.game) b.append(el("span", "dot"));
      b.type = "button";
      b.addEventListener("click", () => { if (curPage !== p.id) { curPage = p.id; manualPage = true; store.set("mt.page", curPage); renderDeck(); } });
      host.append(b);
    }
    host.querySelector(".chip.on")?.scrollIntoView({ inline: "nearest", block: "nearest" });
  }

  function renderDeck() {
    if (!deck) return;
    if (!page()) curPage = deck.pages[0].id;
    renderPages();
    $("editBtn").textContent = editing ? "Done" : "Edit";
    $("editbar").hidden = !editing;
    document.body.classList.toggle("editing", editing);
    const grid = $("grid");
    grid.textContent = "";
    for (const k in refs) delete refs[k];
    const p = page();
    if (!p.widgets.length) {
      const empty = el("div", "empty");
      empty.append(el("b", "", "This page is empty"), el("span", "", editing ? "Add widgets below." : "Tap Edit to add widgets."));
      grid.append(empty);
    }
    for (const w of p.widgets) grid.append(widget(w));
    updateWidgets();
  }

  function widget(w) {
    const card = el("div", `w size-${w.size} t-${w.type}`);
    card.dataset.id = w.id;
    const r = { card, w };
    if (METRICS[w.type]) {
      const m = METRICS[w.type];
      card.append(r.lbl = el("div", "lbl", m.label + (w.warn != null ? ` · warn ${w.warn}${m.unit}` : "")), r.val = el("div", "val", "…"), r.sub = el("div", "sub"), r.canvas = el("canvas"));
    } else if (w.type === "frametime") {
      card.append(el("div", "lbl", "Frame time"), r.val = el("div", "val", "…"), r.sub = el("div", "sub"), r.canvas = el("canvas"));
    } else if (w.type === "alerts") {
      card.append(el("div", "lbl", "Warnings"), r.list = el("div", "wlist"));
    } else if (w.type === "game") {
      card.append(el("div", "lbl", "Game"), r.val = el("div", "val gm", "…"), r.sub = el("div", "sub"));
    } else if (w.type === "button") {
      card.classList.add("btnw");
      card.append(r.lbl = el("div", "lbl"), r.val = el("div", "val bt"), r.sub = el("div", "sub"));
      card.addEventListener("click", () => { if (!editing) press(w); });
    }
    if (editing) card.append(editTools(w));
    refs[w.id] = r;
    return card;
  }

  function updateWidgets() {
    if (!frame) return;
    const stale = Date.now() - lastFrameAt > 4000;
    for (const id in refs) {
      const { w, card, sub, list, val, lbl } = refs[id];
      if (METRICS[w.type]) {
        const m = METRICS[w.type], v = target[w.type];
        sub.textContent = m.sub(frame) || "";
        card.classList.toggle("na", v == null);
        if (v == null) val.textContent = "not available";
        const over = v != null && w.warn != null && v >= w.warn;
        card.classList.toggle("alarm", over);
        if (over && !alarm[w.id] && !stale) { buzz([120, 60, 120]); toast(`${m.label} ${fmt(v, m.digits)}${m.unit} is at or above your ${w.warn}${m.unit} warning.`); }
        alarm[w.id] = over;
      } else if (w.type === "frametime") {
        const ft = frame.frametimes;
        card.classList.toggle("na", !ft || !ft.length);
        if (ft && ft.length) {
          const avg = ft.reduce((a, b) => a + b, 0) / ft.length, max = Math.max(...ft);
          val.textContent = ""; val.append(fmt(avg, 1)); val.append(el("small", "", "ms avg"));
          sub.textContent = `worst ${fmt(max, 1)} ms · ${ft.length} frames`;
        } else { val.textContent = "not available"; sub.textContent = frame.fps?.note || "Turn the overlay on to measure frame times."; }
      } else if (w.type === "alerts") {
        list.textContent = "";
        const a = frame.alerts || [];
        if (!a.length) list.append(el("div", "ok", "No warnings right now."));
        for (const x of a) { const d = el("div", "wa " + (x.level === "Critical" ? "crit" : "")); d.append(el("b", "", x.title), el("span", "", x.detail || "")); list.append(d); }
      } else if (w.type === "game") {
        val.textContent = frame.game ? frame.game.name : "No game";
        sub.textContent = frame.game ? (deck.pages.some((p) => p.game === frame.game.key) ? "Has its own page" : "Running now") : frame.profile ? `Profile: ${frame.profile}` : "Detected from your Game Library";
      } else if (w.type === "button") {
        const c = controls.find((x) => x.id === w.action);
        lbl.textContent = c ? c.group : "Button";
        val.textContent = w.label || (c ? c.label : "Not available");
        card.classList.toggle("disabled", !c);
        card.classList.toggle("on", !!c && c.kind !== "button" && isOn(c));
        card.classList.toggle("busy", !!pending[w.action]);
        sub.textContent = !c ? "This action isn't offered by the PC right now." : c.kind === "switch" ? (isOn(c) ? "On" : "Off") : c.kind === "choice" ? (isOn(c) ? "Active" : "Tap to switch") : c.hint || "Tap";
      }
    }
  }

  // Numbers glide to new values and graphs redraw smoothly, only while the page is visible.
  function tick() {
    if (!document.hidden) {
      for (const id in refs) {
        const { w, val, canvas } = refs[id];
        if (METRICS[w.type]) {
          const t = target[w.type];
          if (t != null) {
            const cur = shown[id] ?? t;
            const next = Math.abs(t - cur) < 0.05 ? t : cur + (t - cur) * 0.2;
            shown[id] = next;
            val.textContent = ""; val.append(next.toFixed(METRICS[w.type].digits));
            if (METRICS[w.type].unit) val.append(el("small", "", METRICS[w.type].unit));
          } else shown[id] = null;
          draw(canvas, hist[w.type] || [], METRICS[w.type].max, refs[id].card.classList.contains("alarm"), 90);
        } else if (w.type === "frametime") draw(canvas, frame?.frametimes || [], null, false, 120, true);
      }
    }
    requestAnimationFrame(tick);
  }

  function draw(c, data, fixedMax, hot, slots, bars) {
    const dpr = window.devicePixelRatio || 1, w = c.clientWidth, h = c.clientHeight;
    if (!w || !h) return;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const g = c.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const vals = data.filter((v) => v != null);
    if (vals.length < 2) return;
    const max = fixedMax ?? (Math.max(...vals) * 1.15 || 1);
    const min = fixedMax || bars ? 0 : Math.min(...vals) * 0.85;
    const step = w / (slots - 1), off = slots - data.length;
    const y = (v) => h - 2 - ((v - min) / (max - min || 1)) * (h - 4);
    const css = getComputedStyle(document.documentElement);
    const col = css.getPropertyValue(hot ? "--danger" : "--accent").trim(), col2 = css.getPropertyValue("--violet").trim();
    if (bars) {
      const bw = Math.max(1, step * 0.7);
      data.forEach((v, i) => { if (v == null) return; g.fillStyle = v > (vals.reduce((a, b) => a + b, 0) / vals.length) * 1.8 ? css.getPropertyValue("--gold").trim() : col; g.fillRect((off + i) * step, y(v), bw, h - y(v)); });
      return;
    }
    const grad = g.createLinearGradient(0, 0, w, 0); grad.addColorStop(0, col); grad.addColorStop(1, hot ? col : col2);
    g.beginPath();
    let started = false, lastX = 0;
    data.forEach((v, i) => { if (v == null) { started = false; return; } const x = (off + i) * step; if (!started) { g.moveTo(x, y(v)); started = true; } else g.lineTo(x, y(v)); lastX = x; });
    g.strokeStyle = grad; g.lineWidth = 2; g.lineJoin = "round"; g.stroke();
    g.lineTo(lastX, h); g.lineTo(off * step, h); g.closePath();
    const fill = g.createLinearGradient(0, 0, 0, h); fill.addColorStop(0, col + "44"); fill.addColorStop(1, col + "00");
    g.fillStyle = fill; g.fill();
  }

  // ---- automatic pages for detected games -----------------------------------------------------------------------------
  let lastGame = null, autoFrom = null, manualPage = false;
  function autoPage(game) {
    const key = game?.key || null;
    if (key === lastGame) return;
    lastGame = key;
    const note = $("autonote");
    if (!opts.auto || !deck) { note.hidden = true; return; }
    const p = key && deck.pages.find((x) => x.game === key);
    if (p && curPage !== p.id) {
      autoFrom = curPage; manualPage = false; curPage = p.id; renderDeck();
      note.textContent = `Opened “${p.name}” for ${game.name}.`; note.hidden = false; buzz(20);
    } else if (!key && autoFrom && !manualPage) {
      curPage = autoFrom; autoFrom = null; renderDeck();
      note.textContent = "Game closed: back to your previous page."; note.hidden = false;
    } else note.hidden = true;
  }

  // ---- editing: drag, resize, add, settings ---------------------------------------------------------------------------
  function editTools(w) {
    const t = el("div", "tools");
    const grip = el("button", "grip", "⠿"); grip.type = "button"; grip.title = "Drag to move";
    grip.addEventListener("pointerdown", (e) => startDrag(e, w));
    const size = el("button", "", { s: "S", m: "M", l: "L" }[w.size]); size.type = "button"; size.title = "Size";
    size.addEventListener("click", () => { w.size = w.size === "s" ? "m" : w.size === "m" ? "l" : "s"; buzz(10); changed(); renderDeck(); });
    t.append(grip, size);
    if (w.type === "button" || METRICS[w.type]) {
      const set = el("button", "", "⚙"); set.type = "button"; set.title = "Settings";
      set.addEventListener("click", () => widgetSheet(w)); t.append(set);
    }
    const del = el("button", "del", "×"); del.type = "button"; del.title = "Remove";
    del.addEventListener("click", () => { const p = page(); p.widgets = p.widgets.filter((x) => x !== w); buzz(15); changed(); renderDeck(); });
    t.append(del);
    return t;
  }

  // Touch drag by the grip: the card follows the finger and the others make room as it passes over them.
  function startDrag(e, w) {
    e.preventDefault();
    const card = refs[w.id].card, grid = $("grid");
    const r0 = card.getBoundingClientRect();
    const dx = e.clientX - r0.left, dy = e.clientY - r0.top;
    let tx = 0, ty = 0;
    dragging = true;
    card.classList.add("lift");
    try { card.setPointerCapture(e.pointerId); } catch { /* scripted or already released pointer: moves still arrive */ }
    buzz(15);
    const move = (ev) => {
      const r = card.getBoundingClientRect();
      const baseX = r.left - tx, baseY = r.top - ty;
      tx = ev.clientX - dx - baseX; ty = ev.clientY - dy - baseY;
      card.style.transform = `translate(${tx}px, ${ty}px) scale(1.03)`;
      card.style.pointerEvents = "none";
      const under = document.elementFromPoint(ev.clientX, ev.clientY)?.closest(".w");
      card.style.pointerEvents = "";
      if (under && under !== card) {
        const ur = under.getBoundingClientRect();
        const after = ev.clientY > ur.top + ur.height / 2 || (ev.clientY > ur.top && ev.clientX > ur.left + ur.width / 2);
        const ref = after ? under.nextSibling : under;
        if (ref !== card && ref !== card.nextSibling) { grid.insertBefore(card, ref); buzz(5); }
      }
      if (ev.clientY < 70) window.scrollBy(0, -12); else if (ev.clientY > innerHeight - 90) window.scrollBy(0, 12);
    };
    const end = () => {
      card.removeEventListener("pointermove", move); card.removeEventListener("pointerup", end); card.removeEventListener("pointercancel", end);
      card.classList.remove("lift"); card.style.transform = "";
      dragging = false;
      const order = [...grid.querySelectorAll(".w")].map((x) => x.dataset.id);
      const p = page();
      p.widgets.sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
      changed();
      renderDeck();
    };
    card.addEventListener("pointermove", move);
    card.addEventListener("pointerup", end);
    card.addEventListener("pointercancel", end);
  }

  const newId = () => Math.random().toString(36).slice(2, 10);

  function sheet(title, build) {
    const box = $("sheetbox");
    box.textContent = "";
    const head = el("div", "shead"); head.append(el("h2", "", title));
    const close = el("button", "ghost", "Close"); close.type = "button"; close.addEventListener("click", closeSheet);
    head.append(close); box.append(head);
    build(box);
    $("sheet").hidden = false;
  }
  function closeSheet() { $("sheet").hidden = true; }
  $("sheet").addEventListener("click", (e) => { if (e.target === $("sheet")) closeSheet(); });

  function actionPicker(box, onPick, current) {
    let group = null;
    for (const c of controls) {
      if (c.group !== group) { group = c.group; box.append(el("div", "sgroup", group)); }
      const b = el("button", "pick" + (c.id === current ? " on" : ""));
      b.type = "button";
      b.append(el("b", "", c.label), el("span", "", c.kind === "switch" ? "Switch" : c.kind === "choice" ? "Choose" : c.hint || "Action"));
      b.addEventListener("click", () => onPick(c));
      box.append(b);
    }
  }

  $("addWidget").addEventListener("click", () => sheet("Add a widget", (box) => {
    for (const t of TYPES) {
      const b = el("button", "pick"); b.type = "button";
      b.append(el("b", "", t.name), el("span", "", t.desc));
      b.addEventListener("click", () => {
        if (t.type === "button") { sheet("Button action", (b2) => actionPicker(b2, (c) => { addWidget({ type: "button", size: "s", action: c.id }); })); return; }
        addWidget({ type: t.type, size: t.size, warn: t.type === "gputemp" ? 85 : t.type === "cputemp" ? 90 : undefined });
      });
      box.append(b);
    }
  }));
  function addWidget(w) {
    const p = page();
    if (p.widgets.length >= 40) { toast("A page can hold up to 40 widgets."); return; }
    p.widgets.push(Object.assign({ id: newId() }, w));
    closeSheet(); buzz(15); changed(); renderDeck();
    $("grid").lastElementChild?.scrollIntoView({ behavior: "smooth", block: "center" });
  }

  function widgetSheet(w) {
    sheet(w.type === "button" ? "Button" : METRICS[w.type].label, (box) => {
      if (w.type === "button") {
        const lab = el("label", "field", "Caption (optional)");
        const inp = el("input"); inp.maxLength = 24; inp.value = w.label || ""; inp.placeholder = controls.find((c) => c.id === w.action)?.label || "";
        inp.addEventListener("change", () => { w.label = inp.value.trim() || undefined; changed(); renderDeck(); });
        lab.append(inp); box.append(lab, el("div", "sgroup", "Action"));
        actionPicker(box, (c) => { w.action = c.id; changed(); renderDeck(); closeSheet(); }, w.action);
      } else {
        const m = METRICS[w.type];
        const lab = el("label", "field", `Warn at or above (${m.unit || "value"})`);
        const inp = el("input"); inp.type = "number"; inp.inputMode = "decimal"; inp.min = "0"; inp.max = "1000"; inp.value = w.warn ?? "";
        inp.placeholder = "No warning";
        inp.addEventListener("change", () => { const v = parseFloat(inp.value); w.warn = Number.isFinite(v) ? v : undefined; changed(); renderDeck(); });
        lab.append(inp);
        box.append(lab, el("p", "small", w.type === "fps" ? "Note: for FPS the warning triggers at or above the number." : "The card turns red and the phone vibrates when the reading reaches it. Readings come from the PC; nothing is estimated."));
      }
    });
  }

  $("pageSettings").addEventListener("click", () => {
    const p = page();
    sheet("Page settings", (box) => {
      const lab = el("label", "field", "Name");
      const inp = el("input"); inp.maxLength = 32; inp.value = p.name;
      inp.addEventListener("change", () => { const v = inp.value.trim(); if (v) { p.name = v; changed(); renderPages(); } });
      lab.append(inp); box.append(lab);
      const glab = el("label", "field", "Open automatically while this game runs");
      const sel = el("select");
      sel.append(new Option("No game", ""));
      for (const g of games) sel.append(new Option(g.name, g.key, false, g.key === p.game));
      if (frame?.game && !games.some((g) => g.key === frame.game.key)) sel.append(new Option(frame.game.name, frame.game.key, false, frame.game.key === p.game));
      sel.addEventListener("change", () => {
        const g = sel.value ? (games.find((x) => x.key === sel.value) || frame?.game) : null;
        for (const other of deck.pages) if (g && other !== p && other.game === g.key) { other.game = null; other.gameName = null; }   // one page per game
        p.game = g ? g.key : null; p.gameName = g ? g.name : null; changed(); renderPages();
      });
      glab.append(sel); box.append(glab);
      const i = deck.pages.indexOf(p);
      const row = el("div", "bar");
      const left = el("button", "", "← Move left"), right = el("button", "", "Move right →"), del = el("button", "del", "Delete page");
      [left, right, del].forEach((b) => (b.type = "button"));
      left.disabled = i === 0; right.disabled = i === deck.pages.length - 1; del.disabled = deck.pages.length === 1;
      left.addEventListener("click", () => { deck.pages.splice(i - 1, 0, deck.pages.splice(i, 1)[0]); changed(); closeSheet(); renderDeck(); });
      right.addEventListener("click", () => { deck.pages.splice(i + 1, 0, deck.pages.splice(i, 1)[0]); changed(); closeSheet(); renderDeck(); });
      let armed = false;
      del.addEventListener("click", () => {
        if (!armed) { armed = true; del.textContent = "Tap again to delete"; return; }
        deck.pages.splice(i, 1); curPage = deck.pages[Math.max(0, i - 1)].id; changed(); closeSheet(); renderDeck();
      });
      row.append(left, right, del); box.append(row);
    });
  });

  $("addPage").addEventListener("click", () => {
    if (deck.pages.length >= 12) { toast("You can have up to 12 pages."); return; }
    const p = { id: newId(), name: `Page ${deck.pages.length + 1}`, game: null, gameName: null, widgets: [] };
    deck.pages.push(p); curPage = p.id; store.set("mt.page", curPage); changed(); renderDeck();
  });
  $("editBtn").addEventListener("click", () => { editing = !editing; buzz(10); renderDeck(); });

  // ---- saving and live sync -------------------------------------------------------------------------------------------
  let saveTimer = null, saving = false, dirty = false;
  function changed() { dirty = true; clearTimeout(saveTimer); saveTimer = setTimeout(save, 600); }
  async function save() {
    if (saving) { saveTimer = setTimeout(save, 300); return; }
    saving = true; dirty = false;
    try {
      const r = await api("PUT", "/api/deck", { baseVersion: deck.version, pages: deck.pages });
      const j = await r.json();
      if (j.ok) {
        if (dirty) deck.version = j.deck.version;   // more edits came in meanwhile: keep them, they save next
        else { deck = j.deck; renderDeck(); }
      } else if (j.deck) {
        deck = j.deck; dirty = false; renderDeck();   // changed elsewhere first: show the current layout
        toast(j.message || "The layout wasn't saved.");
      } else toast(j.message || "The layout wasn't saved.");
    } catch { dirty = true; toast("Not saved yet: the PC didn't respond. It will retry."); saveTimer = setTimeout(save, 3000); }
    finally { saving = false; }
  }
  function applyDeck(d, force) {
    if (deck && d.version === deck.version && !force) return;   // already showing it (e.g. our own save echoed back)
    if (!force && (dragging || dirty || saving) && deck && d.version !== deck.version) return;   // our own save is on its way
    deck = d;
    renderDeck();
  }

  // ---- controls (only the actions the PC allows) ----------------------------------------------------------------------
  const isOn = (c) => (pending[c.id] ? pending[c.id].active : c.active);
  let lastControlsKey = "";
  function syncControls(list) {
    const now = Date.now();
    for (const c of list) { const p = pending[c.id]; if (p && (p.active === c.active || now > p.until)) delete pending[c.id]; }
    controls = list;
    const key = JSON.stringify(list.map((c) => [c.id, c.active, c.label])) + JSON.stringify(pending);
    if (key === lastControlsKey) return;
    lastControlsKey = key;
    renderControls();
    updateWidgets();
  }

  function switchRow(c) {
    const b = el("button", "sw" + (isOn(c) ? " on" : "") + (pending[c.id] ? " busy" : ""));
    b.type = "button"; b.setAttribute("role", "switch"); b.setAttribute("aria-checked", String(isOn(c)));
    const txt = el("span", "txt"); txt.append(el("b", "", c.label)); if (c.hint) txt.append(el("small", "", c.hint));
    b.append(txt, el("span", "knob"));
    b.addEventListener("click", () => run(c, !isOn(c)));
    return b;
  }

  function renderControls() {
    const host = $("controls");
    host.textContent = "";
    const groups = [];
    for (const c of controls) { let g = groups.find((x) => x.name === c.group); if (!g) groups.push((g = { name: c.group, items: [] })); g.items.push(c); }
    for (const g of groups) {
      const box = el("div", "group"); box.append(el("h3", "", g.name));
      const choices = g.items.filter((c) => c.kind === "choice");
      for (const c of g.items.filter((c) => c.kind === "switch")) box.append(switchRow(c));
      for (const c of g.items.filter((c) => c.kind === "button")) {
        const b = el("button", "act"); b.type = "button";
        const txt = el("span", "txt"); txt.append(el("b", "", c.label)); if (c.hint) txt.append(el("small", "", c.hint));
        b.append(txt, el("span", "go", "›")); b.addEventListener("click", () => run(c, true)); box.append(b);
      }
      if (choices.length) {
        const seg = el("div", "seg");
        for (const c of choices) {
          const b = el("button", (isOn(c) ? "on" : "") + (pending[c.id] ? " busy" : ""), c.label);
          b.type = "button"; b.setAttribute("aria-pressed", String(isOn(c)));
          b.addEventListener("click", () => { if (!isOn(c)) run(c, true, choices); });
          seg.append(b);
        }
        box.append(seg);
      }
      host.append(box);
    }
  }

  function press(w) {
    const c = controls.find((x) => x.id === w.action);
    if (!c) { toast("This action isn't offered by the PC right now."); return; }
    if (c.kind === "choice") { if (!isOn(c)) run(c, true, controls.filter((x) => x.group === c.group && x.kind === "choice")); }
    else run(c, c.kind === "switch" ? !isOn(c) : true);
  }

  async function run(c, want, siblings) {
    if (pending[c.id]) return;
    buzz(10);
    if (c.kind !== "button") {
      pending[c.id] = { active: want, until: Date.now() + 6000 };
      if (siblings) for (const s of siblings) if (s.id !== c.id && isOn(s)) pending[s.id] = { active: false, until: Date.now() + 6000 };
      lastControlsKey = ""; renderControls(); updateWidgets();
    }
    try {
      const r = await api("POST", "/api/action", { id: c.id });
      const j = await r.json();
      if (!j.ok) { delete pending[c.id]; if (siblings) for (const s of siblings) delete pending[s.id]; }
      if (j.message && (!j.ok || c.kind === "button" || c.group === "Gaming profile")) toast(j.message);
      lastControlsKey = "";
      if (j.actions) syncControls(j.actions); else renderControls();
    } catch {
      delete pending[c.id]; if (siblings) for (const s of siblings) delete pending[s.id];
      lastControlsKey = ""; renderControls(); updateWidgets(); toast("The PC didn't respond.");
    }
  }

  // ---- alerts ---------------------------------------------------------------------------------------------------------
  const seenAlerts = new Set();
  function alertsUi(list) {
    const host = $("alerts");
    host.textContent = "";
    for (const a of list.filter((x) => x.level === "Critical")) {
      const d = el("div", "alert critical"); d.append(el("b", "", a.title), document.createTextNode(a.detail || ""));
      host.append(d);
    }
    for (const a of list) if (!seenAlerts.has(a.key)) { seenAlerts.add(a.key); if (a.level === "Critical") buzz([200, 100, 200]); }
    for (const k of [...seenAlerts]) if (!list.some((a) => a.key === k)) seenAlerts.delete(k);
  }

  // ---- tabs -----------------------------------------------------------------------------------------------------------
  function tab(name) {
    const deckTab = name !== "controls";
    $("viewDeck").hidden = !deckTab; $("viewControls").hidden = deckTab;
    $("tabDeck").className = deckTab ? "on" : ""; $("tabControls").className = deckTab ? "" : "on";
    $("tabDeck").setAttribute("aria-pressed", String(deckTab)); $("tabControls").setAttribute("aria-pressed", String(!deckTab));
    if (!deckTab && editing) { editing = false; renderDeck(); }
    store.set("mt.tab", deckTab ? "deck" : "controls");
    window.scrollTo({ top: 0 });
  }
  $("tabDeck").addEventListener("click", () => tab("deck"));
  $("tabControls").addEventListener("click", () => tab("controls"));

  // ---- connection: pairing, live stream, reconnecting -----------------------------------------------------------------
  function api(method, path, body, signal) {
    return fetch(path, {
      method, cache: "no-store", signal,
      headers: { Authorization: "Bearer " + session, ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
  }

  function status(text, cls) { const c = $("conn"); c.className = "conn " + (cls || ""); c.querySelector("span").textContent = text; }

  function needPairing(text) {
    session = null; store.set("mt.session", null);
    $("dash").hidden = true; $("tabbar").hidden = true; $("pair").hidden = false; closeSheet();
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
      session = j.session; store.set("mt.session", session);
      return true;
    } catch { needPairing("Couldn't reach Money Tweaks. Make sure this device is on the same network as the PC."); return false; }
  }

  let unpairing = false, abort = null, backoff = 1000;
  // A silent connection (sleeping phone, Wi-Fi switch) is noticed within 20 s and replaced; coming back to the page or
  // the network reconnects at once. Readings older than 4 s are greyed out, never shown as live.
  setInterval(() => {
    const now = Date.now();
    const stale = lastFrameAt && now - lastFrameAt > 4000;
    document.body.classList.toggle("isstale", !!stale);
    $("stale").hidden = !stale;
    if (stale) $("stale").textContent = `Last update ${Math.round((now - lastFrameAt) / 1000)} s ago · reconnecting…`;
    if (abort && lastMsgAt && now - lastMsgAt > 20000) abort.abort();
  }, 1000);
  document.addEventListener("visibilitychange", () => { if (!document.hidden && abort && Date.now() - lastMsgAt > 5000) abort.abort(); });
  window.addEventListener("online", () => abort?.abort());

  async function refreshDeck() {
    try { const r = await api("GET", "/api/deck"); if (r.ok) applyDeck(await r.json(), true); } catch { /* the stream retries */ }
  }

  async function stream() {
    while (session) {
      abort = new AbortController();
      try {
        status("Connecting…");
        const r = await api("GET", "/api/events", null, abort.signal);
        if (r.status === 401) { needPairing("This device isn't paired anymore. Scan a new QR code in Money Tweaks › Pocket Panel."); return; }
        if (!r.ok || !r.body) throw new Error("bad response");
        status("Live", "live");
        backoff = 1000; lastMsgAt = Date.now();
        if (!dirty) refreshDeck();   // catch up on layout changes made while disconnected
        const reader = r.body.getReader(), dec = new TextDecoder();
        let buf = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          lastMsgAt = Date.now();
          buf += dec.decode(value, { stream: true });
          let i;
          while ((i = buf.indexOf("\n\n")) >= 0) {
            const chunk = buf.slice(0, i); buf = buf.slice(i + 2);
            const ev = /^event: (.*)$/m.exec(chunk)?.[1], data = /^data: (.*)$/m.exec(chunk)?.[1];
            if (ev === "frame" && data) onFrame(JSON.parse(data));
            else if (ev === "deck" && data) applyDeck(JSON.parse(data), false);
            else if (ev === "revoked") { if (!unpairing) needPairing("This device was unpaired on the PC."); return; }
          }
        }
      } catch { /* aborted or dropped: reconnect below */ }
      if (!session) return;
      status("Reconnecting…", "down");
      await new Promise((res) => setTimeout(res, backoff));
      backoff = Math.min(backoff * 2, 10000);
    }
  }

  function toast(text) {
    document.querySelectorAll(".toast").forEach((t) => t.remove());
    const t = el("div", "toast", text);
    document.body.append(t); setTimeout(() => t.remove(), 3800);
  }

  $("optVibrate").checked = !!opts.vibrate; $("optAuto").checked = !!opts.auto;
  $("optVibrate").addEventListener("change", (e) => { opts.vibrate = e.target.checked; store.set("mt.opts", opts); });
  $("optAuto").addEventListener("change", (e) => { opts.auto = e.target.checked; store.set("mt.opts", opts); lastGame = undefined; });
  $("unpairBtn").addEventListener("click", async () => {
    unpairing = true;
    try { await api("POST", "/api/unpair"); } catch { /* unpaired locally anyway */ }
    needPairing("Unpaired. Scan a new QR code in Money Tweaks › Pocket Panel to connect again.");
  });

  // ---- start ----------------------------------------------------------------------------------------------------------
  (async () => {
    const m = /[#&]pair=([A-Za-z0-9_-]{20,100})/.exec(location.hash);
    if (m && !(await pair(m[1]))) return;
    if (!session) { needPairing(); return; }
    try {
      const r = await api("GET", "/api/hello");
      if (r.status === 401) { needPairing("This device isn't paired anymore. Scan a new QR code in Money Tweaks › Pocket Panel."); return; }
      const j = await r.json();
      controls = j.actions || []; deck = j.deck; games = j.games || [];
    } catch { /* stream() keeps retrying */ }
    $("pair").hidden = true; $("dash").hidden = false; $("tabbar").hidden = false;
    if (deck) renderDeck();
    renderControls();
    tab(store.get("mt.tab", "deck"));
    requestAnimationFrame(tick);
    stream();
  })();
})();
