using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Tests;

public class GameDataTests
{
    [Fact]
    public void ShippedData_LoadsAndValidates()
    {
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        Assert.Equal(338, data.Locations.Count);
        Assert.Empty(data.Validate());
    }

    [Fact]
    public void Load_KeepsDefaultsForFieldsAFileLeavesOut()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "regions.json"), """[ { "id": "r", "name": "R" } ]""");
            File.WriteAllText(Path.Combine(directory, "items.json"), """[ { "name": "Key", "category": "key" } ]""");
            File.WriteAllText(Path.Combine(directory, "settings.json"), """[ { "id": "s", "name": "S", "type": "bool", "default": false } ]""");
            File.WriteAllText(Path.Combine(directory, "locations.json"), """[ { "id": "l", "name": "L", "region": "r", "category": "majorBoss" } ]""");

            var data = GameData.Load(directory);
            Assert.Equal(1, data.Items[0].MaxCount);
            Assert.Equal("", data.Items[0].Notes);
            Assert.Empty(data.Settings[0].Options);
            Assert.Empty(data.Regions[0].Requires);
            Assert.Equal("", data.Locations[0].Details);
            Assert.Empty(data.Locations[0].LotIds);
            Assert.Empty(data.Locations[0].Requires);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ReportsEachKindOfMistake()
    {
        var data = new GameData
        {
            Regions = [new Region { Id = "r", Name = "R" }],
            Items = [new Item { Name = "Key", Category = "key" }],
            Settings = [],
            Locations =
            [
                new Location { Id = "dup", Name = "A", Region = "r", Category = LocationCategory.MajorBoss },
                new Location { Id = "dup", Name = "B", Region = "nowhere", Category = LocationCategory.MajorBoss },
                new Location
                {
                    Id = "c",
                    Name = "C",
                    Region = "r",
                    Category = LocationCategory.MajorBoss,
                    Requires = [["Missing Item", "Key x2", "check:gone", "route:gone", "setting:gone"]],
                },
            ],
        };

        var errors = string.Join("\n", data.Validate());
        Assert.Contains("Duplicate location id: 'dup'", errors);
        Assert.Contains("unknown region 'nowhere'", errors);
        Assert.Contains("'Missing Item': unknown item", errors);
        Assert.Contains("'Key x2': count is higher", errors);
        Assert.Contains("'check:gone': unknown location", errors);
        Assert.Contains("'route:gone': unknown region", errors);
        Assert.Contains("'setting:gone': unknown setting", errors);
    }

    [Fact]
    public void Load_ReportsMissingFiles()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var error = Assert.Throws<GameDataException>(() => GameData.Load(missing));
        Assert.Equal(4, error.Errors.Count);
    }
}
