using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LeaseReminder.IntegrationTests;

/// <summary>
/// A live PostgreSQL database for the integration tests, plus the two
/// connections they need.
///
/// <para>
/// The distinction between the two connections is the entire point of these
/// tests. Seeding uses an OWNER connection, because RLS does not apply to a
/// table's owner and fixtures have to be able to create data in two different
/// organizations. Assertions use an APPLICATION connection as app_api, which
/// is a non-owner role with no BYPASSRLS, so every statement it issues is
/// filtered by the policies in db/migrations/0009.
/// </para>
///
/// <para>
/// Running these against the real database rather than an in-memory provider
/// is not optional. RLS, the composite foreign keys, the CHECK constraints
/// encoding the approval gate, and the column-level privileges on the token
/// columns are the things under test, and none of them exist in a fake.
/// </para>
///
/// <para>
/// The tests skip rather than fail when no database is reachable, so a
/// checkout with no Docker running still gets a green unit test suite. CI runs
/// a PostgreSQL service and must not skip; see .github/workflows/ci.yml.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultOwnerConnection =
        "Host=localhost;Port=54329;Database=leasereminder;Username=postgres;" +
        "Password=localdev;SSL Mode=Disable";

    private const string DefaultAppConnection =
        "Host=localhost;Port=54329;Database=leasereminder;Username=app_api;" +
        "Password=localdev;SSL Mode=Disable";

    private NpgsqlDataSource? _ownerDataSource;
    private NpgsqlDataSource? _appDataSource;

    /// <summary>
    /// False when no database is reachable. Tests assert on this and skip.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var ownerConnectionString =
            Environment.GetEnvironmentVariable("TEST_OWNER_DATABASE_URL")
            ?? DefaultOwnerConnection;

        var appConnectionString =
            Environment.GetEnvironmentVariable("TEST_APP_DATABASE_URL")
            ?? DefaultAppConnection;

        try
        {
            _ownerDataSource = new NpgsqlDataSourceBuilder(ownerConnectionString)
                .Build();

            // requireTls: false, because the local development database has no
            // certificate. Production connections go through the same factory
            // with the check on.
            _appDataSource = NpgsqlDataSourceFactory.Create(
                appConnectionString, requireTls: false);

            await using var probe = await _ownerDataSource.OpenConnectionAsync()
                .ConfigureAwait(false);

            await using var command = probe.CreateCommand();

            // Confirms the migrations have actually run, not merely that
            // something is listening on the port.
            command.CommandText = "SELECT count(*) FROM pg_policy";

            var policyCount = Convert.ToInt32(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            if (policyCount == 0)
            {
                UnavailableReason =
                    "The database has no RLS policies, so migrations have not been applied. " +
                    "Run ./db/migrate.sh local";
                return;
            }

            IsAvailable = true;
        }
        catch (Exception exception)
        {
            UnavailableReason =
                $"No test database reachable ({exception.GetType().Name}). " +
                "Start one with: docker compose up -d postgres && ./db/migrate.sh local";
        }
    }

    public async Task DisposeAsync()
    {
        if (_ownerDataSource is not null)
        {
            await _ownerDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_appDataSource is not null)
        {
            await _appDataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An owner connection, for fixture setup only. RLS does not apply to it,
    /// which is exactly why no assertion may use it.
    /// </summary>
    public NpgsqlConnection OpenOwnerConnection() =>
        _ownerDataSource!.OpenConnection();

    /// <summary>
    /// A DbContext as the application sees it: the app_api role, the RLS
    /// interceptor, and a fixed identity standing in for a verified JWT
    /// subject.
    /// </summary>
    public AppDbContext CreateContextAs(Guid? authUserId)
    {
        var identity = new StaticRequestIdentity(authUserId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_appDataSource!, npgsql => npgsql.UseNodaTime().MapApplicationEnums())
            .AddInterceptors(new RlsIdentityInterceptor(
                identity,
                NullLogger<RlsIdentityInterceptor>.Instance))
            .Options;

        return new AppDbContext(options);
    }
}

[CollectionDefinition(DatabaseCollection.Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
