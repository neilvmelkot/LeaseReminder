using LeaseReminder.Domain;
using Npgsql;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// Builds the Npgsql data source, and refuses to build one whose settings
/// would undermine the isolation design.
///
/// <para>
/// Three things are checked, because each is a configuration mistake that
/// produces no error at runtime and no visible symptom until data leaks:
/// </para>
/// <list type="number">
///   <item>
///     <b>Connection reset must stay on.</b>
///     <see cref="RlsIdentityInterceptor"/> sets the caller identity at session
///     scope and relies on Npgsql clearing it when the connection returns to
///     the pool. With <c>No Reset On Close=true</c>, one request's identity
///     would persist on a pooled connection and the next request could read
///     the previous user's rows. This is the single most dangerous setting in
///     the connection string.
///   </item>
///   <item>
///     <b>TLS must be required.</b> Section 4.I asks for TLS on database
///     connections, and the data here is lease terms and personal details.
///   </item>
///   <item>
///     <b>The application must not connect as an owner or superuser.</b> RLS
///     does not apply to a table's owner, so connecting as the migration role
///     would disable every policy in db/migrations/0009 at once, silently.
///   </item>
/// </list>
/// </summary>
public static class NpgsqlDataSourceFactory
{
    /// <summary>Roles the services must never connect as.</summary>
    private static readonly string[] ForbiddenRoles =
        ["postgres", "app_owner", "supabase_admin", "rds_superuser", "root"];

    public static NpgsqlDataSource Create(string connectionString, bool requireTls = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Validate(builder, requireTls);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);

        // Map the PostgreSQL enum types from db/migrations/0001. Without
        // these, every enum column round-trips as text and a typo in a status
        // value becomes a runtime cast error rather than a compile error.
        MapEnums(dataSourceBuilder);

        // NodaTime instead of DateTime, matching the domain model.
        dataSourceBuilder.UseNodaTime();

        return dataSourceBuilder.Build();
    }

    private static void Validate(NpgsqlConnectionStringBuilder builder, bool requireTls)
    {
        if (builder.NoResetOnClose)
        {
            throw new InvalidOperationException(
                "The connection string sets 'No Reset On Close=true'. Row Level Security " +
                "identity is applied at session scope and depends on Npgsql resetting " +
                "session state when a connection returns to the pool. With reset disabled, " +
                "one request's identity would persist on a pooled connection and the next " +
                "request could read another user's data. Remove the setting.");
        }

        // Prefer is included in the rejected set deliberately. It silently
        // falls back to an unencrypted connection when the server declines
        // TLS, which is the one outcome that must not be possible here.
        if (requireTls && builder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
        {
            throw new InvalidOperationException(
                $"The connection string uses SSL Mode={builder.SslMode}, which permits an " +
                "unencrypted connection. Lease terms, addresses, and personal data must " +
                "travel over TLS. Use VerifyFull in production, or Require at minimum.");
        }

        var username = builder.Username;

        if (!string.IsNullOrEmpty(username)
            && ForbiddenRoles.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The application is configured to connect as '{username}'. Row Level " +
                "Security does not apply to a table's owner or to a superuser, so " +
                "connecting as that role would disable every isolation policy at once. " +
                "Use app_api for the API and app_worker for the worker; the owner role is " +
                "for migrations only.");
        }
    }

    /// <summary>
    /// Every PostgreSQL enum type and the CLR enum it maps to.
    ///
    /// <para>
    /// Exposed so <see cref="StartupEnumMappingCheck"/> can verify each pair
    /// against the live database at boot. A mapping that is merely declared
    /// here and never checked fails at the first write of an unusual value,
    /// which in this system could be months after deployment.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(Type ClrType, string PostgresTypeName)> MappedEnums { get; } =
    [
        (typeof(OrgRole), "app.org_role"),
        (typeof(PropertyType), "app.property_type"),
        (typeof(UnitStatus), "app.unit_status"),
        (typeof(TenantStatus), "app.tenant_status"),
        (typeof(LeaseLifecycleStatus), "app.lease_lifecycle_status"),
        (typeof(LeaseApprovalStatus), "app.lease_approval_status"),
        (typeof(DocumentProcessingStatus), "app.document_processing_status"),
        (typeof(MalwareScanStatus), "app.malware_scan_status"),
        (typeof(ExtractionReviewStatus), "app.extraction_review_status"),
        (typeof(FieldReviewStatus), "app.field_review_status"),
        (typeof(ValueProvenance), "app.value_provenance"),
        (typeof(RentFrequency), "app.rent_frequency"),
        (typeof(DueDayRule), "app.due_day_rule"),
        (typeof(ShortMonthFallback), "app.short_month_fallback"),
        (typeof(BusinessDayShift), "app.business_day_shift"),
        (typeof(NotificationEventType), "app.notification_event_type"),
        (typeof(NotificationTriggerType), "app.notification_trigger_type"),
        (typeof(OffsetUnit), "app.offset_unit"),
        (typeof(RuleInheritanceMode), "app.rule_inheritance_mode"),
        (typeof(RecipientKind), "app.recipient_kind"),
        (typeof(NotificationAutomationStatus), "app.notification_automation_status"),
        (typeof(EmailConnectionStatus), "app.email_connection_status"),
        (typeof(EmailTemplateStatus), "app.email_template_status"),
        (typeof(ScheduledNotificationStatus), "app.scheduled_notification_status"),
        (typeof(EmailSendStatus), "app.email_send_status"),
        (typeof(EmailErrorCategory), "app.email_error_category"),
        (typeof(JobKind), "app.job_kind"),
        (typeof(JobStatus), "app.job_status"),

        // Maintenance module (0013, 0014).
        (typeof(TaskPriority), "app.task_priority"),
        (typeof(TaskStatusCategory), "app.task_status_category"),
        (typeof(TaskCategory), "app.task_category"),
        (typeof(TaskSource), "app.task_source"),
        (typeof(TaskEventType), "app.task_event_type"),
        (typeof(TaskRuleTrigger), "app.task_rule_trigger"),
        (typeof(TaskScheduleBasis), "app.task_schedule_basis"),
        (typeof(TaskRecipientRole), "app.task_recipient_role"),
        (typeof(OutboxStatus), "app.outbox_status"),
        (typeof(DeliveryChannel), "app.delivery_channel"),
        (typeof(DeliveryStatus), "app.delivery_status"),

        // OccurrenceAdjustment is deliberately absent: rent_occurrences
        // .adjustment_reason is a text column with a CHECK constraint, not a
        // PostgreSQL enum, so it needs no type mapping.
    ];

    private static void MapEnums(NpgsqlDataSourceBuilder builder)
    {
        // The custom translator exists because the built-in snake-case one
        // turns Every30Days into every30_days rather than every_30_days. See
        // PostgresEnumNameTranslator.
        var names = PostgresEnumNameTranslator.Instance;

        builder.MapEnum<OrgRole>("app.org_role", names);
        builder.MapEnum<PropertyType>("app.property_type", names);
        builder.MapEnum<UnitStatus>("app.unit_status", names);
        builder.MapEnum<TenantStatus>("app.tenant_status", names);
        builder.MapEnum<LeaseLifecycleStatus>("app.lease_lifecycle_status", names);
        builder.MapEnum<LeaseApprovalStatus>("app.lease_approval_status", names);
        builder.MapEnum<DocumentProcessingStatus>("app.document_processing_status", names);
        builder.MapEnum<MalwareScanStatus>("app.malware_scan_status", names);
        builder.MapEnum<ExtractionReviewStatus>("app.extraction_review_status", names);
        builder.MapEnum<FieldReviewStatus>("app.field_review_status", names);
        builder.MapEnum<ValueProvenance>("app.value_provenance", names);
        builder.MapEnum<RentFrequency>("app.rent_frequency", names);
        builder.MapEnum<DueDayRule>("app.due_day_rule", names);
        builder.MapEnum<ShortMonthFallback>("app.short_month_fallback", names);
        builder.MapEnum<BusinessDayShift>("app.business_day_shift", names);
        builder.MapEnum<NotificationEventType>("app.notification_event_type", names);
        builder.MapEnum<NotificationTriggerType>("app.notification_trigger_type", names);
        builder.MapEnum<OffsetUnit>("app.offset_unit", names);
        builder.MapEnum<RuleInheritanceMode>("app.rule_inheritance_mode", names);
        builder.MapEnum<RecipientKind>("app.recipient_kind", names);
        builder.MapEnum<NotificationAutomationStatus>("app.notification_automation_status", names);
        builder.MapEnum<EmailConnectionStatus>("app.email_connection_status", names);
        builder.MapEnum<EmailTemplateStatus>("app.email_template_status", names);
        builder.MapEnum<ScheduledNotificationStatus>("app.scheduled_notification_status", names);
        builder.MapEnum<EmailSendStatus>("app.email_send_status", names);
        builder.MapEnum<EmailErrorCategory>("app.email_error_category", names);
        builder.MapEnum<JobKind>("app.job_kind", names);
        builder.MapEnum<JobStatus>("app.job_status", names);

        // Maintenance module.
        builder.MapEnum<TaskPriority>("app.task_priority", names);
        builder.MapEnum<TaskStatusCategory>("app.task_status_category", names);
        builder.MapEnum<TaskCategory>("app.task_category", names);
        builder.MapEnum<TaskSource>("app.task_source", names);
        builder.MapEnum<TaskEventType>("app.task_event_type", names);
        builder.MapEnum<TaskRuleTrigger>("app.task_rule_trigger", names);
        builder.MapEnum<TaskScheduleBasis>("app.task_schedule_basis", names);
        builder.MapEnum<TaskRecipientRole>("app.task_recipient_role", names);
        builder.MapEnum<OutboxStatus>("app.outbox_status", names);
        builder.MapEnum<DeliveryChannel>("app.delivery_channel", names);
        builder.MapEnum<DeliveryStatus>("app.delivery_status", names);
    }
}
