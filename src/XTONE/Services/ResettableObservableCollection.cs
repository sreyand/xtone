using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace XTONE.Services;

/// <summary>Publishes one reset for a prepared list instead of one event per imported row.</summary>
public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        var replacement = values.ToArray();
        if (Count == replacement.Length && this.SequenceEqual(replacement)) return;
        CheckReentrancy(); Items.Clear();
        foreach (var value in replacement) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

