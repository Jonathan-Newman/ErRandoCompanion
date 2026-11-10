namespace ErRandoCompanion.App.ViewModels;

/// <summary>A boss in the Run goal tab's list for the "defeat chosen bosses" goal. Never says where the boss is.</summary>
public sealed class BossChoiceViewModel(string id, string label, Action<string> toggle) : ObservableObject
{
    private bool _isChosen;
    private bool _defeated;

    public string Id { get; } = id;

    /// <summary>"Rennala  (Academy of Raya Lucaria)": the boss and its home region.</summary>
    public string Label { get; } = label;

    /// <summary>Ticking or unticking changes the run's goal.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (value == _isChosen)
                return;
            toggle(Id);
        }
    }

    public bool Defeated
    {
        get => _defeated;
        private set => Set(ref _defeated, value);
    }

    /// <summary>Shows the goal's state without changing it.</summary>
    public void Update(bool chosen, bool defeated)
    {
        Set(ref _isChosen, chosen, nameof(IsChosen));
        Defeated = chosen && defeated;
    }
}
