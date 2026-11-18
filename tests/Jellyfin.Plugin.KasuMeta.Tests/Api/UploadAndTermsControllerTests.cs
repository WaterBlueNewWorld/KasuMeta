using System.Buffers.Binary;
using System.Security.Claims;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Storage;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

/// <summary>Image uploads, genre and tag overrides, and the term dictionary, through the controller.</summary>
public sealed class UploadAndTermsControllerTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid UserId = Guid.Parse("5eb3da7c3bf744ffa98cb4c5c3011705");
    private static readonly Guid Visible = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Restricted = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private readonly TestDatabase _db = new();
    private readonly FakeLibrary _library = new();
    private readonly FetchQueue _queue = new();
    private readonly FakeConfig _config = new(new PluginConfiguration { EnabledLocales = ["es-MX", "ja-JP"], TmdbApiKey = "k" });
    private readonly TermDictionary _terms;

    public UploadAndTermsControllerTests()
    {
        _terms = new TermDictionary(_db.Store);
        _library.Add(FakeLibrary.Movie(Visible, "1"));
        _library.Add(FakeLibrary.Movie(Restricted, "2"));
        _library.VisibleTo[UserId] = [Visible];
    }

    public void Dispose()
    {
        _terms.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task An_uploaded_png_is_stored_with_its_size_and_served_back()
    {
        var png = Png(640, 960, 2_000);

        var put = await Controller(apiKey: true).StoreUploadAsync(Visible, "es_mx", "logo", File(png), locked: null, None);

        var logo = put.Value!.Locales["es-MX"].Images!["Logo"];
        Assert.True(logo.Uploaded);
        Assert.Equal((640, 960, MetadataSources.Manual, true), (logo.Width!.Value, logo.Height!.Value, logo.Source, logo.Locked));
        Assert.StartsWith("KasuMeta/Items/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/es-MX/Images/Logo?v=", logo.Url, StringComparison.Ordinal);

        var reader = Controller(user: UserId);
        var file = Assert.IsType<FileContentResult>(await reader.GetUploadedImage(Visible, "es-MX", "Logo", None));
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(png, file.FileContents);
        Assert.Equal("nosniff", reader.Response.Headers.XContentTypeOptions.ToString());

        var overlay = (await reader.GetItem(Visible, "es-MX", None)).Value!;
        Assert.True(overlay.Images!["Logo"].Uploaded);
    }

    [Fact]
    public async Task A_matching_etag_answers_not_modified()
    {
        await Controller(apiKey: true).StoreUploadAsync(Visible, "es-MX", "Primary", File(Png(10, 10, 100)), locked: false, None);
        var first = Controller(user: UserId);
        await first.GetUploadedImage(Visible, "es-MX", "Primary", None);
        var etag = first.Response.Headers.ETag.ToString();

        var second = Controller(user: UserId);
        second.Request.Headers.IfNoneMatch = etag;
        var result = Assert.IsType<StatusCodeResult>(await second.GetUploadedImage(Visible, "es-MX", "Primary", None));
        Assert.Equal(StatusCodes.Status304NotModified, result.StatusCode);
    }

    [Fact]
    public async Task An_upload_the_caller_cannot_see_is_not_found()
    {
        await Controller(apiKey: true).StoreUploadAsync(Restricted, "es-MX", "Logo", File(Png(10, 10, 100)), locked: null, None);

        Assert.IsType<NotFoundResult>(await Controller(user: UserId).GetUploadedImage(Restricted, "es-MX", "Logo", None));
        Assert.IsType<NotFoundResult>(await Controller(user: UserId).GetUploadedImage(Visible, "es-MX", "Logo", None));
    }

    [Fact]
    public async Task A_file_over_ten_megabytes_is_refused()
    {
        var result = await Controller(apiKey: true).StoreUploadAsync(
            Visible, "es-MX", "Logo", File(Png(10, 10, KasuMetaController.MaxUploadBytes + 1)), locked: null, None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_the_form_is_read()
    {
        var controller = Controller(apiKey: true);
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.ContentLength = 11L * 1024 * 1024;

        var result = await controller.UploadImage(Visible, "es-MX", "Logo", None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task The_action_reads_the_file_and_locked_from_the_multipart_form()
    {
        using var content = new MultipartFormDataContent("kasumeta-boundary");
        content.Add(new ByteArrayContent(Png(32, 16, 200)), "file", "logo.png");
        content.Add(new StringContent("false"), "locked");
        var controller = Controller(apiKey: true);
        controller.Request.ContentType = content.Headers.ContentType!.ToString();
        controller.Request.Body = new MemoryStream(await content.ReadAsByteArrayAsync());

        var put = await controller.UploadImage(Visible, "es-MX", "Logo", None);

        var logo = put.Value!.Locales["es-MX"].Images!["Logo"];
        Assert.Equal((true, 32, false), (logo.Uploaded, logo.Width!.Value, logo.Locked));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("not an image at all")]
    public async Task Anything_but_a_raster_image_is_refused(string content)
    {
        var result = await Controller(apiKey: true).StoreUploadAsync(
            Visible, "es-MX", "Logo", File(System.Text.Encoding.UTF8.GetBytes(content)), locked: null, None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _db.Store.GetImagesAsync([Visible], null, None));
    }

    [Fact]
    public async Task Bad_routes_and_unknown_items_are_refused()
    {
        var admin = Controller(apiKey: true);
        Assert.IsType<BadRequestObjectResult>((await admin.StoreUploadAsync(Visible, "not a locale", "Logo", File(Png(1, 1, 50)), null, None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.StoreUploadAsync(Visible, "es-MX", "Banner", File(Png(1, 1, 50)), null, None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.StoreUploadAsync(Visible, "es-MX", "Logo", null, null, None)).Result);
        Assert.IsType<NotFoundResult>((await admin.StoreUploadAsync(Guid.NewGuid(), "es-MX", "Logo", File(Png(1, 1, 50)), null, None)).Result);
    }

    [Fact]
    public async Task A_manual_url_replaces_an_upload_and_null_removes_it()
    {
        var admin = Controller(apiKey: true);
        await admin.StoreUploadAsync(Visible, "es-MX", "Logo", File(Png(10, 10, 100)), locked: null, None);

        var request = new ItemOverridesRequest
        {
            Images = new Dictionary<string, ManualImageValue?> { ["Logo"] = new() { Url = "https://example.com/logo.png" } },
        };
        var put = (await admin.SetOverrides(Visible, "es-MX", request, None)).Value!;

        var logo = put.Locales["es-MX"].Images!["Logo"];
        Assert.False(logo.Uploaded);
        Assert.Equal("https://example.com/logo.png", logo.Url);
        Assert.Null(await _db.Store.GetImageFileAsync(Visible, "es-MX", "Logo", None));
    }

    [Fact]
    public async Task Genres_and_tags_are_overridden_as_lists()
    {
        var request = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?>
            {
                ["Genres"] = new() { Values = [" Acción ", "acción", "Drama", ""] },
                ["tags"] = new() { Values = ["cabaña"], Locked = false },
            },
        };

        var put = (await Controller(apiKey: true).SetOverrides(Visible, "es-MX", request, None)).Value!;

        var overlay = put.Locales["es-MX"];
        Assert.Equal(["Acción", "Drama"], overlay.Genres!.Values);
        Assert.Equal((MetadataSources.Manual, true), (overlay.Genres.Source, overlay.Genres.Locked));
        Assert.Equal(["cabaña"], overlay.Tags!.Values);
        Assert.False(overlay.Tags.Locked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_empty_list_is_refused(int variant)
    {
        string[] values = variant == 0 ? [] : [" ", ""];
        var request = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?> { ["Genres"] = new() { Values = values } },
        };

        Assert.IsType<BadRequestObjectResult>((await Controller(apiKey: true).SetOverrides(Visible, "es-MX", request, None)).Result);
    }

    [Fact]
    public async Task Too_many_or_too_long_terms_are_refused()
    {
        var tooMany = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?>
            {
                ["Tags"] = new() { Values = Enumerable.Range(0, KasuMetaController.MaxListValues + 1).Select(i => "t" + i).ToList() },
            },
        };
        var tooLong = new ItemOverridesRequest
        {
            Fields = new Dictionary<string, ManualFieldValue?>
            {
                ["Tags"] = new() { Values = [new string('x', KasuMetaController.MaxTermLength + 1)] },
            },
        };

        Assert.IsType<BadRequestObjectResult>((await Controller(apiKey: true).SetOverrides(Visible, "es-MX", tooMany, None)).Result);
        Assert.IsType<BadRequestObjectResult>((await Controller(apiKey: true).SetOverrides(Visible, "es-MX", tooLong, None)).Result);
    }

    [Fact]
    public async Task The_dictionary_lists_terms_in_use_untranslated_first_and_translates_at_once()
    {
        await _db.Store.UpsertFieldsAsync(
            [
                new(Visible, "en", MetadataFields.Genres, ListValues.Serialize(["Action", "Horror"])!, MetadataSources.Tvdb),
                new(Restricted, "en", MetadataFields.Genres, ListValues.Serialize(["Horror"])!, MetadataSources.Tvdb),
            ],
            None);
        var admin = Controller(apiKey: true);

        var before = (await admin.GetTerms("genre", "es-MX", None)).Value!;
        Assert.Equal([("Horror", null, 2), ("Action", null, 1)], before.Terms.Select(t => (t.Term, t.Translation, t.Items)));

        var after = (await admin.SetTerms(
            "genres",
            "es_mx",
            new TermsRequest { Translations = new Dictionary<string, string?> { ["action"] = "Acción" } },
            None)).Value!;
        Assert.Equal([("Horror", null, 2), ("Action", "Acción", 1)], after.Terms.Select(t => (t.Term, t.Translation, t.Items)));

        var overlay = (await Controller(user: UserId).GetItem(Visible, "es-MX", None)).Value!;
        Assert.Equal(["Acción", "Horror"], overlay.Genres!.Values);
        Assert.Equal((MetadataSources.Dictionary, "es-MX"), (overlay.Genres.Source, overlay.Genres.ResolvedLocale));

        // With no term translated, the English list isn't served as Spanish: nothing is.
        await admin.SetTerms("genre", "es-MX", new TermsRequest { Translations = new Dictionary<string, string?> { ["Action"] = null } }, None);
        Assert.IsType<NotFoundResult>((await Controller(user: UserId).GetItem(Visible, "es-MX", None)).Result);
    }

    [Fact]
    public async Task Bad_dictionary_requests_are_refused()
    {
        var admin = Controller(apiKey: true);
        Assert.IsType<BadRequestObjectResult>((await admin.GetTerms("studio", "es-MX", None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.GetTerms("genre", null, None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.SetTerms("genre", "es-MX", new TermsRequest(), None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.SetTerms(
            "genre",
            "es-MX",
            new TermsRequest { Translations = new Dictionary<string, string?> { ["Action"] = "a", ["ACTION"] = "b" } },
            None)).Result);
        Assert.IsType<BadRequestObjectResult>((await admin.SetTerms(
            "genre",
            "es-MX",
            new TermsRequest { Translations = new Dictionary<string, string?> { [" "] = "a" } },
            None)).Result);
    }

    [Fact]
    public void Info_says_whether_the_original_language_is_used()
    {
        _config.Current.FallbackToOriginalLanguage = false;
        Assert.False(Controller(user: UserId).GetInfo().Value!.FallbackToOriginalLanguage);
    }

    /// <summary>A PNG signature and IHDR with the given size, padded to <paramref name="length"/> bytes.</summary>
    internal static byte[] Png(int width, int height, int length)
    {
        var data = new byte[Math.Max(length, 24)];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        return data;
    }

    private static FormFile File(byte[] data) =>
        new(new MemoryStream(data), 0, data.Length, "file", "upload.bin");

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

        return new KasuMetaController(
            _config,
            new OverlayService(_db.Store, _config, _terms),
            new SearchService(_db.Store, _library),
            _library,
            _db.Store,
            _queue,
            new StatusService(_db.Store, _db.Database, _queue, new ProviderStats(TimeProvider.System), _library, _config),
            _terms)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
