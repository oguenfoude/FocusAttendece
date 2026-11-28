# FocusPlus v1 — Local-First Attendance System Spec (OPGI M'Sila)

Status: buildable specification. Date: 2026-11-28. Scope: existing .NET 10 solution `FocusPlus.slnx` (Domain, Data, Hardware, Application, Desktop, tools/zk-probe, tests). This document specifies changes; it does not implement them.

Terminology: **site PC** = one workstation running FocusPlus with its own SQLite file and LAN access to ZKTeco terminals. **HQ** = the consolidating workstation, also running FocusPlus with a local SQLite file. **Package** = deterministic `.fpcpkg` export/import file. **Punch** = immutable raw scan. **Adjustment** = audited HR correction (manual punch, mission, exit permit, leave).

---

## 1. Acceptance criteria

Each criterion is independently verifiable. “Pass” means the stated command or UI action produces the stated observable result.

| ID | Criterion | Verification |
|---|---|---|
| AC-01 | **Per-PC local SQLite.** A fresh launch creates exactly one database at the OS data directory (`%LOCALAPPDATA%/FocusPlus/focus-attendance.db`, `~/Library/Application Support/FocusPlus/focus-attendance.db`, `$XDG_DATA_HOME/FocusPlus/focus-attendance.db` or `~/.local/share/FocusPlus/focus-attendance.db`), WAL mode, `PRAGMA user_version = 2`, foreign keys ON. Fresh DB contains 0 employees, 0 devices, 0 punches; only the documented OPGI baseline (1 site, 12 departments, 2 shifts, 1 SuperAdmin) is seeded. No sample punches or devices. | Delete the data directory, launch app, inspect `DatabaseInfo`/DB counts; `DatabaseAndSeedTests`. |
| AC-02 | **Background services actually start.** After `Host.Initialize()`, `App.axaml.cs` calls `Host.StartBackgroundServices()`; device monitor and backup scheduler run; shutdown stops both. Today `src/FocusPlus.Desktop/App.axaml.cs` omits this call, so no automatic backup occurs. | App log shows monitor start and “daily backup” tick; unit test asserts `BackupScheduler.IsRunning` after `AppHost.StartBackgroundServices()`. |
| AC-03 | **Automatic daily backup.** The scheduler creates exactly one `Kind=Daily` backup per local date (Africa/Algiers, UTC+1, no DST), on startup catch-up and at the configured daily time. `CreateDailyBackup(force:false)` is idempotent: a second call the same day returns the existing file, `AlreadyExisted=true`, and writes no new file. Every successful backup records `CreatedUtc`, `LocalDate`, `SizeBytes`, `Sha256`, `FileName`, `Kind`. Zero-byte or failed `VACUUM INTO` is rejected and never recorded as Present. | `BackupRetentionTests`; backup list in Settings shows one daily row per date. |
| AC-04 | **Retention keeps newest 30 and prunes old.** Defaults: `keepDays=30`, `keepMonthly=12`. Retained set = newest 30 daily backups by `CreatedUtc` ∪ newest backup in each of the last 12 calendar months ∪ safety/pre-migration copies younger than 7 days. All other backup payload files are deleted; metadata rows are retained with `Status=Pruned`, `PrunedAtUtc` set. Prune runs only after today’s daily backup succeeds. A failed backup aborts pruning. A locked file is left in place, reported, and retried next run. | `BackupRetentionTests` with 45 simulated days: 30 daily + ≤12 monthly anchors survive; no file younger than 30 days is deleted; failed backup leaves all files. |
| AC-05 | **Restore is safe and auditable.** Restore first creates `Kind=SafetyRestore`. If the safety copy fails, restore aborts. Source hash is checked against metadata when present. The candidate is copied to a temp file and passes `PRAGMA quick_check` before the live DB is replaced. WAL/SHM are removed. If post-swap check fails, the safety copy is restored and `restore.rollback-completed` is returned. Audit records actor, source, safety path, result. | `BackupLifecycleTests` plus a corrupt-file test: live DB unchanged on failure; audit row exists. |
| AC-06 | **Arabic RTL is first-class; French LTR is complete.** Arabic sets `FlowDirection=RightToLeft` on the shell and every workspace; navigation, drawers, tables, toolbars, scrollbars, dialogs and icons mirror correctly. French is LTR. Technical values (IP, MAC, serial, matricule, time, date, amounts, phone) are LTR-isolated inside Arabic text. Numeric UI uses ASCII `0-9` and `HH:mm` in both languages; no Arabic-Indic digits in v1. DB, CSV and HTML contain no bidi control characters. Arabic renders with an Arabic-capable bundled font, no missing-glyph boxes. AR and FR resource dictionaries have identical key sets. | `RtlTextServiceTests`, `LangKeyParityTests`; screenshots at 1024/1440 px, AR/FR, light/dark; grep exports for `\u2066-\u2069`. |
| AC-07 | **Every data-update drawer is reachable.** Selecting an employee row opens the employee drawer; selecting a device row opens the device drawer; selecting an attendance row opens the attendance detail drawer. Today these drawers are unreachable because row selection is not wired from views to view models. Drawer supports keyboard open/close, `Esc` closes, focus returns to the originating row. Creating a manual punch requires time, actor (session), and reason; it is stored as an audited adjustment, never as a raw punch edit. | Manual UI walkthrough; `ViewModelTests` asserts `IsDetailOpen`, `SelectedRow`, command can-execute, and audit fields. |
| AC-08 | **7-day and monthly sheets work.** Attendance view provides 7-day and month ranges; every date in range for each employed person appears, including Friday and off-schedule days; off-day work is counted as extra, never dropped; totals (worked, absent, late, extra, credited, DZD preview) match `AttendanceCalculator`; missing OUT is flagged; Friday is rest by default. | Existing `AttendanceCalculatorTests` remain green; new sheet-range tests for 7/28/30/31 days and Friday. |
| AC-09 | **Device states are clear.** Device UI shows one of `Verified`, `CommKey required`, `Unverified`, `Offline`, `Error`, plus a `Clock drift` badge when `|device clock − workstation time| > 5 min`. No raw socket exception text is shown. Serial and IP are LTR-isolated. A locked device shows serial/IP and an “Enter device password” action. | UI walkthrough against `192.168.100.201` key `0` and an unreachable host; `DeviceTests`. |
| AC-10 | **Consolidation export is deterministic.** Same site, window, and data produce the same `packageId` and `contentHash` on repeated exports, independent of wall-clock export time. Package contains `manifest.json` + `data.ndjson.gz`; excludes `CommKey`, `IpAddress`, and any device secret; is written atomically with owner-only permissions. | Two exports of the same fixture are byte-identical except informational `createdAtUtc`; `ConsolidationTests`. |
| AC-11 | **Consolidation import is idempotent and all-or-nothing.** Import key is `siteCode|deviceSerial|employeeMatricule|dedupeKey`. Re-importing the same package returns `AlreadyImported=true`, inserts 0 rows. Duplicate package, duplicate punch, duplicate adjustment, and older employee versions are skipped. A duplicate device serial across sites, hash mismatch, schema mismatch, or any write error rolls back the whole import and leaves zero partial rows. Conflicts are recorded, not silently overwritten. | `ConsolidationTests` with duplicate package, duplicate serial, conflicting manual edit, forced mid-import failure. |
| AC-12 | **Legal guardrails are enforced in configuration.** Saving a work rule rejects daily amplitude > 720 min, weekly target > 2400 min, overtime multiplier < 1.5, or a break > 60 min. Overtime above 480 min/week is flagged in the sheet. Friday is rest by default; scheduling Friday produces a rest-day/overtime warning. Annual leave accrual is displayed as 2.5 days per completed month, capped at 30 calendar days/year; over-accrual requires SuperAdmin override with reason and audit. Biometric processing is blocked until the ANPDP declaration receipt is recorded. | `ComplianceTests`, `WorkRuleService` tests, UI validation messages AR/FR. |
| AC-13 | **No hard delete; punches immutable; audit complete.** Business tables never issue `DELETE`; archive columns are used. `Punches` are insert-only; no update path for raw scans. Audit rows exist for backup create/prune/restore, package export/import, conflict resolution, manual adjustment, device IP/credential change, employee biometric erasure, compliance settings. Backup payload files are the only retention-deleted files; their metadata is retained. | Code audit: grep for `DELETE FROM` in `src/FocusPlus.Data/Repositories`; `Audit` tests. |
| AC-14 | **Empty, loading, error, success, disabled states are explicit.** Empty lists show localized guidance, never fake rows. Backup/consolidation in progress disables the action and shows progress. Failures show a localized error with error code and retry; no unhandled exception dialog. | UI walkthrough with empty DB, failed backup (read-only folder), invalid package. |
| AC-15 | **Verification gates pass.** Build, tests, and headless launch succeed with the exact commands in §7. | §7. |

---

## 2. Legal / compliance requirements

These are engineering requirements derived from the cited texts, not legal advice. HR/legal counsel must validate the organisation’s application and the ANPDP filing before production use.

### 2.1 Verified statutory facts

| ID | Rule | Source (verified) | System requirement |
|---|---|---|---|
| L1 | Legal working week = **40 h**, normally distributed over **at least 5 working days**. | Ordonnance n° 97-03 du 11 janvier 1997, art. 2 (JORA n° 3 du 12/01/1997); applies to employers under law 90-11 and to public institutions/administrations (art. 1). | `WorkRules.WeeklyTargetMinutes ≤ 2400`; full-time employee schedules must include ≥ 5 working days; validator warning when weekly target × scheduled days exceeds 2400. |
| L2 | Continuous-shift (`séance continue`) break must **not exceed 1 h**, of which **30 min is counted as working time**. | Ordonnance 97-03, art. 6. | `WorkRules.BreakMinutes ≤ 60`; when `IsContinuousShift=1`, `BreakCountedMinutes ≥ 30`; attendance credits counted break minutes. OPGI’s normal 12:00–13:00 lunch is not a continuous shift by default; `IsContinuousShift=0`. |
| L3 | Daily **amplitude of effective work must never exceed 12 h**. | Ordonnance 97-03, art. 7; replaces law 90-11 arts. 22–26 (art. 8). | Rule editor rejects `EveningEndMin − MorningStartMin > 720`; attendance flags any day exceeding 720 min; overnight shifts unsupported v1 and rejected (`EveningEndMin > MorningStartMin`). |
| L4 | Overtime is exceptional and **may not exceed 20 % of legal duration** (8 h on a 40 h week), subject to the 12 h daily cap. | Loi n° 90-11 du 21 avril 1990, art. 31. | `WorkTimeCompliance` flags weekly extra > 480 min; sheet shows “overtime cap exceeded”; approval of extra time requires SuperAdmin when over cap. |
| L5 | Overtime pay increase **may not be less than 50 %** of normal hourly wage. | Loi 90-11, art. 32. | `WorkRules.OvertimeMultiplier ≥ 1.5`; new rules default 1.5; existing rules below 1.5 are flagged and cannot be used for payroll preview until corrected. Historical payroll values are not silently changed. |
| L6 | Weekly rest is **one full day**, normally **Friday**. Rotation is allowed only under legal conditions. | Loi 90-11, art. 33; rotation art. 37. | Default `WeekDays.Administrative` (Sunday–Thursday) excludes Friday. Friday work is `ExtraDay`/overtime, never absence. A Friday rotation requires an explicit, audited schedule reason. |
| L7 | Annual paid leave = **2.5 days per month of work**, total **not exceeding 30 calendar days per year**. | Loi 90-11, art. 41. | `LeaveEntitlementCalculator` displays accrued days = min(30, completed months × 2.5); AnnualLeave adjustment days are derived from inclusive `StartLocal`/`EndLocal`; over-accrual requires SuperAdmin override + reason + audit. |
| L8 | Biometric fingerprint data for attendance is **personal data** under law 18-07 (as amended by law 25-11), **not** a “sensitive” category per ANPDP; processing for timekeeping **does not require explicit prior employee consent** because it falls under work-time/discipline purposes. | ANPDP délibération n° 03 du 6 mai 2026, as reported by Algerie Eco (07/05/2026), Maghreb Emergent, Legal Doctrine, Gide (summary). Official PDF not yet retrieved from `anpdp.dz`; verify before filing. | Employee consent is not the legal basis. The app must not present consent as the basis. It must record the ANPDP filing and purpose. |
| L9 | **Prior declaration to ANPDP** is required; staff must be informed of collection, purpose, retention, and their access/rectification/opposition rights; security/confidentiality measures are required; data may not be used for other purposes. | ANPDP délibération n° 03 du 6 mai 2026 (as reported); law 18-07 art. 12 (declaration), arts. 32–36 (rights). | `ComplianceService` stores declaration receipt number/date, authority contact, DPO/contact, purpose statement, retention statement, and employee notice AR/FR. Device pull and biometric enrollment are blocked until a receipt is recorded. Purpose limitation is enforced by not exporting biometric templates (none stored) and not reusing punch data for unrelated modules. |
| L10 | Biometric data is retained **only during the employment relationship** and **destroyed at its end** (resignation, retirement, dismissal); no post-employment retention period is stated. | ANPDP délibération n° 03 du 6 mai 2026 (as reported). | On employee end/archive, `EmployeeBiometricErasureService` clears `DevicePin`, `DevicePassword`, `CardNumber`, `FpCount`, `FaceCount`, sets `BiometricErasedAtUtc`, records `BiometricErasureLogs`, and queues/executes device-user deletion. Employee record remains archived (no hard delete) with non-biometric identity for lawful attendance/audit retention. |
| L11 | **Discrepancy to resolve before go-live:** Algerian press and the délibération summary describe a **prior declaration**; one legal summary (Gide) describes **prior authorisation**. | Sources conflict; official text pending. | Record both `DeclarationReceiptNumber` and, if ANPDP qualifies the processing as requiring authorisation, `AuthorizationNumber`. Go-live gate requires at least the declaration receipt; legal counsel decides whether authorisation is also required. Do not claim either in UI until recorded. |

### 2.2 Compliance implementation requirements

- `src/FocusPlus.Application/Services/ComplianceService.cs` exposes `GetStatus()`, `SaveDeclaration(...)`, `SaveNotice(...)`, `CanProcessBiometrics()`, `GetEmployeeNotice(arabic)`, `RecordErasure(...)`, all audited.
- Settings → Compliance page shows: legal entity, site, ANPDP receipt/authorisation, purpose, retention rule, employee notice AR/FR, last review date, and “export employee data” action.
- Purpose limitation: consolidation packages contain only attendance identity, schedule, punch, and adjustment data needed for payroll/attendance; no biometric templates, no device credentials, no unrelated HR data.
- Retention: backup payloads are operational copies, not a separate legal archive; retention deletes old payloads while audit metadata remains. Employee biometric fields are erased at end of employment; punch/adjustment records remain subject to the organisation’s lawful archive policy.
- No legal claim is hard-coded as “consent obtained”. UI wording: “Traitement fondé sur l’organisation du temps de travail — déclaration ANPDP n° …” / “معالجة أساسها تنظيم وقت العمل — تصريح …”.

### 2.3 Sources

- Loi n° 90-11 consolidated text with Ordonnance 97-03 appended: `https://www.univ-bejaia.dz/doc/vrdpo2018/Loi%20n%C2%B0%2090-11%20du%2021%20avril%201990%20relative%20aux%20relations%20de%20travail%20compl%C3%A9t%C3%A9e.pdf` (arts. 31, 32, 33, 37, 41; Ordonnance arts. 1–10).
- Ordonnance 97-03 (JORA n° 3 du 12/01/1997), as reproduced in the consolidated text above and in `https://cour-constitutionnelle.dz/portail/wp-content/uploads/2023/11/loi-90-11.pdf`.
- ANPDP délibération n° 03 du 6 mai 2026: `https://algerie-eco.com/2026/05/07/pointage-biometrique-au-travail-ne-necessite-pas-un-consentement-explicite-prealable-de-lemploye-repond-lanpdp`; `https://maghrebemergent.news/fr/pointage-biometrique-au-travail-en-algerie-pas-besoin-daccord-prealable-dit-lanpdp`; `https://legal-doctrine.com/fr/edition/la-cndp-algerienne-precise-les-regles-sur-la-biometrie-au-travail-6d632a8302ce6151b522dc506e531c0f`; `https://www.gide.com/news-insights/93294-2`; authority site `https://anpdp.dz/ar`.
- Law 18-07 declaration/rights summary: `https://www.upgrowth.dz/journal-officiel/loi-18-07-protection-donnees-personnelles`; `https://profitpilot.dz/blog/declaration-anpdp-donnees-personnelles-algerie`.

---

## 3. Architecture decision

### 3.1 Decision

**Keep one full SQLite database on every PC. No central server in v1.** Multi-site consolidation is file-based: each site PC exports a deterministic package; HQ imports packages into its own local SQLite database. HQ is a peer, not a server. The same package format becomes the wire format when a central service is added later.

```
Site PC A (SQLite A) ──export──┐
Site PC B (SQLite B) ──export──┼──> .fpcpkg files ──import──> HQ PC (SQLite HQ)
Site PC C (SQLite C) ──export──┘                              │
                                                              └── consolidated sheets/reports
```

### 3.2 Why no central server in v1

1. **Requirement:** the organisation requires local SQLite on each PC and offline operation; sites cannot depend on WAN availability.
2. **Operational reality:** no existing server, TLS certificate lifecycle, backup infrastructure, or database administrator for a central service. A central server would add a single point of failure and an unowned security surface.
3. **Data residency and ANPDP:** keeping personal data on organisation-controlled PCs avoids cross-border transfer and third-party processing; packages move by controlled media/shared folder, not cloud.
4. **Failure isolation:** a corrupted or unavailable HQ does not stop site attendance capture; a site outage does not block other sites.
5. **Cost and time:** v1 ships without server procurement, hardening, patching, or 24/7 operations.
6. **Correctness:** punches are immutable and deduplicated, so merging is append-only; conflicts are limited to mutable master data and manual adjustments, which are explicitly modelled.

### 3.3 Consolidation identity and key

- Site identity: `Sites.Code` (stable, unique, assigned by HQ), plus `NationalCode` and `WilayaCode`. Example format is an HQ decision; the app never invents codes. `Sites.Code` is the package `siteCode`.
- Device identity: `Devices.SerialNumber` (verified from terminal). Duplicate serial across sites is a hard import error.
- Employee identity: `(siteCode, Matricule)`. Matricule is unique **within a site**, not globally. The existing global `UX_Employees_Matricule` index is replaced (see §4).
- Punch identity: `DedupeKey = DeviceSerial|DevicePin|PunchTimeUtc` (existing) plus `ConsolidationKey = siteCode|deviceSerial|employeeMatricule|dedupeKey`. The latter is the cross-site/import key. Unresolved punches export with `employeeMatricule=""`; their `dedupeKey` still includes PIN and time, so the key remains unique.
- Adjustment identity: `(siteCode, originAdjustmentId)`. `originAdjustmentId` is the adjustment’s GUID at the origin site; local pre-v2 adjustments use their local `Id`.

### 3.4 Package determinism

- Outer container: ZIP (`.fpcpkg`) with exactly two entries, in order: `manifest.json`, `data.ndjson.gz`. Fixed DOS timestamp `1980-01-01 00:00:00`; no extra fields; manifest stored, NDJSON entry stored (already gzip-compressed).
- Manifest: UTF-8 without BOM, LF, keys in fixed order, invariant formatting, UTC timestamps `yyyy-MM-ddTHH:mm:ss.fffffffZ`.
- NDJSON: UTF-8 without BOM, LF, one JSON object per line, fixed key order, records sorted by `(typeOrder, naturalKey)` ordinal.
- Gzip: `CompressionLevel.SmallestSize`, no filename, no mtime (header mtime 0).
- `contentHash = SHA-256(data.ndjson.gz)`.
- `packageId = first 32 hex chars of SHA-256(canonical manifest with packageId="")`, where only `formatVersion`, `siteCode`, `windowFromUtc`, `windowToUtc`, and `contentHash` participate. `createdAtUtc`, `appVersion`, counts, and names are informational and do not affect `packageId`.
- Consequence: same site/window/data → same `packageId` across repeated exports and across app builds. Re-import is a no-op.

### 3.5 Path to a central server (post-v1)

Add `ISyncTransport` in Application with `UploadPackage(Stream, PackageMetadata)` and `DownloadPackage(packageId)`. The transport uploads the **same `.fpcpkg` bytes**; the server stores packages and an index; HQ imports from the server instead of USB. No schema or domain change is required. The local SQLite remains the system of record and the source for package generation. A server-side merge is deliberately deferred; if added, it consumes the same manifest/NDJSON contract.

### 3.6 Non-negotiable invariants

- `Punches` insert-only; no update/delete.
- Business tables no hard delete; archive columns.
- Backup payload files are retention-managed exceptions; metadata is retained.
- Exactly one daily backup per local date; prune only after success.
- Every state-changing operation is audited with actor and UTC timestamp.
- No device, employee, punch, IP, or serial is invented; empty is valid.

---

## 4. Data model changes

### 4.1 Migration

New embedded migration: `src/FocusPlus.Data/Migrations/002_consolidation.sql`. Bump `Database.CurrentSchemaVersion` from 1 to 2. Existing `001_initial.sql` is unchanged. Migration runs transactionally under the existing `Database.ApplyMigrations`; `AppHost.Initialize` already creates a pre-migration safety backup when the DB exists.

**Blast radius:** additive tables/columns plus one index replacement (`Employees.Matricule`). Existing app code that looks up employees by matricule alone must become site-scoped. Existing backup code/tests and scheduler change behavior. Old databases remain readable by v1 because new columns are nullable/defaulted; the global matricule index is replaced, so cross-site duplicate matricules become legal.

**Preflight:** before dropping `UX_Employees_Matricule`, check for duplicate `(SiteId, Matricule)` groups. If duplicates exist, abort migration with `migration.duplicate-matricule`, keep the pre-migration backup, and refuse to start with an actionable message. If all employees have `SiteId NULL`, keep a global unique index for NULL-site rows.

**Rollback:** stop app; copy the pre-migration `Kind=PreMigration` backup over `focus-attendance.db`; delete `-wal`/`-shm`; restart. Manual SQL rollback is only allowed after confirming no cross-site duplicate matricules: drop new indexes/tables/columns, recreate `UX_Employees_Matricule`. Never run rollback without the safety backup.

### 4.2 New tables

```sql
CREATE TABLE IF NOT EXISTS Backups (
    Id                  TEXT PRIMARY KEY NOT NULL,
    Kind                INTEGER NOT NULL,              -- 0 Daily, 1 Manual, 2 SafetyRestore, 3 PreMigration
    DailyKey            TEXT NULL,                     -- yyyy-MM-dd for Kind=0
    LocalDate           TEXT NOT NULL,                -- yyyy-MM-dd, Africa/Algiers
    CreatedUtc          TEXT NOT NULL,
    FileName            TEXT NOT NULL,
    SizeBytes           INTEGER NOT NULL,
    Sha256              TEXT NOT NULL,
    Status              INTEGER NOT NULL DEFAULT 0,   -- 0 Present, 1 Pruned, 2 Failed, 3 Missing
    IsRetentionAnchor   INTEGER NOT NULL DEFAULT 0,
    PrunedAtUtc         TEXT NULL,
    FailureCode         TEXT NULL,
    FailureMessage      TEXT NULL,
    CreatedBy           TEXT NULL,
    Note                TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Backups_FileName ON Backups(FileName);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Backups_DailyKey
    ON Backups(DailyKey) WHERE Kind = 0 AND Status IN (0, 1);
CREATE INDEX IF NOT EXISTS IX_Backups_LocalDate ON Backups(LocalDate, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Backups_Status ON Backups(Status);

CREATE TABLE IF NOT EXISTS ConsolidationPackages (
    Id                  TEXT PRIMARY KEY NOT NULL,
    PackageId           TEXT NOT NULL,
    Direction           INTEGER NOT NULL,              -- 0 Export, 1 Import
    SiteCode            TEXT NOT NULL,
    FileName            TEXT NOT NULL,
    ContentHash         TEXT NOT NULL,
    FormatVersion       INTEGER NOT NULL,
    WindowFromUtc       TEXT NULL,
    WindowToUtc         TEXT NULL,
    EmployeeCount       INTEGER NOT NULL DEFAULT 0,
    DeviceCount         INTEGER NOT NULL DEFAULT 0,
    PunchCount          INTEGER NOT NULL DEFAULT 0,
    AdjustmentCount     INTEGER NOT NULL DEFAULT 0,
    CreatedUtc          TEXT NOT NULL,
    Actor               TEXT NOT NULL,
    Status              INTEGER NOT NULL DEFAULT 0,   -- 0 Completed, 1 Failed, 2 Duplicate
    ErrorCode           TEXT NULL,
    ErrorMessage        TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ConsolidationPackages_Package
    ON ConsolidationPackages(PackageId, Direction);
CREATE INDEX IF NOT EXISTS IX_ConsolidationPackages_Site
    ON ConsolidationPackages(SiteCode, CreatedUtc);

CREATE TABLE IF NOT EXISTS ConsolidationImportRuns (
    Id                  TEXT PRIMARY KEY NOT NULL,
    PackageId           TEXT NOT NULL,
    SiteCode            TEXT NOT NULL,
    StartedUtc          TEXT NOT NULL,
    CompletedUtc        TEXT NULL,
    Status              INTEGER NOT NULL DEFAULT 0,   -- 0 Running, 1 Completed, 2 Failed, 3 DryRun
    InsertedPunches     INTEGER NOT NULL DEFAULT 0,
    SkippedPunches      INTEGER NOT NULL DEFAULT 0,
    InsertedEmployees   INTEGER NOT NULL DEFAULT 0,
    UpdatedEmployees    INTEGER NOT NULL DEFAULT 0,
    InsertedAdjustments INTEGER NOT NULL DEFAULT 0,
    ConflictCount       INTEGER NOT NULL DEFAULT 0,
    Actor               TEXT NOT NULL,
    ErrorCode           TEXT NULL,
    ErrorMessage        TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_ConsolidationImportRuns_Package
    ON ConsolidationImportRuns(PackageId, StartedUtc);

CREATE TABLE IF NOT EXISTS ConsolidationConflicts (
    Id                  TEXT PRIMARY KEY NOT NULL,
    PackageId           TEXT NOT NULL,
    EntityType          TEXT NOT NULL,                 -- Employee, AttendanceAdjustment, Device, Site
    NaturalKey          TEXT NOT NULL,                 -- siteCode|matricule|...
    LocalHash           TEXT NULL,
    IncomingHash        TEXT NULL,
    DetailsJson         TEXT NOT NULL,
    ResolutionStatus    INTEGER NOT NULL DEFAULT 0,   -- 0 Pending, 1 KeepLocal, 2 AcceptIncoming, 3 Superseded
    ResolvedBy          TEXT NULL,
    ResolvedAtUtc       TEXT NULL,
    ResolutionNote      TEXT NULL,
    CreatedUtc          TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ConsolidationConflicts_Key
    ON ConsolidationConflicts(PackageId, EntityType, NaturalKey);
CREATE INDEX IF NOT EXISTS IX_ConsolidationConflicts_Status
    ON ConsolidationConflicts(ResolutionStatus, CreatedUtc);

CREATE TABLE IF NOT EXISTS BiometricErasureLogs (
    Id                  TEXT PRIMARY KEY NOT NULL,
    EmployeeId          TEXT NOT NULL,
    SiteCode            TEXT NULL,
    Matricule           TEXT NOT NULL,
    DeviceSerial        TEXT NULL,
    RequestedUtc        TEXT NOT NULL,
    CompletedUtc        TEXT NULL,
    Status              INTEGER NOT NULL DEFAULT 0,   -- 0 Pending, 1 Completed, 2 Failed, 3 ManualEvidence
    Actor               TEXT NOT NULL,
    Evidence            TEXT NULL,
    ErrorCode           TEXT NULL,
    ErrorMessage        TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_BiometricErasure_Employee
    ON BiometricErasureLogs(EmployeeId, RequestedUtc);
```

### 4.3 Existing-table changes

```sql
ALTER TABLE Sites ADD COLUMN NationalCode TEXT NULL;
ALTER TABLE Sites ADD COLUMN WilayaCode TEXT NULL;
ALTER TABLE Sites ADD COLUMN IsHeadquarters INTEGER NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Sites_NationalCode
    ON Sites(NationalCode) WHERE NationalCode IS NOT NULL AND NationalCode <> '';

ALTER TABLE Devices ADD COLUMN SiteCode TEXT NULL;
ALTER TABLE Devices ADD COLUMN ClockDriftMinutes INTEGER NULL;
CREATE INDEX IF NOT EXISTS IX_Devices_Site ON Devices(SiteCode);

ALTER TABLE Employees ADD COLUMN BiometricErasedAtUtc TEXT NULL;
DROP INDEX IF EXISTS UX_Employees_Matricule;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Employees_Site_Matricule
    ON Employees(SiteId, Matricule) WHERE SiteId IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Employees_NoSite_Matricule
    ON Employees(Matricule) WHERE SiteId IS NULL;

ALTER TABLE Punches ADD COLUMN SiteCode TEXT NULL;
ALTER TABLE Punches ADD COLUMN ConsolidationKey TEXT NULL;
ALTER TABLE Punches ADD COLUMN QualityFlags INTEGER NOT NULL DEFAULT 0; -- bit0 clock-suspect, bit1 duplicate-serial
CREATE UNIQUE INDEX IF NOT EXISTS UX_Punches_ConsolidationKey
    ON Punches(ConsolidationKey) WHERE ConsolidationKey IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Punches_SiteTime ON Punches(SiteCode, PunchTimeUtc);

ALTER TABLE AttendanceAdjustments ADD COLUMN SiteCode TEXT NULL;
ALTER TABLE AttendanceAdjustments ADD COLUMN OriginAdjustmentId TEXT NULL;
ALTER TABLE AttendanceAdjustments ADD COLUMN ContentHash TEXT NULL;
ALTER TABLE AttendanceAdjustments ADD COLUMN SupersedesAdjustmentId TEXT NULL;
ALTER TABLE AttendanceAdjustments ADD COLUMN IsConflict INTEGER NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Adjustments_Origin
    ON AttendanceAdjustments(SiteCode, OriginAdjustmentId) WHERE OriginAdjustmentId IS NOT NULL;

ALTER TABLE WorkRules ADD COLUMN BreakMinutes INTEGER NOT NULL DEFAULT 60;
ALTER TABLE WorkRules ADD COLUMN BreakCountedMinutes INTEGER NOT NULL DEFAULT 30;
ALTER TABLE WorkRules ADD COLUMN IsContinuousShift INTEGER NOT NULL DEFAULT 0;
ALTER TABLE WorkRules ADD COLUMN WeeklyTargetMinutes INTEGER NOT NULL DEFAULT 2400;
```

### 4.4 Model rules

- `Backups`: append-only metadata. `Status=Pruned` replaces file deletion evidence; never `DELETE`.
- `Sites`: `Code` remains the consolidation key. `NationalCode`/`WilayaCode` are assigned by HQ; never inferred. Partial unique prevents duplicate national codes but permits NULL.
- `Employees`: `Matricule` unique per site; NULL-site rows retain global uniqueness for backward compatibility. `BiometricErasedAtUtc` marks ANPDP end-of-employment erasure; employee stays archived.
- `Devices`: `SiteCode` attribute; `ClockDriftMinutes` last observed drift; credentials never exported.
- `Punches`: `DedupeKey` remains the device-level unique key; `ConsolidationKey` is the cross-site/import key. `QualityFlags` excludes suspect punches from attendance totals but retains the raw row. No update/delete.
- `AttendanceAdjustments`: `OriginAdjustmentId` is the stable cross-site ID; `ContentHash` detects tampering/conflicting copies; `SupersedesAdjustmentId` supports append-only conflict resolution; `IsConflict` excludes unresolved rows from totals.
- `WorkRules`: legal guardrail columns. `WeeklyTargetMinutes` default 2400; OPGI daily target 450 min × 5 = 2250 is within cap.
- New tables are additive; no existing column is dropped or retyped.

### 4.5 Settings keys (no schema change)

`AppSettings`: `Site.Code`, `Site.WilayaCode`, `Site.NationalCode`, `Backup.KeepDays=30`, `Backup.KeepMonthly=12`, `Backup.DailyTime=20:00`, `Compliance.DeclarationReceipt`, `Compliance.DeclarationDate`, `Compliance.AuthorizationNumber`, `Compliance.AuthorityContact`, `Compliance.DpoContact`, `Compliance.PurposeAr/Fr`, `Compliance.RetentionAr/Fr`, `Compliance.NoticeAr/Fr`.

---

## 5. Contracts and edge cases

### 5.1 Backup contracts

```csharp
namespace FocusPlus.Data;

public enum BackupKind { Daily = 0, Manual = 1, SafetyRestore = 2, PreMigration = 3 }
public enum BackupStatus { Present = 0, Pruned = 1, Failed = 2, Missing = 3 }

public sealed record BackupResult(
    bool Success,
    BackupKind Kind,
    string? Path,
    DateTime CreatedUtc,
    long SizeBytes,
    string? Sha256,
    bool AlreadyExisted,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record PruneResult(
    int DeletedFiles,
    long BytesFreed,
    IReadOnlyList<string> RetainedFiles,
    IReadOnlyList<string> FailedFiles);

public sealed record RestoreResult(
    bool Success,
    string? RestoredFrom,
    string? SafetyBackupPath,
    bool RollbackPerformed,
    string? ErrorCode,
    string? ErrorMessage);

public sealed class BackupService
{
    public BackupService(Database database, Func<DateTime>? clock = null);

    // One canonical daily snapshot per local date. force=false is idempotent.
    public BackupResult CreateDailyBackup(bool force = false);

    // Keeps newest keepDays daily backups + newest per month for keepMonthly months.
    // Call only after a successful daily backup.
    public PruneResult PruneBackups(int keepDays = 30, int keepMonthly = 12);

    public BackupResult CreateSafetyBackup(string reason);
    public RestoreResult Restore(string backupPath, string actor);
    public IReadOnlyList<BackupRecord> ListBackups(BackupStatus? status = null);
}
```

Implementation rules:

- `CreateDailyBackup`: require `File.Exists(DatabasePath)`; zero-length DB → `backup.database-empty`; missing → `backup.database-missing`. Check free disk ≥ `dbSize + 64 MiB`; else `backup.disk-full`. `VACUUM INTO` a temp file in `Backups/`; open temp read-only and run `PRAGMA quick_check`; compute SHA-256; flush to disk; atomically move to `focus-attendance_yyyy-MM-dd_daily.db`. Insert metadata after the file exists. If metadata insert fails, keep the file, return `backup.metadata-write-failed`, and let startup reconciliation adopt the orphan by hash.
- If today’s `DailyKey` row is Present and hash matches → `AlreadyExisted=true`. If file missing/hash mismatch → mark old row `Status=Missing`, create a replacement, return success with warning `backup.metadata-mismatch`. `force=true` replaces the same filename atomically and updates metadata; it never creates a second daily file.
- `PruneBackups`: load Present rows + reconcile filesystem orphans first. Retain newest `keepDays` daily rows, newest row per calendar month for `keepMonthly` months, and `SafetyRestore`/`PreMigration` younger than 7 days. Mark anchors `IsRetentionAnchor=1`; clear others. Delete non-retained payload files; on success set `Status=Pruned`, `PrunedAtUtc`. On `IOException`/`UnauthorizedAccessException`, leave `Status=Present`, add to `FailedFiles`, continue. Never delete the last remaining Present backup in a single run. Audit `backup.pruned` with counts/bytes.
- `Restore`: create `SafetyRestore` first; abort on failure. Verify source SHA-256 when metadata exists; mismatch → `restore.hash-mismatch`. Copy to temp, `quick_check` temp, then close pools, replace DB, delete WAL/SHM, reopen, `Initialize()`, `quick_check`. On post-check failure, copy safety back and return `restore.rollback-completed`; if rollback itself fails, `restore.rollback-failed` and leave the safety file path in the result. Audit `backup.restored`.
- All public methods serialize on a `SemaphoreSlim(1,1)`; no concurrent backup/restore/prune.

### 5.2 Consolidation contracts

```csharp
namespace FocusPlus.Application.Services;

public sealed record ExportOptions(
    string SiteCode,
    DateTime FromUtc,
    DateTime ToUtc,                 // exclusive
    bool IncludeArchived = false,
    bool IncludeEmployees = true,
    bool IncludeDevices = true);

public sealed record ExportResult(
    bool Success,
    string? PackagePath,
    string? PackageId,
    string? ContentHash,
    int EmployeeCount,
    int DeviceCount,
    int PunchCount,
    int AdjustmentCount,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ImportOptions(
    string PackagePath,
    bool DryRun = false);

public sealed record ImportResult(
    bool Success,
    bool AlreadyImported,
    int InsertedPunches,
    int SkippedPunches,
    int InsertedEmployees,
    int UpdatedEmployees,
    int InsertedAdjustments,
    int ConflictCount,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? ErrorMessage);

public sealed class ConsolidationService
{
    public ExportResult ExportPackage(ExportOptions options, string destinationDirectory, string actor);
    public ImportResult ImportPackage(ImportOptions options, string actor);
    public IReadOnlyList<PackageRecord> ListPackages();
    public IReadOnlyList<ConsolidationConflict> ListConflicts(int? status = null);
    public OperationResult ResolveConflict(string conflictId, ConflictResolution resolution, string actor);
}
```

Manifest fields (fixed order):

| Field | Type | Rule |
|---|---|---|
| `formatVersion` | int | `1`. |
| `packageId` | string(32 hex) | Deterministic hash per §3.4. |
| `createdAtUtc` | string | Informational; excluded from `packageId`. |
| `appVersion` | string | Informational; excluded from `packageId`. |
| `siteCode` | string | `Sites.Code`; non-empty. |
| `siteNameAr`, `siteNameFr` | string | Informational. |
| `wilayaCode`, `nationalCode` | string? | Informational; may be null. |
| `windowFromUtc`, `windowToUtc` | string | `[from, to)`; UTC ISO-8601. |
| `employeeCount`, `deviceCount`, `punchCount`, `adjustmentCount` | int | Must match NDJSON counts. |
| `minPunchUtc`, `maxPunchUtc` | string? | Null when zero punches. |
| `contentHash` | string | SHA-256 of `data.ndjson.gz`. |
| `containsPersonalData` | bool | `true`. |
| `unresolvedPunchCount` | int | Punches with empty matricule. |
| `deviceSerials` | string[] | Sorted ordinal; duplicates impossible in one site. |

NDJSON record types (one JSON object per line; keys fixed):

- `site`: `siteCode`, `nameAr`, `nameFr`, `wilayaCode`, `nationalCode`, `isHeadquarters`.
- `employee`: `matricule`, `siteCode`, `devicePin` (nullable), `fullNameAr`, `firstNameFr`, `lastNameFr`, `departmentCode`, `workRuleCode`, `scheduledWorkDays`, `hireDate`, `endDate`, `isArchived`, `updatedAtUtc`.
- `device`: `serialNumber`, `siteCode`, `name`, `modelName`, `firmwareVersion`, `isEnabled`, `isArchived`, `lastSeenUtc`. **No `IpAddress`, `CommKey`, `MacAddress`.**
- `punch`: `siteCode`, `deviceSerial`, `devicePin`, `employeeMatricule`, `punchTimeUtc`, `punchTimeLocal`, `verifyMode`, `punchState`, `workCode`, `source`, `isManual`, `dedupeKey`, `qualityFlags`, `receivedAtUtc`.
- `adjustment`: `siteCode`, `originAdjustmentId`, `employeeMatricule`, `workDate`, `kind`, `startLocal`, `endLocal`, `hoursCreditedMinutes`, `reference`, `note`, `createdBy`, `createdAtUtc`, `isArchived`, `supersedesAdjustmentId`, `contentHash`.

Export rules:

- Window filters `PunchTimeUtc` and `WorkDate`; employees/devices referenced by exported punches/adjustments are included even if archived, with `isArchived=true`.
- `punch.dedupeKey` is the origin `DedupeKey`; `ConsolidationKey` is computed on import as `siteCode|deviceSerial|employeeMatricule|dedupeKey`.
- Package written to temp, flushed, hashed, then atomically moved to `<destination>/<packageId>.fpcpkg`. If an identical file exists, return it with success. Set Unix mode `0600` on Linux/macOS; on Windows rely on user-profile ACL and document it.
- Export is read-only on the DB. Audit `consolidation.exported`.

Import rules:

1. Validate ZIP: exactly two entries, no absolute paths/`..`, entry sizes bounded (manifest ≤ 64 KiB, gzip ≤ 512 MiB compressed, decompressed NDJSON ≤ 2 GiB), SHA-256 match, `formatVersion=1`, counts match. Errors: `consolidation.invalid-package`, `consolidation.hash-mismatch`, `consolidation.schema-unsupported`, `consolidation.limit`.
2. If `(packageId, Import)` already `Completed` → `AlreadyImported=true`, no writes, audit `consolidation.import.skipped`.
3. Preflight duplicate device serials: package serial vs local `Devices.SerialNumber` at a different `SiteCode` → `consolidation.duplicate-device-serial`; abort before writes. Same site/same serial → update model/firmware only; never credentials/IP.
4. Open `BEGIN IMMEDIATE`; `busy_timeout=5000`. Insert site if absent (by `Code`); if `NationalCode` collides with another site, conflict row and abort. Insert/update employees by `(siteCode, matricule)`: incoming `updatedAtUtc > local` updates mutable fields; older skips; equal timestamp + different hash → `ConsolidationConflicts` row, local wins, import continues. Never change local `Employees.Id`.
5. Insert punches `INSERT OR IGNORE` by `ConsolidationKey`; set `SiteCode`, `EmployeeId` resolved by `(siteCode, matricule)`, `QualityFlags`; never update an existing punch. Count inserted vs skipped.
6. Insert adjustments by `(siteCode, originAdjustmentId)`; same id + same `contentHash` skip; same id + different hash → conflict row, no overwrite. New id imports. Unresolved same employee/date/kind from a different origin → conflict row `EntityType=AttendanceAdjustment`, natural key `site|matricule|yyyy-MM-dd|kind`; both rows remain with `IsConflict=1`, excluded from totals.
7. Commit; on any exception rollback, insert `ConsolidationImportRuns` `Failed` in a separate transaction, leave zero partial business rows, return error. `DryRun=true` performs validation and counts only, writes no business rows, records `Status=DryRun`.
8. Successful package copied to `DataDirectory/Consolidation/Archive/<packageId>.fpcpkg`; failed package kept in `.../Failed/` for diagnosis. Audit `consolidation.imported` with counts and conflicts.

Conflict resolution (`ResolveConflict`):

- `KeepLocal`: mark conflict resolved; incoming data not applied.
- `AcceptIncoming`: for employees, apply the incoming snapshot with a new `UpdatedAtUtc`; for adjustments, import incoming as a new local adjustment and archive the superseded local row with reason; never overwrite the origin row.
- `Supersede`: create a new audited adjustment with `SupersedesAdjustmentId` pointing at the chosen row; archive the other; recompute sheet.
- All resolution actions require actor and note; audit `consolidation.conflict.resolved`.

### 5.3 RTL text contract

```csharp
namespace FocusPlus.Application.Localization;

public enum TextDirection { LeftToRight, RightToLeft }

public interface IRtlTextService
{
    bool IsArabic { get; }
    TextDirection Direction { get; }
    string IsolateLtr(string value);                 // U+2066 ... U+2069
    string FormatDate(DateTime value);               // yyyy-MM-dd, ASCII digits
    string FormatTime(DateTime value);               // HH:mm, ASCII digits
    string FormatDuration(int minutes);              // HH:mm, ASCII digits
    string FormatInteger(long value);                // ASCII digits, invariant grouping
    string FormatDecimal(decimal value, int decimals = 2);
    string FormatMatricule(string value);            // no grouping, LTR-isolated
    string StripBidi(string value);                  // removes U+202A..U+202E, U+2066..U+2069
}
```

Rules:

- Arabic UI uses Western digits `0-9` for all numeric, time, date, serial, matricule, IP, and amount values. No Arabic-Indic digits in v1; this keeps device output, payroll, and audit comparable.
- Every technical value rendered inside Arabic text must pass through `IsolateLtr` (or a converter using it). The isolated run sets `FlowDirection=LeftToRight`.
- `StripBidi` is applied before CSV/HTML export and before writing audit `Details`. Database values never contain bidi controls.
- Desktop mapping: `RtlTextService.Direction` maps to `Avalonia.Media.FlowDirection` in a converter; Application does not reference Avalonia.
- Arabic font: bundle `src/FocusPlus.Desktop/Assets/Fonts/NotoNaskhArabic-Regular.ttf` (OFL) with license file; set it as the Arabic font family with a Latin fallback. No NuGet dependency.

### 5.4 Edge cases

| Case | Required behavior | Code |
|---|---|---|
| Database file missing | `CreateDailyBackup` fails; no empty DB is fabricated; UI says database not initialized. | `backup.database-missing` |
| Database file zero bytes | Fail before `VACUUM INTO`; do not create a backup. | `backup.database-empty` |
| Disk full / permission denied | Fail before writing; delete temp; old backups untouched; no prune. | `backup.disk-full`, `backup.io` |
| Backup succeeds, metadata write fails | Keep the valid file; return warning; startup reconciliation adopts it by hash. | `backup.metadata-write-failed` |
| Prune encounters locked file | Leave file and `Status=Present`; report `FailedFiles`; retry next run. | `backup.prune-locked` |
| Restore source hash mismatch | Abort before touching live DB; safety copy not needed but still created first. | `restore.hash-mismatch` |
| Restore candidate fails integrity | Restore safety copy; live DB healthy; report rollback. | `restore.rollback-completed` |
| Restore safety copy fails | Abort restore; live DB untouched. | `restore.safety-failed` |
| Duplicate device serial across sites | Reject whole package before writes; list local and incoming site. | `consolidation.duplicate-device-serial` |
| Duplicate package | Return `AlreadyImported=true`; zero inserts; audit skip. | `consolidation.duplicate-package` |
| Conflicting manual edits | Keep both origin rows; mark `IsConflict=1`; create conflict row; exclude from totals until resolved; never overwrite. | `consolidation.conflict` |
| Older employee snapshot | Skip; local newer data wins; no conflict row unless same timestamp + different hash. | — |
| Device clock drift > 5 min | Record `ClockDriftMinutes`, log hardware operation, show badge; do not rewrite historical punches; mark new punches `QualityFlags bit0` if timestamp is > 24 h ahead or before site activation; exclude from totals. | `device.clock-drift` |
| Punch with unknown PIN | Import with `employeeMatricule=""`, keep `DevicePin`; exclude from employee totals; show unresolved PIN list. | — |
| Empty database / empty package | Valid; show localized empty state; export manifest with zero counts and `minPunchUtc=null`. | — |
| Arabic numerals / bidi | ASCII digits everywhere; technical values LTR-isolated; exports stripped of bidi controls. | — |
| Invalid ZIP / path traversal | Reject before extraction; no filesystem writes outside package temp. | `consolidation.invalid-package` |
| Import interrupted / crash | SQLite transaction rolls back; import run marked Failed on next start; no partial rows. | `consolidation.interrupted` |
| Two PCs pull same device | Same `DedupeKey`; second insert ignored; no duplicate. | — |
| Employee end date / biometric erasure | Clear biometric fields, log erasure, queue device-user deletion; archived employee retained. | `compliance.erasure-pending` |
| ANPDP declaration missing | Block device pull and biometric enrollment; allow local read-only review of existing data. | `compliance.declaration-missing` |
| Work rule violates legal limit | Reject save with field-level error; existing invalid rules flagged, not silently changed. | `compliance.rule-amplitude`, `compliance.rule-multiplier`, `compliance.rule-weekly` |
| Annual leave over accrual | Block unless SuperAdmin override with reason; audit both. | `compliance.leave-exceeded` |

### 5.5 UI state contract

| Surface | Loading | Empty | Error | Success | Disabled/submitting |
|---|---|---|---|---|---|
| Settings → Backup | List skeleton; “Checking…” | “No backups yet. First automatic backup runs at 20:00 or click Backup now.” | InfoBar with code + Retry; restore confirmation remains closed | InfoBar “Backup created · 12:34 · 4.2 MB”; list refreshes | Backup/Restore/Prune buttons disabled; progress ring; no duplicate command |
| Settings → Consolidation | Import/export progress with counts | “No packages yet. Export from a site PC, then import here.” | InfoBar with code; failed package path shown; Dry run remains available | “Imported 1,240 punches, 0 conflicts”; conflict badge | Import/Export disabled; cancel only between files, never mid-transaction |
| Employees/Devices/Attendance drawer | Drawer skeleton | Row-level “No data for this day” | Inline field error; no modal cascade | Drawer remains open with saved values; audit stamp | Save disabled while invalid or saving |
| Device card | “Checking…” | “No terminals configured. Scan LAN or add by IP.” | Human state (`Offline`, `CommKey required`, `Error`) + Retry/Edit credentials | `Verified` + serial/firmware + drift badge | Pull/sync actions disabled while a command runs |
| Attendance sheet | Range progress | “No employees / no punches for this range.” | Banner with range invalid reason | Rows + totals; conflicts badge | Export disabled while building |

Drawers: one in-page drawer per workspace, no nested popups. `Esc` closes; focus trap; `Enter` submits only when valid; actor is always the session user; reason required for manual punch and conflict resolution.

---

## 6. File-level task graph

Tags: `backend` (Domain/Data/Application/tests), `frontend` (Desktop), `qa`. Dependencies are by ID. Parallel-safe groups are marked; do not run two tasks that edit the same file concurrently.

### Group G1 — schema, domain, repositories (serial, then parallel)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| B01 | backend | `src/FocusPlus.Data/Migrations/002_consolidation.sql` (new), `src/FocusPlus.Data/Database.cs` | Add tables/columns/indexes from §4; bump `CurrentSchemaVersion=2`; preflight duplicate-matricule check; migration failure message. | — |
| B02 | backend | `src/FocusPlus.Domain/Entities/System.cs`, `src/FocusPlus.Domain/Entities/Organization.cs`, `src/FocusPlus.Domain/Entities/Attendance.cs`, `src/FocusPlus.Domain/Entities/Device.cs`, `src/FocusPlus.Domain/Enums.cs` | Add `BackupKind/Status`, `DeviceState`, `AttendanceStatus.Conflict`, `QualityFlags`, `BiometricErasedAtUtc`, `SiteCode`, `OriginAdjustmentId`, `ContentHash`, `SupersedesAdjustmentId`, `IsConflict`, `BreakMinutes`, `BreakCountedMinutes`, `IsContinuousShift`, `WeeklyTargetMinutes`. | B01 |
| B03 | backend | `src/FocusPlus.Data/Repositories/BackupRepository.cs` (new), `src/FocusPlus.Data/Repositories/ConsolidationRepository.cs` (new), `src/FocusPlus.Data/Repositories/EmployeeRepository.cs`, `src/FocusPlus.Data/Repositories/DeviceRepository.cs`, `src/FocusPlus.Data/Repositories/PunchRepository.cs`, `src/FocusPlus.Data/Repositories/AttendanceRepositories.cs`, `src/FocusPlus.Data/Repositories/OrganizationRepositories.cs` | Site-scoped employee lookup; backup metadata CRUD (no delete); package/run/conflict repositories; punch import insert; adjustment origin/hash; site national/wilaya fields. | B01, B02 |
| B04 | backend | `src/FocusPlus.Data/SeedData.cs`, `src/FocusPlus.Application/Services/OrganizationService.cs` | Seed site with `Code` only; leave `NationalCode`/`WilayaCode` null; no invented identifiers; validate site code uniqueness. | B01 |
| B05 | backend | `src/FocusPlus.Domain/Compliance/WorkTimeCompliance.cs` (new), `src/FocusPlus.Domain/Compliance/LeaveEntitlementCalculator.cs` (new) | 40h/2400 min, 12h/720 min amplitude, overtime 480 min, multiplier 1.5, Friday rest, 2.5 days/month cap 30; return structured violations. | B02 |

### Group G2 — backup (parallel with G3/G4 after G1)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| B06 | backend | `src/FocusPlus.Data/BackupService.cs` | Implement `CreateDailyBackup`, `PruneBackups`, `CreateSafetyBackup`, `Restore`; atomic temp/hash/quick_check; metadata reconciliation; serialized gate. Keep legacy `CreateBackup` as a manual wrapper or remove and update callers. | B03 |
| B07 | backend | `src/FocusPlus.Application/Services/BackupSchedulerService.cs`, `src/FocusPlus.Application/Services/SettingsService.cs` | Daily timer + startup catch-up; call `CreateDailyBackup` then `PruneBackups`; settings keys; manual backup/prune commands; audit. | B06 |
| B08 | backend | `src/FocusPlus.Application/AppHost.cs`, `src/FocusPlus.Application/Services/ComplianceService.cs` (new) | Wire `ConsolidationService`, `ComplianceService`, `BackupRepository`; startup reconciliation; dispose order. | B06, B03 |
| B09 | backend | `tests/FocusPlus.Tests/BackupRetentionTests.cs` (new), `tests/FocusPlus.Tests/BackupLifecycleTests.cs`, `tests/FocusPlus.Tests/TestInfrastructure.cs` | Rewrite 6-slot tests to daily/30/12 policy; idempotence; failed backup no prune; locked file; restore rollback; metadata orphan adoption. | B06, B07 |

### Group G3 — consolidation (parallel with G2/G4)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| B10 | backend | `src/FocusPlus.Domain/Consolidation/ConsolidationManifest.cs` (new), `src/FocusPlus.Domain/Consolidation/PackageRecords.cs` (new), `src/FocusPlus.Application/Consolidation/CanonicalJson.cs` (new), `src/FocusPlus.Application/Consolidation/PackageBuilder.cs` (new), `src/FocusPlus.Application/Consolidation/PackageReader.cs` (new) | Fixed-order manifest/NDJSON, canonical serialization, SHA-256, deterministic `packageId`, ZIP/gzip with fixed timestamps, size limits, path traversal rejection. | B02 |
| B11 | backend | `src/FocusPlus.Application/Services/ConsolidationService.cs` (new) | `ExportPackage`: query window, resolve site/employee/device, exclude secrets, write atomic package, audit. | B10, B03 |
| B12 | backend | `src/FocusPlus.Application/Services/ConsolidationService.cs` (same file; serialize with B11), `src/FocusPlus.Data/Repositories/ConsolidationRepository.cs` | `ImportPackage`: validation, duplicate detection, transaction, employee/punch/adjustment merges, conflicts, dry run, archive failed package, audit. | B11 |
| B13 | backend | `src/FocusPlus.Application/Services/AttendanceService.cs`, `src/FocusPlus.Domain/Attendance/AttendanceCalculator.cs`, `src/FocusPlus.Domain/Attendance/AttendanceSheetBuilder.cs` | Exclude `QualityFlags bit0`, unresolved conflicts, and unresolved punches from totals; show conflict status; count Friday/off-day correctly; audit manual adjustments. | B02, B03 |
| B14 | backend | `tests/FocusPlus.Tests/ConsolidationTests.cs` (new), `tests/FocusPlus.Tests/ComplianceTests.cs` (new) | Determinism, idempotence, duplicate serial, conflicting edits, rollback, dry run, compliance validators, leave accrual. | B11, B12, B05 |

### Group G4 — RTL service (parallel with G2/G3)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| B15 | backend | `src/FocusPlus.Application/Localization/IRtlTextService.cs` (new), `src/FocusPlus.Application/Localization/RtlTextService.cs` (new), `src/FocusPlus.Application/AppSession.cs`, `src/FocusPlus.Application/Labels.cs` | ASCII digits, LTR isolation, bidi stripping, date/time/duration/amount formatting, Arabic-aware labels and error messages including new codes. | B02 |
| B16 | backend | `tests/FocusPlus.Tests/RtlTextServiceTests.cs` (new), `tests/FocusPlus.Tests/LangKeyParityTests.cs` (new) | Formatting, isolation markers, stripping, AR/FR key parity. | B15 |

### Group G5 — desktop/UI (after G2–G4)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| F01 | frontend | `src/FocusPlus.Desktop/App.axaml.cs`, `src/FocusPlus.Desktop/Program.cs` | Call `Host.StartBackgroundServices()` after `Host.Initialize()`; dispose host on shutdown; surface startup failure with localized message. | B08 |
| F02 | frontend | `src/FocusPlus.Desktop/Converters/BidiConverters.cs` (new), `src/FocusPlus.Desktop/App.axaml`, `src/FocusPlus.Desktop/Styles.axaml`, `src/FocusPlus.Desktop/Resources/Theme.axaml`, `src/FocusPlus.Desktop/Assets/Fonts/NotoNaskhArabic-Regular.ttf` (new), `src/FocusPlus.Desktop/Assets/Fonts/OFL.txt` (new) | RTL/LTR converters, Arabic font resource, mirrored spacing/icons, no bidi in technical cells. | B15 |
| F03 | frontend | `src/FocusPlus.Desktop/Views/ShellView.axaml`, `src/FocusPlus.Desktop/Views/ShellView.axaml.cs`, `src/FocusPlus.Desktop/Views/EmployeesView.axaml`, `src/FocusPlus.Desktop/Views/EmployeesView.axaml.cs`, `src/FocusPlus.Desktop/Views/DevicesView.axaml`, `src/FocusPlus.Desktop/Views/DevicesView.axaml.cs`, `src/FocusPlus.Desktop/Views/AttendanceView.axaml`, `src/FocusPlus.Desktop/Views/AttendanceView.axaml.cs` | Wire row selection → `OpenDetailCommand`; fix unreachable drawers; `Esc` close; focus return; mirror drawer side in RTL. | F02, B02 |
| F04 | frontend | `src/FocusPlus.Desktop/Views/SettingsView.axaml`, `src/FocusPlus.Desktop/Views/SettingsView.axaml.cs`, `src/FocusPlus.Application/ViewModels/SettingsViewModel.cs` | Backup panel: last/next, create, prune, restore confirm, failure InfoBar; compliance panel: declaration receipt, notice AR/FR, purpose/retention, erasure log. | F01, B07, B08 |
| F05 | frontend | `src/FocusPlus.Desktop/Views/ConsolidationView.axaml` (new), `src/FocusPlus.Desktop/Views/ConsolidationView.axaml.cs` (new), `src/FocusPlus.Application/ViewModels/ConsolidationViewModel.cs` (new), `src/FocusPlus.Desktop/Views/ShellView.axaml.cs` | Export/import workflow, site/window pickers, dry run, package list, conflict list + resolve; add Settings sub-nav entry. | F04, B11, B12 |
| F06 | frontend | `src/FocusPlus.Desktop/Views/DevicesView.axaml`, `src/FocusPlus.Application/ViewModels/DevicesViewModel.cs`, `src/FocusPlus.Application/Services/DeviceService.cs` | State pills `Verified/CommKey required/Unverified/Offline/Error`; drift badge; locked-device action; LTR serial/IP; no raw exceptions. | F03, B02 |
| F07 | frontend | `src/FocusPlus.Desktop/Views/AttendanceView.axaml`, `src/FocusPlus.Application/ViewModels/AttendanceViewModel.cs` | 7-day/monthly toggle, totals row, conflict badge, missing-OUT detail, manual punch drawer validation, export buttons. | F03, B13 |
| F08 | frontend | `src/FocusPlus.Desktop/Resources/Lang.ar.axaml`, `src/FocusPlus.Desktop/Resources/Lang.fr.axaml`, `src/FocusPlus.Desktop/Lang.cs` | All new keys in both languages; no English fallback; empty/error/loading strings; compliance notice; delete unused keys. | F04, F05 |
| F09 | backend | `tests/FocusPlus.Tests/ViewModelTests.cs`, `tests/FocusPlus.Tests/ServiceTests.cs`, `tests/FocusPlus.Tests/DatabaseAndSeedTests.cs`, `tests/FocusPlus.Tests/DeviceTests.cs`, `tests/FocusPlus.Tests/AttendanceCalculatorTests.cs` | Drawer open/close, backup/consolidation VM states, site-scoped matricule, device states, sheet ranges, compliance gates. | F03–F07 |

### Group G6 — verification (serial)

| ID | Tag | Files (exact) | Work | Depends |
|---|---|---|---|---|
| Q01 | qa | `FocusPlus.slnx`, no source edits | Run §7 build/test; report exact output and counts. | all |
| Q02 | qa | no source edits | Headless launch; UI walkthrough: login, AR/FR switch, three drawers, backup, export/import fixture, device states, empty DB. | Q01 |
| Q03 | qa | no source edits | Grep audit: no `DELETE FROM` in repositories; no bidi controls in exported CSV/HTML; no `TODO`/stub; no raw exception text in UI strings. | Q01 |

**Parallel-safe execution:** G1 serial (B01→B02→B03→B04/B05). Then G2, G3, G4 in parallel. G5 after the backend tasks it depends on; F04/F05/F06/F07 can run in parallel once F01–F03 land, but F05 and F04 share `SettingsView`/`SettingsViewModel` only if the consolidation UI is a separate view—keep F05 using a separate `ConsolidationView` and a single nav entry to avoid file conflict. F08 last in G5. G6 last.

---

## 7. Verification gates

Run from `/home/focus/FocusPlus`, in this order. Do not claim completion without the output.

```bash
dotnet build FocusPlus.slnx -c Release
```

Expected: exit 0, 0 warnings, 0 errors. `Directory.Build.props` treats nullable warnings as errors. Any new warning fails the gate.

```bash
dotnet test tests/FocusPlus.Tests/FocusPlus.Tests.csproj -c Release --no-build
```

Expected: exit 0, 0 failed, 0 skipped; total ≥ 109 (existing) plus new backup/consolidation/RTL/compliance tests. `--no-build` is valid only after the build above. Report the exact total.

```bash
xvfb-run -a dotnet src/FocusPlus.Desktop/bin/Release/net10.0/FocusPlus.dll
```

Expected: GUI starts on the virtual display with no unhandled exception; login screen renders in Arabic RTL by default. Manually verify: language toggle to French (LTR and back), open employee/device/attendance drawers, run Backup now, import a fixture package, and confirm localized empty/error states. Close the window to exit 0. In CI, wrap with `timeout 30s` only if the runner cannot close the window; a timeout exit is not a substitute for the manual check.

Additional gate checks (not substitutes):

```bash
grep -rn "DELETE FROM" src/FocusPlus.Data/Repositories
grep -rn "TODO\|FIXME\|NotImplementedException" src tests
```

Expected: no `DELETE FROM` against business tables (backup metadata uses `UPDATE`), no shipped placeholders. If a legitimate SQL `DELETE` exists for a temp table, document it in code and exclude it explicitly.

---

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Backup retention deletes a needed historical copy | Medium | High | 30 daily + 12 monthly anchors + 7-day safety window; prune only after successful backup; metadata retained; restore tested; pre-migration backup. |
| Per-PC divergence: two PCs edit the same employee/adjustment | High in multi-PC sites | Medium | One capture/master PC per site in v1; conflicts recorded, never overwritten; HQ resolves by superseding adjustment; audit. |
| Duplicate device serial across sites | Low | High | Hard preflight rejection of the whole package; site+serial key; operator must correct inventory. |
| Device clock drift corrupts attendance | Medium | High | 5-minute drift badge; record drift; do not rewrite history; flag >24h future / pre-activation punches; exclude from totals; clock sync action. |
| Arabic RTL regressions (reversed serial/IP, broken columns) | High without tests | Medium | `IRtlTextService` + converters; ASCII digits; AR/FR screenshots; key parity test; no bidi in exports. |
| Drawers remain unreachable because views bypass VMs | Medium | High | Explicit selection handlers and command tests; manual gate; no code-behind business logic. |
| Automatic backup still not running after install | Medium | High | `App.axaml.cs` startup call + test; startup catch-up; visible last/next backup status; failure InfoBar. |
| Migration breaks matricule uniqueness expectations | Medium | High | Preflight duplicate check; site-scoped index; pre-migration backup; rollback procedure; tests with duplicate matricules across sites. |
| Consolidation package contains personal data on removable media | Medium | High | Owner-only permissions; no cloud/email; manifest `containsPersonalData`; controlled transfer procedure; audit; encryption deferred and documented as residual risk. |
| ANPDP filing/authorisation ambiguity | Medium | High | Record both declaration and authorisation fields; block biometric processing until receipt; legal counsel validates before go-live; official text verification task. |
| Payroll/legal mismatch from existing rules below 1.5 multiplier | Medium | Medium | Flag invalid existing rules; do not silently change historical values; require correction before payroll preview. |
| SQLite corruption or disk failure on a site PC | Low | High | WAL, daily hash-verified backups, restore rollback, safety copies; off-PC package export; health check on startup. |
| Scope creep into central server | Medium | Medium | v1 file contract only; `ISyncTransport` seam specified; no server code in v1. |
