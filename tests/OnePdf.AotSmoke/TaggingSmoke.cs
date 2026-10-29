using System;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Text;

namespace OnePdf.AotSmoke;

/// <summary>Writes an untagged page with a heading and a paragraph, tags it through the happy path and the plan API, and checks the tree and the text.</summary>
internal static class TaggingSmoke
{
    public static string? Run()
    {
        using var source = new MemoryStream();
        using (PdfDocument doc = PdfDocument.CreateEmpty())
        {
            PdfPage page = doc.InsertBlankPage(0, 300, 300);
            PdfCreatedFont font = doc.AddStandardFont("Helvetica");
            using (PdfPageContentBuilder b = page.BeginContent())
            {
                b.SetFont(font, 14);
                b.DrawText("Heading words", 10, 250);
                b.DrawText("Paragraph words here", 10, 150);
                b.Commit();
            }

            doc.Save(source, new PdfSaveOptions { DocumentId = new byte[16] });
        }

        byte[] original = source.ToArray();
        string? happy = Tag(original, useHappyPath: true);
        return happy ?? Tag(original, useHappyPath: false);
    }

    private static string? Tag(byte[] original, bool useHappyPath)
    {
        using PdfDocument edit = PdfDocument.Open(original, new PdfOpenOptions { AllowEditing = true });
        var options = new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false };
        string before = string.Concat(edit.ExtractText(options).Select(static p => p.Text));
        System.Collections.Generic.IReadOnlyList<PdfPageText> pages = edit.ExtractText(options);
        if (pages[0].Blocks.Count != 2)
        {
            return "expected 2 blocks, got " + pages[0].Blocks.Count;
        }

        if (useHappyPath)
        {
            edit.Tag(pages, static b => b.BlockIndex == 0 ? PdfBlockRole.Heading(1) : PdfBlockRole.Paragraph, new PdfTagOptions { Title = "T", Language = "en" });
        }
        else
        {
            PdfTaggingPlan plan = edit.CreateTaggingPlan();
            plan.Title = "T";
            plan.Language = "en";
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
            edit.ApplyTagging(plan);
        }

        using var saved = new MemoryStream();
        edit.Save(saved);
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        if (back.StructureTreeRoot is null || string.Concat(back.ExtractText(options).Select(static p => p.Text)) != before)
        {
            return "tagging changed the text or wrote no tree";
        }

        if (back.CheckPdfUA().Any(static v => v.Message.Contains("neither tagged", StringComparison.Ordinal)))
        {
            return "untagged content after tagging";
        }

        // Taking the tags out again leaves the text as it was and the file taggable.
        using PdfDocument again = PdfDocument.Open(saved.ToArray(), new PdfOpenOptions { AllowEditing = true });
        PdfRemoveTagsResult removed = again.RemoveTags();
        if (!removed.StructureTreeRemoved || removed.SequencesRemoved == 0 || again.StructureTreeRoot is not null)
        {
            return "RemoveTags left the tree or removed nothing";
        }

        if (string.Concat(again.ExtractText(options).Select(static p => p.Text)) != before)
        {
            return "RemoveTags changed the text";
        }

        again.Tag(again.ExtractText(options), static _ => PdfBlockRole.Paragraph);
        if (again.StructureTreeRoot is null)
        {
            return "tagging after RemoveTags wrote no tree";
        }

        return EditTree(again, before);
    }

    // Reads the tree, changes it, saves and reads it back.
    private static string? EditTree(PdfDocument doc, string text)
    {
        PdfStructureTree? tree = doc.StructureTree;
        if (tree is null || tree.ElementCount < 2)
        {
            return "the structure tree was not read";
        }

        PdfStructureNode paragraph = tree.Elements().First(static n => n.StandardType == "P");
        if (paragraph.GetText().Length == 0)
        {
            return "an element has no text";
        }

        paragraph.Alt = "alt";
        paragraph.SetType(PdfTagRole.H2);
        paragraph.Parent!.WrapChildren("Sect", 0, 1);
        tree.MapRole("Custom", "Span");
        paragraph.Remove(PdfStructureRemoval.Unwrap);
        using var saved = new MemoryStream();
        doc.Save(saved);
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        PdfStructureTree? again = back.StructureTree;
        if (again is null || again.Diagnostics.Count != 0 || again.RoleMap["Custom"] != "Span")
        {
            return "the edited tree does not read back cleanly";
        }

        string after = string.Concat(back.ExtractText(new PdfTextOptions { UseStructureTree = false }).Select(static p => p.Text));
        return after == text ? null : "editing the tree changed the text";
    }
}
