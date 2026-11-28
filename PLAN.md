# FocusPlus working plan (live)

Status date: 2026-11-28 · Repo: `oguenfoude/FocusAttendece` · Branch: `main`

## Frame — acceptance criteria for this phase
1. Every PC runs the app locally with its own SQLite DB; no server required.
2. A verified backup is created **every day automatically**; old backups are pruned and the newest 30 daily backups are kept.
3. Arabic RTL is first-class: shell mirrors, drawers on the correct side, technical values (IP, serial, time, numbers) stay LTR-isolated; French LTR remains perfect.
4. All critical data-update flows are reachable from the GUI: terminal, employee, attendance detail, shift rule, backup/restore.
5. Large-company path is specified: per-PC DB + portable consolidation packages merged by site/serial/matricule/dedupe key.
6. Verification: clean build, all tests, GUI starts headless, no unhandled errors.

## Pipeline
- [x] Frame — acceptance criteria above
- [x] Research — Algeria 40h week, 12h daily cap, overtime rules, ANPDP biometric declaration, enterprise multi-site needs
- [x] Spec — `docs/SPEC.md` written with 15 acceptance criteria, legal requirements, consolidation contract, task graph
- [x] Design — RTL/backup UX state contract in SPEC §5.5; no separate design doc needed
- [x] Build — daily backup/retention + safe restore + policy editor (Data/Application/tests); drawers/RTL/exports/startup (Desktop). Disjoint files, integrated. Consolidation package service is specified in SPEC.md but not implemented in this slice.
- [x] Verify — build 0 warnings/0 errors, 133 tests passed (10 consecutive green runs), headless GUI no runtime errors, AR/FR 119 identical keys, live ZKTeco probe Verified with key 0, probe exit codes correct.
- [x] Review — reviewer found RTL shell-width blocker and 6 major findings; all fixed and re-reviewed, verdict SHIP.
- [ ] Deploy — not requested; installers remain a later phase.

## Defects resolved in this phase
- `App.axaml.cs` now starts/disposes background services; app log confirms monitor + daily backup tick.
- `BackupService` now creates one verified daily backup/day, keeps newest 30 + 12 monthly anchors, prunes old payloads safely, audits restore on every path.
- `EmployeesView`, `DevicesView`, `AttendanceView` now open their drawers from row selection; Esc/focus trap implemented.
- RTL now mirrors the shell (including column widths), drawers, toolbars and technical values; startup reads persisted language; AR/FR key sets identical (119).
- CSV/HTML export buttons wired in Attendance view; raw exception text removed from UI.
- `zk-probe` retries transient misses and returns non-zero when no evidence; live device verified.

## Remaining known gap
- Consolidation package export/import is fully specified in `docs/SPEC.md` but not implemented.
- Installers/auto-update and the ANPDP compliance screen remain later phases.

## Decisions
- No central server in v1 (user requirement: local SQLite on every PC). Consolidation via export/import packages.
- Backup retention: newest 30 daily files; manual "Backup now" can force a same-day replacement with a safety copy before restore.
- Legal figures are configuration defaults, not hard-coded law claims; OPGI must confirm its own collective rules.

Keep this file current as phases complete.
