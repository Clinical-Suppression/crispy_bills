using System;
using Microsoft.Maui.Controls;
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

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        if (bill.IsPaid)
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#143D2C") : Color.FromArgb("#DCFCE7");
            StatusBadgeTextColor = dark ? Color.FromArgb("#86EFAC") : Color.FromArgb("#16A34A");
            // Full row: lighter wash than the pill so lists feel less heavy.
            CardRowBackground = dark ? Color.FromArgb("#0F241C") : Color.FromArgb("#F0FDF4");
        }
        else if (bill.IsPastDue)
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#3F1D1D") : Color.FromArgb("#FEE2E2");
            StatusBadgeTextColor = dark ? Color.FromArgb("#FCA5A5") : Color.FromArgb("#DC2626");
            CardRowBackground = dark ? Color.FromArgb("#2A1818") : Color.FromArgb("#FFF5F5");
        }
        else if (IsSoon)
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#3D3420") : Color.FromArgb("#FEF3C7");
            StatusBadgeTextColor = dark ? Color.FromArgb("#FCD34D") : Color.FromArgb("#92400E");
            CardRowBackground = dark ? Color.FromArgb("#241F14") : Color.FromArgb("#FFFBEB");
        }
        else
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#1E2A3D") : Color.FromArgb("#DBEAFE");
            StatusBadgeTextColor = dark ? Color.FromArgb("#93C5FD") : Color.FromArgb("#1E3A8A");
            CardRowBackground = dark ? Color.FromArgb("#141C28") : Color.FromArgb("#F0F9FF");
        }
    }
}
