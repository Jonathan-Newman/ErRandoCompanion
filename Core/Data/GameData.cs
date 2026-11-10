using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ErRandoCompanion.Core.Json;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Data;

public sealed class GameDataException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Everything in the Data folder, loaded and cross-checked.</summary>
public sealed class GameData
{
    public required IReadOnlyList<Location> Locations { get; init; }
    public required IReadOnlyList<Region> Regions { get; init; }
    public required IReadOnlyList<Item> Items { get; init; }
    public required IReadOnlyList<RandomizerSetting> Settings { get; init; }

    /// <exception cref="GameDataException">A file is unreadable or the data is inconsistent.</exception>
    public static GameData Load(string dataDirectory)
    {
        var errors = new List<string>();
        var data = new GameData
        {
            Locations = ReadList(dataDirectory, "locations.json", DataJsonContext.Default.ListLocation, errors),
            Regions = ReadList(dataDirectory, "regions.json", DataJsonContext.Default.ListRegion, errors),
            Items = ReadList(dataDirectory, "items.json", DataJsonContext.Default.ListItem, errors),
            Settings = ReadList(dataDirectory, "settings.json", DataJsonContext.Default.ListRandomizerSetting, errors),
        };

        if (errors.Count == 0)
            errors.AddRange(data.Validate());
        if (errors.Count > 0)
            throw new GameDataException(errors);
        return data;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var locations = UniqueKeys(Locations.Select(l => l.Id), "location id", errors);
        var regions = UniqueKeys(Regions.Select(r => r.Id), "region id", errors);
        var items = UniqueKeys(Items.Select(i => i.Name), "item name", errors);
        var settings = UniqueKeys(Settings.Select(s => s.Id), "setting id", errors);
        var itemsByName = Items.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());
        var settingsById = Settings.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

        foreach (var item in Items)
        {
            if (item.OnlyIfSetting is { } setting && !settings.Contains(setting))
                errors.Add($"Item '{item.Name}': unknown setting '{setting}'");
        }

        foreach (var location in Locations)
        {
            if (!regions.Contains(location.Region))
                errors.Add($"Location '{location.Id}': unknown region '{location.Region}'");
            CheckRequires($"Location '{location.Id}'", location.Requires);
        }

        foreach (var region in Regions)
            CheckRequires($"Region '{region.Id}'", region.Requires);

        return errors;

        void CheckRequires(string owner, IReadOnlyList<IReadOnlyList<string>> requires)
        {
            foreach (var text in requires.SelectMany(option => option))
            {
                var error = CheckToken(RequirementToken.Parse(text));
                if (error is not null)
                    errors.Add($"{owner}: '{text}': {error}");
            }
        }

        string? CheckToken(RequirementToken token)
        {
            switch (token.Kind)
            {
                case TokenKind.Check:
                    return locations.Contains(token.Name) ? null : "unknown location";
                case TokenKind.Route:
                    return regions.Contains(token.Name) ? null : "unknown region";
                case TokenKind.Setting:
                    if (!settingsById.TryGetValue(token.Name, out var setting))
                        return "unknown setting";
                    if (token.SettingValue is null)
                        return setting.Type == SettingType.Bool ? null : "needs '=value' because this is not a bool setting";
                    if (setting.Type == SettingType.Count)
                        return int.TryParse(token.SettingValue, out _) ? null : "value must be a number";
                    return setting.Options.Contains(token.SettingValue) ? null : "value is not one of the setting's options";
                default:
                    if (!itemsByName.TryGetValue(token.Name, out var item))
                        return "unknown item";
                    if (token.CountSetting is { } countSetting)
                    {
                        if (!settingsById.TryGetValue(countSetting, out var source) || source.Type != SettingType.Count)
                            return "count must name a count setting";
                        return source.Max <= item.MaxCount ? null : "setting can ask for more than the item's maxCount";
                    }
                    return token.Count <= item.MaxCount ? null : "count is higher than the item's maxCount";
            }
        }
    }

    private static List<T> ReadList<T>(string directory, string fileName, JsonTypeInfo<List<T>> type, List<string> errors)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, fileName));
            return JsonSerializer.Deserialize(stream, type) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            errors.Add($"{fileName}: {e.Message}");
            return [];
        }
    }

    private static HashSet<string> UniqueKeys(IEnumerable<string> keys, string description, List<string> errors)
    {
        var seen = new HashSet<string>();
        foreach (var key in keys)
        {
            if (!seen.Add(key))
                errors.Add($"Duplicate {description}: '{key}'");
        }
        return seen;
    }
}
