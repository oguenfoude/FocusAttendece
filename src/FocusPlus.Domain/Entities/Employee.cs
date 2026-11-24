namespace FocusPlus.Domain.Entities;

/// <summary>A real OPGI employee. Nothing is seeded: employees only come from operator entry or device import.</summary>
public sealed class Employee : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>OPGI staff number (matricule). Unique.</summary>
    public string Matricule { get; set; } = string.Empty;

    /// <summary>Biometric identifier enrolled in the terminal (PIN).</summary>
    public string? DevicePin { get; set; }

    public string? FullNameAr { get; set; }
    public string? FirstNameFr { get; set; }
    public string? LastNameFr { get; set; }

    public string? SiteId { get; set; }
    public string? DepartmentId { get; set; }
    public string? JobTitleAr { get; set; }
    public string? JobTitleFr { get; set; }
    public string? Phone { get; set; }
    public DateTime? BirthDate { get; set; }
    public DateTime? HireDate { get; set; }
    public DateTime? EndDate { get; set; }

    /// <summary>Defaults to the official OPGI shift; can be a custom shift or free time.</summary>
    public string? WorkRuleId { get; set; }

    /// <summary>Seven-day schedule. Default: Sunday → Thursday.</summary>
    public WeekDays ScheduledWorkDays { get; set; } = WeekDays.Administrative;

    public decimal BaseSalaryDzd { get; set; }
    public decimal DailyRateDzd { get; set; }
    public decimal HourlyRateDzd { get; set; }
    public decimal OvertimeMultiplier { get; set; } = 1.0m;
    public string? PaymentAccount { get; set; }

    public string? PrimaryDeviceId { get; set; }

    /// <summary>0 = user, 2 = enroller, 6 = manager, 14 = super admin (device privilege mask).</summary>
    public int DevicePrivilege { get; set; }

    public int CardNumber { get; set; }
    public string? DevicePassword { get; set; }
    public int FpCount { get; set; }
    public int FaceCount { get; set; }
    public string? StatusNote { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Fallback display name: Arabic full name, then French names, then matricule.</summary>
    public string DisplayNameAr
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FullNameAr))
            {
                return FullNameAr.Trim();
            }

            var fr = DisplayNameFr;
            return fr.Length > 0 ? fr : Matricule;
        }
    }

    public string DisplayNameFr
    {
        get
        {
            var parts = new[] { FirstNameFr, LastNameFr }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim());
            var joined = string.Join(' ', parts);
            return joined.Length > 0 ? joined : Matricule;
        }
    }

    public string DisplayName(bool arabic) => arabic ? DisplayNameAr : DisplayNameFr;

    /// <summary>True when the employee is expected to work on the supplied date.</summary>
    public bool IsScheduledOn(DateTime date)
    {
        if (IsArchived)
        {
            return false;
        }

        if (HireDate.HasValue && date.Date < HireDate.Value.Date)
        {
            return false;
        }

        if (EndDate.HasValue && date.Date > EndDate.Value.Date)
        {
            return false;
        }

        return ScheduledWorkDays.Includes(date.DayOfWeek);
    }
}
