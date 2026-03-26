using Microsoft.Maui.Controls;

namespace CrispyBills.Mobile.Android;

public sealed class SelectAllEntryBehavior : Behavior<Entry>
{
    protected override void OnAttachedTo(Entry bindable)
    {
        base.OnAttachedTo(bindable);
        bindable.Focused += OnFocused;
    }

    protected override void OnDetachingFrom(Entry bindable)
    {
        bindable.Focused -= OnFocused;
        base.OnDetachingFrom(bindable);
    }

    private static void OnFocused(object? sender, FocusEventArgs e)
    {
        if (sender is not Entry entry)
        {
            return;
        }

        entry.Dispatcher.Dispatch(() =>
        {
            var text = entry.Text ?? string.Empty;
            entry.CursorPosition = 0;
            entry.SelectionLength = text.Length;
        });
    }
}
