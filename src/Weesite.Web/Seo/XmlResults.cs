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
}
