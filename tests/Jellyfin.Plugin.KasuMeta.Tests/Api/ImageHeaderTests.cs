using Jellyfin.Plugin.KasuMeta.Api;

namespace Jellyfin.Plugin.KasuMeta.Tests.Api;

public class ImageHeaderTests
{
    [Fact]
    public void Png_size_comes_from_ihdr() =>
        Assert.Equal(("image/png", 1920, 1080), Read(UploadAndTermsControllerTests.Png(1920, 1080, 64)));

    [Fact]
    public void Gif_size_comes_from_the_screen_descriptor() =>
        Assert.Equal(("image/gif", 300, 200), Read([.. "GIF89a"u8, 0x2C, 0x01, 0xC8, 0x00, 0, 0, 0]));

    [Fact]
    public void Jpeg_size_comes_from_the_first_frame_header()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, // APP0, 2 bytes of data
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0xD0, 0x05, 0x00, 0x03, // SOF0: 720 high, 1280 wide
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        ];

        Assert.Equal(("image/jpeg", 1280, 720), Read(jpeg));
    }

    [Fact]
    public void Webp_lossless_size_is_read()
    {
        // 400 x 300: width-1 = 399 = 0x18F, height-1 = 299 = 0x12B, packed in 14-bit fields.
        const int W = 399, H = 299;
        byte[] webp =
        [
            .. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8L"u8, 0, 0, 0, 0,
            0x2F,
            (byte)(W & 0xFF),
            (byte)(((W >> 8) & 0x3F) | ((H & 0x03) << 6)),
            (byte)((H >> 2) & 0xFF),
            (byte)((H >> 10) & 0x0F),
        ];

        Assert.Equal(("image/webp", 400, 300), Read(webp));
    }

    [Fact]
    public void Webp_extended_size_is_read()
    {
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0x7F, 0x07, 0x00, 0x37, 0x04, 0x00];

        Assert.Equal(("image/webp", 1920, 1080), Read(webp));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<?xml version=\"1.0\"?><svg/>")]
    [InlineData("GIF")]
    [InlineData("")]
    public void Other_content_is_not_an_image(string content) =>
        Assert.Null(ImageHeader.Read(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void A_truncated_jpeg_is_still_a_jpeg_of_unknown_size() =>
        Assert.Equal(("image/jpeg", (int?)null, (int?)null), ImageHeader.Read([0xFF, 0xD8, 0xFF, 0xE0]));

    private static (string, int, int) Read(byte[] data)
    {
        var header = ImageHeader.Read(data)!.Value;
        return (header.ContentType, header.Width!.Value, header.Height!.Value);
    }
}
