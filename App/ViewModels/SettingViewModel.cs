using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.App.ViewModels;

public sealed record SettingOption(string Value, string Label);

public sealed class SettingViewModel : ObservableObject
{
    private readonly RandomizerSetting _setting;
    private readonly Action<RandomizerSetting, string> _setValue;
    private string _value = "";

    public SettingViewModel(RandomizerSetting setting, Action<RandomizerSetting, string> setValue)
    {
        _setting = setting;
        _setValue = setValue;
        Options = setting.Type switch
        {
            SettingType.Count => Enumerable.Range(0, setting.Max + 1).Select(n => new SettingOption($"{n}", $"{n}")).ToList(),
            SettingType.Choice => setting.Options.Zip(setting.OptionNames, (v, l) => new SettingOption(v, l)).ToList(),
            _ => [],
        };
    }

    public string Id => _setting.Id;
    public string Name => _setting.Name;
    public string Notes => _setting.Notes;
    public bool HasNotes => Notes.Length > 0;
    public bool IsBool => _setting.Type == SettingType.Bool;
    public bool IsList => !IsBool;
    public IReadOnlyList<SettingOption> Options { get; }

    public bool BoolValue
    {
        get => _value == "true";
        set => _setValue(_setting, value ? "true" : "false");
    }

    public string ListValue
    {
        get => _value;
        set
        {
            // WPF clears the selection while a seed switch rebuilds bindings; that is not a choice.
            if (!string.IsNullOrEmpty(value))
                _setValue(_setting, value);
        }
    }

    public void Update(string value)
    {
        if (_value == value)
            return;
        _value = value;
        Raise(nameof(BoolValue));
        Raise(nameof(ListValue));
    }
}
