namespace OnePdf.Structure;

/// <summary>Reads and rewrites the content streams the structure tree's marked-content references point into.</summary>
internal static class StructureContent
{
    /// <summary>The decoded content of a page: its streams one after the other, as the page draws them.</summary>
    public static ReadOnlyMemory<byte> PageData(PdfDocument doc, PdfPage page, CancellationToken ct)
    {
        IReadOnlyList<PdfStream> streams = page.ContentStreams;
        if (streams.Count == 1)
        {
            return streams[0].GetDecodedBytes(ct);
        }

        var ms = new MemoryStream();
        foreach (PdfStream s in streams)
        {
            ms.Write(s.GetDecodedBytes(ct).Span);
            ms.WriteByte((byte)'\n');
            if (ms.Length > doc.Limits.MaxDecodedStreamBytes)
            {
                throw new PdfLimitException("The page content is larger than the decoded stream limit.");
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Works out how the sequences with these identifiers become artifacts in one content stream (a form, or the page's own content), and adds
    /// the step that stores the result to <paramref name="steps"/>. Nothing in the document changes until the steps run.
    /// </summary>
    /// <exception cref="PdfFormatException">The stream cannot be decoded or read to its end.</exception>
    public static void PrepareArtifacts(PdfDocument doc, MarkedContentRewriter rewriter, int pageIndex, PdfStream? stream, HashSet<int> ids, List<Action> steps)
    {
        ReadOnlyMemory<byte> data;
        PdfDictionary? resources;
        PdfPage? page = pageIndex >= 0 && pageIndex < doc.Pages.Count ? doc.Pages[pageIndex] : null;
        if (stream is not null)
        {
            data = stream.GetDecodedBytes();
            resources = stream.Dictionary.Get("Resources") as PdfDictionary ?? page?.Resources;
        }
        else if (page is not null)
        {
            data = PageData(doc, page, default);
            resources = page.Resources;
        }
        else
        {
            return;
        }

        byte[]? bytes = rewriter.Rewrite(data, resources, (_, _, mcid) => mcid >= 0 && ids.Contains(mcid) ? McAction.ToArtifact : McAction.Keep, out bool complete);
        if (!complete)
        {
            throw new PdfFormatException("The content has an inline image without an end, so its marked content cannot be rewritten safely.");
        }

        if (bytes is null)
        {
            return;
        }

        if (stream is not null)
        {
            bool compress = stream.Dictionary.ContainsKey("Filter");
            steps.Add(() => stream.SetData(bytes, compress));
        }
        else
        {
            steps.Add(() => page!.Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(bytes, compress: true))));
        }
    }
}
