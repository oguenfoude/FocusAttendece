using System.Globalization;
using FocusPlus.Application.Mvvm;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

/// <summary>Editable projection of a work rule (times as text, validated on save).</summary>
public sealed class WorkRuleEditor : ObservableObject
{
    private string _code = string.Empty;
    private string _nameAr = string.Empty;
    private string _nameFr = string.Empty;
    private bool _isFreeTime;
    private string _morningStart = "08:00";
    private string _lunchStart = "12:00";
    private string _lunchEnd = "13:00";
    private string _eveningEnd = "16:30";
    private int _morningGrace = 15;
    private int _afternoonGrace = 15;
    private int _earlyLeaveGrace = 5;
    private bool _autoDeductLunch = true;
    private int _lunchDeduction = 60;
    private int _antiBounce = 3;
    private int _target = 450;
    private bool _isDefault;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Code { get => _code; set => SetProperty(ref _code, value); }

    public string NameAr { get => _nameAr; set => SetProperty(ref _nameAr, value); }

    public string NameFr { get => _nameFr; set => SetProperty(ref _nameFr, value); }

    public bool IsFreeTime
    {
        get => _isFreeTime;
        set
        {
            if (SetProperty(ref _isFreeTime, value))
            {
                OnPropertyChanged(nameof(IsFixedShift));
            }
        }
    }

    public bool IsFixedShift => !IsFreeTime;

    public string MorningStart { get => _morningStart; set => SetProperty(ref _morningStart, value); }

    public string LunchStart { get => _lunchStart; set => SetProperty(ref _lunchStart, value); }

    public string LunchEnd { get => _lunchEnd; set => SetProperty(ref _lunchEnd, value); }

    public string EveningEnd { get => _eveningEnd; set => SetProperty(ref _eveningEnd, value); }

    public int MorningGrace { get => _morningGrace; set => SetProperty(ref _morningGrace, value); }

    public int AfternoonGrace { get => _afternoonGrace; set => SetProperty(ref _afternoonGrace, value); }

    public int EarlyLeaveGrace { get => _earlyLeaveGrace; set => SetProperty(ref _earlyLeaveGrace, value); }

    public bool AutoDeductLunch { get => _autoDeductLunch; set => SetProperty(ref _autoDeductLunch, value); }

    public int LunchDeduction { get => _lunchDeduction; set => SetProperty(ref _lunchDeduction, value); }

    public int AntiBounce { get => _antiBounce; set => SetProperty(ref _antiBounce, value); }

    public int Target { get => _target; set => SetProperty(ref _target, value); }

    public bool IsDefault { get => _isDefault; set => SetProperty(ref _isDefault, value); }

    public static WorkRuleEditor From(WorkRule rule) => new()
    {
        Id = rule.Id,
        Code = rule.Code,
        NameAr = rule.NameAr,
        NameFr = rule.NameFr,
        IsFreeTime = rule.Mode == WorkMode.FreeTime,
        MorningStart = Format(rule.MorningStartMin),
        LunchStart = Format(rule.LunchStartMin),
        LunchEnd = Format(rule.LunchEndMin),
        EveningEnd = Format(rule.EveningEndMin),
        MorningGrace = rule.MorningGraceMinutes,
        AfternoonGrace = rule.AfternoonGraceMinutes,
        EarlyLeaveGrace = rule.EarlyLeaveGraceMinutes,
        AutoDeductLunch = rule.AutoDeductLunchBreak,
        LunchDeduction = rule.LunchDeductionMinutes,
        AntiBounce = rule.AntiBounceMinutes,
        Target = rule.StandardDailyTargetMinutes,
        IsDefault = rule.IsDefault,
        CreatedAtUtc = rule.CreatedAtUtc,
        IsArchived = rule.IsArchived
    };

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsArchived { get; set; }

    public WorkRule ToEntity()
    {
        var entity = new WorkRule
        {
            Id = Id,
            Code = Code,
            NameAr = NameAr,
            NameFr = NameFr,
            Mode = IsFreeTime ? WorkMode.FreeTime : WorkMode.FixedShift,
            MorningGraceMinutes = MorningGrace,
            AfternoonGraceMinutes = AfternoonGrace,
            EarlyLeaveGraceMinutes = EarlyLeaveGrace,
            AutoDeductLunchBreak = AutoDeductLunch,
            LunchDeductionMinutes = LunchDeduction,
            AntiBounceMinutes = AntiBounce,
            StandardDailyTargetMinutes = Target,
            IsDefault = IsDefault,
            CreatedAtUtc = CreatedAtUtc,
            IsArchived = IsArchived
        };

        entity.MorningStartMin = ParseTime(MorningStart, entity.MorningStartMin);
        entity.LunchStartMin = ParseTime(LunchStart, entity.LunchStartMin);
        entity.LunchEndMin = ParseTime(LunchEnd, entity.LunchEndMin);
        entity.EveningEndMin = ParseTime(EveningEnd, entity.EveningEndMin);
        return entity;
    }

    private static int ParseTime(string text, int fallback)
    {
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time))
        {
            return (int)time.TotalMinutes;
        }

        return fallback;
    }

    private static string Format(int minutes) =>
        $"{minutes / 60:00}:{minutes % 60:00}";
}

/// <summary>Editable projection of a department / service.</summary>
public sealed class DepartmentEditor : ObservableObject
{
    private string _code = string.Empty;
    private string _nameAr = string.Empty;
    private string _nameFr = string.Empty;
    private string? _parentId;
    private string? _workRuleId;
    private string? _managerAr;
    private string? _managerFr;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Code { get => _code; set => SetProperty(ref _code, value); }

    public string NameAr { get => _nameAr; set => SetProperty(ref _nameAr, value); }

    public string NameFr { get => _nameFr; set => SetProperty(ref _nameFr, value); }

    public string? SiteId { get; set; }

    public string? ParentId { get => _parentId; set => SetProperty(ref _parentId, value); }

    public string? WorkRuleId { get => _workRuleId; set => SetProperty(ref _workRuleId, value); }

    public string? ManagerAr { get => _managerAr; set => SetProperty(ref _managerAr, value); }

    public string? ManagerFr { get => _managerFr; set => SetProperty(ref _managerFr, value); }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsArchived { get; set; }

    public static DepartmentEditor From(Department department) => new()
    {
        Id = department.Id,
        Code = department.Code,
        NameAr = department.NameAr,
        NameFr = department.NameFr,
        SiteId = department.SiteId,
        ParentId = department.ParentDepartmentId,
        WorkRuleId = department.DefaultWorkRuleId,
        ManagerAr = department.ManagerNameAr,
        ManagerFr = department.ManagerNameFr,
        CreatedAtUtc = department.CreatedAtUtc,
        IsArchived = department.IsArchived
    };

    public Department ToEntity() => new()
    {
        Id = Id,
        Code = Code,
        NameAr = NameAr,
        NameFr = NameFr,
        SiteId = SiteId,
        ParentDepartmentId = ParentId,
        DefaultWorkRuleId = WorkRuleId,
        ManagerNameAr = ManagerAr,
        ManagerNameFr = ManagerFr,
        CreatedAtUtc = CreatedAtUtc,
        IsArchived = IsArchived
    };
}
