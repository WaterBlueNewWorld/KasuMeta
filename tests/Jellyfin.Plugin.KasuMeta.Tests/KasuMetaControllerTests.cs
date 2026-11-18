using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Providers;
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
    private readonly FetchQueue _queue = new();
    private readonly ProviderStats _stats = new(TimeProvider.System);
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
        Assert.Equal(["text", "images", "search", "overrides", "genres", "tags", "uploads", "terms", "original-language"], info.Features);
    }

    [Theory]
    [InlineData(nameof(KasuMetaController.GetAllLocales))]
    [InlineData(nameof(KasuMetaController.SetOverrides))]
    [InlineData(nameof(KasuMetaController.Refresh))]
    [InlineData(nameof(KasuMetaController.GetStatus))]
    [InlineData(nameof(KasuMetaController.UploadImage))]
    [InlineData(nameof(KasuMetaController.GetTerms))]
    [InlineData(nameof(KasuMetaController.SetTerms))]
    public void Admin_routes_require_elevation(string action)
    {
        var method = typeof(KasuMetaController).GetMethod(action)!;

        Assert.Equal("RequiresElevation", method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }

    [Fact]
    public void Read_routes_need_no_elevation()
    {
        foreach (var action in new[] { nameof(KasuMetaController.GetInfo), nameof(KasuMetaController.GetBatch), nameof(KasuMetaController.GetItem), nameof(KasuMetaController.Search), nameof(KasuMetaController.GetUploadedImage) })
        {
            Assert.Null(typeof(KasuMetaController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>());
        }
    }

    [Fact]
    public async Task A_locked_override_is_served_and_survives_a_provider_refresh()
    {
        await SeedAsync();
        var admin = Controller(apiKey: true);

        var put = await admin.SetOverrides(Visible, "es_mx", Overrides(name: new ManualFieldValue { Value = "  Curada  " }), None);
        await _db.Store.ReplaceProviderFieldsAsync(
            Visible,
            MetadataSources.Tmdb,
            [new(Visible, "es-MX", MetadataFields.Name, "De TMDB", MetadataSources.Tmdb)],
            None);
        var served = (await Controller(user: UserId).GetItem(Visible, "es-MX", None)).Value!;

        var stored = put.Value!.Locales["es-MX"].Name!;
        Assert.Equal(("Curada", MetadataSources.Manual, true, "es-MX"), (stored.Value, stored.Source, stored.Locked, stored.ResolvedLocale));
        Assert.Equal("Curada", served.Name!.Value);
        Assert.Equal(0, _queue.Pending); // setting a value needs no re-fetch
    }

    [Fact]
    public async Task Removing_a_value_queues_a_forced_refetch()
    {
        await _db.Store.UpsertFieldsAsync([new(Visible, "es-MX", MetadataFields.Name, "Curada", MetadataSources.Manual, Locked: true)], None);

        var put = await Controller(apiKey: true).SetOverrides(Visible, "es-MX", Overrides(removeName: true), None);

        Assert.Empty(put.Value!.Locales);
        var (id, force) = await FirstQueuedAsync();
        Assert.Equal((Visible, true), (id, force));
    }

    [Fact]
    public async Task Overrides_for_an_unknown_item_are_404_and_store_nothing()
    {
        var unknown = Guid.NewGuid();
        var admin = Controller(apiKey: true);

        var put = await admin.SetOverrides(unknown, "es-MX", Overrides(name: new ManualFieldValue { Value = "x" }), None);
        var all = await admin.GetAllLocales(unknown, None);
        var refresh = admin.Refresh(unknown);

        Assert.IsType<NotFoundResult>(put.Result);
        Assert.IsType<NotFoundResult>(all.Result);
        Assert.IsType<NotFoundResult>(refresh);
        Assert.Empty(await _db.Store.GetFieldsAsync([unknown], null, None));
        Assert.Equal(0, _queue.Pending);
    }

    [Theory]
    [InlineData("not a locale")]
    [InlineData("")]
    public async Task An_override_with_a_bad_locale_is_a_bad_request(string locale)
    {
        var result = await Controller(apiKey: true).SetOverrides(Visible, locale, Overrides(name: new ManualFieldValue { Value = "x" }), None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task An_invalid_override_is_a_bad_request_and_stores_nothing()
    {
        var result = await Controller(apiKey: true).SetOverrides(Visible, "es-MX", Overrides(name: new ManualFieldValue { Value = " " }), None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _db.Store.GetFieldsAsync([Visible], null, None));
    }

    [Fact]
    public void Refresh_queues_a_forced_fetch()
    {
        var result = Controller(apiKey: true).Refresh(Visible);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(1, _queue.Pending);
    }

    [Fact]
    public async Task All_lists_every_stored_locale_without_fallback_and_the_fetch_state()
    {
        await _db.Store.UpsertFieldsAsync(
            [
                new(Visible, "es-ES", MetadataFields.Name, "La película (España)", MetadataSources.Tmdb),
                new(Visible, "ja-JP", MetadataFields.Overview, "映画", MetadataSources.Tmdb),
            ],
            None);
        await _db.Store.UpsertImagesAsync(
            [new(Visible, "zh-CN", LocalizedImageTypes.Logo, "https://image.tmdb.org/t/p/original/zh.png", MetadataSources.Tmdb)],
            None);
        await _db.Store.SetFetchStateAsync(
            new FetchState(Visible, new Dictionary<string, string> { ["Tmdb"] = "1" }, _db.Time.Now, FetchStatus.Ok, FetchVersion: 3),
            None);

        var all = (await Controller(apiKey: true).GetAllLocales(Visible, None)).Value!;

        Assert.Equal(Visible.ToString("N"), all.ItemId);
        Assert.Equal(["es-ES", "ja-JP", "zh-CN"], all.Locales.Keys);
        Assert.Null(all.Locales["es-ES"].Overview); // no fallback between locales
        Assert.Equal("映画", all.Locales["ja-JP"].Overview!.Value);
        Assert.Equal("zh-CN", all.Locales["zh-CN"].Images!["Logo"].ResolvedLocale);
        Assert.Equal(new ItemFetchInfo("ok", _db.Time.Now, null), all.Fetch);
    }

    [Fact]
    public async Task All_for_a_never_fetched_item_is_empty_without_a_fetch_state()
    {
        var all = (await Controller(apiKey: true).GetAllLocales(Visible, None)).Value!;

        Assert.Empty(all.Locales);
        Assert.Null(all.Fetch);
    }

    [Theory]
    [InlineData("rating", "x", "Unknown field")]
    [InlineData("Name", "", "empty")]
    [InlineData("Name", "   ", "empty")]
    public void Bad_field_overrides_are_refused(string key, string value, string message)
    {
        var request = new ItemOverridesRequest { Fields = new Dictionary<string, ManualFieldValue?> { [key] = new() { Value = value } } };

        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", request, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Poster", "https://example.org/a.jpg", "Unknown image type")]
    [InlineData("Primary", "", "absolute http(s) URL")]
    [InlineData("Primary", "/relative/a.jpg", "absolute http(s) URL")]
    [InlineData("Primary", "javascript:alert(1)", "absolute http(s) URL")]
    [InlineData("Primary", "file:///etc/passwd", "absolute http(s) URL")]
    public void Bad_image_overrides_are_refused(string key, string url, string message)
    {
        var request = new ItemOverridesRequest { Images = new Dictionary<string, ManualImageValue?> { [key] = new() { Url = url } } };

        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", request, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_long_values_are_refused()
    {
        var text = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?> { ["Overview"] = new() { Value = new string('a', KasuMetaController.MaxTextLength + 1) } },
        };
        var url = new ItemOverridesRequest
        {
            Images = new Dictionary<string, ManualImageValue?> { ["Primary"] = new() { Url = "https://example.org/" + new string('a', KasuMetaController.MaxUrlLength) } },
        };

        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", text, out _, out _));
        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", url, out _, out _));
    }

    [Fact]
    public void An_empty_override_and_a_key_given_twice_are_refused()
    {
        var twice = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?> { ["SortName"] = new() { Value = "a" }, ["sort_name"] = null },
        };

        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", new ItemOverridesRequest(), out _, out var empty));
        Assert.False(KasuMetaController.TryBuildEdit(Visible, "es-MX", twice, out _, out var duplicate));
        Assert.Contains("Nothing to change", empty, StringComparison.Ordinal);
        Assert.Contains("twice", duplicate, StringComparison.Ordinal);
    }

    [Fact]
    public void An_override_request_becomes_a_store_edit()
    {
        var request = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?>
            {
                ["name"] = new() { Value = " Curada " },
                ["TAGLINE"] = new() { Value = "Rápida", Locked = false },
                ["Overview"] = null,
            },
            Images = new Dictionary<string, ManualImageValue?>
            {
                ["primary"] = new() { Url = " https://example.org/p.jpg " },
                ["Logo"] = null,
            },
        };

        Assert.True(KasuMetaController.TryBuildEdit(Visible, "es-MX", request, out var edit, out _));

        Assert.Equal(
            ["name=Curada locked", "tagline=Rápida unlocked"],
            edit.SetFields.Select(f => $"{f.Field}={f.Value} {(f.Locked ? "locked" : "unlocked")}").Order(StringComparer.Ordinal));
        Assert.All(edit.SetFields, f => Assert.Equal((Visible, "es-MX", MetadataSources.Manual), (f.ItemId, f.Locale, f.Source)));
        var image = Assert.Single(edit.SetImages);
        Assert.Equal(("Primary", "https://example.org/p.jpg", true), (image.ImageType, image.Url, image.Locked));
        Assert.Equal([MetadataFields.Overview], edit.RemoveFields);
        Assert.Equal([LocalizedImageTypes.Logo], edit.RemoveImages);
    }

    [Fact]
    public async Task Search_finds_what_the_user_may_see()
    {
        await SeedAsync();

        var visible = (await Controller(user: UserId).Search("pelicula", "es-MX", null, null, None)).Value!;
        var restricted = (await Controller(user: UserId).Search("prohibida", "es-MX", null, null, None)).Value!;
        var asApiKey = (await Controller(apiKey: true).Search("prohibida", "es-MX", null, null, None)).Value!;

        var result = Assert.Single(visible.Results);
        Assert.Equal((Visible.ToString("N"), "Movie", "La película", "es-MX"), (result.ItemId, result.Type, result.MatchedName, result.MatchedLocale));
        Assert.Empty(restricted.Results);
        Assert.Single(asApiKey.Results);
    }

    [Theory]
    [InlineData(null, "es-MX", null, null)]
    [InlineData("  ", "es-MX", null, null)]
    [InlineData("!?", "es-MX", null, null)]
    [InlineData("pelicula", "not a locale", null, null)]
    [InlineData("pelicula", null, null, null)]
    [InlineData("pelicula", "es-MX", 0, null)]
    [InlineData("pelicula", "es-MX", 101, null)]
    [InlineData("pelicula", "es-MX", null, "Movie,Album")]
    [InlineData("pelicula", "es-MX", null, "1")]
    public async Task A_bad_search_request_is_a_bad_request(string? q, string? locale, int? limit, string? types)
    {
        var result = await Controller(user: UserId).Search(q, locale, limit, types, None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_without_a_user_or_api_key_is_forbidden()
    {
        var result = await Controller().Search("pelicula", "es-MX", null, null, None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData(null, "Episode,Movie,Series")]
    [InlineData("", "Episode,Movie,Series")]
    [InlineData("movie", "Movie")]
    [InlineData(" Series , SEASON ", "Season,Series")]
    public void Search_types_default_to_all_but_seasons(string? raw, string expected)
    {
        Assert.True(KasuMetaController.TryParseKinds(raw, out var kinds, out _));
        Assert.Equal(expected, string.Join(',', kinds.Select(k => k.ToString()).Order(StringComparer.Ordinal)));
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

    private static ItemOverridesRequest Overrides(ManualFieldValue? name = null, bool removeName = false) =>
        new() { Fields = new Dictionary<string, ManualFieldValue?> { ["Name"] = removeName ? null : name } };

    private async Task<(Guid ItemId, bool Force)> FirstQueuedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var entry in _queue.ReadAllAsync(timeout.Token))
        {
            return entry;
        }

        throw new InvalidOperationException("Nothing was queued.");
    }

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

        var terms = new TermDictionary(_db.Store);
        return new KasuMetaController(
            _config,
            new OverlayService(_db.Store, _config, terms),
            new SearchService(_db.Store, _library),
            _library,
            _db.Store,
            _queue,
            new StatusService(_db.Store, _db.Database, _queue, _stats, _library, _config),
            terms)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
