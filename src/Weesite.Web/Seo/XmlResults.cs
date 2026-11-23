using System.Text;
using System.Xml;

namespace Weesite.Web.Seo;

/// <summary>Writes XML documents as UTF-8 bytes (feeds, the sitemap).</summary>
internal static class XmlResults
{
    private static readonly XmlWriterSettings Settings = new() { Indent = true, Encoding = new UTF8Encoding(false) };

    /// <summary>Writes a document.</summary>
    /// <param name="write">Writes the root element and its content.</param>
    /// <returns>The document, with a UTF-8 XML declaration.</returns>
    public static byte[] Write(Action<XmlWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Removes the characters XML 1.0 can't carry (control characters other than tab, line feed and
    /// carriage return, U+FFFE, U+FFFF, unpaired surrogates). The writer throws on them, so one
    /// pasted escape code in a post would fail the whole feed (F-0234).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without them; the same instance when it has none.</returns>
    public static string Safe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var invalid = IndexOfInvalid(text, 0);
        if (invalid < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var start = 0;
        while (invalid >= 0)
        {
            builder.Append(text, start, invalid - start);
            start = invalid + 1;
            invalid = IndexOfInvalid(text, start);
        }

        return builder.Append(text, start, text.Length - start).ToString();
    }

    // The next character XML can't carry, from start; a valid surrogate pair is skipped whole.
    private static int IndexOfInvalid(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (!XmlConvert.IsXmlChar(c))
            {
                return i;
            }
        }

        return -1;
    }
}
