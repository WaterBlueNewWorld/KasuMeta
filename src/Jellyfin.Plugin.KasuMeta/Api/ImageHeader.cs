using System.Buffers.Binary;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Recognizes an uploaded image by its first bytes (never by its name or declared type) and reads
/// its pixel size from the header. Only raster formats every client draws are accepted: JPEG, PNG,
/// WebP and GIF. SVG is refused: it is a document that can carry script.
/// </summary>
public static class ImageHeader
{
    /// <summary>Identifies an image.</summary>
    /// <returns>The MIME type and the size (null where the header doesn't say), or null if not an accepted format.</returns>
    public static (string ContentType, int? Width, int? Height)? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ("image/png", Positive(BinaryPrimitives.ReadInt32BigEndian(data[16..])), Positive(BinaryPrimitives.ReadInt32BigEndian(data[20..])));
        }

        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return ("image/gif", Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[6..])), Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[8..])));
        }

        if (data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            var (width, height) = WebPSize(data);
            return ("image/webp", width, height);
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            var (width, height) = JpegSize(data);
            return ("image/jpeg", width, height);
        }

        return null;
    }

    private static int? Positive(int value) => value > 0 ? value : null;

    private static (int? Width, int? Height) WebPSize(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && data.Length >= 30 && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return (Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF),
                Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF));
        }

        if (chunk.SequenceEqual("VP8L"u8) && data.Length >= 25 && data[20] == 0x2F)
        {
            int b1 = data[21], b2 = data[22], b3 = data[23], b4 = data[24];
            return (1 + (((b2 & 0x3F) << 8) | b1), 1 + (((b4 & 0x0F) << 10) | (b3 << 2) | ((b2 & 0xC0) >> 6)));
        }

        if (chunk.SequenceEqual("VP8X"u8) && data.Length >= 30)
        {
            return (1 + (data[24] | (data[25] << 8) | (data[26] << 16)), 1 + (data[27] | (data[28] << 8) | (data[29] << 16)));
        }

        return (null, null);
    }

    /// <summary>Walks the JPEG segments to the first frame header (SOFn), which holds the size.</summary>
    private static (int? Width, int? Height) JpegSize(ReadOnlySpan<byte> data)
    {
        var i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return (null, null);
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                i += 2; // no length
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return i + 9 <= data.Length
                    ? (Positive(BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..])), Positive(BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..])))
                    : (null, null);
            }

            if (length < 2)
            {
                return (null, null);
            }

            i += 2 + length;
        }

        return (null, null);
    }
}
