using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Lightweight bill model used by the mobile UI and data layer.
/// Provides change notification for binding scenarios and a <see cref="Clone"/> helper.
/// </summary>
public sealed class BillItem : INotifyPropertyChanged
{
    private bool _isPaid;
    private DateTime _dueDate;

    /// <summary>Identifier for this bill instance.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Bill name or short description.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Amount due for this bill.</summary>
    public decimal Amount { get; set; }
    /// <summary>Category for grouping and reporting.</summary>
    public string Category { get; set; } = "General";
    public bool IsRecurring { get; set; }
    public int RecurrenceEveryMonths { get; set; } = 1;
    public RecurrenceEndMode RecurrenceEndMode { get; set; } = RecurrenceEndMode.None;
    public DateTime? RecurrenceEndDate { get; set; }
    public int? RecurrenceMaxOccurrences { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }

    /// <summary>The date the bill is due.</summary>
    public DateTime DueDate
    {
        get => _dueDate;
        set
        {
            if (_dueDate == value)
            {
                return;
            }

            _dueDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPastDue));
        }
    }

    /// <summary>Whether the bill has been paid.</summary>
    public bool IsPaid
    {
        get => _isPaid;
        set
        {
            if (_isPaid == value)
            {
                return;
            }

            _isPaid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPastDue));
        }
    }

    /// <summary>True if the bill is unpaid and the due date has passed.</summary>
    public bool IsPastDue => !IsPaid && DueDate.Date < DateTime.Today;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a deep copy of the bill suitable for creating editable drafts or copies.</summary>
    /// <returns>A new <see cref="BillItem"/> with the same values.</returns>
    public BillItem Clone()
    {
        return new BillItem
        {
            Id = Id,
            Name = Name,
            Amount = Amount,
            Category = Category,
            DueDate = DueDate,
            IsPaid = IsPaid,
            IsRecurring = IsRecurring,
            RecurrenceEveryMonths = RecurrenceEveryMonths,
            RecurrenceEndMode = RecurrenceEndMode,
            RecurrenceEndDate = RecurrenceEndDate,
            RecurrenceMaxOccurrences = RecurrenceMaxOccurrences,
            Year = Year,
            Month = Month
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
