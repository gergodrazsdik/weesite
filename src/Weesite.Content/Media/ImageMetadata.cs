using System.Buffers.Binary;
using System.Text;

namespace Weesite.Content.Media;

/// <summary>
/// Tells whether an uploaded JPEG or WebP carries nothing but the picture (F-0172), from the file's
/// structure: an allowlist of JPEG segments and WebP chunks, so a kind of metadata it doesn't know
/// counts as metadata. Such a file may be stored as uploaded instead of re-encoded.
/// </summary>
internal static class ImageMetadata
{
    /// <summary>Gets whether the file holds no metadata: no EXIF, XMP, IPTC, ICC profile, comment, thumbnail or trailing data.</summary>
    /// <param name="name">The file name; its extension says the format.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>True only for a JPEG or WebP made of picture data alone; false for any other format.</returns>
    public static bool IsClean(string name, ReadOnlySpan<byte> data) => Path.GetExtension(name).ToUpperInvariant() switch
    {
        ".JPG" or ".JPEG" => IsCleanJpeg(data),
        ".WEBP" => IsCleanWebp(data),
        _ => false,
    };

    private static bool IsCleanJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var position = 2;
        while (position < data.Length)
        {
            if (data[position] != 0xFF)
            {
                return false;
            }

            while (position < data.Length && data[position] == 0xFF)
            {
                position++; // fill bytes
            }

            if (position >= data.Length)
            {
                return false;
            }

            var marker = data[position++];
            if (marker == 0xD9)
            {
                return position == data.Length; // anything after the end, such as a second picture, counts
            }

            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                continue; // no length
            }

            if (position + 2 > data.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || position + length > data.Length || !IsPictureSegment(marker, data.Slice(position + 2, length - 2)))
            {
                return false;
            }

            position += length;
            if (marker == 0xDA)
            {
                position = SkipScan(data, position);
            }
        }

        return false;
    }

    private static bool IsPictureSegment(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        // Frames, Huffman and arithmetic tables, quantisation tables, restart interval, scans, line count.
        >= 0xC0 and <= 0xCF or 0xDA or 0xDB or 0xDC or 0xDD => true,
        // JFIF without a thumbnail: identifier, version, units, two densities, thumbnail 0 × 0.
        0xE0 => payload.Length == 14 && payload.StartsWith("JFIF\0"u8) && payload[12] == 0 && payload[13] == 0,
        // Adobe's colour transform flag, which CMYK and YCCK files need to decode.
        0xEE => payload.Length == 12 && payload.StartsWith("Adobe"u8),
        // APP1 to APP15 (EXIF, XMP, ICC, IPTC, MPF and the rest), comments and anything unknown.
        _ => false,
    };

    // Entropy-coded data ends at the first marker that isn't a stuffed 0xFF00 or a restart marker.
    private static int SkipScan(ReadOnlySpan<byte> data, int position)
    {
        for (; position + 1 < data.Length; position++)
        {
            if (data[position] == 0xFF && data[position + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7) and not 0xFF)
            {
                return position;
            }
        }

        return data.Length;
    }

    private static bool IsCleanWebp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.StartsWith("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != (uint)(data.Length - 8))
        {
            return false;
        }

        var position = 12;
        while (position < data.Length)
        {
            if (position + 8 > data.Length)
            {
                return false;
            }

            var chunk = Encoding.ASCII.GetString(data.Slice(position, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if (size > (uint)(data.Length - position - 8))
            {
                return false;
            }

            var payload = data.Slice(position + 8, (int)size);
            var clean = chunk switch
            {
                "VP8 " or "VP8L" or "ALPH" => true,
                // Flags: ICC 0x20, EXIF 0x08, XMP 0x04, animation 0x02; alpha (0x10) is picture.
                "VP8X" => payload.Length >= 1 && (payload[0] & 0x2E) == 0,
                _ => false,
            };
            if (!clean)
            {
                return false;
            }

            position += 8 + (int)size + (int)(size & 1);
        }

        return position == data.Length;
    }
}
