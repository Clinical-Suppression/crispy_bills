using System;

namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Read-only projection of a bill suitable for list display.
/// Contains computed status text for quick UI consumption.
/// </summary>
public sealed class BillListItem
{
    public Guid Id { get; }
    public string Name { get; }
    public decimal Amount { get; }
    public string Category { get; }
    public DateTime DueDate { get; }
    public bool IsPaid { get; }
    public bool IsPastDue { get; }

    /// <summary>Short status label used in list views.</summary>
    public string StatusText => IsPaid ? "Paid" : (IsPastDue ? "Past Due" : "Open");

    /// <summary>Constructs a projection from a <see cref="BillItem"/> instance.</summary>
    /// <param name="bill">Source bill to project.</param>
    public BillListItem(BillItem bill)
    {
        Id = bill.Id;
        Name = bill.Name;
        Amount = bill.Amount;
        Category = bill.Category;
        DueDate = bill.DueDate;
        IsPaid = bill.IsPaid;
        IsPastDue = bill.IsPastDue;
    }
}
