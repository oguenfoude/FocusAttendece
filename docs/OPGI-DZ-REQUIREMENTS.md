# OPGI DZ —system requirements (clear, single source of truth)

**Organisation:** Office de Promotion et de Gestion Immobilière (OPGI), wilaya de M'Sila, Algeria.
**Product:** FocusPlus — local-first desktop biometric attendance system.
**Purpose:** replace the old paper attendance sheets with a clear, auditable bilingual system.

---

## 1. Administrative working time (official rule)

| Item | Value | Where it is configured |
|---|---|---|
| Morning | **08:00 → 12:00** | Settings → Shifts (*Rule editor*) |
| Lunch | **12:00 → 13:00** | same |
| Evening | **13:00 → 16:30** | same |
| Morning grace | **15 min** (late if > 08:15:59) | same |
| Afternoon grace | **15 min** | same |
| Early-leave grace | **5 min** | same |
| Daily target | **450 min** (07:30) | same |
| Lunch deduction | **60 min**, automatic when the span crosses lunch | same |
| Anti-bounce | **3 min** between scans | same |

The rule is **editable at runtime**; the sheet recalculates immediately (covered by tests).

## 2. Attendance calculation rules

- **4 scans:** `(LunchOut − MorningIn) + (EveningOut − LunchIn)` → 08:00/12:00/13:00/16:30 = **07:30**.
- **2 scans:** span minus the lunch break when it crosses 12:00–13:00 → **07:30**.
- **Multi-scan / free time:** sequential IN/OUT pairs are summed; every completed pair is credited.
- **Missing OUT:** flagged; HR can add a **verified manual punch** (audited).
- **Off-day / Friday / Saturday:** never dropped; counted as **extra / free-time hours**.
- **Late / early leave:** computed from grace settings, with a DZD deduction preview.

## 3. Official exceptions (all auditable, archivable)

| Type | Effect |
|---|---|
| Ordre de mission | credited minutes |
| Bon de sortie | credited minutes |
| Congé annuel / maladie | credited minutes, no absence flag |
| Manual verified punch | completes a missing scan, records actor + reason |

## 4. Salary preview (DZD)

Hourly rate from `HourlyRateDzd`, else `DailyRateDzd`, else `BaseSalary / 30`.
Extra hours use `OvertimeMultiplier`. Deductions are previewed, never applied silently.

## 5. Language, reporting, organisation

- **Fully bilingual:** Arabic (RTL) and French (LTR), switch live without restart.
- Technical values (IP, MAC, serial, time) stay LTR-isolated inside RTL text.
- **CSV export** (UTF-8 BOM, AR/FR headers) and **printable HTML report** with A4 landscape,
  signature blocks and totals.
- OPGI baseline seed: **1 site, 12 services** (DG, SEC-DG, DRH, DFC, DGI, DPI, DPF, DMO,
  CI, AG-MSL, service technique, service archives), **2 shifts**, **1 SuperAdmin**.
- Roles: **SuperAdmin** (devices, network changes, operators) and **Operator** (daily work).

## 6. Data integrity rules (non-negotiable)

1. **No fake data** — fresh DB has 0 employees, 0 devices, 0 punches. Only the OPGI baseline is seeded.
2. **No hard delete** — archive columns + restore on every mutable table; raw punches are immutable.
3. **Strict dedup** — `UNIQUE(serial + pin + punch time)`; re-pulling a terminal never duplicates.
4. **Audit trail** — logins, device IP changes, archives/restores, HR adjustments, hardware operations.
5. **Backups** — one daily copy/day via `VACUUM INTO` (single canonical file per local date, hash-stamped),
   newest 30 daily copies plus 12 monthly anchors retained, older payloads deleted,
   startup safety copy, pre-restore safety copy.
6. **Local-first** — SQLite file on the workstation, WAL, foreign keys ON. No cloud dependency.

## 7. Terminals (ZKTeco / ZKSoftware)

- Pure-managed **TCP/UDP port 4370** protocol — no vendor DLL, works on Windows/Linux/macOS.
- Handshake: `CMD_CONNECT → ACK_UNAUTH → CMD_AUTH(MakeCommKey(key, sessionId))`.
- **Comm key may be `0`** — it is a valid key and is always attempted.
- Per-device: name, IP, port, mask, gateway, comm key, note, enabled flag.
- Status shown clearly: **Verified / CommKey required / Unverified / Offline**.
- Operations: pull punches, sync clock, reboot, power off, read users, import users,
  push users, device console, IP change pipeline with serial verification.
- Never clear device logs until punches are committed locally.

### Verified live terminal
```
192.168.100.201 · comm key 0 · Verified
Serial KMY2260600018 · Model UF100Plus-S · Platform ZMM221_TFT
Firmware Ver 6.60 Sep 10 2019 · Users 4 · Fingers 2 · Faces 2 · Punches 22
```

---

## 8. Implementation status (honest)

| Requirement | Status |
|---|---|
| Login / roles / first-password change | ✅ done |
| Attendance engine + editable shifts | ✅ done (rule editor now in GUI) |
| 7-day / monthly sheet + details | ✅ done |
| Manual punch + exceptions | ✅ done |
| Auto approve permissions (default ON, audited; OFF = SuperAdmin only) | ✅ done |
| Employee CRUD + device import/push | ✅ done |
| Device discovery, snapshot, pull, clock sync | ✅ done |
| AR/FR live switch, OPGI design | ✅ done |
| SQLite, dedup, audit, backups | ✅ done |
| CSV + HTML export buttons in GUI | ⏳ service ready, buttons not wired |
| Installers (Windows/Linux) + auto-update | ⏳ not yet |
| Dark theme | ⏳ not yet |

## 9. Where to update data in the app

| To change… | Go to |
|---|---|
| Official times, grace, lunch, target | **Settings → Shifts → edit** (rule drawer) |
| Services / departments | **Settings → Services** |
| Employee identity, schedule, salary, device PIN | **Employees → select → drawer** |
| Terminal IP, comm key, name | **Terminals → select → drawer** |
| Missing punch / exception | **Attendance → open row → detail drawer** |
| Language / theme / branding | **Settings → General** |
| Operators / password | **Settings → Security** |
| Backup / restore | **Settings → Backup** |

Every change is written to the audit log. Nothing is hard-deleted.
