namespace CrispyBills.Mobile.Android.Models;

public sealed class IntegrityCheckReport
{
    public int EmptyNameRepaired { get; set; }
    public int NegativeAmountRepaired { get; set; }
    public int InvalidDateRepaired { get; set; }
    public int DuplicateIdRepaired { get; set; }
    public int CategoryRepaired { get; set; }
    public int BillsScanned { get; set; }

    public bool HasRepairs =>
        EmptyNameRepaired > 0
        || NegativeAmountRepaired > 0
        || InvalidDateRepaired > 0
        || DuplicateIdRepaired > 0
        || CategoryRepaired > 0;

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
