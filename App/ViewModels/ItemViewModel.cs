using System.Windows.Input;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.App.ViewModels;

public sealed class ItemViewModel : ObservableObject
{
    private readonly Item _item;
    private readonly Action<Item, int> _setCount;
    private int _count;
    private bool _isRelevant = true;

    public ItemViewModel(Item item, Action<Item, int> setCount)
    {
        _item = item;
        _setCount = setCount;
        Increase = new RelayCommand(() => _setCount(_item, _count + 1), () => _count < _item.MaxCount);
        Decrease = new RelayCommand(() => _setCount(_item, _count - 1), () => _count > 0);
    }

    public Item Item => _item;
    public string Name => _item.Name;
    public string Notes => _item.Notes;
    public bool IsCounted => _item.MaxCount > 1;
    public bool IsSingle => !IsCounted;
    public ICommand Increase { get; }
    public ICommand Decrease { get; }

    public bool IsOwned
    {
        get => _count > 0;
        set => _setCount(_item, value ? 1 : 0);
    }

    public string CountText => $"{_count} / {_item.MaxCount}";

    public bool IsRelevant
    {
        get => _isRelevant;
        private set => Set(ref _isRelevant, value);
    }

    public void Update(int count, bool isRelevant)
    {
        IsRelevant = isRelevant;
        if (_count == count)
            return;
        _count = count;
        Raise(nameof(IsOwned));
        Raise(nameof(CountText));
    }
}
