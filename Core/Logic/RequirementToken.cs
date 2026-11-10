namespace ErRandoCompanion.Core.Logic;

public enum TokenKind
{
    Item,
    Check,
    Route,
    Setting,
}

/// <summary>
/// One entry in a "requires" list. Any form can be negated with a leading "!".
/// <list type="bullet">
/// <item>"Rusty Key": own the item</item>
/// <item>"Great Runes x2": own at least 2</item>
/// <item>"Great Runes x$leyndellRunes": own at least the number in that setting</item>
/// <item>"check:id": that location is checked</item>
/// <item>"route:id": that region or route is reachable</item>
/// <item>"setting:id": that bool setting is on</item>
/// <item>"setting:id=value": that setting has this value</item>
/// </list>
/// </summary>
public sealed record RequirementToken(
    TokenKind Kind,
    string Name,
    bool Negated = false,
    int Count = 1,
    string? CountSetting = null,
    string? SettingValue = null)
{
    public static RequirementToken Parse(string text)
    {
        var negated = text.StartsWith('!');
        var body = negated ? text[1..] : text;

        if (body.StartsWith("check:"))
            return new(TokenKind.Check, body["check:".Length..], negated);
        if (body.StartsWith("route:"))
            return new(TokenKind.Route, body["route:".Length..], negated);
        if (body.StartsWith("setting:"))
        {
            var parts = body["setting:".Length..].Split('=', 2);
            return new(TokenKind.Setting, parts[0], negated, SettingValue: parts.Length == 2 ? parts[1] : null);
        }

        var separator = body.LastIndexOf(" x", StringComparison.Ordinal);
        if (separator > 0)
        {
            var amount = body[(separator + 2)..];
            if (amount.StartsWith('$'))
                return new(TokenKind.Item, body[..separator], negated, CountSetting: amount[1..]);
            if (int.TryParse(amount, out var count))
                return new(TokenKind.Item, body[..separator], negated, count);
        }

        return new(TokenKind.Item, body, negated);
    }
}
