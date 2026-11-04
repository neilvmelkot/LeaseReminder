using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Application.Notifications;

/// <summary>
/// Works out which notification rules actually apply to one lease, and why.
///
/// <para>
/// Rules live at three levels (section 6.G): organization-wide defaults,
/// property rules, and lease rules. A narrower level may add a rule, replace
/// an inherited one, or switch an inherited one off. The resolver returns
/// every rule the UI should show together with its origin and its effect, so
/// the screen can say "inherited from Global", "overridden here", or
/// "turned off for this lease" rather than presenting a flat list that hides
/// where a reminder came from.
/// </para>
///
/// <para>
/// The requirement that drives the design is the warning in section 6.G:
/// "Editing one tenant's rules must not unintentionally alter other leases."
/// So resolution is per-lease and read-only. Nothing here mutates a rule; a
/// lease-level change creates a lease-scoped row that overrides an ancestor,
/// and the ancestor is left exactly as it was for every other lease.
/// </para>
/// </summary>
public static class NotificationRuleResolver
{
    /// <summary>
    /// Resolves the rules applying to one lease.
    /// </summary>
    /// <param name="candidateRules">
    /// Every rule in the organization. The resolver selects what is relevant;
    /// the caller does not have to pre-filter, and pre-filtering incorrectly is
    /// how cross-lease bugs appear.
    /// </param>
    /// <param name="propertyId">The property the lease belongs to.</param>
    /// <param name="leaseId">The lease being resolved.</param>
    public static RuleResolution Resolve(
        IEnumerable<NotificationRuleNode> candidateRules,
        Guid propertyId,
        Guid leaseId)
    {
        ArgumentNullException.ThrowIfNull(candidateRules);

        // Only rules that address this lease: the organization defaults, this
        // property's rules, and this lease's own. Another property's rules and
        // another lease's rules are not merely ignored, they are never
        // considered, which is what makes editing one lease safe.
        var applicable = candidateRules
            .Where(rule => rule.Scope switch
            {
                RuleScope.Global => true,
                RuleScope.Property => rule.PropertyId == propertyId,
                RuleScope.Lease => rule.LeaseId == leaseId,
                _ => false,
            })
            .ToList();

        var byId = applicable.ToDictionary(rule => rule.Id);

        // Which ancestor each narrower rule replaces or switches off. Built
        // only from rules that apply to THIS lease, so a sibling lease's
        // override cannot suppress a rule here.
        var supersededBy = new Dictionary<Guid, NotificationRuleNode>();

        foreach (var rule in applicable)
        {
            if (rule.OverridesRuleId is not { } targetId)
            {
                continue;
            }

            if (rule.InheritanceMode is not (RuleInheritanceMode.Overridden
                or RuleInheritanceMode.Disabled))
            {
                continue;
            }

            // An override naming a rule that does not apply here is simply
            // inert: it cannot reach across to another property's rule,
            // because that rule is not in `byId`.
            if (!byId.ContainsKey(targetId))
            {
                continue;
            }

            // If two rules claim the same ancestor, the narrower scope wins;
            // a tie is broken by priority then by id, so resolution is
            // deterministic rather than dependent on row order.
            if (supersededBy.TryGetValue(targetId, out var existing)
                && Precedence(existing).CompareTo(Precedence(rule)) >= 0)
            {
                continue;
            }

            supersededBy[targetId] = rule;
        }

        var resolved = new List<ResolvedNotificationRule>(applicable.Count);

        foreach (var rule in applicable)
        {
            var isSuperseded = supersededBy.TryGetValue(rule.Id, out var supersedingRule);

            // A rule in Disabled mode exists only to switch its ancestor off.
            // It never sends anything itself.
            var isDisablingStub = rule.InheritanceMode == RuleInheritanceMode.Disabled;

            var effect = DetermineEffect(rule, isSuperseded, supersedingRule);

            var isActive =
                rule.Enabled
                && rule.PausedAt is null
                && !isSuperseded
                && !isDisablingStub;

            resolved.Add(new ResolvedNotificationRule
            {
                Rule = rule,
                Effect = effect,
                SupersededByRuleId = supersedingRule?.Id,
                IsActive = isActive,
                InactiveReason = isActive ? null : DescribeInactivity(
                    rule, isSuperseded, supersedingRule, isDisablingStub),
            });
        }

        // Stable, meaningful ordering for both the UI and generation: by
        // event, then priority, then how early in the day it sends.
        resolved.Sort(CompareForDisplay);

        return new RuleResolution
        {
            AllRules = resolved,
            ActiveRules = [.. resolved.Where(r => r.IsActive)],
        };
    }

    private static RuleEffect DetermineEffect(
        NotificationRuleNode rule,
        bool isSuperseded,
        NotificationRuleNode? supersedingRule)
    {
        if (isSuperseded)
        {
            return supersedingRule!.InheritanceMode == RuleInheritanceMode.Disabled
                ? RuleEffect.DisabledByDescendant
                : RuleEffect.OverriddenByDescendant;
        }

        return rule.InheritanceMode switch
        {
            RuleInheritanceMode.Disabled => RuleEffect.DisablesAncestor,
            RuleInheritanceMode.Overridden => RuleEffect.OverridesAncestor,

            // A rule defined at a broader level than the lease, still in
            // force, is what the UI labels "inherited".
            _ => rule.Scope == RuleScope.Lease
                ? RuleEffect.LeaseSpecific
                : RuleEffect.Inherited,
        };
    }

    private static string DescribeInactivity(
        NotificationRuleNode rule,
        bool isSuperseded,
        NotificationRuleNode? supersedingRule,
        bool isDisablingStub)
    {
        if (isDisablingStub)
        {
            return "This entry exists only to switch off an inherited rule.";
        }

        if (isSuperseded)
        {
            return supersedingRule!.InheritanceMode == RuleInheritanceMode.Disabled
                ? $"Switched off at {Describe(supersedingRule.Scope)} level."
                : $"Replaced by a {Describe(supersedingRule.Scope)} rule.";
        }

        if (rule.PausedAt is not null)
        {
            return rule.PausedReason ?? "Paused.";
        }

        return "Disabled.";
    }

    private static string Describe(RuleScope scope) => scope switch
    {
        RuleScope.Global => "organization",
        RuleScope.Property => "property",
        RuleScope.Lease => "lease",
        _ => "unknown",
    };

    /// <summary>
    /// Higher wins. Scope dominates, because a lease rule must always beat a
    /// property rule; priority and id only break ties within a scope.
    /// </summary>
    private static (int Scope, int Priority, Guid Id) Precedence(NotificationRuleNode rule) =>
        (rule.Scope switch
        {
            RuleScope.Lease => 3,
            RuleScope.Property => 2,
            RuleScope.Global => 1,
            _ => 0,
        },
        -rule.Priority,
        rule.Id);

    private static int CompareForDisplay(
        ResolvedNotificationRule left,
        ResolvedNotificationRule right)
    {
        var byEvent = left.Rule.EventType.CompareTo(right.Rule.EventType);
        if (byEvent != 0)
        {
            return byEvent;
        }

        var byPriority = left.Rule.Priority.CompareTo(right.Rule.Priority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        var byTime = left.Rule.SendLocalTime.CompareTo(right.Rule.SendLocalTime);
        return byTime != 0 ? byTime : left.Rule.Id.CompareTo(right.Rule.Id);
    }
}

/// <summary>Which level a rule is defined at.</summary>
public enum RuleScope
{
    Global,
    Property,
    Lease,
}

/// <summary>
/// What the UI should say about a rule. Section 6.G asks for inheritance to be
/// visible, which means naming the relationship, not just showing the rule.
/// </summary>
public enum RuleEffect
{
    /// <summary>In force here, but defined at a broader level.</summary>
    Inherited,

    /// <summary>Defined on this lease.</summary>
    LeaseSpecific,

    /// <summary>In force here and replacing a broader rule.</summary>
    OverridesAncestor,

    /// <summary>Exists only to switch a broader rule off.</summary>
    DisablesAncestor,

    /// <summary>Not in force: a narrower rule replaced it.</summary>
    OverriddenByDescendant,

    /// <summary>Not in force: a narrower level switched it off.</summary>
    DisabledByDescendant,
}

/// <summary>A notification rule as the resolver needs to see it.</summary>
public sealed record NotificationRuleNode
{
    public required Guid Id { get; init; }

    public required RuleScope Scope { get; init; }

    public Guid? PropertyId { get; init; }

    public Guid? LeaseId { get; init; }

    public required NotificationEventType EventType { get; init; }

    public RuleInheritanceMode InheritanceMode { get; init; } = RuleInheritanceMode.Additional;

    public Guid? OverridesRuleId { get; init; }

    public required NotificationTriggerType TriggerType { get; init; }

    public int OffsetValue { get; init; }

    public OffsetUnit OffsetUnit { get; init; } = OffsetUnit.Days;

    public required LocalTime SendLocalTime { get; init; }

    /// <summary>
    /// Null means "use the property or rent-schedule zone", resolved when the
    /// send instant is computed rather than here.
    /// </summary>
    public string? TimeZoneId { get; init; }

    public bool Enabled { get; init; } = true;

    public Instant? PausedAt { get; init; }

    public string? PausedReason { get; init; }

    public short Priority { get; init; } = 100;

    public string? Description { get; init; }
}

public sealed record ResolvedNotificationRule
{
    public required NotificationRuleNode Rule { get; init; }

    public required RuleEffect Effect { get; init; }

    public Guid? SupersededByRuleId { get; init; }

    /// <summary>True when this rule will actually produce notifications.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Why it will not, phrased for the UI. Null when active.</summary>
    public string? InactiveReason { get; init; }
}

public sealed record RuleResolution
{
    /// <summary>
    /// Every rule relevant to the lease, active or not, annotated with its
    /// effect. This is what the rule list screen renders.
    /// </summary>
    public required IReadOnlyList<ResolvedNotificationRule> AllRules { get; init; }

    /// <summary>The subset that actually generates notifications.</summary>
    public required IReadOnlyList<ResolvedNotificationRule> ActiveRules { get; init; }
}
