namespace FocusPlus.Domain.Entities;

/// <summary>Marks a record that is archived instead of hard deleted. No physical delete is ever performed.</summary>
public interface IArchivable
{
    bool IsArchived { get; set; }
    DateTime? ArchivedAtUtc { get; set; }
    string? ArchiveReason { get; set; }
    string? ArchivedBy { get; set; }
}

public abstract class ArchivableEntity : IArchivable
{
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? ArchiveReason { get; set; }
    public string? ArchivedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>An OPGI site / local agency. Fully editable by the administrator.</summary>
public sealed class Site : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string? Address { get; set; }
}

/// <summary>A direction, service, department or local unit of OPGI M'Sila.</summary>
public sealed class Department : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string? SiteId { get; set; }
    public string? ParentDepartmentId { get; set; }
    public string? DefaultWorkRuleId { get; set; }
    public string? ManagerNameAr { get; set; }
    public string? ManagerNameFr { get; set; }

    /// <summary>Display helper, never persisted.</summary>
    public string DisplayName(bool arabic) => arabic ? NameAr : NameFr;
}
