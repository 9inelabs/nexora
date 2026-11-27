namespace Nexora.Audio.Remote;

/// <summary>The remote's one web page: plain HTML and script, nothing to install on the phone.</summary>
public static class RemotePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<meta name="theme-color" content="#16171a">
<title>NEX-Audio remote</title>
<style>
:root { --bg:#16171a; --s1:#1e2024; --s2:#262930; --s3:#30343c; --line:#353a43; --t1:#eceef2; --t2:#a4aab5; --t3:#6f7682; --gold:#d1a54b; --red:#e0574f; --green:#4fbf7a; --amber:#e4b54a; }
* { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
html,body { margin:0; background:var(--bg); color:var(--t1); font:15px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
header { position:sticky; top:0; z-index:5; background:var(--bg); border-bottom:1px solid var(--line); padding:12px 16px; display:flex; align-items:center; gap:12px; }
header .brand { font-weight:800; letter-spacing:.06em; font-size:13px; color:var(--gold); }
header .title { font-weight:700; font-size:17px; flex:1; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
header button { flex:none; }
main { padding:14px 16px 40px; max-width:900px; margin:0 auto; }
h2 { font-size:12px; letter-spacing:.08em; color:var(--t3); font-weight:700; margin:18px 0 8px; text-transform:uppercase; }
.card { background:var(--s1); border:1px solid var(--line); border-radius:12px; padding:14px; }
.pick { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:10px; }
.pick button { height:64px; font-size:16px; font-weight:700; }
button { background:var(--s2); color:var(--t1); border:1px solid var(--line); border-radius:10px; padding:8px 14px; font:inherit; font-weight:600; cursor:pointer; }
button:active { background:var(--s3); }
button.gold { background:var(--gold); color:#1b1406; border-color:var(--gold); }
button.small { padding:6px 10px; font-size:13px; }
input[type=password],input[type=tel] { background:var(--s2); border:1px solid var(--line); color:var(--t1); border-radius:10px; padding:10px 12px; font-size:20px; letter-spacing:.3em; width:150px; text-align:center; }
.row { display:grid; grid-template-columns:minmax(84px,130px) 1fr 54px auto; align-items:center; gap:10px; padding:10px 0; border-bottom:1px solid var(--line); }
.row:last-child { border-bottom:none; }
.row .name { font-weight:650; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; display:flex; align-items:center; gap:8px; }
.row .name i { width:4px; height:22px; border-radius:2px; background:var(--t3); flex:none; }
.row .db { font:600 13px ui-monospace,Consolas,monospace; color:var(--t2); text-align:right; }
.row .btns { display:flex; gap:6px; }
.row .btns button { min-width:52px; padding:9px 8px; font-size:13px; }
.row button.on { background:var(--green); border-color:var(--green); color:#07180e; }
.row button.off { background:var(--red); border-color:var(--red); color:#200605; }
.row button.solo { background:var(--amber); border-color:var(--amber); color:#241a04; }
.track { position:relative; }
.meter { position:absolute; left:0; right:0; bottom:-5px; height:3px; border-radius:2px; background:var(--s3); overflow:hidden; }
.meter b { display:block; height:100%; width:0; background:var(--green); transition:width .08s linear; }
input[type=range] { -webkit-appearance:none; appearance:none; width:100%; height:38px; background:transparent; margin:0; touch-action:none; }
input[type=range]::-webkit-slider-runnable-track { height:8px; background:var(--s3); border-radius:4px; }
input[type=range]::-moz-range-track { height:8px; background:var(--s3); border-radius:4px; }
input[type=range]::-webkit-slider-thumb { -webkit-appearance:none; width:30px; height:30px; margin-top:-11px; border-radius:8px; background:var(--t1); border:3px solid var(--gold); }
input[type=range]::-moz-range-thumb { width:26px; height:26px; border-radius:8px; background:var(--t1); border:3px solid var(--gold); }
.master { border-color:var(--gold); }
.tabs { display:flex; gap:6px; flex-wrap:wrap; margin-bottom:10px; }
.tabs button.sel { border-color:var(--gold); color:var(--gold); }
.note { color:var(--t2); font-size:13px; }
.err { color:var(--red); font-weight:600; min-height:20px; margin-top:8px; }
.bar { display:flex; gap:10px; align-items:center; flex-wrap:wrap; }
.status { position:fixed; left:0; right:0; bottom:0; background:var(--red); color:#fff; text-align:center; padding:8px; font-weight:700; display:none; }
@media (max-width:520px) { .row { grid-template-columns:1fr 54px auto; } .row .name { grid-column:1 / -1; } }
</style>
</head>
<body>
<header><span class="brand">NEX-AUDIO</span><span class="title" id="title">Remote</span><button class="small" id="back" hidden>Change</button></header>
<main id="app"></main>
<div class="status" id="status">Not connected to NEX-Audio. Trying again…</div>
<script>
"use strict";
const app = document.getElementById("app"), title = document.getElementById("title"), back = document.getElementById("back"), status = document.getElementById("status");
let ws, hello = null, state = null, filter = "channel", busy = {}, timers = {};
const FLOOR = -90;
// The same fader law as the X32 and NEX-Audio's own faders.
function toPos(db) { if (db >= 10) return 1; if (db >= -10) return (db + 30) / 40; if (db >= -30) return (db + 50) / 80; if (db >= -60) return (db + 70) / 160; if (db > -90) return (db + 90) / 480; return 0; }
function toDb(p) { if (p >= .5) return p * 40 - 30; if (p >= .25) return p * 80 - 50; if (p >= .0625) return p * 160 - 70; if (p > 0) return p * 480 - 90; return FLOOR; }
function dbText(db) { return db <= FLOOR + .5 ? "−∞" : (db > 0 ? "+" : "") + db.toFixed(1); }
function el(tag, attrs, ...kids) { const e = document.createElement(tag); for (const k in attrs || {}) { if (k === "on") for (const ev in attrs.on) e.addEventListener(ev, attrs.on[ev]); else if (k === "text") e.textContent = attrs[k]; else e.setAttribute(k, attrs[k]); } kids.forEach(c => e.append(c)); return e; }
function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }

function connect() {
  ws = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws");
  ws.onopen = () => { status.style.display = "none"; const saved = localStorage.getItem("nexaudio.mix"); if (saved) send({ t: "pick", mix: saved }); };
  ws.onclose = () => { status.style.display = "block"; setTimeout(connect, 1500); };
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.t === "hello") { hello = m; if (m.role === "none") { state = null; start(); } }
    else if (m.t === "state") { const fresh = !state || state.role !== m.role || state.mix !== m.mix || state.strips.length !== m.strips.length; state = m; fresh ? mixer() : update(); }
    else if (m.t === "meters") meters(m.v);
    else if (m.t === "error") { const x = document.getElementById("err"); if (x) x.textContent = m.text; }
  };
}

function start() {
  title.textContent = "Remote"; back.hidden = true; app.innerHTML = "";
  if (hello.mixes.length) {
    app.append(el("h2", { text: "Your monitor mix" }));
    const grid = el("div", { class: "pick" });
    hello.mixes.forEach(x => grid.append(el("button", { text: x.name, on: { click: () => { localStorage.setItem("nexaudio.mix", x.id); send({ t: "pick", mix: x.id }); } } })));
    app.append(grid, el("p", { class: "note", text: "Pick the wedge or in-ear mix you are on. You can turn up what you need in it. Nothing you do here changes what the room hears." }));
  } else app.append(el("p", { class: "note", text: "The engineer has not put any monitor mixes on the remote yet (NEX-Audio, Remote tab)." }));
  if (hello.engineer) {
    app.append(el("h2", { text: "Engineer" }));
    const pin = el("input", { type: "tel", inputmode: "numeric", maxlength: "8", placeholder: "PIN", autocomplete: "off" });
    pin.addEventListener("keydown", e => { if (e.key === "Enter") go(); });
    const go = () => { localStorage.removeItem("nexaudio.mix"); send({ t: "pin", pin: pin.value }); pin.value = ""; };
    app.append(el("div", { class: "card" }, el("div", { class: "bar" }, pin, el("button", { class: "gold", text: "Open the whole desk", on: { click: go } })), el("div", { class: "err", id: "err" })));
  }
}

back.onclick = () => { localStorage.removeItem("nexaudio.mix"); send({ t: "leave" }); };

function mixer() {
  app.innerHTML = ""; back.hidden = false; busy = {};
  const eng = state.role === "engineer";
  title.textContent = eng ? "Whole desk" : state.mixName;
  if (!eng) {
    app.append(el("div", { class: "card master" }, row({ id: state.mix, name: "My mix overall", level: state.master, on: true, master: true })));
    app.append(el("h2", { text: "What is in my mix" }));
  } else {
    const kinds = [["channel", "Channels"], ["fx", "Effects"], ["group", "Groups"], ["mix", "Mixes"], ["matrix", "Matrix"], ["main", "Main"]].filter(k => state.strips.some(s => s.kind === k[0] || k[0] === "main" && ["main", "centre", "stream"].includes(s.kind)));
    const tabs = el("div", { class: "tabs" });
    kinds.forEach(k => tabs.append(el("button", { class: filter === k[0] ? "sel" : "", text: k[1], on: { click: () => { filter = k[0]; mixer(); } } })));
    const bar = el("div", { class: "bar", style: "margin-bottom:10px" });
    if (state.next) bar.append(el("button", { class: "gold", text: "GO  " + state.next, on: { click: () => send({ t: "go" }) } }));
    app.append(bar, tabs);
  }
  const list = el("div", { class: "card" });
  state.strips.filter(s => !eng || s.kind === filter || filter === "main" && ["main", "centre", "stream"].includes(s.kind)).forEach(s => list.append(row(s)));
  app.append(list, el("div", { class: "err", id: "err" }));
}

function row(s) {
  const eng = state.role === "engineer";
  const path = s.master || eng ? s.id + ".fader" : s.id + ".send." + state.mix + ".level";
  const range = el("input", { type: "range", min: "0", max: "1000", step: "1" });
  const db = el("span", { class: "db" }), meter = el("b");
  range.value = Math.round(toPos(s.level) * 1000); db.textContent = dbText(s.level);
  const hold = () => { busy[path] = Date.now() + 600; };
  range.addEventListener("pointerdown", hold);
  range.addEventListener("input", () => {
    hold(); const v = Math.round(toDb(range.value / 1000) * 10) / 10; db.textContent = dbText(v);
    clearTimeout(timers[path]); timers[path] = setTimeout(() => send({ t: "set", path, value: v }), 40);
  });
  const btns = el("div", { class: "btns" });
  if (!s.master) {
    const onPath = eng ? s.id + ".mute" : s.id + ".send." + state.mix + ".on";
    const b = el("button", { "data-on": onPath });
    b.onclick = () => { const cur = findStrip(s.id); const isOn = cur ? cur.on : s.on; send({ t: "set", path: onPath, value: eng ? isOn : !isOn }); };
    btns.append(b);
    if (eng) { const so = el("button", { "data-solo": s.id + ".solo", text: "SOLO" }); so.onclick = () => { const cur = findStrip(s.id); send({ t: "set", path: s.id + ".solo", value: !(cur && cur.solo) }); }; btns.append(so); }
  }
  const r = el("div", { class: "row", "data-id": s.id, "data-path": path },
    el("div", { class: "name" }, el("i", { style: s.colour ? "background:" + s.colour : "" }), el("span", { text: s.name })),
    el("div", { class: "track" }, range, el("div", { class: "meter" }, meter)), db, btns);
  paint(r, s);
  return r;
}

function findStrip(id) { return state && state.strips.find(x => x.id === id); }

function paint(r, s) {
  const eng = state.role === "engineer";
  const b = r.querySelector("[data-on]");
  if (b) { b.textContent = eng ? (s.on ? "MUTE" : "MUTED") : (s.on ? "ON" : "OFF"); b.className = eng ? (s.on ? "" : "off") : (s.on ? "on" : ""); if (s.hard) { b.textContent = "DEAD"; b.className = "off"; } }
  const so = r.querySelector("[data-solo]");
  if (so) so.className = s.solo ? "solo" : "";
}

function update() {
  document.querySelectorAll(".row").forEach(r => {
    const id = r.dataset.id, path = r.dataset.path;
    const s = id === state.mix && path.endsWith(".fader") && state.role !== "engineer" ? { level: state.master, on: true } : findStrip(id);
    if (!s) return;
    if (!(busy[path] > Date.now())) {
      r.querySelector("input").value = Math.round(toPos(s.level) * 1000);
      r.querySelector(".db").textContent = dbText(s.level);
    }
    paint(r, s);
  });
}

function meters(v) {
  if (!state) return;
  const rows = document.querySelectorAll(".row");
  const index = {}; state.strips.forEach((s, i) => index[s.id] = i);
  rows.forEach(r => {
    const id = r.dataset.id; let p = id === state.mix && state.role !== "engineer" ? v[v.length - 1] : v[index[id]];
    p = p || 0; const db = p > 0 ? 20 * Math.log10(p) : -100; const w = Math.max(0, Math.min(100, (db + 60) / 60 * 100));
    const b = r.querySelector(".meter b"); b.style.width = w + "%"; b.style.background = db > -3 ? "var(--red)" : db > -12 ? "var(--amber)" : "var(--green)";
  });
}

connect();
</script>
</body>
</html>
""";
}
