using System.ComponentModel.DataAnnotations;

namespace LeaseReminder.Infrastructure.Storage;

/// <summary>
/// Settings for the Supabase Storage bucket holding lease documents.
/// </summary>
public sealed class SupabaseStorageOptions
{
    public const string SectionName = "Storage:Supabase";

    /// <summary>The project URL, e.g. https://abcdefgh.supabase.co.</summary>
    [Required]
    public Uri? ProjectUrl { get; set; }

    /// <summary>
    /// The service-role key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This key bypasses Row Level Security entirely, which is why section 4
    /// says to keep it on the server and never expose it in frontend code or a
    /// public environment variable. It is held only by the API and worker
    /// processes, is never sent to the browser, and is never logged.
    /// </para>
    /// <para>
    /// The browser never needs it: a user who is entitled to see a document
    /// receives a short-lived signed URL instead, which grants access to that
    /// one object for a few minutes and to nothing else.
    /// </para>
    /// </remarks>
    [Required]
    public string ServiceRoleKey { get; set; } = string.Empty;

    /// <summary>
    /// The bucket name. Must be a private bucket.
    /// </summary>
    /// <remarks>
    /// Section 4.C: "Avoid public buckets for leases." A public bucket would
    /// make every object readable by anyone who learns its path, and the paths
    /// are the one thing an attacker could plausibly obtain from a log.
    /// </remarks>
    [Required]
    public string Bucket { get; set; } = "lease-documents";

    /// <summary>
    /// How long a signed URL stays valid, in seconds.
    /// </summary>
    /// <remarks>
    /// Five minutes. Long enough to open a document and start reading it, and
    /// the viewer requests a fresh one when it needs it. The URL is a bearer
    /// credential for that object: anyone holding it has access, so its value
    /// decays quickly on purpose.
    /// </remarks>
    [Range(30, 3600)]
    public int SignedUrlLifetimeSeconds { get; set; } = 300;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Whether enough is configured to store anything.</summary>
    public bool IsConfigured =>
        this.ProjectUrl is not null
        && !string.IsNullOrWhiteSpace(this.ServiceRoleKey)
        && !string.IsNullOrWhiteSpace(this.Bucket);
}
