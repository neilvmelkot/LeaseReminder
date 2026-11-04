using LeaseReminder.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping for the maintenance module.
///
/// <para>
/// Reads go through these entities; WRITES go through the SECURITY DEFINER
/// functions in db/migrations/0015, called with ExecuteSqlInterpolated. That
/// split is deliberate rather than inconsistent: a task change has to commit
/// together with its timeline entry and its outbox event, and the change
/// tracker cannot express "these three rows or none". Letting the API update
/// `tasks` directly would make it possible to move a card without raising the
/// event, which is the one failure the outbox exists to prevent.
/// </para>
/// </summary>
internal static class MaintenanceModelConfiguration
{
    internal static void ConfigureMaintenance(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TaskColumn>(entity =>
        {
            entity.ToTable("task_columns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Position).HasColumnName("position").HasPrecision(20, 10);
            entity.Property(e => e.WorkInProgressLimit)
                .HasColumnName("work_in_progress_limit");
            entity.Property(e => e.Colour).HasColumnName("colour");
            entity.Property(e => e.CollapsedByDefault).HasColumnName("collapsed_by_default");
            entity.Property(e => e.IsDefaultIntake).HasColumnName("is_default_intake");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(e => new { e.OrganizationId, e.Position });
        });

        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.ToTable("vendors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Company).HasColumnName("company");
            entity.Property(e => e.Email).HasColumnName("email").HasColumnType("citext");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.Trades).HasColumnName("trades");
            entity.Property(e => e.HourlyRate).HasColumnName("hourly_rate").HasPrecision(12, 2);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.Active).HasColumnName("active");
            entity.Property(e => e.InsuranceExpiresOn).HasColumnName("insurance_expires_on");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<MaintenanceTask>(entity =>
        {
            entity.ToTable("tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ReferenceNumber).HasColumnName("reference_number");
            entity.Property(e => e.PropertyId).HasColumnName("property_id");
            entity.Property(e => e.UnitId).HasColumnName("unit_id");
            entity.Property(e => e.ReportedByTenantId)
                .HasColumnName("reported_by_tenant_id");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.TaskColumnId).HasColumnName("task_column_id");
            entity.Property(e => e.StatusCategory).HasColumnName("status_category");
            entity.Property(e => e.Position).HasColumnName("position").HasPrecision(20, 10);
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.DueDate).HasColumnName("due_date");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
            entity.Property(e => e.ArchivedByUserId)
                .HasColumnName("archived_by_user_id");
            entity.Property(e => e.EstimatedCost)
                .HasColumnName("estimated_cost").HasPrecision(14, 2);
            entity.Property(e => e.ActualCost)
                .HasColumnName("actual_cost").HasPrecision(14, 2);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.VendorId).HasColumnName("vendor_id");
            entity.Property(e => e.ParentTaskId).HasColumnName("parent_task_id");
            entity.Property(e => e.RecurrenceIntervalDays)
                .HasColumnName("recurrence_interval_days");
            entity.Property(e => e.RecurrenceParentId).HasColumnName("recurrence_parent_id");
            entity.Property(e => e.LastActivityAt).HasColumnName("last_activity_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");

            // Computed in C# from StatusCategory; there is no such column and
            // EF must not try to read or write one.
            entity.Ignore(e => e.IsOpen);

            // Not mapped. Nothing queries it: every assignee read goes through
            // dbContext.TaskAssignees with an explicit task id, which is what
            // keeps the join shape visible at the call site rather than
            // inferred from a navigation. See the TaskAssignee configuration
            // below for what that inference cost once already.
            entity.Ignore(e => e.Assignees);

            entity.HasOne(e => e.Property).WithMany().HasForeignKey(e => e.PropertyId);
            entity.HasOne(e => e.Unit).WithMany().HasForeignKey(e => e.UnitId);
            entity.HasOne(e => e.Column).WithMany().HasForeignKey(e => e.TaskColumnId);
            entity.HasOne(e => e.Vendor).WithMany().HasForeignKey(e => e.VendorId);
            entity.HasOne(e => e.ReportedByTenant).WithMany()
                .HasForeignKey(e => e.ReportedByTenantId);

            entity.HasIndex(e => new { e.OrganizationId, e.TaskColumnId, e.Position });
        });

        modelBuilder.Entity<TaskAssignee>(entity =>
        {
            entity.ToTable("task_assignees");

            // Keyless, and that is the fix rather than an omission.
            //
            // The table has no primary key. Its identity is really
            // (task, user, vendor), and that was declared as the key here --
            // which is a trap, because EF marks every key property as
            // REQUIRED. UserId and VendorId stopped being nullable in the
            // model even though exactly one of them is null in every row,
            // which is what the table's own exactly-one-target CHECK
            // constraint enforces.
            //
            // Both navigations therefore became mandatory relationships, and
            // reading the assignees emitted INNER JOINs to users AND vendors
            // at once. No row can satisfy both, because a row names a member
            // or a vendor and never both, so the query returned nothing for
            // every task, always. The board read "Unassigned" everywhere
            // while the activity trail beneath it correctly named the
            // assignee -- app.assign_task writes both in one transaction, so
            // only the read was ever wrong.
            //
            // Nothing writes this table through EF, so there is nothing to
            // track and no key to need. The one constraint keyless imposes is
            // that it cannot be a correlated collection subquery inside
            // another projection; the board therefore loads assignees as one
            // separate query and stitches them in memory.
            entity.HasNoKey();

            entity.Property(e => e.TaskId).HasColumnName("task_id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.VendorId).HasColumnName("vendor_id");
            entity.Property(e => e.AssignedAt).HasColumnName("assigned_at");
            entity.Property(e => e.AssignedByUserId).HasColumnName("assigned_by_user_id");
            entity.Property(e => e.IsLead).HasColumnName("is_lead");

            // Left unmapped on purpose. With a proper key these could now be
            // optional relationships and would left-join correctly, but the
            // reads resolve a name by explicit subquery instead, so a mapped
            // navigation would only be a way for the old failure to come
            // back the next time somebody writes an Include.
            entity.Ignore(e => e.User);
            entity.Ignore(e => e.Vendor);
        });

        modelBuilder.Entity<TaskComment>(entity =>
        {
            entity.ToTable("task_comments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.TaskId).HasColumnName("task_id");
            entity.Property(e => e.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(e => e.AuthorVendorId).HasColumnName("author_vendor_id");
            entity.Property(e => e.AuthorDisplayName).HasColumnName("author_display_name");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.IsInternal).HasColumnName("is_internal");
            entity.Property(e => e.EditedAt).HasColumnName("edited_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(e => new { e.TaskId, e.CreatedAt });
        });

        modelBuilder.Entity<TaskActivityEntry>(entity =>
        {
            entity.ToTable("task_activity");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.TaskId).HasColumnName("task_id");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.ActorDisplayName).HasColumnName("actor_display_name");
            entity.Property(e => e.ActorKind).HasColumnName("actor_kind");
            entity.Property(e => e.ActivityType).HasColumnName("activity_type");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(e => new { e.TaskId, e.CreatedAt });

            // Append-only. The API role holds no INSERT, UPDATE, or DELETE
            // privilege on this table (0017), because an entry must always
            // accompany the change it describes.
        });

        modelBuilder.Entity<TaskNotificationRule>(entity =>
        {
            entity.ToTable("task_notification_rules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PropertyId).HasColumnName("property_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TriggerKind).HasColumnName("trigger_kind");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.ScheduleBasis).HasColumnName("schedule_basis");
            entity.Property(e => e.ScheduleOffsetDays).HasColumnName("schedule_offset_days");
            entity.Property(e => e.RepeatDaily).HasColumnName("repeat_daily");
            entity.Property(e => e.MaxRepeats).HasColumnName("max_repeats");
            entity.Property(e => e.OnlyPriorities).HasColumnName("only_priorities");
            entity.Property(e => e.OnlyCategories).HasColumnName("only_categories");
            entity.Property(e => e.OnlyStatusCategories)
                .HasColumnName("only_status_categories");
            entity.Property(e => e.Channels).HasColumnName("channels");
            entity.Property(e => e.EmailTemplateId).HasColumnName("email_template_id");
            entity.Property(e => e.SendLocalTime).HasColumnName("send_local_time");
            entity.Property(e => e.TimeZone).HasColumnName("timezone");
            entity.Property(e => e.Enabled).HasColumnName("enabled");
            entity.Property(e => e.MaxSendsPerTaskPerDay)
                .HasColumnName("max_sends_per_task_per_day");
            entity.Property(e => e.PausedAt).HasColumnName("paused_at");
            entity.Property(e => e.PausedReason).HasColumnName("paused_reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.ToTable("notification_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.OutboxId).HasColumnName("outbox_id");
            entity.Property(e => e.ScheduledNotificationId)
                .HasColumnName("scheduled_notification_id");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.RecipientUserId).HasColumnName("recipient_user_id");
            entity.Property(e => e.RecipientEmail)
                .HasColumnName("recipient_email").HasColumnType("citext");
            entity.Property(e => e.RecipientRole).HasColumnName("recipient_role");
            entity.Property(e => e.TaskNotificationRuleId)
                .HasColumnName("task_notification_rule_id");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.LinkPath).HasColumnName("link_path");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ErrorCategory).HasColumnName("error_category");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.ProviderMessageId).HasColumnName("provider_message_id");
            entity.Property(e => e.ProviderAcceptedAt).HasColumnName("provider_accepted_at");
            entity.Property(e => e.ReadAt).HasColumnName("read_at");
            entity.Property(e => e.DismissedAt).HasColumnName("dismissed_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.Ignore(e => e.IsUnread);

            entity.HasIndex(e => new { e.RecipientUserId, e.CreatedAt });
        });
    }
}
