# NEXORA

Church media software for Windows. One app on every media PC on the church network:
one PC is the **Live Output** (it drives the audience screen and the Stage Monitor and
holds the users, settings and libraries); the others are **Terminals** where operators
run Scripture, Song Lyrics, Cameras, Announcements, Lower Thirds and Countdown.
Everything works offline on the local network.

**Status: Phase 2 (Scripture).** On top of the Phase 1 foundations (PCs find each other,
sign-in with name + 4-digit PIN, roles, named screens, pin live / take down / bring back,
church branding), the Scripture section is real: ten bundled translations checked verse by
verse, a reference box that understands "jn 3 16", word search, the pastor's list for the
day, arrow-key stepping and history, themes with pictures, fonts, colours and effects,
automatic splitting of long passages, and side-by-side translations. Other sections still
send test slides.

Testing at church: see [docs/PHASE2-TESTING.md](docs/PHASE2-TESTING.md) (and
[docs/PHASE1-TESTING.md](docs/PHASE1-TESTING.md) for installing and pairing the PCs).

### Bibles

The bundled translations live in `src/Nexora.App/Bibles/*.json.gz` and are prepared into
fast `.nxbible` files on first start. They are built from the original files with
`tools/Nexora.BibleTool` (`prepare tools/Nexora.BibleTool/sources.json <sourceDir> <outDir>`),
which records every repair made to a source file. An admin can add more translations in
*Settings → Bibles & Scripture*; they are checked from Genesis 1:1 to Revelation 22:21 and
copied to every PC.

## How it fits together

```
 Terminal PCs (Nexora.exe)                 Live Output PC
 ┌───────────────────┐   WebSocket :7400   ┌──────────────────────────────────────┐
 │ Scripture, Lyrics │ ──── commands ────▶ │ Nexora.exe (control screen)          │
 │ … sections        │ ◀─── live state ─── │   ├─ LiveServer (Kestrel, SQLite)    │
 └───────────────────┘                     │   └─ OutputEngineHost (watchdog)     │
        ▲  UDP :7401 "I am Live Output"     │ Nexora.exe --output-engine           │
        └────────────────────────────────── │   └─ one window per named screen     │
                                            └──────────────────────────────────────┘
```

- **Commands, not pictures** travel on the network (small JSON messages). The Live Output
  PC applies them one at a time and draws every screen.
- **The output engine is its own process.** If the control screen freezes or closes, the
  audience screens keep their picture; a watchdog restarts the engine if it ever stops.
- **Nothing blanks the screen by accident.** A terminal that drops keeps its content on
  screen and its pins; it reconnects every ~1.5 s and signs back in by itself. Sign-ins
  and the live picture survive a Live Output restart.
- **Pairing code.** Only PCs that know the church's code can connect.

## Projects

| Path | What |
|---|---|
| `src/Nexora.Core` | Models, wire protocol, routing engine (`LiveState`), client, discovery, PIN hashing |
| `src/Nexora.Server` | Live Output server (Kestrel WebSocket), SQLite library store, activity log |
| `src/Nexora.App` | Avalonia desktop app (control screens and the `--output-engine` mode) |
| `tests/Nexora.Tests` | Routing rules, protocol, server integration (latency, reconnects, restarts, permissions) |

## Build

Requires the .NET 10 SDK.

```
dotnet test
dotnet run --project src/Nexora.App
```

To run two copies on one PC (a Live Output and a Terminal), give each its own data folder:
`set NEXORA_DATA=C:\temp\nexora-terminal` before starting the second.

CI (`.github/workflows/build.yml`) runs the tests on Windows and builds
`NEXORA-Setup.exe` with [Velopack](https://velopack.io). Every pull request has the
installer attached as a build artifact; every push to `main` publishes a test release.

Data lives in `%LOCALAPPDATA%\NexoraData` (config, `library/nexora-library.db`, `logs/`),
outside the install folder, so updates never touch it.

## Fonts

Sora, Manrope and JetBrains Mono, bundled under the SIL Open Font License (see `src/Nexora.App/Assets/Fonts`).
