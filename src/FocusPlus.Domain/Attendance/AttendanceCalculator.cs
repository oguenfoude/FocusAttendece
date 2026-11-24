using FocusPlus.Domain.Entities;

namespace FocusPlus.Domain.Attendance;

/// <summary>
/// Deterministic attendance engine.
/// Rules implemented:
///   • 4-scan pattern  (in / lunch out / lunch in / out) → exact sum of both presence intervals.
///   • 2-scan pattern  (in / out) → span, with the configured lunch break deducted when it crosses lunch.
///   • multi-scan / free-time → sequential IN/OUT pairs are summed.
///   • missing out-scan → flagged, HR can add an audited manual completion punch.
///   • off-day scans → recorded and labelled as extra / free-time hours (never dropped).
///   • 7 days a week, 365 days a year.
/// </summary>
public static class AttendanceCalculator
{
    public static AttendanceDayResult Calculate(AttendanceDayInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rule = input.Rule ?? WorkRule.CreateOfficialDefault();
        var date = input.Date.Date;
        var notes = new List<AttendanceNote>();

        var adjustments = input.Adjustments.Where(a => !a.IsArchived && a.WorkDate.Date == date).ToList();
        var manualPunches = adjustments
            .Where(a => a.Kind == AdjustmentKind.ManualPunch && a.StartLocal.HasValue)
            .Select(a => a.StartLocal!.Value)
            .ToList();

        var devicePunches = input.Punches
            .Where(p => p.Date == date)
            .OrderBy(p => p)
            .ToList();

        var merged = devicePunches.Concat(manualPunches).OrderBy(p => p).ToList();
        var scans = ApplyAntiBounce(merged, rule.AntiBounceMinutes, out var filteredCount);

        if (filteredCount > 0)
        {
            notes.Add(AttendanceNote.AntiBounceFiltered);
        }

        var fullDayLeave = adjustments.FirstOrDefault(a => a.IsFullDayLeave);
        var credited = ComputeCreditedMinutes(rule, adjustments, date);

        if (manualPunches.Count > 0)
        {
            notes.Add(AttendanceNote.ManualPunchUsed);
        }

        if (scans.Count == 0)
        {
            if (fullDayLeave is not null)
            {
                notes.Add(AttendanceNote.LeaveCredited);
                return BuildEmpty(input, date, rule, AttendanceStatus.Leave, credited, notes);
            }

            if (credited > 0 && input.IsScheduledDay)
            {
                notes.Add(AttendanceNote.MissionCredited);
                return BuildEmpty(input, date, rule, AttendanceStatus.Mission, credited, notes);
            }

            return BuildEmpty(
                input,
                date,
                rule,
                input.IsScheduledDay ? AttendanceStatus.Absent : AttendanceStatus.RestDay,
                0,
                notes);
        }

        var slots = AssignSlots(date, rule, scans);
        var intervals = BuildIntervals(scans);
        var missingOut = scans.Count % 2 == 1;

        if (scans.Count == 1)
        {
            notes.Add(AttendanceNote.SingleScan);
        }

        if (missingOut)
        {
            notes.Add(AttendanceNote.MissingOutScan);
        }

        var totalMinutes = (int)Math.Floor(intervals.Sum(i => (i.End - i.Start).TotalMinutes));

        // Automatic lunch deduction: only when the day is a plain 2-scan crossing the lunch window.
        var lunchDeducted = 0;
        if (rule.Mode == WorkMode.FixedShift
            && rule.AutoDeductLunchBreak
            && rule.LunchDeductionMinutes > 0
            && slots.LunchOut is null
            && slots.LunchIn is null)
        {
            var lunchStart = date + rule.LunchStart;
            var lunchEnd = date + rule.LunchEnd;
            var overlap = intervals.Sum(i => OverlapMinutes(i.Start, i.End, lunchStart, lunchEnd));
            if (overlap > 0)
            {
                lunchDeducted = Math.Min(rule.LunchDeductionMinutes, (int)Math.Floor(overlap));
                totalMinutes -= lunchDeducted;
                notes.Add(AttendanceNote.LunchDeducted);
            }
        }

        if (totalMinutes < 0)
        {
            totalMinutes = 0;
        }

        var regular = 0;
        var extra = 0;
        if (input.IsScheduledDay)
        {
            if (rule.Mode == WorkMode.FixedShift)
            {
                var windowStart = date + rule.MorningStart;
                var windowEnd = date + rule.EveningEnd;
                var windowOverlap = intervals.Sum(i => OverlapMinutes(i.Start, i.End, windowStart, windowEnd));
                regular = Math.Max(0, (int)Math.Floor(windowOverlap) - lunchDeducted);
                regular = Math.Min(regular, totalMinutes);
                extra = Math.Max(0, totalMinutes - regular);
            }
            else
            {
                regular = totalMinutes;
            }
        }
        else
        {
            extra = totalMinutes;
            notes.Add(AttendanceNote.OffDayWorked);
        }

        var late = ComputeLateMinutes(date, rule, slots, input.IsScheduledDay);
        var earlyLeave = ComputeEarlyLeaveMinutes(date, rule, slots, input.IsScheduledDay);
        var missionCredit = adjustments
            .Where(a => a.Kind is AdjustmentKind.Mission or AdjustmentKind.ExitPermit)
            .Sum(a => a.HoursCreditedMinutes);
        earlyLeave = Math.Max(0, earlyLeave - missionCredit);

        if (late > 0)
        {
            notes.Add(AttendanceNote.LateArrival);
        }

        if (earlyLeave > 0)
        {
            notes.Add(AttendanceNote.EarlyLeave);
        }

        if (regular + extra >= rule.StandardDailyTargetMinutes && rule.StandardDailyTargetMinutes > 0)
        {
            notes.Add(AttendanceNote.TargetReached);
        }
        else if (input.IsScheduledDay && rule.Mode == WorkMode.FixedShift)
        {
            notes.Add(AttendanceNote.BelowTarget);
        }

        var status = ResolveStatus(input.IsScheduledDay, late, earlyLeave, missingOut);
        var hourly = input.HourlyRateDzd;
        var overtime = input.OvertimeMultiplier <= 0 ? 1m : input.OvertimeMultiplier;

        var payable = regular / 60m * hourly + extra / 60m * hourly * overtime + credited / 60m * hourly;
        var earned = Math.Round(payable, 2);
        var deduction = Math.Round((late + earlyLeave) / 60m * hourly, 2);

        return new AttendanceDayResult
        {
            Date = date,
            IsScheduledDay = input.IsScheduledDay,
            Status = status,
            MorningIn = slots.MorningIn,
            LunchOut = slots.LunchOut,
            LunchIn = slots.LunchIn,
            EveningOut = slots.EveningOut,
            ExtraScans = slots.ExtraScans,
            RawScanCount = devicePunches.Count + manualPunches.Count,
            RetainedScanCount = scans.Count,
            ManualScanCount = manualPunches.Count,
            BreakMinutes = lunchDeducted,
            RegularWorkedMinutes = regular,
            ExtraWorkedMinutes = extra,
            CreditedMinutes = credited,
            TotalNetMinutes = regular + extra,
            EffectiveTotalMinutes = regular + extra + credited,
            LateMinutes = late,
            EarlyLeaveMinutes = earlyLeave,
            EarnedAmountDzd = earned,
            DeductionAmountDzd = deduction,
            Notes = notes
        };
    }

    /// <summary>Filters accidental rapid double scans without touching the raw log.</summary>
    public static List<DateTime> ApplyAntiBounce(IEnumerable<DateTime> punches, int antiBounceMinutes, out int filtered)
    {
        filtered = 0;
        var ordered = punches.OrderBy(p => p).ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var kept = new List<DateTime>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var delta = ordered[i] - kept[^1];
            if (antiBounceMinutes > 0 && delta.TotalMinutes < antiBounceMinutes)
            {
                filtered++;
                continue;
            }

            kept.Add(ordered[i]);
        }

        return kept;
    }

    private static AttendanceDayResult BuildEmpty(
        AttendanceDayInput input,
        DateTime date,
        WorkRule rule,
        AttendanceStatus status,
        int credited,
        List<AttendanceNote> notes)
    {
        var hourly = input.HourlyRateDzd;
        return new AttendanceDayResult
        {
            Date = date,
            IsScheduledDay = input.IsScheduledDay,
            Status = status,
            CreditedMinutes = credited,
            EffectiveTotalMinutes = credited,
            EarnedAmountDzd = Math.Round(credited / 60m * hourly, 2),
            Notes = notes
        };
    }

    private static AttendanceStatus ResolveStatus(bool scheduled, int late, int earlyLeave, bool missingOut)
    {
        if (missingOut)
        {
            return AttendanceStatus.MissingOutScan;
        }

        if (!scheduled)
        {
            return AttendanceStatus.ExtraDay;
        }

        if (late > 0 && earlyLeave > 0)
        {
            return AttendanceStatus.LateAndEarlyLeave;
        }

        if (late > 0)
        {
            return AttendanceStatus.Late;
        }

        if (earlyLeave > 0)
        {
            return AttendanceStatus.EarlyLeave;
        }

        return AttendanceStatus.Present;
    }

    private static (DateTime? MorningIn, DateTime? LunchOut, DateTime? LunchIn, DateTime? EveningOut, List<DateTime> ExtraScans)
        AssignSlots(DateTime date, WorkRule rule, List<DateTime> scans)
    {
        var extra = new List<DateTime>();
        DateTime? morningIn = scans[0];
        DateTime? lunchOut = null;
        DateTime? lunchIn = null;
        DateTime? eveningOut = null;

        if (rule.Mode == WorkMode.FreeTime)
        {
            if (scans.Count % 2 == 0)
            {
                eveningOut = scans[^1];
            }
            else
            {
                extra.AddRange(scans.Skip(1));
            }

            return (morningIn, null, null, eveningOut, extra);
        }

        var lunchStart = date + rule.LunchStart;
        switch (scans.Count)
        {
            case 1:
                break;

            case 2:
                // Spec: 2-scan pattern = Morning In / Evening Out (an early departure is an early leave).
                eveningOut = scans[1];
                break;

            case 3:
                lunchOut = scans[1];
                lunchIn = scans[2];
                break;

            default:
                lunchOut = scans[1];
                lunchIn = scans[2];
                eveningOut = scans[^1];
                if (scans.Count > 4)
                {
                    extra.AddRange(scans.Skip(3).Take(scans.Count - 4));
                }

                break;
        }

        return (morningIn, lunchOut, lunchIn, eveningOut, extra);
    }

    private static List<(DateTime Start, DateTime End)> BuildIntervals(List<DateTime> scans)
    {
        var intervals = new List<(DateTime Start, DateTime End)>();
        for (var i = 0; i + 1 < scans.Count; i += 2)
        {
            if (scans[i + 1] > scans[i])
            {
                intervals.Add((scans[i], scans[i + 1]));
            }
        }

        return intervals;
    }

    private static double OverlapMinutes(DateTime start, DateTime end, DateTime windowStart, DateTime windowEnd)
    {
        var s = start > windowStart ? start : windowStart;
        var e = end < windowEnd ? end : windowEnd;
        return e > s ? (e - s).TotalMinutes : 0;
    }

    private static int ComputeLateMinutes(DateTime date, WorkRule rule, (DateTime? MorningIn, DateTime? LunchOut, DateTime? LunchIn, DateTime? EveningOut, List<DateTime> ExtraScans) slots, bool scheduled)
    {
        if (!scheduled || rule.Mode != WorkMode.FixedShift || slots.MorningIn is null)
        {
            return 0;
        }

        var expected = date + rule.MorningStart + TimeSpan.FromMinutes(rule.MorningGraceMinutes);
        var late = slots.MorningIn.Value > expected
            ? (int)Math.Floor((slots.MorningIn.Value - expected).TotalMinutes)
            : 0;

        if (slots.LunchIn is not null)
        {
            var expectedReturn = date + rule.LunchEnd + TimeSpan.FromMinutes(rule.AfternoonGraceMinutes);
            if (slots.LunchIn.Value > expectedReturn)
            {
                late += (int)Math.Floor((slots.LunchIn.Value - expectedReturn).TotalMinutes);
            }
        }

        return Math.Max(0, late);
    }

    private static int ComputeEarlyLeaveMinutes(DateTime date, WorkRule rule, (DateTime? MorningIn, DateTime? LunchOut, DateTime? LunchIn, DateTime? EveningOut, List<DateTime> ExtraScans) slots, bool scheduled)
    {
        if (!scheduled || rule.Mode != WorkMode.FixedShift || slots.EveningOut is null)
        {
            return 0;
        }

        var expected = date + rule.EveningEnd - TimeSpan.FromMinutes(rule.EarlyLeaveGraceMinutes);
        return slots.EveningOut.Value < expected
            ? (int)Math.Floor((expected - slots.EveningOut.Value).TotalMinutes)
            : 0;
    }

    private static int ComputeCreditedMinutes(WorkRule rule, List<AttendanceAdjustment> adjustments, DateTime date)
    {
        var total = 0;
        foreach (var adjustment in adjustments)
        {
            switch (adjustment.Kind)
            {
                case AdjustmentKind.AnnualLeave:
                case AdjustmentKind.SickLeave:
                case AdjustmentKind.OtherLeave:
                    total += adjustment.HoursCreditedMinutes > 0
                        ? adjustment.HoursCreditedMinutes
                        : rule.StandardDailyTargetMinutes;
                    break;

                case AdjustmentKind.Mission:
                case AdjustmentKind.ExitPermit:
                    if (adjustment.HoursCreditedMinutes > 0)
                    {
                        total += adjustment.HoursCreditedMinutes;
                    }
                    else if (adjustment.StartLocal.HasValue && adjustment.EndLocal.HasValue)
                    {
                        var start = adjustment.StartLocal.Value.Date == date
                            ? adjustment.StartLocal.Value
                            : date;
                        var end = adjustment.EndLocal.Value.Date == date
                            ? adjustment.EndLocal.Value
                            : date.AddDays(1);
                        total += (int)Math.Floor(Math.Max(0, (end - start).TotalMinutes));
                    }

                    break;

                default:
                    break;
            }
        }

        return total;
    }

    /// <summary>Derives the hourly rate from whatever financial data the operator filled in.</summary>
    public static decimal ResolveHourlyRate(Employee employee, WorkRule rule)
    {
        if (employee.HourlyRateDzd > 0)
        {
            return employee.HourlyRateDzd;
        }

        var target = Math.Max(1, rule.StandardDailyTargetMinutes);
        if (employee.DailyRateDzd > 0)
        {
            return employee.DailyRateDzd / (target / 60m);
        }

        if (employee.BaseSalaryDzd > 0)
        {
            return employee.BaseSalaryDzd / 30m / (target / 60m);
        }

        return 0m;
    }
}
