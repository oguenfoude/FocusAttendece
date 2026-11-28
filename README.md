# FocusPlus

Cross-platform biometric attendance system for **OPGI M'Sila**.
Windows · Linux · macOS — one codebase, one clean design, no vendor DLLs.

> New build replacing the old Windows-only WPF app. The pure, tested core
> (attendance rules, SQLite data layer, native ZKTeco protocol) has been kept;
> the Windows UI and the `zkemkeeper` COM SDK have been removed.

---

## Status (Phase 0 complete)

| Item | State |
|---|---|
| Solution `net10.0` (Domain, Data, Hardware, Application) | ✅ builds on Linux, 0 warnings |
| Tests | ✅ **109 passed** |
| Windows-only COM SDK / registry / DLLs | ✅ removed completely |
| Live ZKTeco probe at `192.168.100.201` | ✅ correctly reports `CommKey required` |
| Cross-platform data directory | ✅ Windows / macOS / Linux (XDG) |
| Avalonia GUI | ✅ 5 workspaces, AR/RTL + FR/LTR, OPGI design system — starts cleanly on Linux/Xvfb |
| Installers + auto-update | ⏳ Phase 5 |

- [Rebuild plan](docs/REBUILD-PLAN.md)
- [OPGI DZ requirements, status and where to update data](docs/OPGI-DZ-REQUIREMENTS.md)

---

## Build and test

Requires **.NET 10 SDK**.

```bash
dotnet build FocusPlus.slnx -c Release
dotnet test tests/FocusPlus.Tests/FocusPlus.Tests.csproj -c Release --no-build
```

## Probe a ZKTeco terminal (no GUI needed)

```bash
# List local networks (physical ones first; bridges/containers skipped)
dotnet run --project tools/zk-probe -c Release -- --networks

# Scan the LAN on TCP 4370
dotnet run --project tools/zk-probe -c Release -- --scan --cidr 192.168.100.0/24

# Probe one terminal and show diagnostics
dotnet run --project tools/zk-probe -c Release -- 192.168.100.201 --dump

# With a device communication key
dotnet run --project tools/zk-probe -c Release -- 192.168.100.201 --key 12345 --dump
```

Discovery states: `Verified` · `CommKey required` · `Port open — unverified` · `Unreachable`.
No device is ever invented: an empty scan is a valid result.

Verified live terminal (comm key `0`):

```
192.168.100.201  Verified ZKTeco
Serial  KMY2260600018   Model  UF100Plus-S
Firmware Ver 6.60 Sep 10 2019   Platform ZMM221_TFT
Users 4 · Fingers 2 · Faces 2 · Punches 22
```

---

## Run the GUI

```bash
dotnet run --project src/FocusPlus.Desktop -c Release
```

Five workspaces: **Dashboard**, **Terminals**, **Employees**, **Attendance**, **Settings**.
Login gate with PBKDF2 auth; live Arabic (RTL) / French (LTR) switch; OPGI emerald/ochre
design system; all editing happens in a single in-page drawer — no nested tabs or popups.
On a headless Linux machine: `xvfb-run -a dotnet src/FocusPlus.Desktop/bin/Release/net10.0/FocusPlus.dll`.

## Projects

```
src/FocusPlus.Domain        entities, enums, attendance rule engine (pure)
src/FocusPlus.Data          SQLite + Dapper, schema, repositories, backups
src/FocusPlus.Hardware      native TCP/UDP 4370 protocol, discovery, parsers
src/FocusPlus.Application   services, view models, exports
src/FocusPlus.Desktop       Avalonia 11 GUI, OPGI theme, AR/FR resources
tools/zk-probe              cross-platform field diagnostics CLI
tests/FocusPlus.Tests       109 xUnit tests
```

## Device access

Pure-managed port-4370 protocol only — works identically on all OS.
Handshake: `CMD_CONNECT` → `ACK_UNAUTH` → `MakeCommKey(key, sessionId)` → `CMD_AUTH`.
The terminal at `192.168.100.201` (firmware `Ver 6.60 Sep 10 2019`) requires a
numeric communication key before any data can be read. It is stored per device;
the app shows a clear `Locked — needs device password` state instead of a raw error.

## Data rules

- Fresh database contains **0 employees, 0 devices, 0 punches**; only the OPGI baseline is seeded.
- No hard delete: every mutable table has archive columns + restore.
- Punches are deduplicated on `serial + pin + time`; re-pulling never duplicates.
- Metadata-stamped SQLite backups (`VACUUM INTO`): one daily copy per day, newest 30 daily
  copies plus 12 monthly anchors retained, older payloads deleted; startup and pre-restore safety copies.
- Full audit trail (logins, device changes, HR adjustments, hardware operations).
