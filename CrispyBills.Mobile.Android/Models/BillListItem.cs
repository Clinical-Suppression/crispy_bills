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

    /// <summary>Name and amount on the row; chosen for contrast against <see cref="CardRowBackground"/>.</summary>
    public Color PrimaryRowTextColor { get; }

    /// <summary>Category and due line; chosen for contrast against <see cref="CardRowBackground"/>.</summary>
    public Color SecondaryRowTextColor { get; }

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
            StatusBadgeBackground = dark ? Color.FromArgb("#2A3D34") : Color.FromArgb("#E8F5EE");
            StatusBadgeTextColor = dark ? Color.FromArgb("#86EFAC") : Color.FromArgb("#15803D");
            CardRowBackground = dark ? Color.FromArgb("#4A524E") : Color.FromArgb("#FDFEFC");
            PrimaryRowTextColor = dark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0A0F1A");
            SecondaryRowTextColor = dark ? Color.FromArgb("#E2E8F0") : Color.FromArgb("#334155");
        }
        else if (bill.IsPastDue)
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#4A3E3E") : Color.FromArgb("#FEECEC");
            StatusBadgeTextColor = dark ? Color.FromArgb("#FCA5A5") : Color.FromArgb("#B91C1C");
            CardRowBackground = dark ? Color.FromArgb("#504648") : Color.FromArgb("#FFFDFD");
            PrimaryRowTextColor = dark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0A0F1A");
            SecondaryRowTextColor = dark ? Color.FromArgb("#E2E8F0") : Color.FromArgb("#334155");
        }
        else if (IsSoon)
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#4A4438") : Color.FromArgb("#FEF8E8");
            StatusBadgeTextColor = dark ? Color.FromArgb("#FCD34D") : Color.FromArgb("#A16207");
            CardRowBackground = dark ? Color.FromArgb("#4E4A42") : Color.FromArgb("#FFFEF8");
            PrimaryRowTextColor = dark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0A0F1A");
            SecondaryRowTextColor = dark ? Color.FromArgb("#E2E8F0") : Color.FromArgb("#334155");
        }
        else
        {
            StatusBadgeBackground = dark ? Color.FromArgb("#384458") : Color.FromArgb("#E8EEF8");
            StatusBadgeTextColor = dark ? Color.FromArgb("#93C5FD") : Color.FromArgb("#1D4ED8");
            CardRowBackground = dark ? Color.FromArgb("#454A58") : Color.FromArgb("#FAFCFF");
            PrimaryRowTextColor = dark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0A0F1A");
            SecondaryRowTextColor = dark ? Color.FromArgb("#E2E8F0") : Color.FromArgb("#334155");
        }
    }
}
