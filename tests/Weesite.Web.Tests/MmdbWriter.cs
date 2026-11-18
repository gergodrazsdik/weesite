using System.Net;
using System.Text;

namespace Weesite.Web.Tests;

/// <summary>
/// Writes a tiny IP to country database in the MaxMind DB format
/// (https://maxmind.github.io/MaxMind-DB/), so the tests need no third-party data (T-0143). An
/// IPv6 search tree with 24-bit records, IPv4 networks under <c>::/96</c> as real databases have
/// them, and a data section of <c>{ country: { iso_code } }</c> maps. Networks must not overlap.
/// </summary>
internal sealed class MmdbWriter
{
    private const int Empty = int.MinValue;

    // Each node's two records: a node index, Empty, or a data offset encoded as ~offset.
    private readonly List<int[]> _nodes = [[Empty, Empty]];
    private readonly Dictionary<string, int> _offsets = new(StringComparer.Ordinal);
    private readonly List<byte> _data = [];

    /// <summary>Gets or sets the metadata's database type.</summary>
    public string DatabaseType { get; set; } = "Weesite-Test-Country";

    /// <summary>Adds a network and its country.</summary>
    /// <param name="network">Such as <c>203.0.113.0/24</c> or <c>2001:db8::/32</c>.</param>
    /// <param name="isoCode">The country code, or null for a record without a country.</param>
    /// <returns>The writer.</returns>
    public MmdbWriter Add(string network, string? isoCode)
    {
        var parts = network.Split('/');
        var address = IPAddress.Parse(parts[0]);
        var length = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // IPv4 sits at ::a.b.c.d in an IPv6 tree.
            bytes = [.. new byte[12], .. bytes];
            length += 96;
        }

        var data = DataOffset(isoCode);
        var node = 0;
        for (var bit = 0; bit < length; bit++)
        {
            var side = (bytes[bit / 8] >> (7 - (bit % 8))) & 1;
            if (bit == length - 1)
            {
                _nodes[node][side] = ~data;
                break;
            }

            if (_nodes[node][side] == Empty)
            {
                _nodes.Add([Empty, Empty]);
                _nodes[node][side] = _nodes.Count - 1;
            }

            node = _nodes[node][side];
        }

        return this;
    }

    /// <summary>Writes the database.</summary>
    /// <returns>The file's bytes.</returns>
    public byte[] ToBytes()
    {
        using var file = new MemoryStream();
        var count = _nodes.Count;
        foreach (var node in _nodes)
        {
            foreach (var record in node)
            {
                var value = record == Empty ? count : record < 0 ? count + 16 + ~record : record;
                file.Write([(byte)(value >> 16), (byte)(value >> 8), (byte)value]);
            }
        }

        file.Write(new byte[16]);
        file.Write([.. _data]);
        file.Write([0xAB, 0xCD, 0xEF]);
        file.Write(Encoding.ASCII.GetBytes("MaxMind.com"));
        WriteMap(file, 9);
        WriteString(file, "binary_format_major_version");
        WriteUnsigned(file, 5, 2);
        WriteString(file, "binary_format_minor_version");
        WriteUnsigned(file, 5, 0);
        WriteString(file, "build_epoch");
        WriteUnsigned(file, 9, 1_700_000_000);
        WriteString(file, "database_type");
        WriteString(file, DatabaseType);
        WriteString(file, "description");
        WriteMap(file, 1);
        WriteString(file, "en");
        WriteString(file, "A test database");
        WriteString(file, "ip_version");
        WriteUnsigned(file, 5, 6);
        WriteString(file, "languages");
        WriteControl(file, 11, 1);
        WriteString(file, "en");
        WriteString(file, "node_count");
        WriteUnsigned(file, 6, (ulong)count);
        WriteString(file, "record_size");
        WriteUnsigned(file, 5, 24);
        return file.ToArray();
    }

    /// <summary>Writes the database to a file.</summary>
    /// <param name="path">The file.</param>
    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    private int DataOffset(string? isoCode)
    {
        var key = isoCode ?? "";
        if (_offsets.TryGetValue(key, out var offset))
        {
            return offset;
        }

        offset = _data.Count;
        using var record = new MemoryStream();
        WriteMap(record, 1);
        if (isoCode is null)
        {
            // A record with other data and no country.
            WriteString(record, "continent");
            WriteMap(record, 1);
            WriteString(record, "code");
            WriteString(record, "EU");
        }
        else
        {
            WriteString(record, "country");
            WriteMap(record, 1);
            WriteString(record, "iso_code");
            WriteString(record, isoCode);
        }

        _data.AddRange(record.ToArray());
        _offsets[key] = offset;
        return offset;
    }

    private static void WriteMap(Stream stream, int pairs) => WriteControl(stream, 7, pairs);

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteControl(stream, 2, bytes.Length);
        stream.Write(bytes);
    }

    // An unsigned integer of type 5 (uint16), 6 (uint32) or 9 (uint64), in as few bytes as it needs.
    private static void WriteUnsigned(Stream stream, int type, ulong value)
    {
        var bytes = new List<byte>();
        for (var v = value; v > 0; v >>= 8)
        {
            bytes.Insert(0, (byte)v);
        }

        WriteControl(stream, type, bytes.Count);
        stream.Write([.. bytes]);
    }

    // The control byte: three bits of type (0 and a second byte for extended types) and five of size.
    private static void WriteControl(Stream stream, int type, int size)
    {
        if (size >= 29)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Sizes from 29 aren't needed here.");
        }

        if (type <= 7)
        {
            stream.WriteByte((byte)((type << 5) | size));
        }
        else
        {
            stream.WriteByte((byte)size);
            stream.WriteByte((byte)(type - 7));
        }
    }
}
