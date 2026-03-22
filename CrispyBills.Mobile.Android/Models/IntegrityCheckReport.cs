namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Result object produced by an integrity scan over a year's data.
/// Tracks counts of repaired issues and provides a quick string summary.
/// </summary>
public sealed class IntegrityCheckReport
{
    /// <summary>Number of bills repaired due to missing names.</summary>
    public int EmptyNameRepaired { get; set; }
    /// <summary>Number of bills repaired due to negative amounts.</summary>
    public int NegativeAmountRepaired { get; set; }
    /// <summary>Number of bills repaired due to invalid dates.</summary>
    public int InvalidDateRepaired { get; set; }
    /// <summary>Number of duplicate id conflicts corrected.</summary>
    public int DuplicateIdRepaired { get; set; }
    /// <summary>Number of category normalization repairs applied.</summary>
    public int CategoryRepaired { get; set; }
    /// <summary>Total number of bills scanned during the integrity run.</summary>
    public int BillsScanned { get; set; }

    /// <summary>True when any repairs were performed during the scan.</summary>
    public bool HasRepairs =>
        EmptyNameRepaired > 0
        || NegativeAmountRepaired > 0
        || InvalidDateRepaired > 0
        || DuplicateIdRepaired > 0
        || CategoryRepaired > 0;

    /// <summary>Return a compact diagnostic summary string for logging or display.</summary>
    public override string ToString()
    {
        return $"Scanned {BillsScanned} bill(s). Repairs: "
            + $"EmptyName={EmptyNameRepaired}, "
            + $"NegativeAmount={NegativeAmountRepaired}, "
            + $"InvalidDate={InvalidDateRepaired}, "
            + $"DuplicateId={DuplicateIdRepaired}, "
            + $"Category={CategoryRepaired}";
    }
}
