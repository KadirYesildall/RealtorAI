namespace Libs.Compliance;

/// <summary>
/// T134 — advertising disclosure (FR-021; Story 4 scenario 7).
///
/// <para>
/// Every published listing advertises the agent and the brokerage, so the brokerage name and the
/// license numbers must be on record to be shown. In Slice A the listing is the only output, so
/// this checks the listing. Slice B's marketing outputs reuse the same check per output.
/// </para>
/// <para>
/// <b>Which numbers.</b> The brokerage's name and license number, and the agent's license number.
/// California requires a licensee's license number on advertising. Whether other states need
/// both numbers is unverified and recorded in research R36. A missing value is blocking, because
/// an advertisement without it cannot be fixed by acknowledging it.
/// </para>
/// </summary>
public static class DisclosureChecker
{
    public static IReadOnlyList<Finding> Check(string? brokerageName, string? brokerageLicense, string? agentLicense)
    {
        var findings = new List<Finding>();

        if (string.IsNullOrWhiteSpace(brokerageName))
        {
            findings.Add(Blocking("disclosure.brokerage_missing", "The listing has no brokerage on record, so the advertising cannot name one."));
        }
        else if (string.IsNullOrWhiteSpace(brokerageLicense))
        {
            findings.Add(Blocking("disclosure.brokerage_license_missing", "The brokerage has no license number on record to show with the listing."));
        }

        if (string.IsNullOrWhiteSpace(agentLicense))
        {
            findings.Add(Blocking("disclosure.agent_license_missing", "Your license number is not on record to show with the listing."));
        }

        return findings;
    }

    private static Finding Blocking(string ruleId, string reason) =>
        new(ruleId, CheckType.AdvertisingDisclosure, Severity.Blocking, FindingSource.Deterministic, reason);
}
