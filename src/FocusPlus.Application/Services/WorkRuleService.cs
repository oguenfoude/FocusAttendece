using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

/// <summary>Full CRUD over work shifts / attendance rules. Official times are editable at any moment.</summary>
public sealed class WorkRuleService(WorkRuleRepository rules, AuditRepository audit)
{
    public IReadOnlyList<WorkRule> GetRules(bool includeArchived = false) => rules.GetAll(includeArchived);

    public WorkRule GetDefault() => rules.GetDefault();

    public IReadOnlyDictionary<string, int> GetEmployeeCounts() => rules.GetEmployeeCounts();

    public OperationResult Save(WorkRule rule, string actor)
    {
        if (string.IsNullOrWhiteSpace(rule.Code) || string.IsNullOrWhiteSpace(rule.NameAr) || string.IsNullOrWhiteSpace(rule.NameFr))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        var existing = rules.GetAll(includeArchived: true)
            .FirstOrDefault(r => string.Equals(r.Code, rule.Code.Trim(), StringComparison.OrdinalIgnoreCase) && r.Id != rule.Id);
        if (existing is not null)
        {
            return OperationResult.Fail(ErrorCodes.DuplicateCode, rule.Code);
        }

        if (rule.Mode == WorkMode.FixedShift)
        {
            var times = new[] { rule.MorningStartMin, rule.LunchStartMin, rule.LunchEndMin, rule.EveningEndMin };
            if (times.Any(t => t is < 0 or > 1439) || !(rule.MorningStartMin < rule.LunchStartMin
                && rule.LunchStartMin < rule.LunchEndMin
                && rule.LunchEndMin <= rule.EveningEndMin))
            {
                return OperationResult.Fail(ErrorCodes.InvalidTime);
            }
        }

        if (rule.StandardDailyTargetMinutes is < 30 or > 1440
            || rule.MorningGraceMinutes is < 0 or > 240
            || rule.AfternoonGraceMinutes is < 0 or > 240
            || rule.EarlyLeaveGraceMinutes is < 0 or > 120
            || rule.LunchDeductionMinutes is < 0 or > 240
            || rule.AntiBounceMinutes is < 0 or > 120)
        {
            return OperationResult.Fail(ErrorCodes.InvalidTime);
        }

        rule.Code = rule.Code.Trim();
        rule.NameAr = rule.NameAr.Trim();
        rule.NameFr = rule.NameFr.Trim();
        rules.Upsert(rule);
        audit.Add(Log("workrule.saved", actor, rule.Id, $"{rule.Code} {rule.MorningStart}-{rule.EveningEnd}"));
        return OperationResult.Ok();
    }

    public OperationResult Archive(string id, string reason, string actor)
    {
        var all = rules.GetAll();
        var rule = all.FirstOrDefault(r => r.Id == id);
        if (rule is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var remaining = all.Where(r => r.Id != id).ToList();
        if (remaining.Count == 0)
        {
            return OperationResult.Fail(ErrorCodes.LastDefaultRule);
        }

        rules.Archive(id, reason, actor);

        if (rule.IsDefault)
        {
            var replacement = remaining[0];
            replacement.IsDefault = true;
            rules.Upsert(replacement);
        }

        audit.Add(Log("workrule.archived", actor, id, reason));
        return OperationResult.Ok();
    }

    public OperationResult Restore(string id, string actor)
    {
        rules.Restore(id, actor);
        audit.Add(Log("workrule.restored", actor, id, null));
        return OperationResult.Ok();
    }

    public OperationResult MakeDefault(string id, string actor)
    {
        var rule = rules.Get(id);
        if (rule is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        rule.IsDefault = true;
        rules.Upsert(rule);
        audit.Add(Log("workrule.default", actor, id, rule.Code));
        return OperationResult.Ok();
    }

    private static AuditLogEntry Log(string action, string actor, string entityId, string? details) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "WorkRule",
        EntityId = entityId,
        Details = details
    };
}
