using System.ComponentModel.DataAnnotations;

namespace LeaseReminder.Api.Configuration;

/// <summary>
/// Strongly typed configuration, validated at startup rather than at first use.
///
/// <para>
/// Every one of these settings, misconfigured, produces a system that starts
/// and appears to work: a wildcard CORS origin, a missing JWT issuer, an
/// absent encryption key. Validating on start means a bad deployment fails
/// immediately and visibly instead of running in an insecure state.
/// </para>
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// The API connection string. Must name a least-privilege role with RLS
    /// enforced; <see cref="Infrastructure.Persistence.NpgsqlDataSourceFactory"/>
    /// rejects an owner or superuser.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Allows SSL Mode=Disable, for the local docker-compose database only.
    /// Startup refuses to honour this outside Development.
    /// </summary>
    public bool AllowInsecureTransport { get; init; }

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Token issuer, for example https://PROJECT.supabase.co/auth/v1. Used both
    /// to discover signing keys and to validate the iss claim.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    /// Expected audience. Supabase issues "authenticated" for signed-in users.
    /// Validating it stops a token minted for a different application being
    /// replayed here.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = "authenticated";

    /// <summary>
    /// Clock skew allowance. Kept small: section 4.A asks for short-lived
    /// sessions, and a generous skew quietly extends every token's life.
    /// </summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins permitted to call the API with credentials. A wildcard is
    /// rejected at startup: section 4.F forbids a credentialed wildcard, and
    /// the browser would refuse it anyway, so allowing it here would only
    /// produce a confusing failure.
    /// </summary>
    [MinLength(1)]
    public string[] AllowedOrigins { get; init; } = [];
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Largest accepted request body. Lease uploads go to a dedicated endpoint
    /// with its own, larger limit; everything else is JSON and has no business
    /// being large (section 4.F).
    /// </summary>
    [Range(1024, 1048576)]
    public int MaxJsonBodyBytes { get; init; } = 65536;

    /// <summary>Largest accepted lease document, matching the database CHECK constraint.</summary>
    [Range(1024, 52428800)]
    public int MaxUploadBytes { get; init; } = 52428800;

    /// <summary>
    /// Emit HSTS. Off in development, where there is no TLS and a cached HSTS
    /// entry for localhost is a nuisance that outlives the session.
    /// </summary>
    public bool EnableHsts { get; init; } = true;

    [Range(1, 63072000)]
    public int HstsMaxAgeSeconds { get; init; } = 31536000;
}

// TokenEncryptionOptions used to live here. It moved to
// LeaseReminder.Infrastructure.Security because the worker needs it too: the
// API encrypts a Gmail token when a mailbox is connected, and the worker
// decrypts one when it sends. A copy in each host project would be two places
// for the key format to drift apart.
