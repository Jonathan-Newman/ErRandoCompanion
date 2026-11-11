using System.Text.RegularExpressions;

namespace ErRandoCompanion.Core.AllChecks;

/// <summary>
/// The randomizer's own logic, from its diste\Base\annotations.txt: the areas of the world, what each needs ("Req:"),
/// boss events, option switches and key items. Read once per randomizer folder; holds no seed data.
/// </summary>
public sealed partial class RandomizerLogic
{
    /// <summary>Requirement expressions by area or event name.</summary>
    public required IReadOnlyDictionary<string, string> Requirements { get; init; }

    /// <summary>The name each area is shown with ("Stormveil Castle", "Limgrave - Fringefolk Hero's Grave").</summary>
    public required IReadOnlyDictionary<string, string> AreaNames { get; init; }

    /// <summary>Config variables ("imbued_base: imbuedswordkey1 AND ..."), by name.</summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>Groups of options of which exactly one is on; the first is the default.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> OneOf { get; init; }

    /// <summary>Options that are on only when the seed says so ("dlc").</summary>
    public required IReadOnlySet<string> Optional { get; init; }

    /// <summary>Key item config names ("rustykey", "runegodrick", "imbuedswordkey2") and the item each names.</summary>
    public required IReadOnlyDictionary<string, string> ItemNames { get; init; }

    public static RandomizerLogic Load(string annotationsPath) => Parse(File.ReadAllText(annotationsPath));

    public static RandomizerLogic Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        var sections = TopLevel().Split(text);
        // Split keeps the captured section names: "", name, body, name, body...
        var bodies = new Dictionary<string, string>();
        for (var i = 1; i + 1 < sections.Length; i += 2)
            bodies.TryAdd(sections[i], sections[i + 1]);

        var items = new Dictionary<string, string>();
        foreach (Match m in ConfigItem().Matches(bodies.GetValueOrDefault("ConfigItems", "")))
        {
            var name = Unquote(m.Groups[2].Value);
            var config = m.Groups[1].Success ? m.Groups[1].Value : Normalize(name);
            items.TryAdd(config, name);
        }

        var oneOf = new List<IReadOnlyList<string>>();
        var optional = new HashSet<string>();
        foreach (var block in bodies.GetValueOrDefault("Config", "").Split("\n- ").Skip(1))
        {
            if (block.StartsWith("Opt: ", StringComparison.Ordinal))
                optional.Add(block[5..].Trim());
            else if (block.StartsWith("Oneof:", StringComparison.Ordinal))
                oneOf.Add(block.Split('\n').Skip(1).Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l.Length > 0).ToList());
        }

        var variables = new Dictionary<string, string>();
        foreach (var line in bodies.GetValueOrDefault("ConfigVars", "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (line.StartsWith("  ", StringComparison.Ordinal) && colon > 0)
                variables[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var requirements = new Dictionary<string, string>();
        var areaNames = new Dictionary<string, string>();
        foreach (var section in new[] { "Events", "Areas" })
        {
            foreach (var block in bodies.GetValueOrDefault(section, "").Split("\n- Name: ").Select(b => b.StartsWith("- Name: ") ? b[8..] : b))
            {
                var name = block.Split('\n', 2)[0].Trim();
                if (name.Length == 0)
                    continue;
                requirements[name] = Field(block, "Req") ?? "";
                if (section == "Areas")
                    areaNames[name] = Field(block, "Text") ?? name;
            }
        }

        return new RandomizerLogic
        {
            Requirements = requirements,
            AreaNames = areaNames,
            Variables = variables,
            OneOf = oneOf,
            Optional = optional,
            ItemNames = items,
        };
    }

    /// <summary>"Pureblood Knight's Medal" to "purebloodknightsmedal", the randomizer's config name for an item.</summary>
    public static string Normalize(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static string? Field(string block, string field)
    {
        var m = Regex.Match(block, $@"(?:^|\n)  {field}: ([^\n]*)");
        return m.Success ? Unquote(m.Groups[1].Value.Trim()) : null;
    }

    internal static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            return text[1..^1].Replace("''", "'");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return text[1..^1];
        return text;
    }

    [GeneratedRegex(@"^([A-Za-z]+):[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex TopLevel();

    [GeneratedRegex(@"\n *- (?:ConfigName: (\w+)\n +)?Name: ([^\n]+)")]
    private static partial Regex ConfigItem();
}

/// <summary>
/// A requirement expression: names joined by AND / OR with brackets. Names side by side with no operator
/// ("godrick thestormhawkking") all have to hold.
/// </summary>
public abstract record Requirement
{
    public sealed record Name(string Value) : Requirement;
    public sealed record All(IReadOnlyList<Requirement> Parts) : Requirement;
    public sealed record Any(IReadOnlyList<Requirement> Parts) : Requirement;

    public static readonly Requirement Always = new All([]);

    public static Requirement Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Always;
        var tokens = text.Replace("(", " ( ").Replace(")", " ) ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        var result = ParseOr(tokens, ref at);
        return result;
    }

    private static Requirement ParseOr(string[] tokens, ref int at)
    {
        var parts = new List<Requirement> { ParseAnd(tokens, ref at) };
        while (at < tokens.Length && tokens[at] == "OR")
        {
            at++;
            parts.Add(ParseAnd(tokens, ref at));
        }
        return parts.Count == 1 ? parts[0] : new Any(parts);
    }

    private static Requirement ParseAnd(string[] tokens, ref int at)
    {
        var parts = new List<Requirement>();
        while (at < tokens.Length && tokens[at] is not ("OR" or ")"))
        {
            if (tokens[at] == "AND")
            {
                at++;
                continue;
            }
            if (tokens[at] == "(")
            {
                at++;
                parts.Add(ParseOr(tokens, ref at));
                if (at < tokens.Length && tokens[at] == ")")
                    at++;
                continue;
            }
            parts.Add(new Name(tokens[at++]));
        }
        return parts.Count == 1 ? parts[0] : new All(parts);
    }
}
