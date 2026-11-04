using System.ComponentModel.DataAnnotations;

namespace LeaseReminder.Infrastructure.Documents;

/// <summary>
/// Settings for the Gemini extraction provider.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Documents:Gemini";

    /// <summary>
    /// An AI Studio API key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supplied through the environment, never committed. It is sent in the
    /// <c>x-goog-api-key</c> header rather than as a query parameter, which
    /// the API also accepts: a URL ends up in access logs, proxy logs, and
    /// error traces, and a credential should be in none of those.
    /// </para>
    /// <para>
    /// Empty means extraction is switched off. That is a supported state, not
    /// an error: the rest of the application works without it, and an upload
    /// simply reports that automatic extraction is unavailable.
    /// </para>
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Which model to call.
    /// </summary>
    /// <remarks>
    /// Configurable rather than fixed because which models the free tier
    /// offers changes over time, and that is not a reason to redeploy. The
    /// default is a Flash model: this is a long-context, structured-output,
    /// high-volume task, which is what Flash is for, and the free tier covers
    /// it.
    /// </remarks>
    [Required]
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>The API host. Overridable so tests can point at a local stub.</summary>
    [Required]
    public Uri BaseAddress { get; set; } = new("https://generativelanguage.googleapis.com/");

    /// <summary>
    /// How long to wait for one extraction.
    /// </summary>
    /// <remarks>
    /// Generous, because a 40-page scanned lease legitimately takes a while.
    /// The work happens in a background worker, so a slow call costs nobody a
    /// page load.
    /// </remarks>
    [Range(10, 600)]
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Ceiling on what may be sent inline, in bytes.
    /// </summary>
    /// <remarks>
    /// The API accepts roughly 20 MB of inline request body, and base64
    /// inflates by a third, so the practical limit on the file is lower than
    /// the 50 MB the upload endpoint accepts. A document above this is still
    /// stored and still reviewable; it just has to be extracted from its text
    /// rather than sent whole, and is reported as needing manual entry if
    /// there is no text to recover.
    /// </remarks>
    [Range(1_048_576, 20_971_520)]
    public int MaximumInlineBytes { get; set; } = 14_680_064; // 14 MiB

    /// <summary>
    /// Sampling temperature.
    /// </summary>
    /// <remarks>
    /// Zero. This is a reading task with a correct answer, and variation
    /// between runs would make a disagreement between two extractions of the
    /// same document impossible to interpret.
    /// </remarks>
    [Range(0, 2)]
    public double Temperature { get; set; }

    /// <summary>
    /// Cap on the response, in tokens.
    /// </summary>
    /// <remarks>
    /// The response is a bounded structure: about forty fields, each with a
    /// short excerpt. This is several times what a complete answer needs, so
    /// hitting it means something has gone wrong rather than that the lease
    /// was long.
    /// </remarks>
    [Range(1_024, 65_536)]
    public int MaximumOutputTokens { get; set; } = 16_384;
}
