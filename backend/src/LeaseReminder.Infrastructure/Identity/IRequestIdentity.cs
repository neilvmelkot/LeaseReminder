namespace LeaseReminder.Infrastructure.Identity;

/// <summary>
/// The identity the database should act as for the current unit of work.
///
/// <para>
/// There is exactly one source for this value: the <c>sub</c> claim of a JWT
/// whose signature, issuer, audience and expiry have already been validated.
/// Nothing from the request body, the query string, a header, or a route
/// parameter ever reaches it. That is the whole point of routing identity
/// through a single scoped service instead of passing user ids around: section
/// 4.B requires that a browser-supplied user_id, property_id, or lease_id is
/// never trusted, and the simplest way to guarantee that is to leave no path
/// by which one could become the database identity.
/// </para>
/// </summary>
public interface IRequestIdentity
{
    /// <summary>
    /// The verified subject, or <see langword="null"/> when the caller is not
    /// authenticated. Null is the safe value: every RLS policy matches zero
    /// rows without an identity.
    /// </summary>
    Guid? AuthUserId { get; }

    /// <summary>Correlation id for structured logs and audit entries.</summary>
    Guid CorrelationId { get; }
}

/// <summary>
/// Fixed identity, for the worker and for tests.
///
/// The worker normally carries NO identity at all: it reaches the database
/// through the SECURITY DEFINER functions in db/migrations/0007 and holds no
/// table privileges, so an identity would be meaningless to it.
/// </summary>
public sealed class StaticRequestIdentity(Guid? authUserId, Guid? correlationId = null)
    : IRequestIdentity
{
    public Guid? AuthUserId { get; } = authUserId;

    public Guid CorrelationId { get; } = correlationId ?? Guid.NewGuid();

    /// <summary>An explicitly unauthenticated identity.</summary>
    public static StaticRequestIdentity Anonymous { get; } = new(null);
}
