using LeaseReminder.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// EF Core model for the phase 1 entities.
///
/// <para>
/// Two things this context deliberately does NOT do:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>It does not own the schema.</b> There are no EF migrations. The
///     schema is the reviewed SQL in db/migrations, because the security
///     properties that matter here, the RLS policies, the column-level
///     privileges, the CHECK constraints encoding the approval gate, cannot be
///     expressed in the EF model and must not be at risk of being
///     round-tripped away by a scaffolded migration.
///     <see cref="Database"/>.EnsureCreated is never called.
///   </item>
///   <item>
///     <b>It does not filter by organization.</b> It would be easy to add a
///     global query filter on OrganizationId and feel safer. That would be a
///     second, weaker copy of the isolation rule living in application code,
///     and the two would eventually disagree. Isolation is the database's job
///     via RLS, which holds for every query including ones written later, and
///     is tested directly in db/tests/rls_isolation_test.sql.
///   </item>
/// </list>
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<RentalProperty> Properties => Set<RentalProperty>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Lease> Leases => Set<Lease>();

    public DbSet<LeaseTenant> LeaseTenants => Set<LeaseTenant>();

    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    // --- Maintenance module -------------------------------------------------

    public DbSet<TaskColumn> TaskColumns => Set<TaskColumn>();

    public DbSet<Vendor> Vendors => Set<Vendor>();

    public DbSet<MaintenanceTask> Tasks => Set<MaintenanceTask>();

    public DbSet<TaskAssignee> TaskAssignees => Set<TaskAssignee>();

    public DbSet<TaskComment> TaskComments => Set<TaskComment>();

    public DbSet<TaskActivityEntry> TaskActivity => Set<TaskActivityEntry>();

    public DbSet<TaskNotificationRule> TaskNotificationRules => Set<TaskNotificationRule>();

    // Rent reminder addressing: who a reminder goes to, and when.
    public DbSet<Recipient> Recipients => Set<Recipient>();

    public DbSet<RecipientGroup> RecipientGroups => Set<RecipientGroup>();

    public DbSet<RecipientGroupMember> RecipientGroupMembers => Set<RecipientGroupMember>();

    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();

    public DbSet<NotificationRuleRecipient> NotificationRuleRecipients =>
        Set<NotificationRuleRecipient>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    // --- Documents and extraction (phase 2) ---------------------------------

    public DbSet<LeaseDocument> LeaseDocuments => Set<LeaseDocument>();

    public DbSet<LeaseExtraction> LeaseExtractions => Set<LeaseExtraction>();

    public DbSet<LeaseExtractedField> LeaseExtractedFields => Set<LeaseExtractedField>();

    public DbSet<LeaseExtractionIssue> LeaseExtractionIssues => Set<LeaseExtractionIssue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AuthUserId).HasColumnName("auth_user_id");
            entity.Property(e => e.Email).HasColumnName("email").HasColumnType("citext");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(e => e.AuthUserId).IsUnique();
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TimeZone).HasColumnName("timezone");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrganizationMember>(entity =>
        {
            entity.ToTable("organization_members");
            entity.HasKey(e => new { e.OrganizationId, e.UserId });
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Members)
                .HasForeignKey(e => e.OrganizationId);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<RentalProperty>(entity =>
        {
            entity.ToTable("properties");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PropertyType).HasColumnName("property_type");
            entity.Property(e => e.AddressLine1).HasColumnName("address_line1");
            entity.Property(e => e.AddressLine2).HasColumnName("address_line2");
            entity.Property(e => e.Locality).HasColumnName("locality");
            entity.Property(e => e.Region).HasColumnName("region");
            entity.Property(e => e.PostalCode).HasColumnName("postal_code");
            entity.Property(e => e.CountryCode).HasColumnName("country_code").HasMaxLength(2);
            entity.Property(e => e.TimeZone).HasColumnName("timezone");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
            entity.Property(e => e.ArchivedByUserId).HasColumnName("archived_by_user_id");
            entity.HasIndex(e => new { e.OrganizationId, e.Name });
        });

        modelBuilder.Entity<Unit>(entity =>
        {
            entity.ToTable("units");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PropertyId).HasColumnName("property_id");
            entity.Property(e => e.UnitNumber).HasColumnName("unit_number");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Bedrooms).HasColumnName("bedrooms");
            entity.Property(e => e.Bathrooms).HasColumnName("bathrooms").HasPrecision(3, 1);
            entity.Property(e => e.SquareFeet).HasColumnName("square_feet");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            // The real constraint in the database is composite, on
            // (property_id, organization_id), which is what prevents a unit
            // being attached to another organization's property. EF only needs
            // enough of it to navigate.
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Units)
                .HasForeignKey(e => e.PropertyId);

            entity.HasIndex(e => new { e.PropertyId, e.UnitNumber }).IsUnique();
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.LegalName).HasColumnName("legal_name");
            entity.Property(e => e.PreferredName).HasColumnName("preferred_name");
            entity.Property(e => e.PrimaryEmail).HasColumnName("primary_email")
                .HasColumnType("citext");
            entity.Property(e => e.SecondaryEmail).HasColumnName("secondary_email")
                .HasColumnType("citext");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.NotificationsOptedIn).HasColumnName("notifications_opted_in");
            entity.Property(e => e.NotificationsOptInAt)
                .HasColumnName("notifications_opt_in_at");
            entity.Property(e => e.NotificationsOptOutAt)
                .HasColumnName("notifications_opt_out_at");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(e => new { e.OrganizationId, e.LegalName });
        });

        modelBuilder.Entity<Lease>(entity =>
        {
            entity.ToTable("leases");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PropertyId).HasColumnName("property_id");
            entity.Property(e => e.UnitId).HasColumnName("unit_id");
            entity.Property(e => e.Reference).HasColumnName("reference");

            // The PostgreSQL enum store types are registered once, for every
            // context, by EfEnumMappings.MapApplicationEnums. See that file for
            // why the ADO level mapping alone is not enough.
            entity.Property(e => e.LifecycleStatus).HasColumnName("lifecycle_status");
            entity.Property(e => e.ApprovalStatus).HasColumnName("approval_status");
            entity.Property(e => e.AutomationStatus).HasColumnName("automation_status");

            entity.Property(e => e.StartDate).HasColumnName("start_date");
            entity.Property(e => e.EndDate).HasColumnName("end_date");

            entity.Property(e => e.RentAmount).HasColumnName("rent_amount").HasPrecision(14, 2);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.SecurityDepositAmount)
                .HasColumnName("security_deposit_amount").HasPrecision(14, 2);
            entity.Property(e => e.FirstPaymentDate).HasColumnName("first_payment_date");

            entity.Property(e => e.GracePeriodDays).HasColumnName("grace_period_days");
            entity.Property(e => e.LateFeeAmount)
                .HasColumnName("late_fee_amount").HasPrecision(14, 2);
            entity.Property(e => e.LateFeeIsPercentage).HasColumnName("late_fee_is_percentage");
            entity.Property(e => e.LateFeeAfterDays).HasColumnName("late_fee_after_days");

            entity.Property(e => e.RenewalNoticeDays).HasColumnName("renewal_notice_days");
            entity.Property(e => e.RenewalTerms).HasColumnName("renewal_terms");
            entity.Property(e => e.EarlyTerminationTerms).HasColumnName("early_termination_terms");
            entity.Property(e => e.NoticePeriodDays).HasColumnName("notice_period_days");
            entity.Property(e => e.PaymentInstructions).HasColumnName("payment_instructions");
            entity.Property(e => e.SpecialClauses).HasColumnName("special_clauses");
            entity.Property(e => e.Notes).HasColumnName("notes");

            entity.Property(e => e.SourceDocumentId).HasColumnName("source_document_id");
            entity.Property(e => e.ApprovedByUserId).HasColumnName("approved_by_user_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
            entity.Property(e => e.RejectedByUserId).HasColumnName("rejected_by_user_id");
            entity.Property(e => e.RejectedAt).HasColumnName("rejected_at");
            entity.Property(e => e.RejectionReason).HasColumnName("rejection_reason");
            entity.Property(e => e.AutomationPausedAt).HasColumnName("automation_paused_at");
            entity.Property(e => e.AutomationPausedReason)
                .HasColumnName("automation_paused_reason");

            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");

            // Computed in C# from the three status columns; there is no such
            // column, and EF must not try to read or write one.
            entity.Ignore(e => e.CanSendReminders);

            entity.HasOne(e => e.Property).WithMany().HasForeignKey(e => e.PropertyId);
            entity.HasOne(e => e.Unit).WithMany().HasForeignKey(e => e.UnitId);

            entity.HasIndex(e => new { e.OrganizationId, e.ApprovalStatus });
            entity.HasIndex(e => new { e.OrganizationId, e.LifecycleStatus });
        });

        modelBuilder.Entity<LeaseTenant>(entity =>
        {
            entity.ToTable("lease_tenants");
            entity.HasKey(e => new { e.LeaseId, e.TenantId });
            entity.Property(e => e.LeaseId).HasColumnName("lease_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasOne(e => e.Lease)
                .WithMany(l => l.LeaseTenants)
                .HasForeignKey(e => e.LeaseId);

            entity.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.ActorKind).HasColumnName("actor_kind");
            entity.Property(e => e.Action).HasColumnName("action");
            entity.Property(e => e.EntityType).HasColumnName("entity_type");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.SafeMetadataJson)
                .HasColumnName("safe_metadata_json").HasColumnType("jsonb");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            // Append-only. The trigger and the missing privilege both enforce
            // this; the comment is here so nobody wonders why there is no
            // update path in the repository layer.
        });

        // The maintenance module, in its own file: another two hundred lines
        // inline would bury the lease model it sits beside.
        modelBuilder.ConfigureMaintenance();
        modelBuilder.ConfigureDocuments();
        modelBuilder.ConfigureNotifications();
    }
}
