using System;

namespace OnePdf.Benchmarks;

/// <summary>
/// The one place the benchmarks touch OnePDF's text extraction.
/// <list type="bullet">
/// <item><see cref="ExtractText"/>: the default plain text (reading order) of every page through the whole-document call, with
/// <c>degree</c> pages in flight at once (1 is sequential). Returns a number that depends on the text length.</item>
/// <item><see cref="Words"/>: every word with its position box, same degree rule.</item>
/// </list>
/// </summary>
internal static class OnePdfTextSeam
{
    /// <summary>True: the benchmarks run against the real extraction API.</summary>
    public static bool Available => true;

    public static long ExtractText(PdfDocument document, int degree)
    {
        long total = 0;
        foreach (OnePdf.Text.PdfPageText page in document.ExtractText(new OnePdf.Text.PdfTextOptions { MaxDegreeOfParallelism = degree }))
        {
            total += page.Text.Length;
        }

        return total;
    }

    public static long Words(PdfDocument document, int degree)
    {
        long total = 0;
        foreach (OnePdf.Text.PdfPageText page in document.ExtractText(new OnePdf.Text.PdfTextOptions { MaxDegreeOfParallelism = degree }))
        {
            foreach (OnePdf.Text.PdfTextWord word in page.Words)
            {
                total += word.Text.Length + (long)(word.Bounds.Left + word.Bounds.Bottom + word.Bounds.Right + word.Bounds.Top);
            }
        }

        return total;
    }
}

/// <summary>Marks a benchmark that cannot run yet: it needs text extraction (the seam above) or a tool that is not installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PendingAttribute(string needs) : Attribute
{
    /// <summary>What the benchmark waits for: <c>text</c> or <c>mutool</c>.</summary>
    public string Needs { get; } = needs;

    /// <summary>True when the thing it waits for is there.</summary>
    public bool IsMet => Needs switch
    {
        "text" => OnePdfTextSeam.Available,
        "mutool" => Ops.MutoolPath is not null,
        _ => false,
    };
}
