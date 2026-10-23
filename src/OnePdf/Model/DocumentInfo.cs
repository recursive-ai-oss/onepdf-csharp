using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace OnePdf;

/// <summary>The document information dictionary (<c>/Info</c> of the trailer, ISO 32000-2 14.3.3).</summary>
public sealed class DocumentInfo
{
    internal DocumentInfo(PdfDictionary dictionary) => Dictionary = dictionary;

    /// <summary>The Info dictionary; empty (never null) when the document has none.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The title, or null.</summary>
    public string? Title => Text("Title");

    /// <summary>The author, or null.</summary>
    public string? Author => Text("Author");

    /// <summary>The subject, or null.</summary>
    public string? Subject => Text("Subject");

    /// <summary>The keywords, or null.</summary>
    public string? Keywords => Text("Keywords");

    /// <summary>The application that created the original document, or null.</summary>
    public string? Creator => Text("Creator");

    /// <summary>The application that produced the PDF, or null.</summary>
    public string? Producer => Text("Producer");

    /// <summary>The creation date when present and parseable (see <see cref="PdfDate.TryParse"/>), otherwise null.</summary>
    public DateTimeOffset? CreationDate => Date("CreationDate");

    /// <summary>The modification date when present and parseable, otherwise null.</summary>
    public DateTimeOffset? ModificationDate => Date("ModDate");

    /// <summary>The <c>/Trapped</c> value: <c>True</c>, <c>False</c> or <c>Unknown</c>; null when absent.</summary>
    public string? Trapped => Dictionary.Get("Trapped") switch
    {
        PdfName n => n.Value,
        PdfString s => s.GetText(),
        PdfBoolean b => b.Value ? "True" : "False",
        _ => null,
    };

    /// <summary>Every entry whose value is a string (standard and custom keys), decoded as text, in file order.</summary>
    public IReadOnlyDictionary<string, string> Entries
    {
        get
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in Dictionary.Keys)
            {
                if (Dictionary.Get(key) is PdfString s)
                {
                    map[key] = s.GetText();
                }
            }

            return map;
        }
    }

    private string? Text(string key) => Dictionary.GetString(key)?.GetText();

    private DateTimeOffset? Date(string key) => Text(key) is { } t && PdfDate.TryParse(t, out DateTimeOffset d) ? d : null;
}

/// <summary>
/// The XMP metadata packet of the catalog (<c>/Metadata</c>): the raw bytes plus a few fields read from it. Reading is
/// forgiving: a packet that is not well-formed XML still gives its raw bytes, with <see cref="IsWellFormed"/> false.
/// </summary>
public sealed class XmpMetadata
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";

    // Names are looked up once, not rebuilt (a namespace + local-name concatenation and a table lookup) for every query.
    private static readonly XName RdfDescription = Rdf + "Description";
    private static readonly XName RdfLi = Rdf + "li";
    private static readonly XName DcTitle = Dc + "title";
    private static readonly XName DcDescription = Dc + "description";
    private static readonly XName DcCreator = Dc + "creator";
    private static readonly XName DcSubject = Dc + "subject";
    private static readonly XName XmpCreatorTool = Xmp + "CreatorTool";
    private static readonly XName XmpCreateDate = Xmp + "CreateDate";
    private static readonly XName XmpModifyDate = Xmp + "ModifyDate";
    private static readonly XName XmpMetadataDate = Xmp + "MetadataDate";
    private static readonly XName PdfProducer = PdfNs + "Producer";
    private static readonly XName PdfKeywords = PdfNs + "Keywords";
    private static readonly XName XmlLang = XNamespace.Xml + "lang";

    internal XmpMetadata(ReadOnlyMemory<byte> raw)
    {
        RawBytes = raw;
        Creators = [];
        Subjects = [];
        XDocument? doc = null;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 256L << 20,
                CloseInput = true,
            };
            using XmlReader reader = XmlReader.Create(new MemoryStream(raw.ToArray(), writable: false), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
        }

        if (doc?.Root is null)
        {
            return;
        }

        IsWellFormed = true;
        var descriptions = new List<XElement>(doc.Descendants(RdfDescription));
        Title = Alt(descriptions, DcTitle);
        Description = Alt(descriptions, DcDescription);
        Creators = Items(descriptions, DcCreator);
        Subjects = Items(descriptions, DcSubject);
        CreatorTool = Simple(descriptions, XmpCreatorTool);
        Producer = Simple(descriptions, PdfProducer);
        Keywords = Simple(descriptions, PdfKeywords);
        CreateDate = Date(Simple(descriptions, XmpCreateDate));
        ModifyDate = Date(Simple(descriptions, XmpModifyDate));
        MetadataDate = Date(Simple(descriptions, XmpMetadataDate));
    }

    /// <summary>The packet exactly as stored (decoded stream bytes). Do not modify.</summary>
    public ReadOnlyMemory<byte> RawBytes { get; }

    /// <summary>True when the packet parsed as XML (DTDs are never processed).</summary>
    public bool IsWellFormed { get; }

    /// <summary><c>dc:title</c> (the default-language alternative, else the first), or null.</summary>
    public string? Title { get; }

    /// <summary><c>dc:description</c> (default-language alternative), or null.</summary>
    public string? Description { get; }

    /// <summary><c>dc:creator</c> items in order; empty when absent.</summary>
    public IReadOnlyList<string> Creators { get; }

    /// <summary><c>dc:subject</c> items in order; empty when absent.</summary>
    public IReadOnlyList<string> Subjects { get; }

    /// <summary><c>xmp:CreatorTool</c>, or null.</summary>
    public string? CreatorTool { get; }

    /// <summary><c>pdf:Producer</c>, or null.</summary>
    public string? Producer { get; }

    /// <summary><c>pdf:Keywords</c>, or null.</summary>
    public string? Keywords { get; }

    /// <summary><c>xmp:CreateDate</c> when it parses as an ISO 8601 date, with a missing zone read as UTC.</summary>
    public DateTimeOffset? CreateDate { get; }

    /// <summary><c>xmp:ModifyDate</c>, same reading as <see cref="CreateDate"/>.</summary>
    public DateTimeOffset? ModifyDate { get; }

    /// <summary><c>xmp:MetadataDate</c>, same reading as <see cref="CreateDate"/>.</summary>
    public DateTimeOffset? MetadataDate { get; }

    // A property is either a child element or, in the compact form, an attribute of rdf:Description.
    private static string? Simple(List<XElement> descs, XName name)
    {
        foreach (XElement d in descs)
        {
            if (d.Attribute(name) is { } a)
            {
                return a.Value;
            }

            if (d.Element(name) is { } e)
            {
                return !e.HasElements ? e.Value : FirstItem(e);
            }
        }

        return null;
    }

    private static string? FirstItem(XElement e) => e.Descendants(RdfLi).FirstOrDefault()?.Value;

    private static string? Alt(List<XElement> descs, XName name)
    {
        foreach (XElement d in descs)
        {
            if (d.Element(name) is not { } e)
            {
                continue;
            }

            var items = e.Descendants(RdfLi).ToList();
            XElement? pick = items.FirstOrDefault(static i => (string?)i.Attribute(XmlLang) == "x-default") ?? items.FirstOrDefault();
            return pick?.Value ?? (!e.HasElements ? e.Value : null);
        }

        return Simple(descs, name);
    }

    private static List<string> Items(List<XElement> descs, XName name)
    {
        foreach (XElement d in descs)
        {
            if (d.Element(name) is { } e)
            {
                return [.. e.Descendants(RdfLi).Select(static i => i.Value)];
            }
        }

        return [];
    }

    private static DateTimeOffset? Date(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset d) ? d : null;
}

public sealed partial class PdfDocument
{
    /// <summary>The document information dictionary; never null (an empty one when the file has none).</summary>
    public DocumentInfo Info => InfoLazy().Value;

    private Lazy<DocumentInfo>? _info;

    /// <summary>
    /// The catalog's XMP metadata, or null when there is none, when it is larger than <see cref="PdfLimits.MaxMetadataBytes"/>
    /// (a diagnostic is recorded) or when its stream cannot be read.
    /// </summary>
    public XmpMetadata? Xmp => XmpLazy().Value;

    private Lazy<XmpMetadata?>? _xmp;

    private XmpMetadata? LoadXmp()
    {
        if (Catalog.Get("Metadata") is not PdfStream s)
        {
            return null;
        }

        if (s.Dictionary.GetInt("Length") > Limits.MaxMetadataBytes)
        {
            _diag.Warn("The XMP metadata stream is larger than the metadata limit; ignored.", s.Reference?.Number ?? -1);
            return null;
        }

        ReadOnlyMemory<byte> data = s.GetDecodedBytes();
        if (data.Length > Limits.MaxMetadataBytes)
        {
            _diag.Warn("The XMP metadata is larger than the metadata limit; ignored.", s.Reference?.Number ?? -1);
            return null;
        }

        return new XmpMetadata(data);
    }
}
