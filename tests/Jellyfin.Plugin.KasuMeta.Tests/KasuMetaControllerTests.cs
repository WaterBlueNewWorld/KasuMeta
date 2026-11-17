using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Tests;

public sealed class KasuMetaControllerTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid UserId = Guid.Parse("5eb3da7c3bf744ffa98cb4c5c3011705");
    private static readonly Guid Visible = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Restricted = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeConfig _config = new(new PluginConfiguration
    {
        EnabledLocales = ["es-MX", "ja-JP"],
        FallbackToBaseLanguage = false,
        TmdbApiKey = "secret",
    });

    public KasuMetaControllerTests()
    {
        _library.Add(FakeLibrary.Movie(Visible, "1"));
        _library.Add(FakeLibrary.Movie(Restricted, "2"));
        _library.VisibleTo[UserId] = [Visible];
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Info_reports_version_and_configured_locales()
    {
        var info = Controller(user: UserId).GetInfo().Value;

        Assert.NotNull(info);
        Assert.Equal(("KasuMeta", "1.2.3.0", KasuMetaController.ApiVersion), (info.Name, info.Version, info.ApiVersion));
        Assert.Equal(["es-MX", "ja-JP"], info.EnabledLocales);
        Assert.False(info.FallbackToBaseLanguage);
    }

    [Fact]
    public void Info_never_exposes_provider_keys()
    {
        Assert.DoesNotContain(
            typeof(KasuMetaInfo).GetProperties().Select(p => p.Name),
            n => n.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controller_requires_jellyfin_auth_and_lives_under_KasuMeta()
    {
        var type = typeof(KasuMetaController);

        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("KasuMeta", type.GetCustomAttribute<RouteAttribute>()?.Template);
    }

    [Fact]
    public void Config_page_is_embedded_where_Plugin_GetPages_points()
    {
        Assert.Contains(
            $"{typeof(Plugin).Namespace}.Configuration.configPage.html",
            typeof(Plugin).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public async Task Batch_serves_only_what_the_user_may_see()
    {
        await SeedAsync();

        var result = await Controller(user: UserId).GetBatch(Request(Visible, Restricted), None);

        var batch = result.Value!;
        Assert.Equal("La película", batch.Items[Visible.ToString("N")].Name!.Value);
        Assert.Equal([Restricted.ToString("N")], batch.Missing); // indistinguishable from "no data"
    }

    [Fact]
    public async Task An_api_key_sees_everything()
    {
        await SeedAsync();

        var batch = (await Controller(apiKey: true).GetBatch(Request(Visible, Restricted), None)).Value!;

        Assert.Equal(2, batch.Items.Count);
    }

    [Fact]
    public async Task No_user_and_no_api_key_is_forbidden()
    {
        var result = await Controller().GetBatch(Request(Visible), None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData("not a locale")]
    [InlineData("")]
    public async Task A_bad_locale_is_a_bad_request(string locale)
    {
        var request = Request(Visible);
        request.Locale = locale;

        var result = await Controller(user: UserId).GetBatch(request, None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Too_many_ids_is_a_bad_request()
    {
        var request = new BatchRequest
        {
            Locale = "es-MX",
            ItemIds = Enumerable.Range(0, KasuMetaController.MaxBatchSize + 1).Select(_ => Guid.NewGuid()).ToList(),
        };

        var result = await Controller(user: UserId).GetBatch(request, None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task An_unknown_field_is_a_bad_request()
    {
        var request = Request(Visible);
        request.Fields = ["name", "rating"];

        var result = await Controller(user: UserId).GetBatch(request, None);

        Assert.Contains("rating", Assert.IsType<BadRequestObjectResult>(result.Result).Value as string, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name", MetadataFields.Name)]
    [InlineData("Name", MetadataFields.Name)]
    [InlineData("SortName", MetadataFields.SortName)]
    [InlineData("sort_name", MetadataFields.SortName)]
    [InlineData(" OVERVIEW ", MetadataFields.Overview)]
    public void Fields_accept_wire_and_store_spellings(string raw, string expected)
    {
        Assert.True(KasuMetaController.TryParseFields([raw], out var fields, out _));
        Assert.Equal([expected], fields);
    }

    [Theory]
    [InlineData("Primary", "Primary")]
    [InlineData("logo", "Logo")]
    [InlineData(" BACKDROP ", "Backdrop")]
    public void Image_types_accept_any_casing(string raw, string expected)
    {
        Assert.True(KasuMetaController.TryParseImageTypes([raw], out var types, out _));
        Assert.Equal([expected], types);
    }

    [Fact]
    public void Field_filter_null_means_all_and_empty_means_none()
    {
        Assert.True(KasuMetaController.TryParseFields(null, out var all, out _));
        Assert.True(KasuMetaController.TryParseFields([], out var none, out _));

        Assert.Equal(MetadataFields.All, all);
        Assert.Empty(none);
    }

    [Fact]
    public void Image_filter_null_means_all_and_empty_means_none()
    {
        Assert.True(KasuMetaController.TryParseImageTypes(null, out var all, out _));
        Assert.True(KasuMetaController.TryParseImageTypes([], out var none, out _));

        Assert.Equal(LocalizedImageTypes.All, all);
        Assert.Empty(none);
    }

    [Fact]
    public async Task An_unknown_image_type_is_a_bad_request()
    {
        var request = Request(Visible);
        request.Images = ["Poster"];

        var result = await Controller(user: UserId).GetBatch(request, None);

        Assert.Contains("Poster", Assert.IsType<BadRequestObjectResult>(result.Result).Value as string, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_serves_localized_posters()
    {
        await _db.Store.UpsertImagesAsync(
            [new(Visible, "es-MX", LocalizedImageTypes.Primary, "https://image.tmdb.org/t/p/original/mx.jpg", MetadataSources.Tmdb)],
            None);

        var batch = (await Controller(user: UserId).GetBatch(Request(Visible), None)).Value!;

        Assert.Equal("https://image.tmdb.org/t/p/original/mx.jpg", batch.Items[Visible.ToString("N")].Images!["Primary"].Url);
    }

    [Fact]
    public async Task Item_returns_the_overlay_or_404()
    {
        await SeedAsync();
        var controller = Controller(user: UserId);

        var found = await controller.GetItem(Visible, "es_mx", None);
        var hidden = await controller.GetItem(Restricted, "es-MX", None);
        var none = await controller.GetItem(Visible, "ja-JP", None);

        Assert.Equal("La película", found.Value!.Name!.Value);
        Assert.IsType<NotFoundResult>(hidden.Result);
        Assert.IsType<NotFoundResult>(none.Result);
    }

    private static BatchRequest Request(params Guid[] ids) => new() { ItemIds = ids, Locale = "es-MX" };

    private Task<int> SeedAsync() =>
        _db.Store.UpsertFieldsAsync(
            [
                new(Visible, "es-MX", MetadataFields.Name, "La película", MetadataSources.Tmdb),
                new(Restricted, "es-MX", MetadataFields.Name, "Prohibida", MetadataSources.Tmdb),
            ],
            None);

    private KasuMetaController Controller(Guid? user = null, bool apiKey = false)
    {
        var claims = new List<Claim>();
        if (user is { } id)
        {
            claims.Add(new Claim(KasuMetaController.UserIdClaim, id.ToString("N")));
        }

        if (apiKey)
        {
            claims.Add(new Claim(KasuMetaController.IsApiKeyClaim, "True"));
        }

        return new KasuMetaController(_config, new OverlayService(_db.Store, _config), _library)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
