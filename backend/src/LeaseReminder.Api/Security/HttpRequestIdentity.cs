using System.Security.Claims;
using LeaseReminder.Infrastructure.Identity;

namespace LeaseReminder.Api.Security;

/// <summary>
/// Reads the caller identity from the validated JWT, and from nowhere else.
///
/// <para>
/// By the time this runs, the authentication middleware has verified the
/// token's signature against the issuer's published keys, and checked the
/// issuer, audience, and expiry. So <c>ClaimTypes.NameIdentifier</c> here is
/// the <c>sub</c> claim of a token we have cryptographically established was
/// issued by our identity provider to this user.
/// </para>
///
/// <para>
/// Nothing in the request body, query string, route, or any header other than
/// Authorization can influence the result. That is the mechanical guarantee
/// behind section 4.B: a browser-supplied user_id cannot become the database
/// identity, because there is no code path from one to the other.
/// </para>
/// </summary>
public sealed class HttpRequestIdentity : IRequestIdentity
{
    /// <summary>Header used to correlate a client request with server logs.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    public HttpRequestIdentity(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        var context = accessor.HttpContext;

        AuthUserId = ExtractSubject(context?.User);
        CorrelationId = ResolveCorrelationId(context);
    }

    public Guid? AuthUserId { get; }

    public Guid CorrelationId { get; }

    private static Guid? ExtractSubject(ClaimsPrincipal? principal)
    {
        // An unauthenticated or partially authenticated principal yields null,
        // and null means every RLS policy matches nothing. Failing closed is
        // the only acceptable direction here.
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // JwtBearer maps sub to NameIdentifier; "sub" is checked too in case
        // claim mapping is turned off, which Program.cs does.
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? principal.FindFirst("sub")?.Value;

        // A token whose subject is not a UUID is not one of ours. Treated as
        // no identity rather than as an error, so it reads nothing.
        return Guid.TryParse(subject, out var parsed) ? parsed : null;
    }

    private static Guid ResolveCorrelationId(HttpContext? context)
    {
        if (context is null)
        {
            return Guid.NewGuid();
        }

        // A client-supplied correlation id is accepted only when it is a
        // well-formed GUID. It reaches logs and audit metadata, so accepting
        // arbitrary text would let a caller inject content into them.
        if (context.Request.Headers.TryGetValue(CorrelationHeader, out var supplied)
            && Guid.TryParse(supplied.ToString(), out var parsed))
        {
            return parsed;
        }

        // Fall back to the trace identifier so a correlation id always exists,
        // and is the same one the framework is already using.
        return Guid.TryParse(context.TraceIdentifier, out var fromTrace)
            ? fromTrace
            : Guid.NewGuid();
    }
}
