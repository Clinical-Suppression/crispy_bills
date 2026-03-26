using System;
using Microsoft.Maui.Graphics;

namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Read-only projection of a bill suitable for list display.
/// Contains computed status text and badge colors for touch UI.
/// </summary>
public sealed class BillListItem
{
    public Guid Id { get; }
    public string Name { get; }
    public decimal Amount { get; }
    /// <summary>Pre-formatted amount for binding (culture-aware currency).</summary>
    public string AmountDisplay { get; }
    public string Category { get; }
    public DateTime DueDate { get; }
    public bool IsPaid { get; }
    public bool IsPastDue { get; }
    public bool IsSoon { get; }

    /// <summary>Short status label used in list views.</summary>
    public string StatusText => IsPaid ? "Paid" : (IsPastDue ? "Past Due" : (IsSoon ? "Soon" : "Open"));

    /// <summary>Background for the status pill (legacy; row tint uses <see cref="CardRowBackground"/>).</summary>
    public Color StatusBadgeBackground { get; }

    /// <summary>Full row background tint (paid / past due / soon / open).</summary>
    public Color CardRowBackground { get; }

    /// <summary>Foreground for the status pill.</summary>
    public Color StatusBadgeTextColor { get; }

    /// <summary>Constructs a projection from a <see cref="BillItem"/> instance.</summary>
    /// <param name="bill">Source bill to project.</param>
    /// <param name="amountDisplay">Optional formatted currency string; defaults to invariant F2.</param>
    public BillListItem(BillItem bill, string? amountDisplay = null, bool isSoon = false)
    {
        Id = bill.Id;
        Name = bill.Name;
        Amount = bill.Amount;
        AmountDisplay = amountDisplay ?? bill.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        Category = bill.Category;
        DueDate = bill.DueDate;
        IsPaid = bill.IsPaid;
        IsPastDue = bill.IsPastDue;
        IsSoon = isSoon;

        if (bill.IsPaid)
        {
            StatusBadgeBackground = Color.FromArgb("#DCFCE7");
            StatusBadgeTextColor = Color.FromArgb("#16A34A");
        }
        else if (bill.IsPastDue)
        {
            StatusBadgeBackground = Color.FromArgb("#FEE2E2");
            StatusBadgeTextColor = Color.FromArgb("#991B1B");
        }
        else if (IsSoon)
        {
            StatusBadgeBackground = Color.FromArgb("#FEF3C7");
            StatusBadgeTextColor = Color.FromArgb("#92400E");
        }
        else
        {
            StatusBadgeBackground = Color.FromArgb("#DBEAFE");
            StatusBadgeTextColor = Color.FromArgb("#1E3A8A");
        }

        // Full-row tint matches status family; inner chip uses transparent overlay in XAML.
        CardRowBackground = StatusBadgeBackground;
    }
}
