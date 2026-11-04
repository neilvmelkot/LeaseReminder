using LeaseReminder.Domain;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// Registers the PostgreSQL enum types with EF Core.
///
/// <para>
/// Two separate registrations are needed and it is easy to assume one covers
/// both. <see cref="NpgsqlDataSourceFactory"/> maps the enums at the ADO level,
/// which is what lets Npgsql serialise a CLR enum value to the wire. This
/// method maps them at the EF level, which is what stops EF Core treating the
/// column as an integer.
/// </para>
///
/// <para>
/// Without this, EF applies its default enum-to-integer conversion and the
/// failure is split in two confusing halves: a write fails with
/// <c>42804 datatype_mismatch</c> because EF sends an int to an enum column,
/// and a read fails with "reading and writing unmapped enums requires an
/// explicit opt-in" because EF asks the reader for an Int32. Neither message
/// mentions enums being unmapped in the EF model, which is the actual cause.
/// </para>
///
/// <para>
/// It lives here, as one extension method, because the API, the worker, and
/// the integration test fixture all construct their own
/// <see cref="Microsoft.EntityFrameworkCore.DbContextOptions"/>. Three copies
/// of this list would drift, and the symptom of drift is a runtime failure on
/// whichever enum was forgotten.
/// </para>
/// </summary>
public static class EfEnumMappings
{
    /// <summary>
    /// Call inside <c>UseNpgsql(..., npgsql => npgsql.MapApplicationEnums())</c>.
    /// </summary>
    public static NpgsqlDbContextOptionsBuilder MapApplicationEnums(
        this NpgsqlDbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The custom translator, not the default snake-case one: see
        // PostgresEnumNameTranslator for why Every30Days is the case that
        // matters.
        var names = PostgresEnumNameTranslator.Instance;
        const string schema = "app";

        builder.MapEnum<OrgRole>("org_role", schema, names);
        builder.MapEnum<PropertyType>("property_type", schema, names);
        builder.MapEnum<UnitStatus>("unit_status", schema, names);
        builder.MapEnum<TenantStatus>("tenant_status", schema, names);
        builder.MapEnum<LeaseLifecycleStatus>("lease_lifecycle_status", schema, names);
        builder.MapEnum<LeaseApprovalStatus>("lease_approval_status", schema, names);
        builder.MapEnum<DocumentProcessingStatus>("document_processing_status", schema, names);
        builder.MapEnum<MalwareScanStatus>("malware_scan_status", schema, names);
        builder.MapEnum<ExtractionReviewStatus>("extraction_review_status", schema, names);
        builder.MapEnum<FieldReviewStatus>("field_review_status", schema, names);
        builder.MapEnum<ValueProvenance>("value_provenance", schema, names);
        builder.MapEnum<RentFrequency>("rent_frequency", schema, names);
        builder.MapEnum<DueDayRule>("due_day_rule", schema, names);
        builder.MapEnum<ShortMonthFallback>("short_month_fallback", schema, names);
        builder.MapEnum<BusinessDayShift>("business_day_shift", schema, names);
        builder.MapEnum<NotificationEventType>("notification_event_type", schema, names);
        builder.MapEnum<NotificationTriggerType>("notification_trigger_type", schema, names);
        builder.MapEnum<OffsetUnit>("offset_unit", schema, names);
        builder.MapEnum<RuleInheritanceMode>("rule_inheritance_mode", schema, names);
        builder.MapEnum<RecipientKind>("recipient_kind", schema, names);
        builder.MapEnum<NotificationAutomationStatus>(
            "notification_automation_status", schema, names);
        builder.MapEnum<EmailConnectionStatus>("email_connection_status", schema, names);
        builder.MapEnum<EmailTemplateStatus>("email_template_status", schema, names);
        builder.MapEnum<ScheduledNotificationStatus>(
            "scheduled_notification_status", schema, names);
        builder.MapEnum<EmailSendStatus>("email_send_status", schema, names);
        builder.MapEnum<EmailErrorCategory>("email_error_category", schema, names);
        builder.MapEnum<JobKind>("job_kind", schema, names);
        builder.MapEnum<JobStatus>("job_status", schema, names);

        // Maintenance module.
        builder.MapEnum<TaskPriority>("task_priority", schema, names);
        builder.MapEnum<TaskStatusCategory>("task_status_category", schema, names);
        builder.MapEnum<TaskCategory>("task_category", schema, names);
        builder.MapEnum<TaskSource>("task_source", schema, names);
        builder.MapEnum<TaskEventType>("task_event_type", schema, names);
        builder.MapEnum<TaskRuleTrigger>("task_rule_trigger", schema, names);
        builder.MapEnum<TaskScheduleBasis>("task_schedule_basis", schema, names);
        builder.MapEnum<TaskRecipientRole>("task_recipient_role", schema, names);
        builder.MapEnum<OutboxStatus>("outbox_status", schema, names);
        builder.MapEnum<DeliveryChannel>("delivery_channel", schema, names);
        builder.MapEnum<DeliveryStatus>("delivery_status", schema, names);

        // OccurrenceAdjustment is absent on purpose: rent_occurrences
        // .adjustment_reason is a text column with a CHECK constraint, not a
        // PostgreSQL enum. StartupEnumMappingCheck asserts the same split.

        return builder;
    }
}
