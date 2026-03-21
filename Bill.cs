using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrispyBills
{
    public class Bill : INotifyPropertyChanged
    {
        private Guid _id = Guid.NewGuid();
        public Guid Id { get => _id; set { if (_id != value) { _id = value; OnPropertyChanged(); } } }

        private string _name = "";
        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(); } } }

        private decimal _amount;
        public decimal Amount { get => _amount; set { if (_amount != value) { _amount = value; OnPropertyChanged(); } } }

        private string _category = "General";
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(); } } }

        private DateTime _dueDate = DateTime.Now;
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

        public bool IsPastDue => !IsPaid &&
            (DueDate.Date < DateTime.Today || DueDate.Date < ContextPeriodStart.Date);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
