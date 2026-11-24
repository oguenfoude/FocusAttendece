using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Tests;

public sealed class AttendanceCalculatorTests
{
    private static readonly DateTime Sunday = new(2026, 11, 15);
    private static readonly DateTime Friday = new(2026, 11, 20);

    private static AttendanceDayResult Calculate(
        DateTime date,
        IEnumerable<DateTime> punches,
        bool scheduled = true,
        WorkRule? rule = null,
        IEnumerable<AttendanceAdjustment>? adjustments = null,
        decimal hourly = 0,
        decimal overtime = 1m)
    {
        return AttendanceCalculator.Calculate(new AttendanceDayInput
        {
            Date = date,
            Rule = rule ?? WorkRule.CreateOfficialDefault(),
            IsScheduledDay = scheduled,
            Punches = punches.ToList(),
            Adjustments = adjustments?.ToList() ?? [],
            HourlyRateDzd = hourly,
            OvertimeMultiplier = overtime
        });
    }

    private static DateTime At(DateTime day, int hour, int minute, int second = 0) =>
        day.Date.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    [Fact]
    public void SeedDate_IsSunday_AndWorkWeekExcludesFriday()
    {
        Assert.Equal(DayOfWeek.Sunday, Sunday.DayOfWeek);
        Assert.True(WeekDays.Administrative.Includes(DayOfWeek.Sunday));
        Assert.True(WeekDays.Administrative.Includes(DayOfWeek.Thursday));
        Assert.False(WeekDays.Administrative.Includes(DayOfWeek.Friday));
        Assert.True(WeekDays.All.Includes(DayOfWeek.Saturday));
        Assert.Equal(31, (int)WeekDays.Administrative);
    }

    [Fact]
    public void FourScanPattern_ComputesExactNetHours_WithNoLunchDeduction()
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, 8, 0), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 16, 30)
        ]);

        Assert.Equal(AttendanceStatus.Present, result.Status);
        Assert.Equal(450, result.TotalNetMinutes);
        Assert.Equal(450, result.RegularWorkedMinutes);
        Assert.Equal(0, result.ExtraWorkedMinutes);
        Assert.Equal(0, result.BreakMinutes);
        Assert.Equal(0, result.LateMinutes);
        Assert.Equal(4, result.RawScanCount);
        Assert.NotNull(result.MorningIn);
        Assert.NotNull(result.LunchOut);
        Assert.NotNull(result.LunchIn);
        Assert.NotNull(result.EveningOut);
    }

    [Fact]
    public void TwoScanPattern_AcrossLunch_DeductsSixtyMinutes()
    {
        var result = Calculate(Sunday, [At(Sunday, 8, 0), At(Sunday, 16, 30)]);

        Assert.Equal(450, result.TotalNetMinutes);
        Assert.Equal(60, result.BreakMinutes);
        Assert.Contains(AttendanceNote.LunchDeducted, result.Notes);
    }

    [Fact]
    public void TwoScanPattern_ShortMorning_NoLunchDeduction()
    {
        var result = Calculate(Sunday, [At(Sunday, 8, 0), At(Sunday, 11, 0)]);

        Assert.Equal(180, result.TotalNetMinutes);
        Assert.Equal(0, result.BreakMinutes);
        Assert.Equal(AttendanceStatus.EarlyLeave, result.Status);
        Assert.Equal(325, result.EarlyLeaveMinutes);
    }

    [Theory]
    [InlineData(8, 15, 59, 0)]
    [InlineData(8, 16, 0, 1)]
    [InlineData(8, 30, 0, 15)]
    public void MorningGracePeriod_IsFifteenMinutes(int hour, int minute, int second, int expectedLate)
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, hour, minute, second), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 16, 30)
        ]);

        Assert.Equal(expectedLate, result.LateMinutes);
        Assert.Equal(expectedLate == 0 ? AttendanceStatus.Present : AttendanceStatus.Late, result.Status);
    }

    [Fact]
    public void AfternoonGracePeriod_AppliesToLunchReturn()
    {
        var onTime = Calculate(Sunday,
        [
            At(Sunday, 8, 0), At(Sunday, 12, 0), At(Sunday, 13, 15, 59), At(Sunday, 16, 30)
        ]);
        var late = Calculate(Sunday,
        [
            At(Sunday, 8, 0), At(Sunday, 12, 0), At(Sunday, 13, 30), At(Sunday, 16, 30)
        ]);

        Assert.Equal(0, onTime.LateMinutes);
        Assert.Equal(15, late.LateMinutes);
    }

    [Fact]
    public void EarlyLeaveAndLate_AreBothDetected()
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, 8, 30), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 16, 0)
        ]);

        Assert.Equal(15, result.LateMinutes);
        Assert.Equal(25, result.EarlyLeaveMinutes);
        Assert.Equal(AttendanceStatus.LateAndEarlyLeave, result.Status);
    }

    [Fact]
    public void TwoScanEarlyDeparture_IsAnEarlyLeave_NotAMissingScan()
    {
        var result = Calculate(Sunday, [At(Sunday, 8, 0), At(Sunday, 12, 0)]);

        Assert.Equal(AttendanceStatus.EarlyLeave, result.Status);
        Assert.Equal(240, result.TotalNetMinutes);
        Assert.Null(result.LunchOut);
        Assert.NotNull(result.EveningOut);
        Assert.Contains(AttendanceNote.EarlyLeave, result.Notes);
        Assert.DoesNotContain(AttendanceNote.MissingOutScan, result.Notes);
    }

    [Fact]
    public void ThreeScans_IsMissingOutScan()
    {
        var result = Calculate(Sunday, [At(Sunday, 8, 0), At(Sunday, 12, 0), At(Sunday, 13, 0)]);

        Assert.Equal(AttendanceStatus.MissingOutScan, result.Status);
        Assert.Equal(240, result.TotalNetMinutes);
    }

    [Fact]
    public void SingleScan_IsFlagged()
    {
        var result = Calculate(Sunday, [At(Sunday, 8, 0)]);

        Assert.Equal(AttendanceStatus.MissingOutScan, result.Status);
        Assert.Contains(AttendanceNote.SingleScan, result.Notes);
        Assert.Equal(0, result.TotalNetMinutes);
    }

    [Fact]
    public void OffDayWork_IsNeverDropped_AndCountsAsExtra()
    {
        var result = Calculate(Friday, [At(Friday, 9, 0), At(Friday, 12, 0)], scheduled: false);

        Assert.Equal(AttendanceStatus.ExtraDay, result.Status);
        Assert.Equal(0, result.RegularWorkedMinutes);
        Assert.Equal(180, result.ExtraWorkedMinutes);
        Assert.Contains(AttendanceNote.OffDayWorked, result.Notes);
    }

    [Fact]
    public void SaturdayWork_IsAlsoTracked()
    {
        var saturday = Sunday.AddDays(6);
        var result = Calculate(saturday, [At(saturday, 8, 0), At(saturday, 16, 30)], scheduled: false);

        Assert.Equal(AttendanceStatus.ExtraDay, result.Status);
        Assert.Equal(450, result.ExtraWorkedMinutes);
    }

    [Fact]
    public void FreeTimeRule_SumsEveryCompletedPair()
    {
        var rule = WorkRule.CreateFreeTime();
        var result = Calculate(Sunday,
        [
            At(Sunday, 9, 0), At(Sunday, 11, 0), At(Sunday, 14, 0), At(Sunday, 17, 0)
        ], rule: rule);

        Assert.Equal(300, result.RegularWorkedMinutes);
        Assert.Equal(0, result.BreakMinutes);
        Assert.Equal(0, result.LateMinutes);
    }

    [Fact]
    public void AntiBounce_IgnoresRapidDoubleScan_WithoutTouchingRawCount()
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, 8, 0), At(Sunday, 8, 1), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 16, 30)
        ]);

        Assert.Equal(5, result.RawScanCount);
        Assert.Equal(4, result.RetainedScanCount);
        Assert.Equal(450, result.TotalNetMinutes);
        Assert.Contains(AttendanceNote.AntiBounceFiltered, result.Notes);
    }

    [Fact]
    public void ManualPunch_CompletesAMissingOutScan()
    {
        var adjustment = new AttendanceAdjustment
        {
            EmployeeId = "e1",
            WorkDate = Sunday,
            Kind = AdjustmentKind.ManualPunch,
            StartLocal = At(Sunday, 16, 30),
            Note = "Verified by HR"
        };

        var result = Calculate(Sunday, [At(Sunday, 8, 0)], adjustments: [adjustment]);

        Assert.Equal(450, result.TotalNetMinutes);
        Assert.Equal(1, result.ManualScanCount);
        Assert.Contains(AttendanceNote.ManualPunchUsed, result.Notes);
        Assert.Equal(AttendanceStatus.Present, result.Status);
    }

    [Fact]
    public void Mission_CreditsHours_WhenNoScanExists()
    {
        var mission = new AttendanceAdjustment
        {
            EmployeeId = "e1",
            WorkDate = Sunday,
            Kind = AdjustmentKind.Mission,
            StartLocal = At(Sunday, 8, 0),
            EndLocal = At(Sunday, 12, 0),
            HoursCreditedMinutes = 240
        };

        var result = Calculate(Sunday, [], adjustments: [mission]);

        Assert.Equal(AttendanceStatus.Mission, result.Status);
        Assert.Equal(240, result.CreditedMinutes);
        Assert.Equal(240, result.EffectiveTotalMinutes);
    }

    [Fact]
    public void AnnualLeave_CreditsTheFullTarget()
    {
        var leave = new AttendanceAdjustment
        {
            EmployeeId = "e1",
            WorkDate = Sunday,
            Kind = AdjustmentKind.AnnualLeave
        };

        var result = Calculate(Sunday, [], adjustments: [leave]);

        Assert.Equal(AttendanceStatus.Leave, result.Status);
        Assert.Equal(450, result.CreditedMinutes);
    }

    [Fact]
    public void ScheduledDayWithoutScan_IsAbsent()
    {
        var result = Calculate(Sunday, []);
        Assert.Equal(AttendanceStatus.Absent, result.Status);
        Assert.Equal(0, result.EffectiveTotalMinutes);
    }

    [Fact]
    public void NonScheduledDayWithoutScan_IsRestDay()
    {
        var result = Calculate(Friday, [], scheduled: false);
        Assert.Equal(AttendanceStatus.RestDay, result.Status);
    }

    [Fact]
    public void EarlyArrival_ProducesExtraHours()
    {
        var result = Calculate(Sunday, [At(Sunday, 7, 0), At(Sunday, 16, 30)]);

        Assert.Equal(450, result.RegularWorkedMinutes);
        Assert.Equal(60, result.ExtraWorkedMinutes);
        Assert.Equal(510, result.TotalNetMinutes);
    }

    [Fact]
    public void OvertimeMultiplier_IsAppliedToExtraHoursOnly()
    {
        var result = Calculate(Sunday, [At(Sunday, 7, 0), At(Sunday, 16, 30)], hourly: 100m, overtime: 1.5m);

        // 450 regular minutes = 7.5h * 100 = 750 ; 60 extra minutes = 1h * 100 * 1.5 = 150
        Assert.Equal(900m, result.EarnedAmountDzd);
        Assert.Equal(0m, result.DeductionAmountDzd);
    }

    [Fact]
    public void LateDeductionPreview_UsesHourlyRate()
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, 8, 30), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 16, 30)
        ], hourly: 120m);

        // 15 minutes late = 0.25h * 120 = 30
        Assert.Equal(30m, result.DeductionAmountDzd);
    }

    [Fact]
    public void SixScans_ArePairedSequentially()
    {
        var result = Calculate(Sunday,
        [
            At(Sunday, 7, 0), At(Sunday, 9, 0), At(Sunday, 10, 0), At(Sunday, 12, 0), At(Sunday, 13, 0), At(Sunday, 17, 0)
        ]);

        // 2h + 2h + 4h = 8h
        Assert.Equal(480, result.TotalNetMinutes);
        Assert.Equal(2, result.ExtraScans.Count);
    }

    [Fact]
    public void HourlyRate_FallsBackToDailyThenMonthlySalary()
    {
        var rule = WorkRule.CreateOfficialDefault();
        var daily = new Employee { DailyRateDzd = 1500m };
        var monthly = new Employee { BaseSalaryDzd = 45000m };
        var explicitHourly = new Employee { HourlyRateDzd = 500m, DailyRateDzd = 9999m };

        Assert.Equal(200m, AttendanceCalculator.ResolveHourlyRate(daily, rule));
        Assert.Equal(200m, AttendanceCalculator.ResolveHourlyRate(monthly, rule));
        Assert.Equal(500m, AttendanceCalculator.ResolveHourlyRate(explicitHourly, rule));
    }

    [Fact]
    public void CustomShift_TimesAreHonoured()
    {
        var ramadan = new WorkRule
        {
            Code = "RAMADAN",
            NameAr = "توقيت رمضان",
            NameFr = "Horaire Ramadan",
            Mode = WorkMode.FixedShift,
            MorningStartMin = 8 * 60 + 30,
            LunchStartMin = 12 * 60,
            LunchEndMin = 12 * 60 + 30,
            EveningEndMin = 15 * 60 + 30,
            MorningGraceMinutes = 10,
            LunchDeductionMinutes = 30,
            StandardDailyTargetMinutes = 390
        };

        var result = Calculate(Sunday, [At(Sunday, 8, 40), At(Sunday, 15, 30)], rule: ramadan);

        Assert.Equal(0, result.LateMinutes);
        Assert.Equal(380, result.TotalNetMinutes);
        Assert.Equal(30, result.BreakMinutes);
        Assert.Equal(380, result.RegularWorkedMinutes);
    }
}
