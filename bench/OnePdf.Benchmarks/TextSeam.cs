using System;

namespace OnePdf.Benchmarks;

/// <summary>
/// The one place the benchmarks touch OnePDF's text extraction. Until the extraction API is on this branch, rows that need it
/// are marked <see cref="PendingAttribute"/> and left out of runs; when it lands, fill in the two methods, set
/// <see cref="Available"/> to true and the rows run with no other change.
/// <list type="bullet">
/// <item><see cref="ExtractText"/>: the default plain text (reading order) of every page; at <c>degree</c> above 1 the
/// whole-document call running pages in parallel with that many workers. Return a number that depends on the text length.</item>
/// <item><see cref="Words"/>: every word with its position (at least the box corners are read), same degree rule.</item>
/// </list>
/// </summary>
internal static class OnePdfTextSeam
{
    /// <summary>True once the two methods below call the real extraction API.</summary>
    public static bool Available => false;

    public static long ExtractText(PdfDocument document, int degree) =>
        throw new NotSupportedException("Text extraction is not wired into the benchmarks yet.");

    public static long Words(PdfDocument document, int degree) =>
        throw new NotSupportedException("Text extraction is not wired into the benchmarks yet.");
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
