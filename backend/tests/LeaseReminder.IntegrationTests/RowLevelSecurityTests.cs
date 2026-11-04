using LeaseReminder.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Xunit;

namespace LeaseReminder.IntegrationTests;

/// <summary>
/// The security acceptance tests from section 4, exercised through the real
/// application stack: EF Core, the RLS identity interceptor, the app_api role,
/// and the policies in db/migrations/0009.
///
/// <para>
/// db/tests/rls_isolation_test.sql already asserts the same properties in raw
/// SQL. These tests exist because that is not quite the same question. The SQL
/// suite proves the POLICIES are right. These prove the APPLICATION actually
/// gets the benefit of them, which depends on the interceptor running, the
/// identity being set from the right place, and the connection pool not
/// leaking identity between contexts. Those are C# concerns and no amount of
/// SQL will catch a regression in them.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RowLevelSecurityTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Guid _authA;
    private Guid _authB;
    private Guid _userA;
    private Guid _userB;
    private Guid _orgA;
    private Guid _orgB;
    private Guid _propertyA;
    private Guid _propertyB;
    private Guid _leaseA;
    private Guid _leaseB;

    /// <summary>
    /// Seeds two complete, independent organizations through an OWNER
    /// connection. Using the owner here is deliberate: the fixtures must exist
    /// regardless of policy, so that the assertions are testing the policies
    /// rather than testing whether setup succeeded.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        _authA = Guid.CreateVersion7();
        _authB = Guid.CreateVersion7();

        await using var connection = fixture.OpenOwnerConnection();

        _userA = await ScalarAsync<Guid>(connection,
            "INSERT INTO users (auth_user_id, email) VALUES ($1, $2) RETURNING id",
            _authA, $"a-{_authA:N}@example.test").ConfigureAwait(false);

        _userB = await ScalarAsync<Guid>(connection,
            "INSERT INTO users (auth_user_id, email) VALUES ($1, $2) RETURNING id",
            _authB, $"b-{_authB:N}@example.test").ConfigureAwait(false);

        _orgA = await ScalarAsync<Guid>(connection,
            "INSERT INTO organizations (name) VALUES ($1) RETURNING id",
            $"Org A {_authA:N}").ConfigureAwait(false);

        _orgB = await ScalarAsync<Guid>(connection,
            "INSERT INTO organizations (name) VALUES ($1) RETURNING id",
            $"Org B {_authB:N}").ConfigureAwait(false);

        await ExecuteAsync(connection,
            "INSERT INTO organization_members (organization_id, user_id, role) " +
            "VALUES ($1, $2, 'owner')",
            _orgA, _userA).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "INSERT INTO organization_members (organization_id, user_id, role) " +
            "VALUES ($1, $2, 'owner')",
            _orgB, _userB).ConfigureAwait(false);

        _propertyA = await ScalarAsync<Guid>(connection,
            "INSERT INTO properties (organization_id, name, timezone) " +
            "VALUES ($1, 'A Street', 'America/New_York') RETURNING id",
            _orgA).ConfigureAwait(false);

        _propertyB = await ScalarAsync<Guid>(connection,
            "INSERT INTO properties (organization_id, name, timezone) " +
            "VALUES ($1, 'B Avenue', 'America/Los_Angeles') RETURNING id",
            _orgB).ConfigureAwait(false);

        _leaseA = await ScalarAsync<Guid>(connection,
            """
            INSERT INTO leases
              (organization_id, property_id, reference, lifecycle_status,
               approval_status, automation_status, start_date, end_date,
               rent_amount, currency, approved_by_user_id, approved_at)
            VALUES
              ($1, $2, 'A lease', 'active', 'approved', 'active',
               DATE '2026-01-01', DATE '2026-12-31', 2400.00, 'USD', $3, now())
            RETURNING id
            """,
            _orgA, _propertyA, _userA).ConfigureAwait(false);

        _leaseB = await ScalarAsync<Guid>(connection,
            """
            INSERT INTO leases
              (organization_id, property_id, reference, lifecycle_status,
               approval_status, automation_status, start_date, end_date,
               rent_amount, currency, approved_by_user_id, approved_at)
            VALUES
              ($1, $2, 'B lease', 'active', 'approved', 'active',
               DATE '2026-01-01', DATE '2026-12-31', 3100.00, 'USD', $3, now())
            RETURNING id
            """,
            _orgB, _propertyB, _userB).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the fixtures. Organizations cascade to properties and leases,
    /// so two deletes are enough.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var connection = fixture.OpenOwnerConnection();

        await ExecuteAsync(connection,
            "DELETE FROM organizations WHERE id = ANY($1)",
            new[] { _orgA, _orgB }).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "DELETE FROM users WHERE id = ANY($1)",
            new[] { _userA, _userB }).ConfigureAwait(false);
    }

    // =========================================================================
    // Acceptance test 1: User A cannot read User B's lease through the API
    // =========================================================================

    [SkippableFact]
    public async Task User_A_sees_only_their_own_leases()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        var leases = await context.Leases.AsNoTracking()
            .Select(lease => lease.Id)
            .ToListAsync();

        Assert.Contains(_leaseA, leases);
        Assert.DoesNotContain(_leaseB, leases);
    }

    [SkippableFact]
    public async Task Asking_for_another_organization_lease_by_id_returns_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        // The id is correct and the query is well formed. It still returns
        // nothing, because the row is not visible to this identity. This is
        // the shape a tampered request actually takes.
        var lease = await context.Leases.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == _leaseB);

        Assert.Null(lease);
    }

    [SkippableFact]
    public async Task Every_portfolio_table_is_filtered_not_just_leases()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        Assert.DoesNotContain(
            _propertyB,
            await context.Properties.AsNoTracking().Select(p => p.Id).ToListAsync());

        Assert.DoesNotContain(
            _orgB,
            await context.Organizations.AsNoTracking().Select(o => o.Id).ToListAsync());

        Assert.DoesNotContain(
            _orgB,
            await context.OrganizationMembers.AsNoTracking()
                .Select(m => m.OrganizationId).ToListAsync());
    }

    // =========================================================================
    // Acceptance test 3: a modified request cannot change another user's data
    // =========================================================================

    [SkippableFact]
    public async Task User_B_cannot_update_User_A_property_even_with_the_correct_id()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authB);

        // ExecuteUpdate rather than the change tracker, because this is the
        // shape of the attack: a single statement naming a row by id, with no
        // prior read to be filtered.
        var affected = await context.Properties
            .Where(property => property.Id == _propertyA)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Name, "Hijacked"));

        Assert.Equal(0, affected);

        // And confirm through an independent connection that nothing changed.
        await using var asOwner = fixture.OpenOwnerConnection();

        var name = await ScalarAsync<string>(asOwner,
            "SELECT name FROM properties WHERE id = $1", _propertyA);

        Assert.Equal("A Street", name);
    }

    [SkippableFact]
    public async Task User_B_cannot_delete_User_A_property()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authB);

        var affected = await context.Properties
            .Where(property => property.Id == _propertyA)
            .ExecuteDeleteAsync();

        Assert.Equal(0, affected);
    }

    [SkippableFact]
    public async Task User_B_cannot_insert_into_User_A_organization()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authB);

        context.Properties.Add(new RentalProperty
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = _orgA,
            Name = "Smuggled",
            TimeZone = "UTC",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        // The INSERT policy's WITH CHECK rejects the row, which surfaces as a
        // PostgreSQL insufficient_privilege error rather than a silent no-op.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, postgres.SqlState);
    }

    [SkippableFact]
    public async Task A_member_cannot_move_their_own_row_into_another_organization()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        // The subtle one. A owns this property, so the USING half of the
        // UPDATE policy permits touching it. Only the WITH CHECK half stops
        // the row being rewritten into B's organization. A policy written with
        // USING alone would let this through.
        var property = await context.Properties
            .FirstAsync(candidate => candidate.Id == _propertyA);

        property.OrganizationId = _orgB;

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        Assert.IsType<PostgresException>(exception.InnerException);
    }

    // =========================================================================
    // Acceptance test 5: the API cannot reach OAuth token material
    // =========================================================================

    [SkippableFact]
    public async Task The_application_role_cannot_read_the_Gmail_token_columns()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        // The column-level revoke in db/migrations/0010 means PostgreSQL
        // refuses the statement outright. This is a privilege boundary, not an
        // application convention, so it holds even for a hand-written query.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "SELECT encrypted_refresh_token FROM email_accounts LIMIT 1"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [SkippableFact]
    public async Task The_application_role_cannot_call_the_worker_token_function()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "SELECT * FROM app.read_email_account_token(gen_random_uuid())"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    // =========================================================================
    // The interceptor itself
    //
    // These are the tests that only make sense at this layer. If the
    // interceptor stopped running, or the connection pool leaked identity
    // between contexts, every test above would still pass for the wrong
    // reason.
    // =========================================================================

    [SkippableFact]
    public async Task A_context_with_no_identity_reads_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        // The state an unauthenticated request produces, or one whose
        // authorization check was somehow skipped. It must read nothing rather
        // than everything.
        await using var context = fixture.CreateContextAs(null);

        Assert.Empty(await context.Leases.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Properties.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Tenants.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Organizations.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task An_unknown_subject_reads_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        // A well-formed token for a user who has no row here. Valid signature,
        // no access.
        await using var context = fixture.CreateContextAs(Guid.CreateVersion7());

        Assert.Empty(await context.Leases.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Identity_does_not_leak_between_contexts_sharing_the_pool()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        // The failure this guards against is specific and would be invisible:
        // identity is set at session scope, so if Npgsql stopped resetting
        // session state on pool return, a connection released by A and reused
        // by B would carry A's identity. B would then read A's data, with no
        // error anywhere.
        //
        // Interleaving the contexts deliberately encourages connection reuse.
        for (var round = 0; round < 3; round++)
        {
            await using (var asA = fixture.CreateContextAs(_authA))
            {
                var seenByA = await asA.Leases.AsNoTracking()
                    .Select(lease => lease.Id).ToListAsync();

                Assert.Contains(_leaseA, seenByA);
                Assert.DoesNotContain(_leaseB, seenByA);
            }

            await using (var asB = fixture.CreateContextAs(_authB))
            {
                var seenByB = await asB.Leases.AsNoTracking()
                    .Select(lease => lease.Id).ToListAsync();

                Assert.Contains(_leaseB, seenByB);
                Assert.DoesNotContain(_leaseA, seenByB);
            }

            await using (var anonymous = fixture.CreateContextAs(null))
            {
                Assert.Empty(await anonymous.Leases.AsNoTracking().ToListAsync());
            }
        }
    }

    // =========================================================================
    // The approval gate (section 13.C), enforced by the database
    // =========================================================================

    [SkippableFact]
    public async Task An_unapproved_lease_cannot_be_made_active()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        context.Leases.Add(new Lease
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = _orgA,
            PropertyId = _propertyA,
            LifecycleStatus = LeaseLifecycleStatus.Active,
            ApprovalStatus = LeaseApprovalStatus.PendingReview,
            StartDate = new LocalDate(2026, 1, 1),
            RentAmount = 1000m,
            Currency = "USD",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        // "AI extraction alone must never activate a lease" expressed as a
        // constraint: the row is simply not representable.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Contains("live_requires_approval", postgres.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_approved_lease_cannot_claim_approval_without_an_approver()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        context.Leases.Add(new Lease
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = _orgA,
            PropertyId = _propertyA,
            LifecycleStatus = LeaseLifecycleStatus.Active,
            ApprovalStatus = LeaseApprovalStatus.Approved,
            ApprovedAt = SystemClock.Instance.GetCurrentInstant(),
            ApprovedByUserId = null,   // nobody actually approved it
            StartDate = new LocalDate(2026, 1, 1),
            RentAmount = 1000m,
            Currency = "USD",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [SkippableFact]
    public async Task Pausing_automation_leaves_approval_and_lifecycle_untouched()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        var lease = await context.Leases.FirstAsync(candidate => candidate.Id == _leaseA);

        lease.AutomationStatus = NotificationAutomationStatus.Paused;
        lease.AutomationPausedAt = SystemClock.Instance.GetCurrentInstant();
        lease.AutomationPausedReason = "Tenant is disputing the amount.";

        await context.SaveChangesAsync();

        // Three independent state machines, and this is what independence
        // means in practice (section 13.E).
        await using var reread = fixture.CreateContextAs(_authA);

        var updated = await reread.Leases.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == _leaseA);

        Assert.Equal(NotificationAutomationStatus.Paused, updated.AutomationStatus);
        Assert.Equal(LeaseApprovalStatus.Approved, updated.ApprovalStatus);
        Assert.Equal(LeaseLifecycleStatus.Active, updated.LifecycleStatus);

        // And the lease is no longer sendable, without any lease status
        // having changed.
        Assert.False(updated.CanSendReminders);
    }

    // =========================================================================
    // Append-only logs
    // =========================================================================

    [SkippableFact]
    public async Task An_audit_entry_cannot_be_attributed_to_another_user()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        context.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = _orgA,
            ActorUserId = _userB,   // somebody else
            ActorKind = "user",
            Action = "lease.approved",
            EntityType = "lease",
            EntityId = _leaseA,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        // Writing history in another person's name is refused by the INSERT
        // policy, which is what makes the audit trail worth anything.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        Assert.Equal(
            PostgresErrorCodes.InsufficientPrivilege,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [SkippableFact]
    public async Task An_audit_entry_cannot_be_updated_or_deleted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.UnavailableReason);

        await using var context = fixture.CreateContextAs(_authA);

        context.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = _orgA,
            ActorUserId = _userA,
            ActorKind = "user",
            Action = "lease.approved",
            EntityType = "lease",
            EntityId = _leaseA,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        await context.SaveChangesAsync();

        var updateFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "UPDATE audit_logs SET action = 'tampered' WHERE entity_id = {0}",
                _leaseA));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateFailure.SqlState);

        var deleteFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "DELETE FROM audit_logs WHERE entity_id = {0}", _leaseA));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteFailure.SqlState);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return (T)result!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
