using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LeaseReminder.Application.Documents;
using LeaseReminder.Infrastructure.Documents;
using LeaseReminder.Infrastructure.Email;
using LeaseReminder.Infrastructure.Security;
using Microsoft.Extensions.Options;
using LeaseReminder.Infrastructure.Persistence;
using LeaseReminder.Infrastructure.Storage;
using LeaseReminder.Worker;
using Serilog;
using Serilog.Events;

// =============================================================================
// LeaseReminder background worker
//
// Deployed as a Render Background Worker (section 8). It is a separate process
// from the API for two reasons that both matter:
//
//   * Permissions. It connects as app_worker, which has NO table privileges at
//     all and can only EXECUTE the reviewed functions in db/migrations/0007.
//     Even if this process were fully compromised, it could not read the
//     portfolio or rewrite history. Section 4.H asks for worker permissions
//     narrower than API permissions; this is what that looks like in practice.
//
//   * Failure isolation. Document parsing and provider calls are the two
//     places this system touches untrusted input and unreliable networks.
//     Keeping them out of the request path means a wedged parse or a slow
//     Gmail response cannot make the UI unresponsive.
//
// It exposes no HTTP endpoint: section 4.H is explicit that worker management
// endpoints must not be public, and the simplest way to satisfy that is to
// have none.
// =============================================================================

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("service", "leasereminder-worker")
        // Npgsql logs full command text at Information. For this worker that
        // would put rendered subject lines and recipient addresses in the log.
        .MinimumLevel.Override("Npgsql", LogEventLevel.Warning)
        .WriteTo.Console(
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} " +
                "{Properties:j}{NewLine}{Exception}"));

    builder.Services.AddOptions<WorkerOptions>()
        .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddSingleton(serviceProvider =>
    {
        var connectionString = builder.Configuration["Database:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Database:ConnectionString is not configured. The worker connects as " +
                "app_worker; see .env.example for WORKER_DATABASE_URL.");
        }

        var allowInsecure = builder.Configuration
            .GetValue("Database:AllowInsecureTransport", defaultValue: false);

        var isDevelopment = builder.Environment.IsDevelopment();

        if (allowInsecure && !isDevelopment)
        {
            throw new InvalidOperationException(
                "Database:AllowInsecureTransport is set outside Development.");
        }

        // The same factory the API uses, so the worker inherits the same
        // refusals: no owner role, no superuser, TLS required, connection
        // reset left enabled.
        return NpgsqlDataSourceFactory.Create(
            connectionString, requireTls: !(allowInsecure && isDevelopment));
    });

    builder.Services.AddSingleton<WorkerDatabase>();

    // --- Gmail sending (phase 4) -----------------------------------------
    //
    // The worker is the only process that can read a Gmail credential:
    // app.read_email_account_token is granted to app_worker alone, and
    // migration 0010 puts the token columns out of the API role's reach with
    // column-level grants.
    builder.Services
        .Configure<GoogleOAuthOptions>(
            builder.Configuration.GetSection(GoogleOAuthOptions.SectionName))
        .Configure<TokenEncryptionOptions>(
            builder.Configuration.GetSection(TokenEncryptionOptions.SectionName));

    builder.Services.AddSingleton(serviceProvider =>
    {
        var options = serviceProvider
            .GetRequiredService<IOptions<TokenEncryptionOptions>>().Value;

        ITokenProtector? protector = string.IsNullOrWhiteSpace(options.Keys)
            ? null
            : AesGcmTokenProtector.FromConfiguration(options.Keys, options.CurrentKeyVersion);

        return new TokenProtectorAccessor(protector);
    });

    builder.Services.AddHttpClient<GoogleOAuthClient>();

    // Registered as the sender whether or not it is configured. It reports
    // the specific reason it cannot send -- no Google client, no encryption
    // key, no connected mailbox -- which is far more useful than a generic
    // "not configured", and it never marks something sent that was not.
    builder.Services.AddHttpClient<IEmailSender, GmailEmailSender>();

    builder.Services.AddHostedService<NotificationDispatchService>();
    builder.Services.AddHostedService<QueueHealthService>();

    // The maintenance notification engine. Three services rather than one,
    // because composing a notification, raising a deadline event, and sending
    // an email have different failure modes and different retry schedules.
    builder.Services.AddHostedService<OutboxProcessingService>();
    builder.Services.AddHostedService<TaskScheduleSweepService>();
    builder.Services.AddHostedService<NotificationEmailService>();

    // --- Document intelligence (phase 2) ---------------------------------
    //
    // The parsers and the extraction call live in the worker rather than the
    // API on purpose. Both read untrusted input, and a malformed file that
    // hangs or exhausts memory should cost one background job, not a request
    // thread shared with every other caller.
    builder.Services
        .Configure<SupabaseStorageOptions>(
            builder.Configuration.GetSection(SupabaseStorageOptions.SectionName))
        .Configure<GeminiOptions>(
            builder.Configuration.GetSection(GeminiOptions.SectionName));

    builder.Services.AddSingleton<DocumentTextExtractor>();

    // Typed clients, so each gets its own handler lifetime and connection
    // pool rather than sharing one static HttpClient with different base
    // addresses and different credentials.
    builder.Services.AddHttpClient<IDocumentStorage, SupabaseDocumentStorage>();
    builder.Services.AddHttpClient<ILeaseExtractor, GeminiLeaseExtractor>();

    builder.Services.AddHostedService<DocumentExtractionService>();

    // --- Rent schedules (phase 3) ----------------------------------------
    //
    // Expands approved leases into dated occurrences. The arithmetic lives in
    // RentScheduleCalculator and is unit-tested; this service is the part that
    // claims work and writes the result.
    builder.Services.AddHostedService<RentScheduleGenerationService>();

    // Graceful shutdown (section 4.H). Configured BEFORE Build, because
    // service registration after the container is built has no effect.
    //
    // The window has to exceed one provider call, or a redeploy cuts a send
    // mid-flight. That is not a correctness problem, since the claim lock
    // expires and the idempotency key prevents a duplicate, but it does mean
    // an avoidable delay and a confusing log entry.
    builder.Services.Configure<HostOptions>(hostOptions =>
    {
        hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(30);

        // One background service throwing must not take the whole worker down
        // and leave the queue unattended. The dispatch loop already catches
        // per-batch failures; this covers anything that escapes.
        hostOptions.BackgroundServiceExceptionBehavior =
            BackgroundServiceExceptionBehavior.Ignore;
    });

    var host = builder.Build();

    Log.Information(
        "LeaseReminder worker starting in {Environment}",
        builder.Environment.EnvironmentName);

    await host.RunAsync().ConfigureAwait(false);

    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "LeaseReminder worker failed to start");

    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

namespace LeaseReminder.Worker
{
    /// <summary>
    /// Worker tuning. The defaults are deliberately modest: section 2 says not
    /// to add scale machinery before measured need, and a single worker polling
    /// every 30 seconds comfortably handles one landlord's portfolio.
    /// </summary>
    public sealed class WorkerOptions
    {
        public const string SectionName = "Worker";

        /// <summary>
        /// Seconds between polls when the queue is empty. Section 7 suggests
        /// 30 to 60. A due reminder is therefore sent within half a minute of
        /// its scheduled instant, which is well inside what "at 9:00 AM" means
        /// to a reader.
        /// </summary>
        [Range(5, 300)]
        public int PollIntervalSeconds { get; init; } = 30;

        /// <summary>
        /// Notifications claimed per poll. Small enough that a crash loses
        /// little progress, large enough to drain a morning's reminders in a
        /// few passes.
        /// </summary>
        [Range(1, 200)]
        public int DispatchBatchSize { get; init; } = 20;

        /// <summary>
        /// How long a claim is held. Must exceed the slowest realistic provider
        /// call by a wide margin: if it expires mid-send, another worker
        /// reclaims the row, and only the idempotency key then stands between
        /// that and a duplicate email.
        /// </summary>
        [Range(30, 900)]
        public int LockSeconds { get; init; } = 120;

        /// <summary>
        /// Concurrent provider calls (section 4.H). Enough to overlap latency,
        /// not enough to trip a provider rate limit or exhaust the pool.
        /// </summary>
        [Range(1, 32)]
        public int MaxConcurrency { get; init; } = 4;

        /// <summary>
        /// Seconds between queue health reports. Doubles as the liveness
        /// heartbeat, so its absence is the alert.
        /// </summary>
        [Range(30, 3600)]
        public int HealthIntervalSeconds { get; init; } = 300;

        /// <summary>
        /// Seconds between outbox polls. Shorter than the rent dispatcher's,
        /// because a task notification is a reaction to something somebody just
        /// did: "you have been assigned this" arriving a minute later reads as
        /// broken, where a rent reminder is scheduled days ahead.
        /// </summary>
        [Range(2, 120)]
        public int OutboxPollIntervalSeconds { get; init; } = 10;

        /// <summary>Outbox events claimed per poll.</summary>
        [Range(1, 200)]
        public int OutboxBatchSize { get; init; } = 25;

        /// <summary>
        /// Minutes between schedule sweeps. Hourly rather than daily because
        /// "due today" means today in the PROPERTY's zone, and a portfolio can
        /// span many; a single daily run at a fixed UTC hour would fire on the
        /// wrong local day for about half of them. The sweep is date-keyed and
        /// therefore idempotent, so the extra runs cost nothing.
        /// </summary>
        [Range(5, 1440)]
        public int ScheduleSweepIntervalMinutes { get; init; } = 60;

        /// <summary>How many days ahead counts as "due soon".</summary>
        [Range(1, 30)]
        public int DueSoonDays { get; init; } = 1;

        /// <summary>Days without activity before a task is reported stale.</summary>
        [Range(1, 90)]
        public int StaleTaskDays { get; init; } = 5;
    }
}
