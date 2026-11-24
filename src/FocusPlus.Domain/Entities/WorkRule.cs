namespace FocusPlus.Domain.Entities;

/// <summary>
/// A work shift / attendance rule. Everything (official hours, grace periods, lunch deduction,
/// anti-bounce, target) is configurable by the administrator — nothing is locked.
/// </summary>
public sealed class WorkRule : ArchivableEntity
{
    /// <summary>Official OPGI code for the seeded administrative shift.</summary>
    public const string OfficialCode = "OPGI-OFFICIAL";

    /// <summary>Code of the seeded free-time rule.</summary>
    public const string FreeTimeCode = "FREE-TIME";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public WorkMode Mode { get; set; } = WorkMode.FixedShift;

    /// <summary>Morning arrival, minutes from midnight (08:00 = 480).</summary>
    public int MorningStartMin { get; set; } = 8 * 60;

    /// <summary>Lunch departure (12:00 = 720).</summary>
    public int LunchStartMin { get; set; } = 12 * 60;

    /// <summary>Lunch return / afternoon start (13:00 = 780).</summary>
    public int LunchEndMin { get; set; } = 13 * 60;

    /// <summary>Evening departure (16:30 = 990).</summary>
    public int EveningEndMin { get; set; } = 16 * 60 + 30;

    /// <summary>Morning tolerance in minutes (15 → 08:15:59 is still on time).</summary>
    public int MorningGraceMinutes { get; set; } = 15;

    /// <summary>Afternoon tolerance in minutes.</summary>
    public int AfternoonGraceMinutes { get; set; } = 15;

    /// <summary>Tolerance before evening departure is considered an early leave.</summary>
    public int EarlyLeaveGraceMinutes { get; set; } = 5;

    /// <summary>When a 2-scan day spans lunch, automatically deduct the lunch break.</summary>
    public bool AutoDeductLunchBreak { get; set; } = true;

    /// <summary>Minutes deducted when the automatic lunch deduction applies.</summary>
    public int LunchDeductionMinutes { get; set; } = 60;

    /// <summary>Scans closer than this to the previous retained scan are ignored for pairing.</summary>
    public int AntiBounceMinutes { get; set; } = 3;

    /// <summary>Standard daily target (450 = 7h30).</summary>
    public int StandardDailyTargetMinutes { get; set; } = 450;

    public bool IsDefault { get; set; }

    public TimeSpan MorningStart => TimeSpan.FromMinutes(MorningStartMin);
    public TimeSpan LunchStart => TimeSpan.FromMinutes(LunchStartMin);
    public TimeSpan LunchEnd => TimeSpan.FromMinutes(LunchEndMin);
    public TimeSpan EveningEnd => TimeSpan.FromMinutes(EveningEndMin);

    public string DisplayName(bool arabic) => arabic ? NameAr : NameFr;

    /// <summary>The seeded official OPGI office shift (الدوام الإداري الرسمي), fully editable afterwards.</summary>
    public static WorkRule CreateOfficialDefault() => new()
    {
        Code = OfficialCode,
        NameAr = "الدوام الإداري الرسمي — ديوان الترقية والتسيير العقاري",
        NameFr = "Horaire administratif officiel — OPGI",
        Mode = WorkMode.FixedShift,
        MorningStartMin = 8 * 60,
        LunchStartMin = 12 * 60,
        LunchEndMin = 13 * 60,
        EveningEndMin = 16 * 60 + 30,
        MorningGraceMinutes = 15,
        AfternoonGraceMinutes = 15,
        EarlyLeaveGraceMinutes = 5,
        AutoDeductLunchBreak = true,
        LunchDeductionMinutes = 60,
        AntiBounceMinutes = 3,
        StandardDailyTargetMinutes = 450,
        IsDefault = true
    };

    /// <summary>A seeded free-time rule (open hours) that the administrator can edit or archive.</summary>
    public static WorkRule CreateFreeTime() => new()
    {
        Code = FreeTimeCode,
        NameAr = "وقت حر — احتساب ساعات العمل الفعلية",
        NameFr = "Temps libre — heures effectives",
        Mode = WorkMode.FreeTime,
        MorningStartMin = 0,
        LunchStartMin = 12 * 60,
        LunchEndMin = 13 * 60,
        EveningEndMin = 24 * 60 - 1,
        MorningGraceMinutes = 0,
        AfternoonGraceMinutes = 0,
        EarlyLeaveGraceMinutes = 0,
        AutoDeductLunchBreak = false,
        LunchDeductionMinutes = 0,
        AntiBounceMinutes = 2,
        StandardDailyTargetMinutes = 480,
        IsDefault = false
    };
}
