using System.Globalization;
using System.Text;

namespace OnePdf.PdfA;

/// <summary>
/// Writes the XMP packet of a PDF/A file (ISO 19005-2, 6.6.2; XMP Specification, September 2005, part 3 "Storage in files"). The
/// packet is built from the Info dictionary so that both say the same thing, and uses only properties the XMP specification itself
/// defines (Dublin Core, XMP Basic, Adobe PDF) plus the PDF/A identification schema, so it needs no extension schema block.
/// Output is UTF-8 and has no clock reading: the same Info always gives the same bytes.
/// </summary>
internal static class XmpWriter
{
    /// <summary>The XMP packet for <paramref name="info"/> and the PDF/A identification <paramref name="part"/> and <paramref name="conformance"/>.</summary>
    public static byte[] Write(DocumentInfo? info, int part, string conformance)
    {
        var sb = new StringBuilder(1024);
        // The id value is fixed by the XMP specification; the BOM goes in the begin attribute as U+FEFF (6.6.2.1: UTF-8 only,
        // so no encoding or bytes attribute).
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");

        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append("   <pdfaid:part>").Append(part.ToString(CultureInfo.InvariantCulture)).Append("</pdfaid:part>\n");
        sb.Append("   <pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n");
        sb.Append("  </rdf:Description>\n");

        string? title = info?.Title;
        string? author = info?.Author;
        string? subject = info?.Subject;
        if (title is not null || author is not null || subject is not null)
        {
            sb.Append("  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
            if (title is not null)
            {
                sb.Append("   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
            }

            if (author is not null)
            {
                sb.Append("   <dc:creator><rdf:Seq><rdf:li>").Append(Escape(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
            }

            if (subject is not null)
            {
                sb.Append("   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
            }

            sb.Append("  </rdf:Description>\n");
        }

        string? producer = info?.Producer;
        string? keywords = info?.Keywords;
        if (producer is not null || keywords is not null)
        {
            sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
            if (keywords is not null)
            {
                sb.Append("   <pdf:Keywords>").Append(Escape(keywords)).Append("</pdf:Keywords>\n");
            }

            if (producer is not null)
            {
                sb.Append("   <pdf:Producer>").Append(Escape(producer)).Append("</pdf:Producer>\n");
            }

            sb.Append("  </rdf:Description>\n");
        }

        string? creator = info?.Creator;
        DateTimeOffset? created = info?.CreationDate;
        DateTimeOffset? modified = info?.ModificationDate;
        if (creator is not null || created is not null || modified is not null)
        {
            sb.Append("  <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
            if (creator is not null)
            {
                sb.Append("   <xmp:CreatorTool>").Append(Escape(creator)).Append("</xmp:CreatorTool>\n");
            }

            if (created is { } c)
            {
                sb.Append("   <xmp:CreateDate>").Append(Date(c)).Append("</xmp:CreateDate>\n");
            }

            if (modified is { } m)
            {
                sb.Append("   <xmp:ModifyDate>").Append(Date(m)).Append("</xmp:ModifyDate>\n");
            }

            sb.Append("  </rdf:Description>\n");
        }

        sb.Append(" </rdf:RDF>\n");
        sb.Append("</x:xmpmeta>\n");
        sb.Append("<?xpacket end=\"w\"?>");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    // XMP date (a profile of ISO 8601, XMP Specification part 1, "Date"): seconds precision with a zone offset.
    private static string Date(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(ch).Append(s[++i]);
                    }
                    else if (char.IsSurrogate(ch) || ch == '\uFFFE' || ch == '\uFFFF' || (ch < ' ' && ch is not ('\t' or '\n' or '\r')))
                    {
                        // Not allowed in XML 1.0 (section 2.2): replaced rather than written.
                        sb.Append(' ');
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
