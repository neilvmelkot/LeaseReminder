using System.Globalization;
using System.Threading.RateLimiting;
using FluentValidation;
using LeaseReminder.Api.Configuration;
using LeaseReminder.Api.Endpoints;
using LeaseReminder.Application.Documents;
using LeaseReminder.Infrastructure.Documents;
using LeaseReminder.Infrastructure.Email;
using LeaseReminder.Infrastructure.Storage;
using LeaseReminder.Api.Security;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using LeaseReminder.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Serilog;
using Serilog.Events;

// =============================================================================
// LeaseReminder API
//
// Reading order: configuration, then the security pipeline, then endpoints.
// The security decisions are concentrated here on purpose. An authorization
// rule spread across fifty handlers is a rule nobody can verify; a pipeline
// that fails closed, plus Row Level Security underneath, can be read in one
// sitting.
//
// What actually protects the data, in order of how much weight it carries:
//
//   1. PostgreSQL Row Level Security. Every query from this process is
//      filtered by the policies in db/migrations/0009, including queries
//      written in future by someone who has not read this file.
//   2. The database role. app_api is not an owner and has no BYPASSRLS, and
//      cannot read the Gmail token columns at all.
//   3. This pipeline. Authentication, CORS, rate limits, body size caps,
//      security headers.
//
// The ordering matters: the pipeline is the outermost layer and the one most
// likely to have a gap, so it is not relied upon as the only barrier.
// =============================================================================

// Bootstrap logger, so a failure during configuration is still logged
// somewhere rather than vanishing into a non-zero exit code.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Kestrel adds "Server: Kestrel" below the middleware pipeline, so
    // removing the header in middleware does not work; it has to be switched
    // off here. A server banner tells an attacker what to target and tells a
    // legitimate client nothing (section 4.F).
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;

        // Caps on the request itself, below the application's own limits, so
        // an oversized body is rejected before it is buffered.
        kestrel.Limits.MaxRequestBodySize = 52_428_800;
        kestrel.Limits.MaxRequestHeadersTotalSize = 32_768;
        kestrel.Limits.MaxRequestLineSize = 8_192;

        // A slow request holding a connection open is a cheap denial of
        // service; these are the framework defaults made explicit.
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    });

    // -------------------------------------------------------------------------
    // Logging
    //
    // Structured, with a correlation id on every entry (section 4.K). Request
    // bodies are never logged: they contain lease terms and personal data.
    // -------------------------------------------------------------------------
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("service", "leasereminder-api")
        // Npgsql logs full command text at Information, which for this
        // application means lease terms and tenant names in the log stream.
        .MinimumLevel.Override("Npgsql", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command",
            LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} " +
                "{Properties:j}{NewLine}{Exception}"));

    // -------------------------------------------------------------------------
    // Configuration
    //
    // ValidateOnStart turns a misconfiguration into a failed boot. Without it,
    // a missing JWT issuer would surface as the first request being rejected
    // for an unclear reason, or worse, accepted.
    // -------------------------------------------------------------------------
    builder.Services.AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<JwtOptions>()
        .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<CorsOptions>()
        .Bind(builder.Configuration.GetSection(CorsOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(
            options => options.AllowedOrigins.All(origin => origin != "*"),
            "CORS origins must be exact. A wildcard cannot be combined with credentials " +
            "(section 4.F), and the browser would reject it anyway.")
        .Validate(
            options => options.AllowedOrigins.All(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.PathAndQuery == "/"
                && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)),
            "Each CORS origin must be an absolute scheme-host-port with no path, and must " +
            "use HTTPS unless it is a loopback address.")
        .ValidateOnStart();

    builder.Services.AddOptions<SecurityOptions>()
        .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<TokenEncryptionOptions>()
        .Bind(builder.Configuration.GetSection(TokenEncryptionOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    var isDevelopment = builder.Environment.IsDevelopment();

    // -------------------------------------------------------------------------
    // Database
    // -------------------------------------------------------------------------
    builder.Services.AddSingleton(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        // The escape hatch for the local docker-compose database is honoured
        // only in Development, so a production misconfiguration cannot use it.
        var requireTls = !(options.AllowInsecureTransport && isDevelopment);

        if (options.AllowInsecureTransport && !isDevelopment)
        {
            throw new InvalidOperationException(
                "Database:AllowInsecureTransport is set outside Development. " +
                "Lease and tenant data must travel over TLS.");
        }

        return NpgsqlDataSourceFactory.Create(options.ConnectionString, requireTls);
    });

    builder.Services.AddHttpContextAccessor();

    // Scoped: one identity per request, which is what makes the RLS
    // interceptor correct under concurrency.
    builder.Services.AddScoped<IRequestIdentity, HttpRequestIdentity>();
    builder.Services.AddScoped<RlsIdentityInterceptor>();

    builder.Services.AddDbContext<AppDbContext>((serviceProvider, dbOptions) =>
    {
        var dataSource = serviceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();
        var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        dbOptions.UseNpgsql(dataSource, npgsql =>
            {
                npgsql.CommandTimeout(database.CommandTimeoutSeconds);
                npgsql.UseNodaTime();
                npgsql.MapApplicationEnums();
            })
            // Without this interceptor every query runs with no identity and
            // returns nothing. It is registered here, once, rather than being
            // something each repository has to remember.
            .AddInterceptors(serviceProvider.GetRequiredService<RlsIdentityInterceptor>());

        if (isDevelopment)
        {
            // Parameter values are lease terms and personal data, so this is
            // Development-only and deliberately not configurable.
            dbOptions.EnableDetailedErrors();
        }
    });

    // -------------------------------------------------------------------------
    // Token protection
    //
    // Registered when a key is configured. Phase 1 has no Gmail connection
    // flow, so a missing key is not a startup failure yet; the endpoint that
    // needs it will report its absence clearly rather than the whole API
    // refusing to start.
    // -------------------------------------------------------------------------
    builder.Services.AddSingleton(serviceProvider =>
    {
        var options = serviceProvider
            .GetRequiredService<IOptions<TokenEncryptionOptions>>().Value;

        ITokenProtector? protector = string.IsNullOrWhiteSpace(options.Keys)
            ? null
            : AesGcmTokenProtector.FromConfiguration(options.Keys, options.CurrentKeyVersion);

        return new TokenProtectorAccessor(protector);
    });

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    // Keep the claim names the token actually uses. The legacy mapping renames
    // sub to a long URI, which makes claim handling depend on a global static.
    JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwt = builder.Configuration
                .GetSection(JwtOptions.SectionName)
                .Get<JwtOptions>() ?? new JwtOptions();

            // Signing keys are discovered from the issuer's published metadata
            // and refreshed automatically, so a key rotation at the identity
            // provider needs no deployment here. A shared HS256 secret is
            // deliberately not supported: it would have to be distributed to
            // every service that validates a token.
            options.Authority = jwt.Issuer;
            options.Audience = jwt.Audience;

            // Metadata must be fetched over TLS. Relaxed only for a local
            // identity provider during development.
            options.RequireHttpsMetadata = !isDevelopment;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,

                ValidateAudience = true,
                ValidAudience = jwt.Audience,

                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),

                // Only asymmetric algorithms. Pinning this prevents an
                // algorithm-confusion attack in which a token signed with the
                // public key as an HMAC secret is presented as valid.
                ValidAlgorithms = [
                    SecurityAlgorithms.RsaSha256,
                    SecurityAlgorithms.RsaSha384,
                    SecurityAlgorithms.RsaSha512,
                    SecurityAlgorithms.EcdsaSha256,
                    SecurityAlgorithms.EcdsaSha384,
                    SecurityAlgorithms.EcdsaSha512,
                ],

                RequireExpirationTime = true,
                RequireSignedTokens = true,
            };

            options.MapInboundClaims = false;

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    // The reason is logged for operators and never returned to
                    // the caller, who gets a bare 401 (section 4.F).
                    Log.Warning(
                        "JWT validation failed: {Reason}",
                        context.Exception.GetType().Name);

                    return Task.CompletedTask;
                },
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        // Every endpoint requires authentication unless it opts out
        // explicitly. The alternative, remembering RequireAuthorization on
        // each one, fails the first time somebody forgets (section 4.B).
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization
            .AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // -------------------------------------------------------------------------
    // CORS
    // -------------------------------------------------------------------------
    builder.Services.AddCors(options =>
    {
        var allowed = builder.Configuration
            .GetSection(CorsOptions.SectionName)
            .Get<CorsOptions>()?.AllowedOrigins ?? [];

        options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowed)
            .WithMethods("GET", "POST", "PATCH", "PUT", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", HttpRequestIdentity.CorrelationHeader)
            .AllowCredentials()
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
    });

    // -------------------------------------------------------------------------
    // Rate limiting (section 4.F)
    //
    // Partitioned per authenticated user, falling back to the remote address
    // for unauthenticated calls. Limits are per instance; with more than one
    // API instance they become per instance rather than global, which is
    // acceptable for abuse prevention and is noted in docs/security.md.
    // -------------------------------------------------------------------------
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";

            Log.Warning(
                "Rate limit rejected {Method} {Path} for partition {Partition}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                RateLimitPartitionKey(context.HttpContext));

            await context.HttpContext.Response
                .WriteAsJsonAsync(
                    new { error = "too_many_requests", retryAfterSeconds = 60 },
                    cancellationToken)
                .ConfigureAwait(false);
        };

        // Baseline for ordinary reads and writes.
        options.AddPolicy("standard", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

        // Expensive and abuse-prone: document upload and AI extraction.
        options.AddPolicy("upload", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                }));

        // Anything that causes an email to be sent. Deliberately tight: this
        // is what stops the platform being used as an open relay (section 4.G).
        options.AddPolicy("email", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                }));
    });

    // -------------------------------------------------------------------------
    // Request limits and JSON
    // -------------------------------------------------------------------------
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(formOptions =>
    {
        var security = builder.Configuration
            .GetSection(SecurityOptions.SectionName)
            .Get<SecurityOptions>() ?? new SecurityOptions();

        formOptions.MultipartBodyLengthLimit = security.MaxUploadBytes;
        formOptions.ValueCountLimit = 64;
        formOptions.KeyLengthLimit = 256;
    });

    // --- Document intelligence (phase 2) ---------------------------------
    //
    // The API holds storage but NOT the extraction provider. It needs to put
    // an uploaded file somewhere and to issue signed URLs for the viewer; it
    // never calls the model, because reading a document is slow, untrusted
    // work that belongs in the worker. The ILeaseExtractor registered here is
    // injected only so the upload response can say whether automatic
    // extraction is available at all.
    builder.Services
        .Configure<SupabaseStorageOptions>(
            builder.Configuration.GetSection(SupabaseStorageOptions.SectionName))
        .Configure<GeminiOptions>(
            builder.Configuration.GetSection(GeminiOptions.SectionName));

    builder.Services.AddHttpClient<IDocumentStorage, SupabaseDocumentStorage>();
    builder.Services.AddHttpClient<ILeaseExtractor, GeminiLeaseExtractor>();

    // --- Gmail connection (phase 4) ---------------------------------------
    //
    // The API runs the OAuth flow and stores the resulting token; it can
    // never read one back. That asymmetry is enforced by grants rather than
    // by convention: app.store_email_account_token is granted to app_api and
    // app.read_email_account_token is not.
    builder.Services.Configure<GoogleOAuthOptions>(
        builder.Configuration.GetSection(GoogleOAuthOptions.SectionName));

    builder.Services.AddHttpClient<GoogleOAuthClient>();

    builder.Services.Configure<JsonOptions>(json =>
    {
        // NodaTime over the wire, so a LocalDate serialises as "2026-04-01"
        // rather than as a timestamp that invites a time zone mistake.
        json.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        json.SerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        // Nulls are written, not omitted.
        //
        // This used to be WhenWritingNull, which saves a few bytes and costs
        // correctness. The frontend's types say `archivedAt: string | null`,
        // but an omitted property arrives as `undefined`, and
        // `undefined !== null` is true — so a brand-new property read as
        // archived, every unread notification read as already read, and every
        // unpaused rule read as paused. Each of those is a comparison that
        // looks right, against a type that says it is right.
        //
        // A JSON contract where "absent" and "null" mean the same thing on
        // the wire but different things in the client is a trap set for every
        // future field. The payload is marginally larger; every nullable field
        // now means what its type says.
        json.SerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.Never;

        // Enums cross the wire as camelCase NAMES, not ordinals.
        //
        // Without this, OrgRole.Owner serialises as 0. The frontend compares
        // the value against "owner", every comparison fails, and an owner is
        // told their role is read-only — which is exactly how this was found.
        //
        // An ordinal is also a contract that renumbers itself the moment
        // somebody inserts an enum member in the middle, silently promoting
        // every stored or cached value to the wrong meaning. The name is
        // stable against that.
        //
        // It applies to reading as well as writing, so a request body may
        // send "admin" instead of 1.
        json.SerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase));
    });

    builder.Services.AddProblemDetails(options =>
        options.CustomizeProblemDetails = context =>
        {
            // Correlation id in, stack trace out. An operator can find the
            // request in the logs; the caller learns nothing about internals
            // (section 4.F).
            context.ProblemDetails.Extensions["correlationId"] =
                context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions.Remove("exception");
        });

    // Request validators. Registered by assembly scan so adding a validator
    // alongside a new contract needs no second edit here, which is the kind of
    // omission that otherwise surfaces as a startup failure.
    builder.Services.AddValidatorsFromAssemblyContaining<Program>(
        lifetime: ServiceLifetime.Singleton);

    builder.Services.AddEndpointsApiExplorer();

    var app = builder.Build();

    // =========================================================================
    // Pipeline
    //
    // Order is significant and is the reason this is one readable block:
    // headers first so they apply to every response including errors, then
    // error handling, then transport, then identity, then limits, then routing.
    // =========================================================================

    app.UseSecurityHeaders();

    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();

        Log.Error(
            feature?.Error,
            "Unhandled exception for {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        // A fixed, uninformative body. The detail is in the logs, keyed by the
        // correlation id the client can quote.
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "An unexpected error occurred.",
            status = 500,
            correlationId = context.TraceIdentifier,
        }).ConfigureAwait(false);
    }));

    app.UseStatusCodePages();

    if (!isDevelopment)
    {
        var security = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;

        if (security.EnableHsts)
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
    }

    app.UseSerilogRequestLogging(options =>
    {
        // The message template is fixed and contains no user content.
        options.MessageTemplate =
            "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

        options.EnrichDiagnosticContext = (diagnostic, context) =>
        {
            diagnostic.Set("correlationId", context.TraceIdentifier);

            // Whether the caller was authenticated, never who they were.
            diagnostic.Set("authenticated", context.User.Identity?.IsAuthenticated ?? false);
        };

        // Health checks at Debug, so a minute of polling does not bury a real
        // event in the log.
        options.GetLevel = (context, _, exception) =>
            exception is not null ? LogEventLevel.Error
            : context.Response.StatusCode >= 500 ? LogEventLevel.Error
            : context.Response.StatusCode >= 400 ? LogEventLevel.Warning
            : context.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
            : LogEventLevel.Information;
    });

    app.UseCors();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseRateLimiter();

    // -------------------------------------------------------------------------
    // Endpoints
    // -------------------------------------------------------------------------
    app.MapHealthEndpoints();

    var api = app.MapGroup("/api/v1")
        .RequireAuthorization()
        .RequireRateLimiting("standard");

    api.MapOrganizationEndpoints();
    api.MapPropertyEndpoints();
    api.MapUnitEndpoints();
    api.MapTenantEndpoints();
    api.MapLeaseEndpoints();

    // The maintenance board and its task notification rules.
    api.MapMaintenanceEndpoints();

    // The unified notification centre. Not under /organizations, because a
    // notification belongs to a PERSON rather than to a workspace, and a user
    // in two workspaces wants one inbox rather than two.
    api.MapNotificationEndpoints();
    api.MapDocumentEndpoints();
    api.MapRentScheduleEndpoints();
    api.MapReminderEndpoints();

    // Who a rent reminder is addressed to, and when. The tables have existed
    // since 0005; until this there was no way to write to them, so no reminder
    // could be addressed to anybody.
    api.MapReminderRuleEndpoints();
    api.MapEmailAccountEndpoints();
    api.MapEmailTemplateEndpoints();
    api.MapMembershipEndpoints();

    // -------------------------------------------------------------------------
    // Startup verification
    //
    // Checked once, at boot, so a schema or configuration drift fails the
    // deployment rather than a request months later.
    // -------------------------------------------------------------------------
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var dataSource = scope.ServiceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();

        await StartupEnumMappingCheck.VerifyAsync(dataSource).ConfigureAwait(false);

        Log.Information("Database enum mappings verified against the live schema.");
    }

    Log.Information(
        "LeaseReminder API starting in {Environment}",
        app.Environment.EnvironmentName);

    await app.RunAsync().ConfigureAwait(false);

    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "LeaseReminder API failed to start");

    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

/// <summary>
/// Partition key for rate limiting: the authenticated subject when there is
/// one, otherwise the remote address.
///
/// <para>
/// Partitioning by user rather than only by address matters in both
/// directions. It stops one tenant of a shared NAT exhausting everyone else's
/// budget, and it stops an authenticated user evading a limit by changing
/// address.
/// </para>
/// </summary>
static string RateLimitPartitionKey(HttpContext context)
{
    var subject = context.User.FindFirst("sub")?.Value
                  ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    if (!string.IsNullOrEmpty(subject))
    {
        return "user:" + subject;
    }

    // RemoteIpAddress is only trustworthy behind a correctly configured proxy.
    // On Render, forwarded headers are processed by the platform; see
    // docs/deployment.md.
    return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}
