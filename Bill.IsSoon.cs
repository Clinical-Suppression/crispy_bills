namespace CrispyBills
{
    public partial class Bill
    {
        private bool _isSoon;

        /// <summary>True when the bill is unpaid, not past due, and within the "due soon" threshold.</summary>
        public bool IsSoon
        {
            get => _isSoon;
            set
            {
                if (_isSoon != value)
                {
                    _isSoon = value;
                    OnPropertyChanged();
                }
            }
        }
    }
}
