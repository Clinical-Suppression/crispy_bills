using System;

namespace CrispyBills.Mobile.Android.Models;

public sealed class BillListItem
{
    public Guid Id { get; }
    public string Name { get; }
    public decimal Amount { get; }
    public string Category { get; }
    public DateTime DueDate { get; }
    public bool IsPaid { get; }
    public bool IsPastDue { get; }

    public string StatusText => IsPaid ? "Paid" : (IsPastDue ? "Past Due" : "Open");

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
