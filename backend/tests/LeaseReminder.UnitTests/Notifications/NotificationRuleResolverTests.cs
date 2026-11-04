using LeaseReminder.Application.Notifications;
using LeaseReminder.Domain;
using NodaTime;
using Xunit;

namespace LeaseReminder.UnitTests.Notifications;

/// <summary>
/// Covers the rule inheritance and overrides item in section 9.
///
/// The test that matters most is the last group: section 6.G warns that
/// "editing one tenant's rules must not unintentionally alter other leases",
/// and that is a property of resolution, not of the UI.
/// </summary>
public class NotificationRuleResolverTests
{
    private static readonly Guid PropertyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PropertyB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid LeaseA = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LeaseB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static Guid RuleId(int n) =>
        Guid.Parse($"cccccccc-0000-0000-0000-{n:D12}");

    private static NotificationRuleNode Global(
        int id,
        NotificationEventType eventType = NotificationEventType.RentDue,
        int offsetDays = 3,
        int hour = 9,
        bool enabled = true) => new()
        {
            Id = RuleId(id),
            Scope = RuleScope.Global,
            EventType = eventType,
            TriggerType = NotificationTriggerType.Before,
            OffsetValue = offsetDays,
            OffsetUnit = OffsetUnit.Days,
            SendLocalTime = new LocalTime(hour, 0),
            Enabled = enabled,
        };

    private static NotificationRuleNode Property(
        int id,
        Guid propertyId,
        NotificationEventType eventType = NotificationEventType.RentDue,
        RuleInheritanceMode mode = RuleInheritanceMode.Additional,
        Guid? overrides = null,
        int offsetDays = 3,
        int hour = 9) => new()
        {
            Id = RuleId(id),
            Scope = RuleScope.Property,
            PropertyId = propertyId,
            EventType = eventType,
            InheritanceMode = mode,
            OverridesRuleId = overrides,
            TriggerType = NotificationTriggerType.Before,
            OffsetValue = offsetDays,
            OffsetUnit = OffsetUnit.Days,
            SendLocalTime = new LocalTime(hour, 0),
        };

    private static NotificationRuleNode Lease(
        int id,
        Guid leaseId,
        NotificationEventType eventType = NotificationEventType.RentDue,
        RuleInheritanceMode mode = RuleInheritanceMode.Additional,
        Guid? overrides = null,
        int offsetDays = 3,
        int hour = 9) => new()
        {
            Id = RuleId(id),
            Scope = RuleScope.Lease,
            LeaseId = leaseId,
            EventType = eventType,
            InheritanceMode = mode,
            OverridesRuleId = overrides,
            TriggerType = NotificationTriggerType.Before,
            OffsetValue = offsetDays,
            OffsetUnit = OffsetUnit.Days,
            SendLocalTime = new LocalTime(hour, 0),
        };

    private static RuleResolution ResolveForLeaseA(params NotificationRuleNode[] rules) =>
        NotificationRuleResolver.Resolve(rules, PropertyA, LeaseA);

    // =========================================================================
    // Inheritance
    // =========================================================================

    [Fact]
    public void A_global_rule_is_inherited_by_a_lease()
    {
        var resolution = ResolveForLeaseA(Global(1));

        var only = Assert.Single(resolution.AllRules);
        Assert.Equal(RuleEffect.Inherited, only.Effect);
        Assert.True(only.IsActive);
        Assert.Null(only.InactiveReason);
    }

    [Fact]
    public void A_lease_rule_is_labelled_lease_specific_not_inherited()
    {
        // The UI has to be able to tell the user where a reminder came from.
        var resolution = ResolveForLeaseA(Lease(1, LeaseA));

        Assert.Equal(RuleEffect.LeaseSpecific, Assert.Single(resolution.AllRules).Effect);
    }

    [Fact]
    public void All_three_levels_can_contribute_at_once()
    {
        var resolution = ResolveForLeaseA(
            Global(1, offsetDays: 7),
            Property(2, PropertyA, offsetDays: 3),
            Lease(3, LeaseA, offsetDays: 1));

        Assert.Equal(3, resolution.ActiveRules.Count);
        Assert.Equal(
            [RuleEffect.Inherited, RuleEffect.Inherited, RuleEffect.LeaseSpecific],
            resolution.AllRules.Select(r => r.Effect));
    }

    [Fact]
    public void The_realistic_ladder_from_the_specification_resolves_to_six_active_rules()
    {
        // Section 6.G's example: 7 days before, 3 days before, 1 day before,
        // on the day, 3 days overdue, 5 days overdue.
        var resolution = ResolveForLeaseA(
            Global(1, offsetDays: 7),
            Global(2, offsetDays: 3),
            Global(3, offsetDays: 1),
            Global(4, offsetDays: 0),
            Global(5, offsetDays: 3),
            Global(6, offsetDays: 5));

        Assert.Equal(6, resolution.ActiveRules.Count);
    }

    // =========================================================================
    // Overriding
    // =========================================================================

    [Fact]
    public void A_property_rule_can_override_a_global_rule()
    {
        var resolution = ResolveForLeaseA(
            Global(1, hour: 9),
            Property(2, PropertyA, mode: RuleInheritanceMode.Overridden,
                overrides: RuleId(1), hour: 17));

        var global = resolution.AllRules.Single(r => r.Rule.Id == RuleId(1));
        var property = resolution.AllRules.Single(r => r.Rule.Id == RuleId(2));

        Assert.Equal(RuleEffect.OverriddenByDescendant, global.Effect);
        Assert.False(global.IsActive);
        Assert.Equal(RuleId(2), global.SupersededByRuleId);
        Assert.Contains("property", global.InactiveReason!, StringComparison.Ordinal);

        Assert.Equal(RuleEffect.OverridesAncestor, property.Effect);
        Assert.True(property.IsActive);

        // Exactly one reminder survives, at the overridden time.
        var active = Assert.Single(resolution.ActiveRules);
        Assert.Equal(new LocalTime(17, 0), active.Rule.SendLocalTime);
    }

    [Fact]
    public void A_lease_rule_can_override_a_global_rule_directly()
    {
        var resolution = ResolveForLeaseA(
            Global(1),
            Lease(2, LeaseA, mode: RuleInheritanceMode.Overridden, overrides: RuleId(1)));

        Assert.Equal(
            RuleEffect.OverriddenByDescendant,
            resolution.AllRules.Single(r => r.Rule.Id == RuleId(1)).Effect);
        Assert.Single(resolution.ActiveRules);
    }

    [Fact]
    public void A_lease_override_beats_a_property_override_of_the_same_global_rule()
    {
        // Both the property and the lease claim the same ancestor. The
        // narrower scope has to win, or a lease-level decision would be
        // overruled by a property default.
        var resolution = ResolveForLeaseA(
            Global(1, hour: 9),
            Property(2, PropertyA, mode: RuleInheritanceMode.Overridden,
                overrides: RuleId(1), hour: 12),
            Lease(3, LeaseA, mode: RuleInheritanceMode.Overridden,
                overrides: RuleId(1), hour: 17));

        Assert.Equal(
            RuleId(3),
            resolution.AllRules.Single(r => r.Rule.Id == RuleId(1)).SupersededByRuleId);

        // Both overrides remain in force in their own right; only the global
        // rule is suppressed. The property rule is still a rule.
        Assert.Equal(2, resolution.ActiveRules.Count);
    }

    // =========================================================================
    // Disabling
    // =========================================================================

    [Fact]
    public void A_lease_can_switch_off_an_inherited_rule()
    {
        var resolution = ResolveForLeaseA(
            Global(1),
            Lease(2, LeaseA, mode: RuleInheritanceMode.Disabled, overrides: RuleId(1)));

        var global = resolution.AllRules.Single(r => r.Rule.Id == RuleId(1));
        var stub = resolution.AllRules.Single(r => r.Rule.Id == RuleId(2));

        Assert.Equal(RuleEffect.DisabledByDescendant, global.Effect);
        Assert.False(global.IsActive);
        Assert.Contains("Switched off", global.InactiveReason!, StringComparison.Ordinal);

        // The disabling entry exists only to suppress; it never sends anything
        // of its own.
        Assert.Equal(RuleEffect.DisablesAncestor, stub.Effect);
        Assert.False(stub.IsActive);

        // The net effect is silence for this lease.
        Assert.Empty(resolution.ActiveRules);
    }

    [Fact]
    public void A_rule_that_is_simply_disabled_is_inactive_without_suppressing_anything()
    {
        var resolution = ResolveForLeaseA(Global(1, enabled: false), Global(2));

        Assert.False(resolution.AllRules.Single(r => r.Rule.Id == RuleId(1)).IsActive);
        Assert.Single(resolution.ActiveRules);
    }

    [Fact]
    public void A_paused_rule_is_inactive_and_reports_the_pause_reason()
    {
        var paused = Global(1) with
        {
            PausedAt = Instant.FromUtc(2026, 4, 1, 12, 0),
            PausedReason = "Paused automatically after 5 failed sends in 60 minutes.",
        };

        var resolution = ResolveForLeaseA(paused);

        var only = Assert.Single(resolution.AllRules);
        Assert.False(only.IsActive);
        Assert.Contains("failed sends", only.InactiveReason!, StringComparison.Ordinal);
    }

    // =========================================================================
    // Cross-lease and cross-property isolation
    //
    // Section 6.G: "Editing one tenant's rules must not unintentionally alter
    // other leases."
    // =========================================================================

    [Fact]
    public void Another_lease_rules_are_never_considered()
    {
        var resolution = ResolveForLeaseA(
            Global(1),
            Lease(2, LeaseB, offsetDays: 1));   // belongs to a different lease

        var only = Assert.Single(resolution.AllRules);
        Assert.Equal(RuleId(1), only.Rule.Id);
    }

    [Fact]
    public void Another_property_rules_are_never_considered()
    {
        var resolution = ResolveForLeaseA(
            Global(1),
            Property(2, PropertyB));            // belongs to a different property

        var only = Assert.Single(resolution.AllRules);
        Assert.Equal(RuleId(1), only.Rule.Id);
    }

    [Fact]
    public void An_override_created_on_one_lease_does_not_suppress_the_rule_for_another()
    {
        // The exact scenario the specification warns about. Lease A switches
        // off the shared global reminder. Lease B must still receive it.
        var rules = new[]
        {
            Global(1),
            Lease(2, LeaseA, mode: RuleInheritanceMode.Disabled, overrides: RuleId(1)),
        };

        var forLeaseA = NotificationRuleResolver.Resolve(rules, PropertyA, LeaseA);
        var forLeaseB = NotificationRuleResolver.Resolve(rules, PropertyA, LeaseB);

        Assert.Empty(forLeaseA.ActiveRules);

        var stillActiveForB = Assert.Single(forLeaseB.ActiveRules);
        Assert.Equal(RuleId(1), stillActiveForB.Rule.Id);
        Assert.Equal(RuleEffect.Inherited, stillActiveForB.Effect);
    }

    [Fact]
    public void A_property_override_applies_to_its_own_property_only()
    {
        var rules = new[]
        {
            Global(1, hour: 9),
            Property(2, PropertyA, mode: RuleInheritanceMode.Overridden,
                overrides: RuleId(1), hour: 17),
        };

        var leaseOnPropertyA = NotificationRuleResolver.Resolve(rules, PropertyA, LeaseA);
        var leaseOnPropertyB = NotificationRuleResolver.Resolve(rules, PropertyB, LeaseB);

        Assert.Equal(new LocalTime(17, 0),
            Assert.Single(leaseOnPropertyA.ActiveRules).Rule.SendLocalTime);

        // Property B's lease still gets the original global timing.
        Assert.Equal(new LocalTime(9, 0),
            Assert.Single(leaseOnPropertyB.ActiveRules).Rule.SendLocalTime);
    }

    [Fact]
    public void An_override_naming_a_rule_that_does_not_apply_here_is_inert()
    {
        // A lease rule pointing at a rule belonging to another property cannot
        // reach it. It neither throws nor suppresses anything.
        var resolution = ResolveForLeaseA(
            Global(1),
            Property(2, PropertyB),
            Lease(3, LeaseA, mode: RuleInheritanceMode.Overridden, overrides: RuleId(2)));

        Assert.True(resolution.AllRules.Single(r => r.Rule.Id == RuleId(1)).IsActive);
        Assert.Equal(2, resolution.ActiveRules.Count);
    }

    // =========================================================================
    // Determinism and ordering
    // =========================================================================

    [Fact]
    public void Resolution_does_not_depend_on_the_order_rules_arrive_in()
    {
        // Rules come back from a query, and a query without an ORDER BY can
        // return rows in any order. Resolution must not change with it.
        var rules = new[]
        {
            Global(1, offsetDays: 7),
            Property(2, PropertyA, mode: RuleInheritanceMode.Overridden, overrides: RuleId(1)),
            Lease(3, LeaseA, offsetDays: 1),
        };

        var forward = NotificationRuleResolver.Resolve(rules, PropertyA, LeaseA);
        var reversed = NotificationRuleResolver.Resolve(rules.Reverse(), PropertyA, LeaseA);

        Assert.Equal(
            forward.AllRules.Select(r => (r.Rule.Id, r.Effect, r.IsActive)),
            reversed.AllRules.Select(r => (r.Rule.Id, r.Effect, r.IsActive)));
    }

    [Fact]
    public void Rules_are_grouped_by_event_for_display()
    {
        var resolution = ResolveForLeaseA(
            Global(1, eventType: NotificationEventType.LeaseExpiration),
            Global(2, eventType: NotificationEventType.RentDue),
            Global(3, eventType: NotificationEventType.RentDue));

        var eventOrder = resolution.AllRules.Select(r => r.Rule.EventType).ToList();

        Assert.Equal(eventOrder.OrderBy(e => e), eventOrder);
    }

    [Fact]
    public void An_empty_rule_set_resolves_to_nothing_rather_than_failing()
    {
        var resolution = ResolveForLeaseA();

        Assert.Empty(resolution.AllRules);
        Assert.Empty(resolution.ActiveRules);
    }
}
