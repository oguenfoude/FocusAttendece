using FocusPlus.Data.Repositories;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

/// <summary>Full CRUD (create / read / update / archive / restore) over OPGI sites and departments.</summary>
public sealed class OrganizationService(
    SiteRepository sites,
    DepartmentRepository departments,
    AuditRepository audit)
{
    public IReadOnlyList<Site> GetSites(bool includeArchived = false) => sites.GetAll(includeArchived);

    public IReadOnlyList<Department> GetDepartments(bool includeArchived = false) => departments.GetAll(includeArchived);

    public IReadOnlyDictionary<string, int> GetDepartmentEmployeeCounts() => departments.GetEmployeeCounts();

    public OperationResult SaveSite(Site site, string actor)
    {
        if (string.IsNullOrWhiteSpace(site.Code) || string.IsNullOrWhiteSpace(site.NameAr) || string.IsNullOrWhiteSpace(site.NameFr))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        var existing = sites.GetAll(includeArchived: true)
            .FirstOrDefault(s => string.Equals(s.Code, site.Code.Trim(), StringComparison.OrdinalIgnoreCase) && s.Id != site.Id);
        if (existing is not null)
        {
            return OperationResult.Fail(ErrorCodes.DuplicateCode, site.Code);
        }

        site.Code = site.Code.Trim();
        site.NameAr = site.NameAr.Trim();
        site.NameFr = site.NameFr.Trim();
        sites.Upsert(site);
        audit.Add(Log("site.saved", actor, site.Id, site.Code));
        return OperationResult.Ok();
    }

    public OperationResult ArchiveSite(string id, string reason, string actor)
    {
        var site = sites.Get(id);
        if (site is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var inUse = departments.GetAll().Any(d => d.SiteId == id);
        if (inUse)
        {
            return OperationResult.Fail(ErrorCodes.InUse);
        }

        sites.Archive(id, reason, actor);
        audit.Add(Log("site.archived", actor, id, reason));
        return OperationResult.Ok();
    }

    public OperationResult RestoreSite(string id, string actor)
    {
        sites.Restore(id, actor);
        audit.Add(Log("site.restored", actor, id, null));
        return OperationResult.Ok();
    }

    public OperationResult SaveDepartment(Department department, string actor)
    {
        if (string.IsNullOrWhiteSpace(department.Code)
            || string.IsNullOrWhiteSpace(department.NameAr)
            || string.IsNullOrWhiteSpace(department.NameFr))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        var all = departments.GetAll(includeArchived: true);
        if (all.Any(d => string.Equals(d.Code, department.Code.Trim(), StringComparison.OrdinalIgnoreCase) && d.Id != department.Id))
        {
            return OperationResult.Fail(ErrorCodes.DuplicateCode, department.Code);
        }

        if (string.Equals(department.ParentDepartmentId, department.Id, StringComparison.Ordinal))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        department.Code = department.Code.Trim();
        department.NameAr = department.NameAr.Trim();
        department.NameFr = department.NameFr.Trim();
        departments.Upsert(department);
        audit.Add(Log("department.saved", actor, department.Id, department.Code));
        return OperationResult.Ok();
    }

    public OperationResult ArchiveDepartment(string id, string reason, string actor)
    {
        var department = departments.Get(id);
        if (department is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var counts = departments.GetEmployeeCounts();
        if (counts.TryGetValue(id, out var count) && count > 0)
        {
            return OperationResult.Fail(ErrorCodes.InUse, $"{count}");
        }

        departments.Archive(id, reason, actor);
        audit.Add(Log("department.archived", actor, id, reason));
        return OperationResult.Ok();
    }

    public OperationResult RestoreDepartment(string id, string actor)
    {
        departments.Restore(id, actor);
        audit.Add(Log("department.restored", actor, id, null));
        return OperationResult.Ok();
    }

    public Department? GetDepartment(string id) => departments.Get(id);

    private static AuditLogEntry Log(string action, string actor, string entityId, string? details) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "Organization",
        EntityId = entityId,
        Details = details
    };
}
