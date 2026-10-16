using System.Text.RegularExpressions;

namespace Libs.Reso;

/// <summary>
/// RESO Data Dictionary naming, as far as Slice A needs it.
///
/// <para>
/// <b>Field keys.</b> Every Data Dictionary field is a PascalCase name of letters and digits —
/// <c>LivingArea</c>, <c>InteriorFeatures</c>, <c>MiddleOrJuniorSchoolDistrict</c>. A key that is
/// not shaped like one cannot be a RESO field, so it is refused at the boundary rather than
/// stored under a name no MLS will recognise. This is a shape check, not membership in the full
/// dictionary: Slice A does not ship the dictionary, and the MLS rule profile is what restricts
/// which fields and lookup values a given MLS accepts (research R2).
/// </para>
/// </summary>
public static partial class ResoFieldKey
{
    /// <summary>The longest Data Dictionary field names are well under this.</summary>
    public const int MaxLength = 80;

    public static bool IsWellFormed(string? key) =>
        key is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(key);

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex Pattern();
}

/// <summary>
/// The residential values of the RESO <c>PropertySubType</c> lookup that a listing's property
/// type may take (research R33).
///
/// <para>
/// Residential only, because Slice A drafts residential sale listings for CRMLS; land,
/// commercial and lease types have different required fields and are out of scope. The spellings
/// are the Data Dictionary's standard lookup names, which is also what the MLS rule profile's
/// enumerated lookups (0010) are expressed in, so a value accepted here is one a later MLS
/// submission can carry unchanged.
/// </para>
/// </summary>
public static class ResoPropertySubType
{
    public static IReadOnlyList<string> Residential { get; } =
    [
        "SingleFamilyResidence",
        "Condominium",
        "Townhouse",
        "Duplex",
        "Triplex",
        "Quadruplex",
        "ManufacturedHome",
        "ManufacturedOnLand",
        "StockCooperative",
        "Cabin",
    ];

    public static bool IsResidential(string? value) =>
        value is not null && Residential.Contains(value, StringComparer.Ordinal);
}
