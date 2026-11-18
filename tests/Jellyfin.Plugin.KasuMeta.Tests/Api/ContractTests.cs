using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using YamlDotNet.RepresentationModel;

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
    [InlineData("search.json", typeof(SearchResponse))]
    [InlineData("translations.json", typeof(ItemTranslations))]
    [InlineData("status.json", typeof(KasuMetaStatus))]
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
        await db.Store.UpsertImagesAsync(
            [
                new(id, "es-ES", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/p.jpg", MetadataSources.Tmdb, Width: 1920, Height: 2560),
                new(id, "es-ES", LocalizedImageTypes.Logo, "https://image.tmdb.org/t/p/original/l.png", MetadataSources.Tmdb, Width: 558, Height: 92),
            ],
            CancellationToken.None);
        var service = new OverlayService(db.Store, new FakeConfig(new PluginConfiguration { EnabledLocales = ["es-MX"] }));
        var missing = Guid.Parse("0fb78f59088fbebaaa7ec9cfb39e3e6f");

        var batch = await service.BuildAsync([id, missing], [id, missing], "es-MX", MetadataFields.All, LocalizedImageTypes.All, CancellationToken.None);
        var produced = JsonNode.Parse(JsonSerializer.Serialize(batch, JsonDefaults.Options))!;
        var fixture = JsonNode.Parse(Read("batch.json"))!;

        Assert.Equal(Keys(fixture), Keys(produced));
        var producedItem = produced["Items"]![id.ToString("N")]!;
        var fixtureItem = fixture["Items"]![id.ToString("N")]!;
        Assert.Equal(Keys(fixtureItem), Keys(producedItem));
        Assert.Equal(Keys(fixtureItem["Name"]!), Keys(producedItem["Name"]!));
        Assert.Equal(Keys(fixtureItem["Images"]!), Keys(producedItem["Images"]!));
        Assert.Equal(Keys(fixtureItem["Images"]!["Primary"]!), Keys(producedItem["Images"]!["Primary"]!));
        Assert.Equal(fixture["Missing"]!.ToJsonString(), produced["Missing"]!.ToJsonString());
    }

    [Theory]
    [InlineData("KasuMetaInfo", typeof(KasuMetaInfo))]
    [InlineData("BatchRequest", typeof(BatchRequest))]
    [InlineData("BatchResponse", typeof(BatchResponse))]
    [InlineData("ItemOverlay", typeof(ItemOverlay))]
    [InlineData("LocalizedValue", typeof(LocalizedValue))]
    [InlineData("LocalizedImageValue", typeof(LocalizedImageValue))]
    [InlineData("SearchResponse", typeof(SearchResponse))]
    [InlineData("SearchResult", typeof(SearchResult))]
    [InlineData("ItemTranslations", typeof(ItemTranslations))]
    [InlineData("ItemFetchInfo", typeof(ItemFetchInfo))]
    [InlineData("ItemOverridesRequest", typeof(ItemOverridesRequest))]
    [InlineData("ManualFieldValue", typeof(ManualFieldValue))]
    [InlineData("ManualImageValue", typeof(ManualImageValue))]
    [InlineData("KasuMetaStatus", typeof(KasuMetaStatus))]
    [InlineData("ItemCounts", typeof(ItemCounts))]
    [InlineData("FetchProblem", typeof(FetchProblem))]
    [InlineData("ProviderCounters", typeof(ProviderCounters))]
    public void The_openapi_schema_lists_exactly_the_DTO_properties(string schema, Type type)
    {
        var properties = Spec()["components"]["schemas"][schema]["properties"];

        Assert.Equal(
            type.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal),
            ((YamlMappingNode)properties).Children.Keys.Select(k => ((YamlScalarNode)k).Value!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_openapi_examples_point_at_existing_fixtures()
    {
        var refs = Descendants(Spec())
            .OfType<YamlMappingNode>()
            .SelectMany(m => m.Children)
            .Where(kv => ((YamlScalarNode)kv.Key).Value == "$ref" && ((YamlScalarNode)kv.Value).Value!.StartsWith("fixtures/", StringComparison.Ordinal))
            .Select(kv => ((YamlScalarNode)kv.Value).Value!)
            .ToList();

        Assert.Equal(6, refs.Count);
        Assert.All(refs, r => Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Contract", Path.GetFileName(r))), r));
    }

    [Fact]
    public void An_override_body_binds_nulls_as_removals_and_locks_by_default()
    {
        const string body = """
            {
              "Fields": { "Name": { "Value": "Curada" }, "Tagline": { "Value": "Rápida", "Locked": false }, "Overview": null },
              "Images": { "Logo": null }
            }
            """;

        var request = JsonSerializer.Deserialize<ItemOverridesRequest>(body, JsonDefaults.Options)!;

        Assert.True(request.Fields!["Name"]!.Locked);
        Assert.False(request.Fields["Tagline"]!.Locked);
        Assert.True(request.Fields.ContainsKey("Overview"));
        Assert.Null(request.Fields["Overview"]);
        Assert.Null(request.Images!["Logo"]);
    }

    [Fact]
    public void Locale_keys_are_serialized_as_is()
    {
        var produced = JsonSerializer.Serialize(
            new ItemTranslations
            {
                ItemId = "1eb732044aec1079626f8d928d40e1bc",
                Locales = new Dictionary<string, ItemOverlay> { ["es-MX"] = new() { Name = new LocalizedValue("x", MetadataSources.Manual, "es-MX", true) } },
            },
            JsonDefaults.Options);

        Assert.Contains("\"es-MX\":", produced, StringComparison.Ordinal);
        Assert.DoesNotContain("Fetch", produced, StringComparison.Ordinal); // never fetched: omitted
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

    /// <summary>Parses api/openapi.yaml; a YAML error fails every test that uses it.</summary>
    private static YamlNode Spec()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Read("openapi.yaml"));
        stream.Load(reader);
        return stream.Documents[0].RootNode;
    }

    private static IEnumerable<YamlNode> Descendants(YamlNode node) =>
        node switch
        {
            YamlMappingNode m => m.Children.Values.SelectMany(Descendants).Prepend(node),
            YamlSequenceNode s => s.Children.SelectMany(Descendants).Prepend(node),
            _ => [node],
        };

    private static string[] Keys(JsonNode node) => [.. node.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
}
