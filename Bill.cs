using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrispyBills
{
    /// <summary>
    /// Represents a single bill record tracked by the application.
    /// Notifies listeners when properties change so UI can update.
    /// </summary>
    public class Bill : INotifyPropertyChanged
    {
        private Guid _id = Guid.NewGuid();
        /// <summary>Unique identifier for the bill.</summary>
        public Guid Id { get => _id; set { if (_id != value) { _id = value; OnPropertyChanged(); } } }

        private string _name = "";
        /// <summary>Display name or description of the bill.</summary>
        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(); } } }

        private decimal _amount;
        /// <summary>Monetary amount due for the bill.</summary>
        public decimal Amount { get => _amount; set { if (_amount != value) { _amount = value; OnPropertyChanged(); } } }

        private string _category = "General";
        /// <summary>Category used to group similar bills (e.g. Utilities, Housing).</summary>
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(); } } }

        private DateTime _dueDate = DateTime.Now;
        /// <summary>The date the bill is due.</summary>
        public DateTime DueDate
        {
            get => _dueDate;
            set
            {
                if (_dueDate != value)
                {
                    _dueDate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPastDue));
                }
            }
        }

        private bool _isPaid;
        /// <summary>Whether the bill has been marked paid.</summary>
        public bool IsPaid
        {
            get => _isPaid;
            set
            {
                if (_isPaid != value)
                {
                    _isPaid = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPastDue));
                }
            }
        }

        private bool _isRecurring;
        /// <summary>Whether the bill recurs each period.</summary>
        public bool IsRecurring
        {
            get => _isRecurring;
            set
            {
                if (_isRecurring != value)
                {
                    _isRecurring = value;
                    OnPropertyChanged();
                }
            }
        }

        private DateTime _contextPeriodStart = new(DateTime.Now.Year, DateTime.Now.Month, 1);
        /// <summary>
        /// The start of the currently-viewed context period (normalized to the month first day).
        /// Used when computing past-due status relative to a viewing period.
        /// </summary>
        public DateTime ContextPeriodStart
        {
            get => _contextPeriodStart;
            set
            {
                var normalized = new DateTime(value.Year, value.Month, 1);
                if (_contextPeriodStart != normalized)
                {
                    _contextPeriodStart = normalized;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPastDue));
                }
            }
        }

        /// <summary>True when the bill is unpaid and past the due or context start date.</summary>
        public bool IsPastDue => !IsPaid &&
            (DueDate.Date < DateTime.Today || DueDate.Date < ContextPeriodStart.Date);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
