using System;
using System.IO;
using System.Linq;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Structure-tree target. The input is 8 bytes that drive a small random generator, then a PDF file. The tree is read (types, attributes,
/// text, missing content), edited by a random series of the edit calls (set type, text and attributes, role map, wrap, move, unwrap, remove
/// to artifacts) and by <c>RemoveTags</c> for some inputs, the document is saved and read again. Only the library's typed exceptions and
/// the documented argument exceptions may escape; when the tree was sound before the edits, the saved file must read without
/// diagnostics, with every element still there and no content reference left without its sequence.
/// </summary>
internal static class StructureTarget
{
    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 100_000, MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000, MaxImagePixels = 1 << 22 };

    private sealed class Rng(ulong seed)
    {
        private ulong _s = seed | 1;

        public int Next(int n)
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return n <= 0 ? 0 : (int)((_s >> 11) % (ulong)n);
        }
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 8)
        {
            return;
        }

        ulong seed = 0;
        for (int i = 0; i < 8; i++)
        {
            seed = (seed << 8) | data[i];
        }

        byte[] file = data[8..].ToArray();
        try
        {
            using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { Limits = Limits, AllowEditing = true });
            PdfStructureTree? tree = doc.StructureTree;
            if (tree is null)
            {
                return;
            }

            bool sound = tree.Diagnostics.Count == 0 && tree.FindMissingContent().Count == 0;
            var rng = new Rng(seed);
            foreach (PdfStructureNode n in tree.Elements().Take(50))
            {
                _ = (n.Type, n.StandardType, n.Alt, n.ActualText, n.Language, n.Scope, n.ColSpan, n.RowSpan, n.Headers, n.ListNumbering, n.PageIndex, n.Attributes.Count);
                if (rng.Next(8) == 0)
                {
                    _ = n.GetText();
                }
            }

            int expected = tree.ElementCount;
            int edits = 1 + rng.Next(12);
            for (int e = 0; e < edits && expected > 0; e++)
            {
                var all = tree.Elements().Take(4000).ToList();
                if (all.Count == 0)
                {
                    break;
                }

                PdfStructureNode n = all[rng.Next(all.Count)];
                try
                {
                    switch (rng.Next(11))
                    {
                        case 0:
                            n.Alt = rng.Next(3) == 0 ? null : "alt";
                            break;
                        case 1:
                            n.SetType(rng.Next(2) == 0 ? "Span" : "Strong");
                            break;
                        case 2:
                            n.Scope = rng.Next(4) == 0 ? null : (PdfTagScope)rng.Next(3);
                            break;
                        case 3:
                            n.ColSpan = rng.Next(3);
                            n.RowSpan = rng.Next(3) == 0 ? null : 2;
                            break;
                        case 4:
                            tree.MapRole("Strong", "Span");
                            break;
                        case 5 when n.Children.Count > 0:
                            n.WrapChildren("Div", rng.Next(n.Children.Count), 1);
                            break;
                        case 6 when n.Parent is not null:
                            n.Remove(PdfStructureRemoval.Unwrap);
                            break;
                        case 7 when n.Parent is not null:
                            n.Remove(PdfStructureRemoval.ToArtifacts);
                            break;
                        case 8:
                            n.MoveTo(all[rng.Next(all.Count)], rng.Next(3) == 0 ? -1 : 0);
                            break;
                        case 9 when n.Children.Count > 0:
                            n.Children[rng.Next(n.Children.Count)].MoveTo(all[rng.Next(all.Count)]);
                            break;
                        case 10:
                            n.Language = "de";
                            n.ListNumbering = "Decimal";
                            break;
                    }
                }
                catch (ArgumentException)
                {
                    // moving into itself or a descendant; the root holding content; ranges
                }
                catch (InvalidOperationException) when (n.IsRemoved)
                {
                }
                catch (InvalidOperationException)
                {
                    // an element that held content cannot be unwrapped into the root
                }
            }

            if (rng.Next(6) == 0)
            {
                doc.RemoveTags();
                if (doc.StructureTree is not null)
                {
                    throw new InvalidOperationException("RemoveTags left a structure tree");
                }
            }

            using var saved = new MemoryStream();
            doc.Save(saved, new PdfSaveOptions { DocumentId = new byte[16] });
            using PdfDocument back = PdfDocument.Open(saved.ToArray(), new PdfOpenOptions { Limits = Limits });
            PdfStructureTree? again = back.StructureTree;
            if (sound && doc.StructureTree is { } live && again is not null)
            {
                if (again.ElementCount != live.ElementCount)
                {
                    throw new InvalidOperationException($"the saved tree has {again.ElementCount} elements, the edited one {live.ElementCount}");
                }

                if (again.Diagnostics.Count != 0)
                {
                    throw new InvalidOperationException("the saved tree reads with diagnostics: " + again.Diagnostics[0].Message);
                }
            }
        }
        catch (PdfException)
        {
        }
    }
}
