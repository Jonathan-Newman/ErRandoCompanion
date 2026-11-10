using ErRandoCompanion.Core.Logic;

namespace ErRandoCompanion.Core.Tests;

public class RequirementTokenTests
{
    [Fact]
    public void ParsesEveryForm()
    {
        Assert.Equal(new RequirementToken(TokenKind.Item, "Rusty Key"), RequirementToken.Parse("Rusty Key"));
        Assert.Equal(new RequirementToken(TokenKind.Item, "Great Runes", Count: 2), RequirementToken.Parse("Great Runes x2"));
        Assert.Equal(new RequirementToken(TokenKind.Item, "Great Runes", CountSetting: "leyndellRunes"), RequirementToken.Parse("Great Runes x$leyndellRunes"));
        Assert.Equal(new RequirementToken(TokenKind.Check, "some_id"), RequirementToken.Parse("check:some_id"));
        Assert.Equal(new RequirementToken(TokenKind.Route, "altus"), RequirementToken.Parse("route:altus"));
        Assert.Equal(new RequirementToken(TokenKind.Setting, "deathless"), RequirementToken.Parse("setting:deathless"));
        Assert.Equal(new RequirementToken(TokenKind.Setting, "startMode", SettingValue: "normal"), RequirementToken.Parse("setting:startMode=normal"));
    }

    [Fact]
    public void ParsesNegation()
    {
        Assert.Equal(new RequirementToken(TokenKind.Item, "Great Runes", Negated: true), RequirementToken.Parse("!Great Runes x1"));
        Assert.True(RequirementToken.Parse("!setting:deathless").Negated);
    }

    [Fact]
    public void KeepsItemNamesThatMerelyContainX()
    {
        Assert.Equal(new RequirementToken(TokenKind.Item, "Lux Ruins xtra"), RequirementToken.Parse("Lux Ruins xtra"));
    }
}
