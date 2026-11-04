namespace LeaseReminder.Application.Documents;

/// <summary>
/// How a field's value is shaped.
/// </summary>
/// <remarks>
/// These strings are written to <c>lease_extracted_fields.value_type</c>,
/// whose CHECK constraint lists exactly this set.
/// </remarks>
public enum LeaseFieldType
{
    /// <summary>A short single-line value.</summary>
    ShortText,

    /// <summary>A longer passage, such as a clause summary.</summary>
    LongText,

    /// <summary>A calendar date. Normalised to ISO 8601.</summary>
    Date,

    /// <summary>A monetary amount. Normalised to a decimal, currency held separately.</summary>
    Money,

    WholeNumber,

    Fraction,

    Boolean,

    /// <summary>One of a fixed set, listed on the field.</summary>
    Enum,

    /// <summary>A whole number of days.</summary>
    DurationDays,
}

/// <summary>
/// How important a field is to the review.
/// </summary>
public enum LeaseFieldImportance
{
    /// <summary>
    /// Reminders cannot be scheduled without it. A missing one of these is
    /// shown at the top of the review screen as something to supply by hand.
    /// </summary>
    Required,

    /// <summary>Changes what is sent, but a lease can activate without it.</summary>
    Important,

    /// <summary>Useful context. Its absence is unremarkable.</summary>
    Supplementary,
}

/// <summary>
/// One field the extraction pipeline looks for.
/// </summary>
/// <param name="Name">
/// Dotted path, written to <c>lease_extracted_fields.field_name</c>. Must
/// satisfy that column's CHECK constraint: lowercase, starting with a letter,
/// segments joined by dots.
/// </param>
/// <param name="Type">The expected shape of the value.</param>
/// <param name="Group">The heading it appears under in the review screen.</param>
/// <param name="Label">What a human calls it.</param>
/// <param name="Description">
/// Instruction to the model. This is the only per-field guidance it gets, so
/// it carries the disambiguation: which of two dates, which of two amounts.
/// </param>
/// <param name="Importance">How the review screen ranks it.</param>
/// <param name="AllowedValues">For an enum field, the permitted values.</param>
public sealed record LeaseField(
    string Name,
    LeaseFieldType Type,
    string Group,
    string Label,
    string Description,
    LeaseFieldImportance Importance = LeaseFieldImportance.Supplementary,
    IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>The string written to the database's value_type column.</summary>
    public string ValueTypeName => this.Type switch
    {
        LeaseFieldType.ShortText => "string",
        LeaseFieldType.LongText => "text",
        LeaseFieldType.Date => "date",
        LeaseFieldType.Money => "money",
        LeaseFieldType.WholeNumber => "integer",
        LeaseFieldType.Fraction => "decimal",
        LeaseFieldType.Boolean => "boolean",
        LeaseFieldType.Enum => "enum",
        LeaseFieldType.DurationDays => "duration_days",
        _ => throw new InvalidOperationException($"Unmapped field type {this.Type}."),
    };
}

/// <summary>
/// The complete set of lease terms the pipeline extracts.
/// </summary>
/// <remarks>
/// <para>
/// Section 13.D asks for full lease intelligence rather than just the handful
/// of values needed to send a rent reminder. The practical reason is that the
/// expensive part of this process is a human reading a lease; if they are
/// going to read it anyway, capturing the deposit terms and the escalation
/// clause at the same time costs nothing extra and saves a second pass later.
/// </para>
/// <para>
/// This catalogue is the single source of truth for three things that must
/// agree: the schema sent to the model, the validation applied to what comes
/// back, and the grouping shown on the review screen. Defining it once is what
/// stops the model being asked for a field the UI cannot display, or the UI
/// offering one the model was never asked for.
/// </para>
/// <para>
/// Nothing here is treated as authoritative. Every value arrives as a
/// suggestion attached to a page reference, and a human approves it before a
/// lease can activate (section 13.C).
/// </para>
/// </remarks>
public static class LeaseFieldCatalog
{
    /// <summary>
    /// Bumped whenever the field set or their descriptions change.
    /// </summary>
    /// <remarks>
    /// Stored on every extraction run. Without it, a re-extraction months
    /// later would be impossible to compare against the original, because
    /// there would be no record of what was asked for the first time.
    /// </remarks>
    public const string Version = "1.0";

    public const string PartiesGroup = "Parties and property";

    public const string TermGroup = "Term";

    public const string RentGroup = "Rent";

    public const string LateFeeGroup = "Late fees";

    public const string DepositGroup = "Deposit";

    public const string EscalationGroup = "Rent increases";

    public const string ChargesGroup = "Additional charges";

    public const string ObligationsGroup = "Obligations and permissions";

    /// <summary>
    /// The fields, in the order they are presented for review.
    /// </summary>
    public static IReadOnlyList<LeaseField> Fields { get; } =
    [
        // -- Parties and property ------------------------------------------
        new("property.address", LeaseFieldType.LongText, PartiesGroup,
            "Property address",
            "The full street address of the leased premises, as written in the lease.",
            LeaseFieldImportance.Required),

        new("property.unit_number", LeaseFieldType.ShortText, PartiesGroup,
            "Unit number",
            "The apartment, unit, or suite identifier, if the lease names one separately "
            + "from the street address.",
            LeaseFieldImportance.Important),

        new("landlord.name", LeaseFieldType.ShortText, PartiesGroup,
            "Landlord",
            "The landlord, lessor, or owner named as a party to this lease.",
            LeaseFieldImportance.Important),

        new("landlord.email", LeaseFieldType.ShortText, PartiesGroup,
            "Landlord email",
            "An email address given for the landlord or their agent for notices.",
            LeaseFieldImportance.Supplementary),

        new("tenant.names", LeaseFieldType.LongText, PartiesGroup,
            "Tenants",
            "Every tenant or lessee named as a party, separated by semicolons. Include "
            + "all of them, not only the first.",
            LeaseFieldImportance.Required),

        new("tenant.email", LeaseFieldType.ShortText, PartiesGroup,
            "Tenant email",
            "An email address given for the tenant for notices.",
            LeaseFieldImportance.Important),

        // -- Term ----------------------------------------------------------
        new("term.start_date", LeaseFieldType.Date, TermGroup,
            "Start date",
            "The date the tenancy begins. This is the commencement date of the term, "
            + "which is often different from the date the lease was signed. If both "
            + "appear, take the commencement date.",
            LeaseFieldImportance.Required),

        new("term.end_date", LeaseFieldType.Date, TermGroup,
            "End date",
            "The date the fixed term ends. Null if the tenancy is month-to-month with "
            + "no fixed end.",
            LeaseFieldImportance.Required),

        new("term.is_month_to_month", LeaseFieldType.Boolean, TermGroup,
            "Month to month",
            "True if the tenancy runs month to month rather than for a fixed term.",
            LeaseFieldImportance.Important),

        new("term.auto_renews", LeaseFieldType.Boolean, TermGroup,
            "Renews automatically",
            "True if the lease states that it renews automatically unless notice is given.",
            LeaseFieldImportance.Important),

        new("term.notice_period_days", LeaseFieldType.DurationDays, TermGroup,
            "Notice period",
            "How many days of notice a party must give to end or not renew the tenancy. "
            + "Convert months to days at 30 days per month and say so in the excerpt.",
            LeaseFieldImportance.Important),

        // -- Rent ----------------------------------------------------------
        new("rent.amount", LeaseFieldType.Money, RentGroup,
            "Rent amount",
            "The recurring rent for one period. Take the base rent only; parking, pet "
            + "rent, and utilities belong to their own fields even when the lease adds "
            + "them into a single total.",
            LeaseFieldImportance.Required),

        new("rent.currency", LeaseFieldType.ShortText, RentGroup,
            "Currency",
            "The three-letter ISO currency code for the rent, such as USD or CAD. Infer "
            + "it from the currency symbol and the property's location if it is not "
            + "written out.",
            LeaseFieldImportance.Important),

        new("rent.frequency", LeaseFieldType.Enum, RentGroup,
            "Frequency",
            "How often rent falls due.",
            LeaseFieldImportance.Required,
            ["monthly", "weekly", "biweekly", "every_30_days", "one_time", "custom"]),

        new("rent.due_day", LeaseFieldType.WholeNumber, RentGroup,
            "Due day",
            "The day of the month rent is due, as a number from 1 to 31. Use 31 where "
            + "the lease says the last day of the month.",
            LeaseFieldImportance.Required),

        new("rent.first_due_date", LeaseFieldType.Date, RentGroup,
            "First payment due",
            "The date the first rent payment is due, if the lease states one separately "
            + "from the start of the term.",
            LeaseFieldImportance.Important),

        new("rent.payment_method", LeaseFieldType.LongText, RentGroup,
            "How rent is paid",
            "How and where rent is to be paid, summarised in one sentence. Do not "
            + "include bank account or card numbers.",
            LeaseFieldImportance.Supplementary),

        // -- Late fees -----------------------------------------------------
        new("late_fee.grace_period_days", LeaseFieldType.DurationDays, LateFeeGroup,
            "Grace period",
            "How many days after the due date rent may be paid before it is late.",
            LeaseFieldImportance.Important),

        new("late_fee.amount", LeaseFieldType.Money, LateFeeGroup,
            "Late fee",
            "A flat late fee. Leave null if the fee is expressed only as a percentage.",
            LeaseFieldImportance.Important),

        new("late_fee.percentage", LeaseFieldType.Fraction, LateFeeGroup,
            "Late fee percentage",
            "A late fee expressed as a percentage of the rent. Give the number only, so "
            + "five percent is 5.",
            LeaseFieldImportance.Supplementary),

        new("late_fee.is_daily", LeaseFieldType.Boolean, LateFeeGroup,
            "Charged daily",
            "True if the late fee accrues for each day rent remains unpaid, rather than "
            + "once.",
            LeaseFieldImportance.Supplementary),

        // -- Deposit -------------------------------------------------------
        new("deposit.amount", LeaseFieldType.Money, DepositGroup,
            "Security deposit",
            "The security or damage deposit the tenant pays.",
            LeaseFieldImportance.Important),

        new("deposit.return_days", LeaseFieldType.DurationDays, DepositGroup,
            "Deposit return window",
            "How many days after the tenancy ends the deposit must be returned or "
            + "accounted for.",
            LeaseFieldImportance.Supplementary),

        new("deposit.held_by", LeaseFieldType.LongText, DepositGroup,
            "Deposit held by",
            "Who holds the deposit and in what kind of account, if the lease says.",
            LeaseFieldImportance.Supplementary),

        // -- Escalation ----------------------------------------------------
        new("escalation.has_escalation", LeaseFieldType.Boolean, EscalationGroup,
            "Rent increases during term",
            "True if the lease provides for the rent to change during the term.",
            LeaseFieldImportance.Important),

        new("escalation.percentage", LeaseFieldType.Fraction, EscalationGroup,
            "Increase percentage",
            "The percentage the rent increases by, as a number, so three percent is 3.",
            LeaseFieldImportance.Supplementary),

        new("escalation.amount", LeaseFieldType.Money, EscalationGroup,
            "Increase amount",
            "A fixed amount the rent increases by, where the lease states one instead of "
            + "a percentage.",
            LeaseFieldImportance.Supplementary),

        new("escalation.effective_date", LeaseFieldType.Date, EscalationGroup,
            "First increase takes effect",
            "The date the first rent increase applies.",
            LeaseFieldImportance.Supplementary),

        new("escalation.frequency", LeaseFieldType.Enum, EscalationGroup,
            "Increase frequency",
            "How often the rent increases.",
            LeaseFieldImportance.Supplementary,
            ["annually", "biannually", "monthly", "one_time", "custom"]),

        // -- Additional charges --------------------------------------------
        new("charges.pet_rent", LeaseFieldType.Money, ChargesGroup,
            "Pet rent",
            "A recurring charge for keeping a pet. Not the one-off pet deposit.",
            LeaseFieldImportance.Supplementary),

        new("charges.parking", LeaseFieldType.Money, ChargesGroup,
            "Parking",
            "A recurring parking charge, where it is stated separately from the rent.",
            LeaseFieldImportance.Supplementary),

        new("charges.utilities_included", LeaseFieldType.LongText, ChargesGroup,
            "Utilities included",
            "Which utilities the rent covers, and which the tenant pays separately.",
            LeaseFieldImportance.Supplementary),

        // -- Obligations ---------------------------------------------------
        new("obligations.maintenance_responsibility", LeaseFieldType.LongText, ObligationsGroup,
            "Maintenance responsibility",
            "Who is responsible for repairs and maintenance, summarised in one or two "
            + "sentences.",
            LeaseFieldImportance.Supplementary),

        new("obligations.pets_allowed", LeaseFieldType.Boolean, ObligationsGroup,
            "Pets allowed",
            "True if pets are permitted, including where permission is conditional.",
            LeaseFieldImportance.Supplementary),

        new("obligations.subletting_allowed", LeaseFieldType.Boolean, ObligationsGroup,
            "Subletting allowed",
            "True if the tenant may sublet, including with the landlord's consent.",
            LeaseFieldImportance.Supplementary),

        new("termination.early_termination_fee", LeaseFieldType.Money, ObligationsGroup,
            "Early termination fee",
            "What the tenant pays to end the lease early, where a figure is given.",
            LeaseFieldImportance.Supplementary),
    ];

    /// <summary>Field names, for constraining the model's output.</summary>
    public static IReadOnlyList<string> FieldNames { get; } =
        [.. Fields.Select(field => field.Name)];

    private static readonly Dictionary<string, LeaseField> ByNameIndex =
        Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a field by name, or null if it is not one this pipeline asked
    /// for.
    /// </summary>
    /// <remarks>
    /// The null case is not theoretical. A model given an enumerated list of
    /// names can still return one that is not on it, and such a value must be
    /// discarded rather than written: <c>lease_extracted_fields</c> has a
    /// unique constraint per extraction and a CHECK on the name's shape, so an
    /// invented name would either fail the insert or quietly create a row the
    /// review screen has no way to display.
    /// </remarks>
    public static LeaseField? Find(string? name) =>
        name is not null && ByNameIndex.TryGetValue(name, out var field) ? field : null;

    /// <summary>The distinct groups, in presentation order.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        [.. Fields.Select(field => field.Group).Distinct(StringComparer.Ordinal)];
}
