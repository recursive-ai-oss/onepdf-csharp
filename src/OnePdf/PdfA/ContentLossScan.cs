using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf.PdfA;

/// <summary>What a conformance save would have to leave out, and which part of the document it belonged to.</summary>
internal enum LossKind
{
    /// <summary>An embedded font program (<c>FontFile</c>, <c>FontFile2</c>, <c>FontFile3</c>), or another part of a font.</summary>
    Font,

    /// <summary>An ICC profile (output intent or <c>ICCBased</c> colour space).</summary>
    IccProfile,

    /// <summary>An image XObject or inline-image data.</summary>
    Image,

    /// <summary>A page, form XObject, Type 3 glyph or pattern content stream.</summary>
    Content,

    /// <summary>The XMP metadata stream.</summary>
    Metadata,

    /// <summary>An embedded file.</summary>
    EmbeddedFile,

    /// <summary>Anything else: structure elements, pages, annotations, other streams.</summary>
    Other,
}

/// <summary>One thing a save would lose, as the violation text and the places it is filed under.</summary>
internal sealed record ContentLoss(LossKind Kind, int ObjectNumber, int Generation, int PageIndex, string Message);

/// <summary>
/// Finds every object a full save of the document would write as less than it is: a stream whose bytes cannot be read, cannot be decrypted
/// cleanly, or whose filter data is cut short or invalid, and a reference to a part the document needs (a font program, a profile, a content
/// stream, a page, an annotation, a structure element) that leads to no object. A normal save keeps such damage as it is, or writes
/// <c>null</c> for the missing object; a PDF/A or PDF/UA save must not, because it would then claim conformance for a file that lost content,
/// so both the checks and the saves report these findings and the save refuses.
/// </summary>
/// <remarks>
/// The walk visits what the saver visits: the pages first (so a finding carries its page), then the catalog and the Info dictionary, following
/// every reference once. Parts that the preparers rebuild anyway when they are absent (appearance streams, output intents, the XMP packet
/// itself) are not reported when merely absent, but an XMP packet or any other stream that exists and cannot be read is.
/// </remarks>
internal static class ContentLossScan
{
    // References under these keys point at parts without which the object holding them loses meaning; a reference that leads nowhere there
    // means content went missing. Each entry is the key and what it stands for.
    private static readonly Dictionary<string, LossKind> NeededKeys = new(StringComparer.Ordinal)
    {
        ["FontFile"] = LossKind.Font,
        ["FontFile2"] = LossKind.Font,
        ["FontFile3"] = LossKind.Font,
        ["FontDescriptor"] = LossKind.Font,
        ["DescendantFonts"] = LossKind.Font,
        ["ToUnicode"] = LossKind.Font,
        ["CIDToGIDMap"] = LossKind.Font,
        ["Widths"] = LossKind.Font,
        ["W"] = LossKind.Font,
        ["Encoding"] = LossKind.Font,
        ["DestOutputProfile"] = LossKind.IccProfile,
        ["ICCBased"] = LossKind.IccProfile,
        ["Contents"] = LossKind.Content,
        ["SMask"] = LossKind.Image,
        ["Mask"] = LossKind.Image,
        ["EF"] = LossKind.EmbeddedFile,
        ["Annots"] = LossKind.Other,
        ["Kids"] = LossKind.Other,
        ["Pages"] = LossKind.Other,
        ["StructTreeRoot"] = LossKind.Other,
        ["AF"] = LossKind.EmbeddedFile,
    };

    // Resource categories: every entry of such a dictionary is a part the page's content refers to by name.
    private static readonly Dictionary<string, LossKind> Categories = new(StringComparer.Ordinal)
    {
        ["Font"] = LossKind.Font,
        ["XObject"] = LossKind.Other,
        ["ColorSpace"] = LossKind.Other,
        ["Pattern"] = LossKind.Other,
        ["Shading"] = LossKind.Other,
        ["ExtGState"] = LossKind.Other,
        ["Properties"] = LossKind.Other,
        ["CharProcs"] = LossKind.Content,
    };

    private readonly record struct Item(PdfObject Value, int Holder, string? Key, bool Needed, LossKind Hint, int Page, string? Label = null);

    /// <summary>Walks the document and returns every finding, each object once.</summary>
    /// <exception cref="PdfLimitException">The document has more objects than <see cref="PdfLimits.MaxObjects"/>.</exception>
    public static List<ContentLoss> Run(PdfDocument doc, CancellationToken ct)
    {
        var losses = new List<ContentLoss>();
        var seen = new HashSet<int>();
        var stack = new Stack<Item>();
        int maxObjects = doc.Limits.MaxObjects;

        PdfPageCollection pages = doc.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PdfDictionary pd;
            try
            {
                pd = pages[i].Dictionary;
            }
            catch (PdfFormatException)
            {
                continue; // the checkers report an unreadable page themselves
            }

            stack.Push(new Item(pd.Reference is { } r ? r : pd, -1, null, false, LossKind.Other, i));
            Drain(doc, stack, seen, losses, maxObjects, ct);
        }

        PdfObject? root = doc.Trailer.GetRaw("Root");
        if (root is not null)
        {
            stack.Push(new Item(root, -1, "Root", false, LossKind.Other, -1));
        }

        if (doc.Trailer.GetRaw("Info") is { } info)
        {
            stack.Push(new Item(info, -1, "Info", false, LossKind.Other, -1));
        }

        Drain(doc, stack, seen, losses, maxObjects, ct);
        return losses;
    }

    private static void Drain(PdfDocument doc, Stack<Item> stack, HashSet<int> seen, List<ContentLoss> losses, int maxObjects, CancellationToken ct)
    {
        int steps = 0;
        while (stack.Count > 0)
        {
            if ((++steps & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            Item item = stack.Pop();
            PdfObject value = item.Value;
            int owner = item.Holder;
            if (value is PdfReference reference)
            {
                PdfObject target = doc.ResolveChain(reference.Number, 0);
                if (target is PdfNull)
                {
                    if (item.Needed && seen.Add(-reference.Number - 1))
                    {
                        losses.Add(Missing(item, reference));
                    }

                    continue;
                }

                int number = target.Reference?.Number ?? reference.Number;
                if (!seen.Add(number))
                {
                    continue;
                }

                if (seen.Count > maxObjects)
                {
                    throw new PdfLimitException($"The document has more than {maxObjects} objects.");
                }

                value = target;
                owner = number;
            }

            switch (value)
            {
                case PdfStream s:
                {
                    string? type = s.Dictionary.GetName("Type");
                    if (type is not ("XRef" or "ObjStm") && s.FindUnreadableReason(ct) is { } reason)
                    {
                        losses.Add(Unreadable(s, item, owner, reason));
                    }

                    PushDictionary(stack, s.Dictionary, owner, item.Page, inStream: true);
                    break;
                }

                case PdfString { DecryptDamage: not DecryptIssues.None } damaged:
                    losses.Add(DamagedString(damaged, item, owner));
                    break;

                case PdfDictionary d:
                    PushDictionary(stack, d, owner, item.Page, inStream: false, item.Key);
                    break;

                case PdfArray a:
                    PushArray(stack, a, owner, item);
                    break;
            }
        }
    }

    private static void PushDictionary(Stack<Item> stack, PdfDictionary d, int owner, int page, bool inStream, string? viaKey = null)
    {
        bool everyEntry = viaKey is not null && Categories.ContainsKey(viaKey);
        LossKind categoryHint = viaKey is not null && Categories.TryGetValue(viaKey, out LossKind ck) ? ck : LossKind.Other;
        bool structure = d.GetName("Type") is "StructTreeRoot" or "StructElem" || (d.ContainsKey("S") && d.ContainsKey("P"));
        for (int i = 0; i < d.RawCount; i++)
        {
            string key = d.KeyAt(i);
            PdfObject raw = d.ValueAt(i);
            if (raw is PdfName or PdfInteger or PdfReal or PdfBoolean or PdfNull || raw is PdfString { DecryptDamage: DecryptIssues.None })
            {
                continue;
            }

            bool needed = everyEntry;
            LossKind hint = categoryHint;
            if (NeededKeys.TryGetValue(key, out LossKind nk))
            {
                needed = true;
                hint = nk;
            }
            else if (key == "K" && structure)
            {
                needed = true;
            }
            else if (key == "F" && viaKey == "EF")
            {
                needed = true;
                hint = LossKind.EmbeddedFile;
            }

            stack.Push(new Item(raw, owner, key, needed, hint, page, everyEntry ? viaKey : null));
        }
    }

    private static void PushArray(Stack<Item> stack, PdfArray a, int owner, Item parent)
    {
        // A colour space array: [/ICCBased profile]. Its second element is the profile, whatever key the array sits under.
        bool icc = a.Count >= 2 && a.GetName(0) == "ICCBased";
        for (int i = 0; i < a.Count; i++)
        {
            PdfObject raw = a.GetRaw(i);
            if (raw is PdfName or PdfInteger or PdfReal or PdfBoolean or PdfNull || raw is PdfString { DecryptDamage: DecryptIssues.None })
            {
                continue;
            }

            if (icc && i == 1)
            {
                stack.Push(new Item(raw, owner, "ICCBased", true, LossKind.IccProfile, parent.Page));
            }
            else
            {
                stack.Push(new Item(raw, owner, parent.Key, parent.Needed, parent.Hint, parent.Page, parent.Label));
            }
        }
    }

    // ---- wording ----------------------------------------------------------------------------------------------------------------

    private static string Describe(string? key, LossKind hint, PdfDictionary? streamDict)
    {
        if (streamDict is not null)
        {
            string? subtype = streamDict.GetName("Subtype");
            string? type = streamDict.GetName("Type");
            if (type == "Metadata")
            {
                return "XMP metadata stream";
            }

            if (type == "EmbeddedFile")
            {
                return "embedded file";
            }

            if (subtype == "Image")
            {
                return "image";
            }

            if (subtype == "Form")
            {
                return "form XObject content stream";
            }
        }

        return key switch
        {
            "FontFile" or "FontFile2" or "FontFile3" => "embedded font program",
            "FontDescriptor" => "font descriptor",
            "DescendantFonts" => "descendant font",
            "ToUnicode" => "ToUnicode map",
            "CIDToGIDMap" => "CIDToGIDMap",
            "Widths" or "W" => "font width table",
            "Encoding" => "font encoding",
            "DestOutputProfile" or "ICCBased" => "ICC profile",
            "Contents" => "page content stream",
            "SMask" or "Mask" => "image mask",
            "CharProcs" => "Type 3 glyph procedure",
            "EF" or "F" when hint == LossKind.EmbeddedFile => "embedded file",
            "Annots" => "annotation",
            "Kids" => "page tree or structure node",
            "Pages" => "page tree",
            "StructTreeRoot" => "structure tree",
            "K" => "structure element",
            "Font" => "font",
            "XObject" => "XObject",
            "ColorSpace" => "colour space",
            "Pattern" => "pattern",
            "Shading" => "shading",
            "ExtGState" => "graphics state",
            "Properties" => "marked-content property list",
            _ => hint switch
            {
                LossKind.Font => "font part",
                LossKind.Content => "content stream",
                LossKind.Image => "image",
                _ => streamDict is not null ? "stream" : "object",
            },
        };
    }

    private static ContentLoss Unreadable(PdfStream s, Item item, int owner, string reason)
    {
        string what = Describe(item.Label ?? item.Key, item.Hint, s.Dictionary);
        LossKind kind = KindOf(what, item.Hint);
        int gen = s.Reference?.Generation ?? 0;
        return new ContentLoss(kind, owner, gen, item.Page, $"The {what} (object {owner} {gen}) cannot be saved without loss: {reason}.");
    }

    // A string whose ciphertext was damaged (bad padding, a partial last block) was kept as far as it decrypted. A plain save writes those bytes
    // again; a conformance save must not, because the text it would claim to preserve is not the original.
    private static ContentLoss DamagedString(PdfString s, Item item, int owner)
    {
        string where = item.Key is { } k ? $"string under /{k}" : "string";
        LossKind kind = item.Hint is LossKind.Other ? LossKind.Other : item.Hint;
        return new ContentLoss(kind, owner, 0, item.Page, $"The {where} (object {owner}) cannot be saved without loss: it cannot be decrypted cleanly ({s.DecryptDamage}).");
    }

    private static ContentLoss Missing(Item item, PdfReference reference)
    {
        string what = Describe(item.Label ?? item.Key, item.Hint, null);
        LossKind kind = KindOf(what, item.Hint);
        string holder = item.Holder >= 0 ? $"object {item.Holder}" : "the document";
        return new ContentLoss(
            kind,
            reference.Number,
            reference.Generation,
            item.Page,
            $"The {what} (object {reference.Number} {reference.Generation}) that {holder} refers to does not exist or cannot be parsed, and a save would write null in its place.");
    }

    private static LossKind KindOf(string what, LossKind hint) => what switch
    {
        "embedded font program" or "font descriptor" or "descendant font" or "ToUnicode map" or "CIDToGIDMap" or "font width table" or "font encoding" or "font part" or "font" => LossKind.Font,
        "ICC profile" => LossKind.IccProfile,
        "image" or "image mask" => LossKind.Image,
        "page content stream" or "form XObject content stream" or "Type 3 glyph procedure" or "content stream" => LossKind.Content,
        "XMP metadata stream" => LossKind.Metadata,
        "embedded file" => LossKind.EmbeddedFile,
        _ => hint is LossKind.Font or LossKind.Content or LossKind.Image or LossKind.IccProfile or LossKind.EmbeddedFile && what is "stream" or "object" ? hint : LossKind.Other,
    };
}
