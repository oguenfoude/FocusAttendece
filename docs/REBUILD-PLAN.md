# FocusPlus — Complete Rebuild Plan
### From Windows-only WPF to cross-platform, clean, device-friendly attendance app
Date: 2026-11-24 · Source reviewed: `github.com/oguenfoude/FocusAttendance`

## Decisions locked (2026-11-24)

| Decision | Result |
|---|---|
| D1 — Data layer | **Keep SQLite + Dapper** (proven, portable, already tested). Priority is getting real device info, not re-platforming persistence. EF Core can be a later upgrade. |
| D2 — OS priority | **Windows + Linux first** (that is where the existing equipment/workflows are). macOS support in code from day one, packaging/notarization later. |
| D3 — Device password | **User will provide it.** Do not brute-force. Until then: implement credential prompt + vault + fake-server tests; live auth test waits for the key. |
| D5 — Repo | **New GitHub repo** under `oguenfoude`. `gh` CLI is authenticated; git identity is `oguenfoude <oguenfoude@proton.me>`. |
| Environment | Ubuntu 22.04, **.NET SDK not installed** — must install .NET 10 before build/test. |

---

## 0. Scope and principles

1. **New product, proven core.** Build a new application and new UI from scratch, but **port the tested Domain + Application + native protocol logic** instead of re-deriving the attendance rules and ZKTeco binary protocol. Rewriting those from memory is the single biggest risk.
2. **Cross-platform first.** Windows, macOS, Linux from one codebase. No COM, no registry, no Windows-only DLLs.
3. **Local-first, no fake data.** SQLite is the source of truth. Fresh DB = 0 employees, 0 devices, 0 punches. Only the OPGI baseline (1 site, 12 services, 2 shifts, SuperAdmin) is seeded.
4. **Devices are first-class citizens.** Explicit connection states, stored per-device credentials, safe diagnostics, no raw transport exceptions in the UI.
5. **Design:** one calm design system, AR/RTL and FR/LTR equal, light/dark, no nested tabs, no cascading popups — one in-page drawer for all editing.

---

## 1. What exists today (verified facts)

| Layer | Project | TFM | Portable? | Note |
|---|---|---|---|---|
| Domain | `FocusAttendance.Domain` | `net10.0` | ✅ | Entities + attendance engine, pure |
| Data | `FocusAttendance.Data` | `net10.0` | ✅ | SQLite + Dapper, 1 embedded migration, WAL, backups |
| Hardware | `FocusAttendance.Hardware` | `net10.0` | ✅ | Pure-managed TCP/UDP 4370 protocol, LAN scanner, parsers |
| Application | `FocusAttendance.Application` | `net10.0` | ✅ | Services, MVVM view models, exports — no WPF ref |
| App | `FocusAttendance.App` | `net10.0-windows…` | ❌ | WPF + WPF-UI 4.3.0 + COM SDK bootstrap |
| Tests | `FocusAttendance.Tests` | `net10.0` | ✅ | ~104–108 xUnit cases |

- **Only the UI head is Windows-locked.** The hardware layer already implements the full handshake (`CMD_CONNECT → ACK_UNAUTH → CMD_AUTH` with comm-key), TCP framing, buffered reads, 8/16/40-byte punch parsing, 28/72/120-byte user records, clock codec, network config and discovery.
- **The Windows-only part is optional**: `zkemkeeper` COM engine (late binding, HKCU self-registration, 15 x64 DLLs). It can be deleted.
- **Live device**: `192.168.100.201` — ZKSoftware OUI `00:17:61`, port 4370 open, firmware `Ver 6.60 Sep 10 2019`, **password protected** (`ACK_UNAUTH`), clock stuck at 2016-12-08. In the old screenshots it appears as "unidentified" with a raw network error — exactly the UX we must fix.
- **Pain in old repo**: `D:\FocusAttendance` hardcoded paths, ~20 one-off PowerShell/Python patch scripts, 64-bit-only SDK, doc drift (93 vs 108 tests, 30 vs 6 backups), no installer, Windows-only CI.

---

## 2. Keep / port / replace / delete

| Action | Items |
|---|---|
| **Keep as-is (ported)** | Attendance engine (`AttendanceCalculator`, `AttendanceSheetBuilder`, `AttendanceDayInput`), entities/enums, services, view models, `AppLogger`, `PasswordHasher`, CSV + bilingual HTML exports, 108 tests |
| **Port with cleanup** | SQLite schema + repositories + `BackupService` (keep the tested 30-daily + 12-monthly backup policy; align docs to code), native ZKTeco protocol files (`ZkProtocolClient`, `ZkPacket`, `ZkFrameReader`, `ZkBufferParser`, `ZkCommand`, `ZkNativeEngine`, `LanScanner`) |
| **Replace** | WPF shell → Avalonia; WPF-UI controls → FluentAvalonia; Windows COM SDK → native protocol only. SQLite + Dapper is **kept** (D1). |
| **Delete** | `ZkComSdkEngine`, `ZkSdkBootstrapper`, all `sdk/*.dll`, `app.manifest`, `tools/*.ps1`/patch scripts, Windows-specific code-behind, old `UiRender` |

---

## 3. Target architecture

```
FocusPlus.sln
├─ src/FocusPlus.Domain                 net10.0  entities, enums, attendance engine  (ported)
├─ src/FocusPlus.Application            net10.0  services, MVVM VMs, exports, IDeviceGateway (ported)
├─ src/FocusPlus.Data                   net10.0  SQLite + Dapper, repositories, backups, numbered migrations
├─ src/FocusPlus.Hardware               net10.0  native ZK protocol, discovery, DeviceHost
├─ src/FocusPlus.Desktop                net10.0  Avalonia head: App.axaml, Views/, Controls/, Resources/
├─ tools/zk-probe                       net10.0  CLI diagnostics for the LAN/device (replaces PS scripts)
├─ packaging/                           icons, .desktop, entitlements, Velopack config
└─ tests/FocusPlus.Tests                net10.0  engine + protocol + data + VM + UI-viewmodel tests
```

**Boundaries**
- `IDeviceGateway` lives in `Application`; implemented in-process by `Infrastructure.Devices` now, replaceable later by a daemon (named pipe / Unix socket / gRPC) with **zero UI changes**.
- One `DeviceHost` per registered terminal: owns its socket, session, heartbeat, poll timer and event reader; **one command at a time**, publishes `PunchObserved` / `OnlineChanged` through `Channel<T>`.
- Single-instance enforcement (mutex/file lock) so two processes never fight over the same device session or local port.
- Offline-first: device punches → dedupe → local `Punches`; operator edits → `SyncOutbox` with idempotency keys. **Never clear device logs until rows are durably committed** (clearing is irreversible).

---

## 4. Stack decisions

| Area | Decision | Why |
|---|---|---|
| UI | **Avalonia 11.3 + FluentAvalonia 2.x** (`net10.0`) | Only XAML framework with real Win/macOS/Linux support and a WPF-like dialect — existing ViewModels/ResourceDictionaries port with minimal change. FluentAvalonia keeps the current Fluent 2 look on all OS. Full .NET sockets for devices. |
| Device | **Pure-managed native protocol only** | Already cross-platform and tested. Removes COM/x86/WoW64/`regsvr32` pain entirely. |
| Database | **SQLite + Dapper (kept, D1)** | Already portable and covered by tests. Formalize numbered, checksummed SQL migrations + `Database.Migrate()` at startup; EF Core remains a future option, not v1 scope. |
| Updates | **Velopack** (MIT) | One updater for Windows/macOS/Linux, full + delta packages, channels `stable`/`beta`. |
| Packaging | single-file self-contained `dotnet publish` → Windows `win-x64` + ARM64, macOS universal `.app`/DMG (signed + notarized), Linux AppImage + `.deb` | No runtime install required on target machines. |
| Icons | Fluent System Icons via FluentAvalonia | Consistent with existing design language. |

Rejected: .NET MAUI (no Linux), Electron/Tauri (new language + native modules), Flutter (new language), XPF commercial (fast but WPF-UI 4.3 compatibility unverified, license cost).

---

## 5. Feature set for v1 (MVP)

**Parity from the old app**
- Login gate (PBKDF2-SHA256, SuperAdmin/Operator, forced first password change, live AR/FR).
- Dashboard KPIs + activity feed + quick actions (scan-all, pull-all, sync clocks).
- Terminals: LAN discovery, add by IP, snapshot (serial/model/firmware/MAC/clock/users/fingers/punches), full device console, memory inspector.
- Employees: directory, 7-day filters, CRUD drawer, schedule bitmask, salary, device privileges, archive/restore, import from device, push to device.
- Attendance: 7-day/monthly sheet, raw-scan drawer, verified manual punches, exceptions (mission/exit permit/leave), CSV + printable bilingual HTML.
- Settings: language/theme, shifts CRUD, services CRUD, SQLite health, rolling backups, operator accounts.
- Audit trail, zero hard delete, punch deduplication, background device monitor.

**New improvements (the reason for rebuilding)**
1. **Device connection wizard** — scan → pick or type IP → test connection → enter comm-key/password → save. One screen, plain language.
2. **First-class device states** — `Online`, `Locked (needs password)`, `Unverified`, `Offline`, `Error`. Locked devices show the serial/IP and a "Enter device password" action, never a raw socket error.
3. **Per-device password/comm-key vault** stored in the local DB (DPAPI on Windows, Keychain on macOS, libsecret on Linux — graceful fallback to encrypted-at-rest local store).
4. **Guided IP-change pipeline** with preview + confirmation + progress + automatic rollback verification (replaces the fire-and-forget flow).
5. **Cleaner design system** (section 6).
6. **Field diagnostics CLI** (`zk-probe <ip> [--udp] [--key N]`) to test a terminal without launching the GUI.
7. **Installers and auto-update** on all three OS.
8. **Headless `zk-probe selftest`** in CI — no Windows-only `--selftest` path.

---

## 6. Device management UX (detailed)

| Old behaviour | New behaviour |
|---|---|
| Device added by IP, shown as "غير معرف" when auth fails | Add wizard tests connection first; if `ACK_UNAUTH`, prompt for the numeric comm-key and retry immediately |
| Raw error: "Unable to read data from the transport connection" | Human message + reason (`Password required`, `Timeout`, `Wrong password`, `Wrong model record layout`) with a "Retry" / "Edit credentials" action |
| Discovery mixes verified/unverified hosts | Discovery result rows: `Verified (serial …)`, `Locked`, `Port open`, `Offline` — each with a clear next action |
| One monitor fighting with UI reads | One `DeviceHost` owns the socket; UI subscribes to events |
| Device clock trusted silently | Show clock drift badge; "Sync clock" one click; store device-local time + site offset explicitly |
| Bulk pull can be slow / duplicate | Watermark + dedupe + `DisableDevice()/EnableDevice()` in `try/finally` + progress + never auto-clear logs |
| 8/16/40 and 28/72/120 record variance | Auto-detect on first connect, persist per device, expose override in device settings |

**Auth flow (verified against live device):** `CMD_CONNECT(1000)` → `ACK_UNAUTH(2005)` + session id → `MakeCommKey(password, sessionId)` → `CMD_AUTH(1102)` → `ACK_OK(2000)`. Password is numeric (0–999999). If a future device answers `6001`, show "This firmware requires PUSH/ADMS mode" and stop — do not patch the pull handshake.

---

## 7. Design system / UI specification

**Tokens:** OPGI emerald `#0F6B5C`, ochre `#B4761F`, warm sand neutrals, semi-transparent surfaces, 8px spacing scale, 4 radius levels, one type scale. Widgets: FluentAvalonia `NavigationView`, `InfoBar` (notifications), `ContentDialog` (one confirm at a time), `TabView` only at top level if ever needed, in-house `SlideOver` drawer (ported from WPF) for every edit.

| Screen | Layout | Key interactions |
|---|---|---|
| **Login** | Centered card on branded panel, subtle Andalusian lattice, AR/FR toggle top-right, theme toggle | Live RTL/LTR switch, Enter to submit, inline validation |
| **Dashboard** | KPI strip (6 cards), quick-action bar, device rail (left), activity feed (main), empty states with real guidance | Ctrl+1…5 navigation, refresh, click KPI → filter target page |
| **Terminals** | Device cards/table with status pill, latency, clock drift, punch counter; selection → detail drawer; "Add terminal" wizard | Scan LAN, test connection, enter password, sync clock, pull punches, reboot, console (read-only), IP change wizard |
| **Employees** | Search/filter bar + directory table + 7-day mini-grid; edit in slide-over drawer with sections (identity, service, schedule, salary, device) | Import from device, push to device, archive/restore |
| **Attendance** | Date/section filters + 7-day sheet, totals row, color-coded status, raw-scan drawer, exception drawer | Add verified punch, add exception, CSV/HTML export |
| **Settings** | Left sub-nav: General, Shifts, Services, Security, Backup, About | Live language/theme, CRUD, backup now/restore, operator management |

**Cross-cutting:** runtime `FlowDirection` switch; technical values (IP, MAC, serial, time) isolated LTR inside RTL text; light/dark; loading skeletons; responsive minimum 1024px with graceful table horizontal scroll; keyboard shortcuts; no nested TabControl, no cascading popups.

---

## 8. Data and migration

- Keep `001_initial.sql` as-is; add numbered SQL migrations (`002_…`) with a checksum table applied in transaction under a single-instance lock. Preserve all table/column names and unique indexes so an old DB file can be upgraded in place.
- Keep: WAL, `PRAGMA foreign_keys=ON`, soft-delete columns on all mutable tables, immutable `Punches`, `UNIQUE(DedupeKey)`, audit logs.
- Keep the implemented backup policy: **one verified backup per day**, retain the newest **30 daily** copies plus **12 monthly anchors**, and delete older payloads **only after a successful backup**. Add checksummed migration history; single-instance lock around startup migration.
- Default DB path per OS: Windows `%LOCALAPPDATA%/FocusPlus`, macOS `~/Library/Application Support/FocusPlus`, Linux `$XDG_DATA_HOME/FocusPlus` (fallback `~/.local/share/FocusPlus`). Override with env var for tests.
- Seed exactly: 1 site, 12 departments, 2 shifts, 1 SuperAdmin (`admin`, password from `FOCUS_ADMIN_PASSWORD` or forced change on first login). **0** employees/devices/punches.

---

## 9. CI / distribution

GitHub Actions matrix — **Windows + Linux mandatory from day one (D2)**, macOS job added later (code stays cross-platform; signing/notarization deferred):
1. `dotnet restore` → `build` → `test` (must build first, per repo rule) → `zk-probe selftest`.
2. Publish self-contained single-file per RID.
3. Package: Windows NSIS/MSIX + Authenticode; macOS universal `.app` → DMG/PKG + `codesign`/`notarytool`; Linux AppImage + `.deb`.
4. Velopack `vpk pack` → upload to GitHub Releases channel `stable`.

**Prerequisite on this machine:** no .NET SDK is installed (Ubuntu 22.04). Install .NET 10 SDK (or use a container) before any build/test step.

---

## 10. Testing strategy

- **Port all ~108 existing tests** (engine, checksum vector, framing, time codec, comm-key, record parsers, DB seed/dedup/backup, services, VMs) — these are the safety net.
- **New:** `FakeZkServer` tests for `ACK_UNAUTH → AUTH` locked-device flow, discovery classification, `DeviceHost` single-connection rule, outbox/idempotency, migration from old DB file.
- **Live acceptance:** real terminal `192.168.100.201` — discovery → locked state → authenticate with its password → read firmware/serial → pull punches → dedupe on second pull → clock sync.
- **UI:** deterministic screenshot harness (replacement for `tools/UiRender`) rendering every workspace in light/dark × AR/FR on a throwaway DB, in CI on all three OS.

---

## 11. Phased roadmap (exit criteria)

| Phase | Work | Exit criteria |
|---|---|---|
| **P0 — Foundation** | Install .NET 10 SDK; create new solution; port Domain + Application + native Hardware + Tests; delete COM/SDK code | `dotnet test` green on Linux; `zk-probe 192.168.100.201` reports `Locked` + firmware |
| **P1 — Data** | Numbered SQL migrations + old-DB import | Fresh DB has exactly baseline seed; migration from old DB passes; one-backup-per-day policy (30 daily + 12 monthly) verified |
| **P2 — Device UX** | `IDeviceGateway` + `DeviceHost`, add wizard, credential vault, state pills, diagnostics CLI | Locked device can be authenticated and pulled in a CLI-driven test; no raw exceptions in UI |
| **P3 — UI shell + Dashboard** | Avalonia + FluentAvalonia, design tokens, RTL/theme, dashboard end-to-end | Dashboard renders identically on Win/macOS/Linux screenshot harness |
| **P4 — Remaining screens** | Terminals, Employees, Attendance, Settings | Feature parity from §5; all drawer flows and exports working |
| **P5 — Package & ship** | Velopack, CI matrix, installers, signing, auto-update | Working installers on all three OS; update channel works |
| **P6 — Hardening** | Real-time events, PUSH/ADMS detection, perf, accessibility, docs | Live device acceptance test passes; docs match code |

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Losing attendance-rule correctness | Port Domain unchanged + keep all engine tests before touching anything |
| ZKTeco auth blocks progress | Get the numeric comm-key for `192.168.100.201`; vault it; if firmware answers 6001, switch to PUSH/ADMS |
| Avalonia XAML port effort | ViewModels are framework-agnostic; port page by page; FluentAvalonia covers most controls |
| Device clock/timezone confusion | Store device-local time + offset; show drift; never silently accept wrong time |
| Bulk pull duplicates / data loss | Dedupe + watermark; never clear device logs until committed locally |
| macOS/Linux firewall blocks discovery | Manual IP entry always available; `zk-probe` CLI; clear onboarding docs |
| SQLite migration edge cases | Numbered transactional migrations with checksums; test upgrade from the old DB file |

---

## 13. Clarifying the device password (D3)

To read real punches from `192.168.100.201` we need the device's **numeric communication key** (0–999999), not the Windows app login password. It is the code configured on the terminal itself (often the device admin password). Verified behaviour:

1. App sends `CMD_CONNECT(1000)` → device replies **`ACK_UNAUTH(2005)`** with a session ID.
2. App derives a key: `MakeCommKey(password, sessionId)` and sends `CMD_AUTH(1102)`.
3. Device replies `ACK_OK(2000)` → only then can serial/firmware/time/users/punches be read.

So please provide either:
- the numeric comm-key / device admin password, **or**
- physical access to the terminal so we can read/set it in its menu.

We will **not** guess/brute-force it. Meanwhile the app will always show a clean `Locked — needs device password` state with an input box, and all device logic will be tested against a fake ZK server that reproduces `ACK_UNAUTH`.

Remaining minor choices (can be decided later):
- **D4 — Branding:** keep "Focus Attendance / OPGI M'Sila" or rename to "FocusPlus" with fresh visual identity? (Plan uses FocusPlus.)
- **D6 — Scope:** v1 local desktop only — assumed. Web/mobile view is out of v1 scope.

**Immediate next step:** install .NET 10 SDK, create the new GitHub repo under `oguenfoude`, scaffold the solution, port Domain + Hardware + tests, and run `zk-probe` against `192.168.100.201` — it should report `Locked` + firmware `Ver 6.60 Sep 10 2019` before any UI work begins.
