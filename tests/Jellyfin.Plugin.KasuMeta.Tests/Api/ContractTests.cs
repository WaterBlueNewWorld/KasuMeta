using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

/// <summary>
/// Keeps the DTOs and api/fixtures (captured from a live 12.1 server) in step, using the same
/// serializer settings Jellyfin uses (<see cref="JsonDefaults.Options"/>). A renamed or retyped
/// property breaks these, and with them every client built from the fixtures.
/// </summary>
public class ContractTests
{
    [Theory]
    [InlineData("info.json", typeof(KasuMetaInfo))]
    [InlineData("batch.json", typeof(BatchResponse))]
    [InlineData("item.json", typeof(ItemOverlay))]
    public void Fixtures_round_trip_through_the_DTOs_unchanged(string fixture, Type type)
    {
        var json = Read(fixture);

        var dto = JsonSerializer.Deserialize(json, type, JsonDefaults.Options);
        var again = JsonSerializer.Serialize(dto, type, JsonDefaults.Options);

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(again)),
            $"{fixture} no longer matches {type.Name}:\n{again}");
    }

    [Fact]
    public async Task The_service_output_has_the_shape_of_the_fixture()
    {
        using var db = new TestDatabase();
        var id = Guid.Parse("1eb732044aec1079626f8d928d40e1bc");
        await db.Store.UpsertFieldsAsync(
            [
                new(id, "es-MX", MetadataFields.Name, "Amenaza Virtual", MetadataSources.Tmdb),
                new(id, "es-MX", MetadataFields.Overview, "Milo Hoffman…", MetadataSources.Tmdb),
                new(id, "es-MX", MetadataFields.Tagline, "La verdad puede ser peligrosa…", MetadataSources.Tmdb),
            ],
            CancellationToken.None);
        var service = new OverlayService(db.Store, new FakeConfig(new PluginConfiguration { EnabledLocales = ["es-MX"] }));
        var missing = Guid.Parse("0fb78f59088fbebaaa7ec9cfb39e3e6f");

        var batch = await service.BuildAsync([id, missing], [id, missing], "es-MX", MetadataFields.All, CancellationToken.None);
        var produced = JsonNode.Parse(JsonSerializer.Serialize(batch, JsonDefaults.Options))!;
        var fixture = JsonNode.Parse(Read("batch.json"))!;

        Assert.Equal(Keys(fixture), Keys(produced));
        var producedItem = produced["Items"]![id.ToString("N")]!;
        var fixtureItem = fixture["Items"]![id.ToString("N")]!;
        Assert.Equal(Keys(fixtureItem), Keys(producedItem));
        Assert.Equal(Keys(fixtureItem["Name"]!), Keys(producedItem["Name"]!));
        Assert.Equal(fixture["Missing"]!.ToJsonString(), produced["Missing"]!.ToJsonString());
    }

    [Fact]
    public void Absent_fields_are_omitted_not_null()
    {
        var json = JsonSerializer.Serialize(
            new ItemOverlay { Name = new LocalizedValue("x", MetadataSources.Tmdb, "es-MX", false) },
            JsonDefaults.Options);

        Assert.DoesNotContain("Overview", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contract", name));

    private static string[] Keys(JsonNode node) => [.. node.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
}
