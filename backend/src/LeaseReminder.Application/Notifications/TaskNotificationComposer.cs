using System.Globalization;
using System.Text.Json;
using LeaseReminder.Domain;

namespace LeaseReminder.Application.Notifications;

/// <summary>
/// Turns a task event into the words somebody reads.
///
/// <para>
/// Pure: an event payload and a recipient role in, a title and body out. No
/// database, no clock, no provider. That is what makes the wording testable,
/// and the wording is the part most likely to be wrong in a way nobody
/// notices until a tenant receives something odd.
/// </para>
///
/// <h3>Why the message depends on the recipient's role</h3>
///
/// The same event reaches different people who need different things from it.
/// When a repair is completed:
///
/// <list type="bullet">
///   <item>the reporting tenant wants "the repair you reported is finished";</item>
///   <item>the manager wants "task 142 completed, cost recorded";</item>
///   <item>the assignee already knows, and is excluded upstream.</item>
/// </list>
///
/// <para>
/// One message for all of them would either be internal jargon sent to a
/// tenant or a reassurance sent to a manager who needs the reference number.
/// So the role is a parameter, and the tenant-facing wording deliberately
/// omits internal detail: no cost, no assignee, no column names.
/// </para>
/// </summary>
public static class TaskNotificationComposer
{
    /// <summary>
    /// Composes the notification for one recipient.
    /// </summary>
    /// <param name="eventType">The event label, as stored in the outbox.</param>
    /// <param name="payload">The event payload snapshot.</param>
    /// <param name="recipientRole">Why this person is being told.</param>
    /// <param name="actorDisplayName">Who caused it, when a person did.</param>
    public static ComposedNotification Compose(
        string eventType,
        JsonElement payload,
        TaskRecipientRole recipientRole,
        string? actorDisplayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);

        var reference = ReadInt(payload, "reference_number");
        var title = ReadString(payload, "title") ?? "a maintenance task";
        var taskId = ReadString(payload, "task_id");

        // Tenants get a different vocabulary throughout: "the repair you
        // reported", never "task 142" or a column name.
        var isTenant = recipientRole == TaskRecipientRole.ReportingTenant;

        var label = reference.HasValue
            ? $"#{reference.Value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        var linkPath = taskId is null ? null : $"/maintenance/{taskId}";

        return eventType switch
        {
            "task_created" => isTenant
                ? new ComposedNotification(
                    "We have received your maintenance request",
                    $"Thank you for reporting \"{title}\". We have logged it and will be " +
                    "in touch as it progresses.",
                    linkPath)
                : new ComposedNotification(
                    $"New maintenance request {label}",
                    Join(
                        title,
                        DescribePriority(payload),
                        DescribeLocation(payload),
                        DescribeDue(payload)),
                    linkPath),

            "task_assigned" => new ComposedNotification(
                recipientRole == TaskRecipientRole.Assignee
                    // Addressed to the person who now has to act, so it leads
                    // with that rather than with the task number.
                    ? $"You have been assigned {label}: {title}"
                    : $"{label} assigned to {ReadString(payload, "assignee_name") ?? "somebody"}",
                Join(DescribePriority(payload), DescribeLocation(payload), DescribeDue(payload)),
                linkPath),

            "task_reassigned" => new ComposedNotification(
                recipientRole == TaskRecipientRole.Assignee
                    ? $"You have been assigned {label}: {title}"
                    : $"{label} reassigned to {ReadString(payload, "assignee_name") ?? "somebody"}",
                Join(
                    ReadString(payload, "previous_assignees") is { Length: > 0 } previous
                        ? $"Previously with {previous}."
                        : null,
                    DescribePriority(payload),
                    DescribeLocation(payload)),
                linkPath),

            "task_status_changed" => new ComposedNotification(
                $"{label} moved to {ReadString(payload, "to_column") ?? "a new column"}",
                Join(
                    title,
                    ReadString(payload, "from_column") is { } from
                        ? $"Moved from {from}."
                        : null,
                    DescribeLocation(payload)),
                linkPath),

            "task_priority_changed" => new ComposedNotification(
                $"{label} is now {ReadString(payload, "to_priority") ?? "a different"} priority",
                Join(
                    title,
                    ReadString(payload, "from_priority") is { } fromPriority
                        ? $"Changed from {fromPriority}."
                        : null,
                    DescribeLocation(payload)),
                linkPath),

            "task_due_date_changed" => new ComposedNotification(
                ReadString(payload, "to_due_date") is { } newDue
                    ? $"{label} is now due {newDue}"
                    : $"{label} no longer has a due date",
                Join(title, DescribeLocation(payload)),
                linkPath),

            "task_comment_added" => new ComposedNotification(
                actorDisplayName is { Length: > 0 } author
                    ? $"{author} commented on {label}"
                    : $"New comment on {label}",
                // The excerpt is already bounded to 300 characters in the
                // payload, so the whole comment never ends up in an email.
                Join(ReadString(payload, "comment_excerpt"), title),
                linkPath),

            "task_completed" => isTenant
                ? new ComposedNotification(
                    "Your maintenance request has been completed",
                    $"\"{title}\" has been marked complete. If the problem has not been " +
                    "resolved, please let us know and we will reopen it.",
                    linkPath)
                : new ComposedNotification(
                    $"{label} completed",
                    Join(title, DescribeCost(payload), DescribeLocation(payload)),
                    linkPath),

            "task_reopened" => new ComposedNotification(
                $"{label} has been reopened",
                Join(title, DescribeLocation(payload)),
                linkPath),

            "task_cost_updated" => new ComposedNotification(
                $"Cost recorded on {label}",
                Join(title, DescribeCost(payload), DescribeLocation(payload)),
                linkPath),

            // --- Time-based ---------------------------------------------------

            "task_due_soon" => new ComposedNotification(
                $"{label} is due {ReadString(payload, "due_date") ?? "soon"}",
                Join(title, DescribePriority(payload), DescribeLocation(payload)),
                linkPath),

            "task_due_today" => new ComposedNotification(
                $"{label} is due today",
                Join(title, DescribePriority(payload), DescribeLocation(payload)),
                linkPath),

            "task_overdue" => new ComposedNotification(
                ReadInt(payload, "days_overdue") is { } days and > 0
                    // Naming the number of days is what makes an escalation
                    // read as an escalation rather than as the same email again.
                    ? $"{label} is {days} day{(days == 1 ? "" : "s")} overdue"
                    : $"{label} is overdue",
                Join(title, DescribePriority(payload), DescribeLocation(payload)),
                linkPath),

            "task_stale" => new ComposedNotification(
                $"{label} has had no activity for " +
                $"{ReadInt(payload, "days_inactive")?.ToString(CultureInfo.InvariantCulture) ?? "several"} days",
                Join(
                    title,
                    ReadString(payload, "status_category") is { } status
                        ? $"Still in {HumanizeStatus(status)}."
                        : null,
                    DescribeLocation(payload)),
                linkPath),

            // An event with no composer is a programming gap, not a user
            // problem. A generic message is better than no notification, and
            // the event type is included so it is traceable.
            _ => new ComposedNotification(
                $"Update on {label}",
                Join(title, $"({eventType})"),
                linkPath),
        };
    }

    /* ---------------------------------------------------------------------- */
    /* Fragments                                                              */
    /* ---------------------------------------------------------------------- */

    private static string? DescribePriority(JsonElement payload) =>
        ReadString(payload, "priority") switch
        {
            "urgent" => "Urgent priority.",
            "high" => "High priority.",
            // Medium and low are the default and the quiet case; saying so on
            // every notification is noise.
            _ => null,
        };

    private static string? DescribeLocation(JsonElement payload)
    {
        // The property name is not in every payload, because not every event
        // needs it. Absent is fine; a fragment that reads "at ." is not.
        var property = ReadString(payload, "property_name");
        var unit = ReadString(payload, "unit_number");

        if (property is null && unit is null)
        {
            return null;
        }

        return unit is null ? property : $"{property}, unit {unit}";
    }

    private static string? DescribeDue(JsonElement payload) =>
        ReadString(payload, "due_date") is { Length: > 0 } due ? $"Due {due}." : null;

    private static string? DescribeCost(JsonElement payload)
    {
        var actual = ReadString(payload, "actual_cost");

        if (string.IsNullOrEmpty(actual))
        {
            return null;
        }

        var currency = ReadString(payload, "currency") ?? "USD";
        var estimated = ReadString(payload, "estimated_cost");

        // The estimate is shown alongside the actual only when both exist,
        // because the comparison is the useful part.
        return string.IsNullOrEmpty(estimated)
            ? $"Cost {currency} {actual}."
            : $"Cost {currency} {actual}, estimated {currency} {estimated}.";
    }

    private static string HumanizeStatus(string statusCategory) => statusCategory switch
    {
        "in_progress" => "progress",
        "new" => "the new column",
        "assigned" => "assigned",
        "blocked" => "blocked",
        "review" => "review",
        "backlog" => "the backlog",
        _ => statusCategory.Replace('_', ' '),
    };

    /// <summary>
    /// Joins the fragments that exist, with single spaces and no double
    /// punctuation. Returns a non-empty string, because the body column is
    /// NOT NULL and a blank notification would be worse than a terse one.
    /// </summary>
    private static string Join(params string?[] fragments)
    {
        var present = fragments
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
            .Select(fragment => fragment!.Trim())
            .ToList();

        return present.Count > 0
            ? string.Join(' ', present)
            : "Open the task for details.";
    }

    /* ---------------------------------------------------------------------- */
    /* Payload reading                                                        */
    /*                                                                        */
    /* Tolerant by design. The payload is a snapshot written by a database     */
    /* function, and a notification must not fail to render because one        */
    /* optional field is absent or arrived as a number rather than a string.   */
    /* ---------------------------------------------------------------------- */

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>A composed notification, ready to store or send.</summary>
public sealed record ComposedNotification(string Title, string Body, string? LinkPath);
