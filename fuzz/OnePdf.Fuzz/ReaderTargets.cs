using System;
using System.Collections.Generic;
using System.Linq;
using OnePdf;
using OnePdf.Internal;

namespace OnePdf.Fuzz;

/// <summary>
/// Fuzz targets for the object reader. Each lets only the library's documented exceptions escape; anything else (an
/// index error, a null reference, a stack overflow, a hang) is a finding.
/// </summary>
internal static class ReaderTargets
{
    /// <summary>Tokenizes and parses the bytes as a sequence of PDF objects.</summary>
    public static void Parser(ReadOnlySpan<byte> data)
    {
        try
        {
            using var src = new MemorySource(data.ToArray());
            using var lx = new Lexer(src);
            var limits = PdfLimits.Default;
            var parser = new ObjectParser(lx, null, limits, new DiagnosticBag(false));
            for (int i = 0; i < 100_000; i++)
            {
                long before = lx.Position;
                PdfObject? o = parser.ReadObject(out string? kw);
                if (o is null && kw is null)
                {
                    break;
                }

                if (lx.Position <= before)
                {
                    lx.Position = before + 1;
                }
            }
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>Opens the bytes as a document (xref reading and repair), then touches every object, stream and the page tree.</summary>
    public static void Document(ReadOnlySpan<byte> data)
    {
        var options = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 } };
        try
        {
            using PdfDocument doc = PdfDocument.Open(data.ToArray(), options);
            _ = doc.PageCount;
            int n = Math.Min(doc.ObjectCount, 2000);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    if (doc.GetObject(i) is PdfStream s)
                    {
                        _ = s.GetRawBytes();
                        _ = s.GetDecodedBytes();
                    }
                }
                catch (PdfLimitException)
                {
                }
                catch (PdfFormatException)
                {
                }
            }

            foreach (PdfRevision r in doc.Revisions)
            {
                _ = r.EndOffset;
                _ = r.Catalog;
            }
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>Opens the bytes as a document and walks the whole typed layer: pages, annotations and their actions, outline, labels, metadata, embedded files, form and scripts.</summary>
    public static void DocumentModel(ReadOnlySpan<byte> data)
    {
        var options = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 } };
        try
        {
            using PdfDocument doc = PdfDocument.Open(data.ToArray(), options);
            WalkModel(doc, static (_, _) => { });
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>
    /// Touches every part of the typed layer. A typed library exception from one part is passed to <paramref name="onTyped"/>
    /// and the walk continues with the next part; anything else escapes.
    /// </summary>
    public static void WalkModel(PdfDocument doc, Action<string, PdfException> onTyped)
    {
        Section("pages", () =>
        {
            foreach (PdfPage p in doc.Pages.Take(500))
            {
                _ = (p.MediaBox, p.CropBox, p.Rotate, p.UserUnit, p.Label, p.Resources.Count, p.ContentStreams.Count);
                foreach (PdfAnnotation a in p.Annotations.Take(500))
                {
                    _ = (a.Subtype, a.Rect, a.Contents, a.Title, a.Flags, a.Color, a.QuadPoints, a.HasAppearance, a.ModifiedDate);
                    _ = a.GetAppearanceStream();
                    _ = (a.Destination, a.Uri);
                    if (a.Action is { } act)
                    {
                        _ = (act.ActionType, act.Uri, act.FileName, act.NamedAction, act.JavaScript, act.Destination, act.Next.Count);
                    }

                    _ = a.AttachedFile?.Name;
                }
            }
        });
        Section("outline", () =>
        {
            var stack = new Stack<PdfOutlineItem>(doc.Outline);
            while (stack.Count > 0)
            {
                PdfOutlineItem i = stack.Pop();
                _ = (i.Title, i.Destination, i.Action?.ActionType, i.Color, i.IsOpen);
                foreach (PdfOutlineItem c in i.Children)
                {
                    stack.Push(c);
                }
            }
        });
        Section("labels", () =>
        {
            for (int i = 0; i < Math.Min(doc.Pages.Count, 500); i++)
            {
                _ = doc.PageLabels?.GetLabel(i);
            }
        });
        Section("metadata", () =>
        {
            DocumentInfo info = doc.Info;
            _ = (info.Title, info.Author, info.CreationDate, info.ModificationDate, info.Trapped, info.Entries.Count);
            _ = (doc.Xmp?.Title, doc.Xmp?.Creators.Count, doc.Xmp?.CreateDate);
            _ = (doc.Permissions, doc.Encryption, doc.Language, doc.IsTagged);
        });
        Section("embedded", () =>
        {
            foreach (PdfEmbeddedFile f in doc.EmbeddedFiles.Take(50))
            {
                _ = (f.Name, f.Description, f.MimeType, f.Size, f.CreationDate);
                try
                {
                    _ = f.ReadContent();
                }
                catch (PdfLimitException)
                {
                }
            }
        });
        Section("form", () =>
        {
            if (doc.Form is { } form)
            {
                foreach (PdfFormField f in form.AllFields.Take(2000))
                {
                    _ = (f.FullName, f.Kind, f.Value, f.Values, f.Options, f.Flags, f.MaxLength, f.IsSigned, f.Widgets.Count);
                }

                _ = form.GetXfaXml();
            }
        });
        Section("scripts", () =>
        {
            _ = doc.JavaScript.Count;
            _ = doc.OptionalContentGroups.Count;
            foreach (string name in doc.NamedDestinationNames.Take(500))
            {
                _ = doc.GetNamedDestination(name);
            }
        });

        void Section(string name, Action body)
        {
            try
            {
                body();
            }
            catch (PdfException e)
            {
                onTyped(name, e);
            }
        }
    }
}
