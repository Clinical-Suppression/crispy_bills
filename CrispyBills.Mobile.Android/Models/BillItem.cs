using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrispyBills.Mobile.Android.Models;

public sealed class BillItem : INotifyPropertyChanged
{
    private bool _isPaid;
    private DateTime _dueDate;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Category { get; set; } = "General";
    public bool IsRecurring { get; set; }
    public int RecurrenceEveryMonths { get; set; } = 1;
    public RecurrenceEndMode RecurrenceEndMode { get; set; } = RecurrenceEndMode.None;
    public DateTime? RecurrenceEndDate { get; set; }
    public int? RecurrenceMaxOccurrences { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }

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

    public bool IsPastDue => !IsPaid && DueDate.Date < DateTime.Today;

    public event PropertyChangedEventHandler? PropertyChanged;

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
