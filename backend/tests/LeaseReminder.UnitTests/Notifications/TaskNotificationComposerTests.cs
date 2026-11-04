using System.Text.Json;
using LeaseReminder.Application.Notifications;
using LeaseReminder.Domain;
using Xunit;

namespace LeaseReminder.UnitTests.Notifications;

/// <summary>
/// The wording of a task notification.
///
/// <para>
/// Worth testing because it is the part most likely to be wrong in a way
/// nobody notices until a tenant receives something strange. Two properties
/// matter more than the exact phrasing:
/// </para>
///
/// <list type="bullet">
///   <item>
///     A tenant never receives internal vocabulary. No reference numbers, no
///     column names, no costs, no assignee names. The same event sent to a
///     manager carries all of it.
///   </item>
///   <item>
///     A missing or oddly-typed payload field never produces a blank or
///     crashing notification. The payload is written by a database function
///     and read here; a mismatch must degrade, not fail.
///   </item>
/// </list>
/// </summary>
public class TaskNotificationComposerTests
{
    private static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static JsonElement StandardPayload(
        int referenceNumber = 142,
        string title = "Roof leak above the stairwell",
        string priority = "urgent") =>
        Payload($$"""
            {
              "task_id": "11111111-1111-1111-1111-111111111111",
              "reference_number": {{referenceNumber}},
              "title": "{{title}}",
              "priority": "{{priority}}",
              "property_name": "Maple Street",
              "unit_number": "2",
              "due_date": "2026-11-02"
            }
            """);

    // =========================================================================
    // A tenant gets tenant language
    // =========================================================================

    [Fact]
    public void A_tenant_hearing_about_their_own_report_gets_an_acknowledgement()
    {
        var result = TaskNotificationComposer.Compose(
            "task_created",
            StandardPayload(),
            TaskRecipientRole.ReportingTenant,
            actorDisplayName: "Ada Owner");

        Assert.Contains("received your maintenance request", result.Title,
            StringComparison.OrdinalIgnoreCase);

        // No internal vocabulary: a tenant has no use for "#142" and it invites
        // them to quote a number nobody asked them for.
        Assert.DoesNotContain("#142", result.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("#142", result.Body, StringComparison.Ordinal);

        // The thing they reported is named back to them, so they can tell
        // which of two reports this is.
        Assert.Contains("Roof leak", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manager_hearing_about_the_same_report_gets_the_reference_and_priority()
    {
        var result = TaskNotificationComposer.Compose(
            "task_created",
            StandardPayload(),
            TaskRecipientRole.PropertyManager,
            actorDisplayName: "Ada Owner");

        Assert.Contains("#142", result.Title, StringComparison.Ordinal);
        Assert.Contains("Urgent", result.Body, StringComparison.Ordinal);
        Assert.Contains("Maple Street", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tenant_hearing_about_completion_is_told_what_to_do_if_it_is_not_fixed()
    {
        var result = TaskNotificationComposer.Compose(
            "task_completed",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak above the stairwell",
                  "actual_cost": "480.00",
                  "currency": "USD"
                }
                """),
            TaskRecipientRole.ReportingTenant,
            actorDisplayName: "Mike Plumbing");

        Assert.Contains("completed", result.Title, StringComparison.OrdinalIgnoreCase);

        // The cost is internal. Telling a tenant what the repair cost invites a
        // conversation nobody intended to have.
        Assert.DoesNotContain("480", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", result.Body, StringComparison.Ordinal);

        // And they are told how to push back, because "completed" is sometimes
        // wrong and a tenant with no route to say so just stops reporting.
        Assert.Contains("reopen", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_manager_hearing_about_completion_gets_the_cost()
    {
        var result = TaskNotificationComposer.Compose(
            "task_completed",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak",
                  "actual_cost": "480.00",
                  "estimated_cost": "350.00",
                  "currency": "USD"
                }
                """),
            TaskRecipientRole.PropertyManager,
            actorDisplayName: "Mike Plumbing");

        Assert.Contains("480.00", result.Body, StringComparison.Ordinal);

        // The estimate appears alongside the actual, because the comparison is
        // the useful part of a cost notification.
        Assert.Contains("350.00", result.Body, StringComparison.Ordinal);
    }

    // =========================================================================
    // Assignment is addressed to whoever has to act
    // =========================================================================

    [Fact]
    public void An_assignee_is_addressed_directly()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 7,
                  "title": "Heating repair",
                  "priority": "high",
                  "assignee_name": "Mike Plumbing",
                  "due_date": "2026-11-05"
                }
                """),
            TaskRecipientRole.Assignee,
            actorDisplayName: "Mia Manager");

        // Leads with "you", not with the task number: the recipient has to act.
        Assert.StartsWith("You have been assigned", result.Title, StringComparison.Ordinal);
        Assert.Contains("Heating repair", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void An_observer_is_told_who_it_went_to()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 7,
                  "title": "Heating repair",
                  "assignee_name": "Mike Plumbing"
                }
                """),
            TaskRecipientRole.PropertyManager,
            actorDisplayName: "Mia Manager");

        Assert.DoesNotContain("You have been assigned", result.Title, StringComparison.Ordinal);
        Assert.Contains("Mike Plumbing", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reassignment_names_who_it_was_taken_from()
    {
        var result = TaskNotificationComposer.Compose(
            "task_reassigned",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 7,
                  "title": "Heating repair",
                  "assignee_name": "Mike Plumbing",
                  "previous_assignees": "Mia Manager"
                }
                """),
            TaskRecipientRole.PropertyManager,
            actorDisplayName: "Ada Owner");

        // Without the previous holder, the person losing the task cannot tell
        // whether they are still responsible for it.
        Assert.Contains("Mia Manager", result.Body, StringComparison.Ordinal);
    }

    // =========================================================================
    // Escalation reads as escalation
    // =========================================================================

    [Fact]
    public void An_overdue_notice_names_the_number_of_days()
    {
        var result = TaskNotificationComposer.Compose(
            "task_overdue",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak",
                  "priority": "urgent",
                  "days_overdue": 3
                }
                """),
            TaskRecipientRole.Assignee,
            actorDisplayName: null);

        // The day count is what distinguishes the third daily notice from the
        // first. Without it, an escalation ladder is the same email repeated.
        Assert.Contains("3 days overdue", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void One_day_overdue_is_singular()
    {
        var result = TaskNotificationComposer.Compose(
            "task_overdue",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak",
                  "days_overdue": 1
                }
                """),
            TaskRecipientRole.Assignee,
            actorDisplayName: null);

        Assert.Contains("1 day overdue", result.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("1 days", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_notice_says_how_long_and_where_it_is_stuck()
    {
        var result = TaskNotificationComposer.Compose(
            "task_stale",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 9,
                  "title": "Window replacement",
                  "status_category": "in_progress",
                  "days_inactive": 7
                }
                """),
            TaskRecipientRole.PropertyManager,
            actorDisplayName: null);

        Assert.Contains("7 days", result.Title, StringComparison.Ordinal);

        // "Still in progress" rather than "still in in_progress".
        Assert.Contains("Still in progress", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("in_progress", result.Body, StringComparison.Ordinal);
    }

    // =========================================================================
    // Status changes
    // =========================================================================

    [Fact]
    public void A_status_change_names_both_columns()
    {
        var result = TaskNotificationComposer.Compose(
            "task_status_changed",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak",
                  "from_column": "New",
                  "to_column": "In Progress"
                }
                """),
            TaskRecipientRole.TaskCreator,
            actorDisplayName: "Mia Manager");

        Assert.Contains("In Progress", result.Title, StringComparison.Ordinal);

        // Where it came from is in the body, because "moved to In Progress" on
        // its own does not say whether that is forward or backward.
        Assert.Contains("New", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_notification_carries_the_excerpt_and_the_author()
    {
        var result = TaskNotificationComposer.Compose(
            "task_comment_added",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 142,
                  "title": "Roof leak",
                  "comment_excerpt": "The contractor cannot get access on Tuesday.",
                  "is_internal": false
                }
                """),
            TaskRecipientRole.Assignee,
            actorDisplayName: "Rory Reader");

        Assert.Contains("Rory Reader", result.Title, StringComparison.Ordinal);
        Assert.Contains("cannot get access", result.Body, StringComparison.Ordinal);
    }

    // =========================================================================
    // The link
    // =========================================================================

    [Fact]
    public void Every_notification_links_to_the_task()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned", StandardPayload(), TaskRecipientRole.Assignee, null);

        Assert.Equal("/maintenance/11111111-1111-1111-1111-111111111111", result.LinkPath);
    }

    [Fact]
    public void The_link_is_relative_so_it_cannot_become_an_open_redirect()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned", StandardPayload(), TaskRecipientRole.Assignee, null);

        // A notification is a link somebody clicks from an email, which is
        // exactly where a redirect to a lookalike sign-in page would work best.
        // The database CHECK enforces the same thing.
        Assert.NotNull(result.LinkPath);
        Assert.StartsWith("/", result.LinkPath, StringComparison.Ordinal);
        Assert.DoesNotContain("://", result.LinkPath, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_with_no_task_id_produces_no_link_rather_than_a_broken_one()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned",
            Payload("""{"reference_number": 1, "title": "Something"}"""),
            TaskRecipientRole.Assignee,
            null);

        Assert.Null(result.LinkPath);
    }

    // =========================================================================
    // Degrading, not failing
    //
    // The payload is written by a database function and read here. A mismatch
    // between the two must produce a terse notification, never a blank one and
    // never an exception: the alternative is an event that dead-letters and a
    // person who is simply never told.
    // =========================================================================

    [Fact]
    public void An_empty_payload_still_produces_a_usable_notification()
    {
        var result = TaskNotificationComposer.Compose(
            "task_assigned", Payload("{}"), TaskRecipientRole.Assignee, null);

        Assert.False(string.IsNullOrWhiteSpace(result.Title));
        Assert.False(string.IsNullOrWhiteSpace(result.Body));
    }

    [Fact]
    public void An_unknown_event_type_still_produces_a_notification()
    {
        var result = TaskNotificationComposer.Compose(
            "task_teleported", StandardPayload(), TaskRecipientRole.Assignee, null);

        Assert.False(string.IsNullOrWhiteSpace(result.Title));

        // The event type is included, so an unhandled event is traceable from
        // the notification somebody received.
        Assert.Contains("task_teleported", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_field_arriving_as_a_string_is_tolerated()
    {
        // jsonb_build_object can produce either, depending on how a value was
        // cast on the way in.
        var result = TaskNotificationComposer.Compose(
            "task_overdue",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": "142",
                  "title": "Roof leak",
                  "days_overdue": "3"
                }
                """),
            TaskRecipientRole.Assignee,
            null);

        Assert.Contains("#142", result.Title, StringComparison.Ordinal);
        Assert.Contains("3 days overdue", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_field_is_treated_as_absent()
    {
        var result = TaskNotificationComposer.Compose(
            "task_created",
            Payload("""
                {
                  "task_id": "11111111-1111-1111-1111-111111111111",
                  "reference_number": 5,
                  "title": "Gutter clearing",
                  "priority": null,
                  "property_name": null,
                  "unit_number": null,
                  "due_date": null
                }
                """),
            TaskRecipientRole.PropertyManager,
            null);

        Assert.Contains("#5", result.Title, StringComparison.Ordinal);

        // No dangling punctuation from fragments that resolved to nothing.
        Assert.DoesNotContain(" ,", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("..", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("null", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("low")]
    public void Ordinary_priorities_are_not_announced(string priority)
    {
        // A chip on every notification saying "Medium priority" is noise
        // competing with the two that say Urgent.
        var result = TaskNotificationComposer.Compose(
            "task_created",
            StandardPayload(priority: priority),
            TaskRecipientRole.PropertyManager,
            null);

        Assert.DoesNotContain("priority", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("urgent", "Urgent")]
    [InlineData("high", "High")]
    public void Raised_priorities_are_announced(string priority, string expected)
    {
        var result = TaskNotificationComposer.Compose(
            "task_created",
            StandardPayload(priority: priority),
            TaskRecipientRole.PropertyManager,
            null);

        Assert.Contains(expected, result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_event_type_the_engine_raises_has_wording()
    {
        // A missing composer is not a crash, it falls through to the generic
        // branch, so nothing else would catch it. This does.
        string[] raisedByTheEngine =
        [
            "task_created", "task_assigned", "task_reassigned",
            "task_status_changed", "task_priority_changed", "task_due_date_changed",
            "task_comment_added", "task_completed", "task_reopened",
            "task_cost_updated", "task_due_soon", "task_due_today",
            "task_overdue", "task_stale",
        ];

        foreach (var eventType in raisedByTheEngine)
        {
            var result = TaskNotificationComposer.Compose(
                eventType, StandardPayload(), TaskRecipientRole.Assignee, "Mia Manager");

            Assert.False(string.IsNullOrWhiteSpace(result.Title));

            // The generic fallback puts the raw event type in the body. If one
            // of these reaches it, the wording is missing.
            Assert.DoesNotContain(
                eventType, result.Body, StringComparison.Ordinal);
        }
    }
}
