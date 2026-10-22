using System;
using System.Collections.Generic;

namespace OnePdf.Filters;

/// <summary>
/// Reads <c>/DecodeParms</c> (or the inline-image <c>/DP</c>) into <see cref="FilterParameters"/>. Stream objects and
/// inline images share this code, so both apply the same defaults (ISO 32000-2 7.4.4.3 Table 8, 7.4.6 Table 11).
/// </summary>
internal static class FilterParametersReader
{
    /// <summary>
    /// The parameter dictionary for the filter at <paramref name="index"/>. A lone dictionary belongs to a lone filter or
    /// the first of an array (ISO 32000-2 7.3.8.2 Table 5: the array form is parallel to <c>/Filter</c>); null entries and
    /// anything that is not a dictionary mean "no parameters".
    /// </summary>
    public static PdfDictionary? DictionaryAt(PdfObject? decodeParms, int index) => decodeParms switch
    {
        PdfDictionary d => index == 0 ? d : null,
        PdfArray a when index >= 0 && index < a.Count => a[index] as PdfDictionary,
        _ => null,
    };

    /// <summary>
    /// Reads one filter's parameters. Absent keys keep their defaults; CCITTFaxDecode <c>/Columns</c> defaults to 1728
    /// (Table 11), every other filter's to 1 (Table 8). Without a dictionary the result is null, which the filter layer
    /// reads as all defaults (including 1728 columns).
    /// </summary>
    public static FilterParameters? From(PdfDictionary? p, string filterName)
    {
        if (p is null)
        {
            return null;
        }

        bool ccitt = filterName == "CCITTFaxDecode";
        return new FilterParameters(
            Predictor: Clamp(p.GetInt("Predictor", 1)),
            Colors: Clamp(p.GetInt("Colors", 1)),
            BitsPerComponent: Clamp(p.GetInt("BitsPerComponent", 8)),
            Columns: Clamp(p.GetInt("Columns", 1)),
            EarlyChange: Clamp(p.GetInt("EarlyChange", 1)),
            K: ccitt ? Clamp(p.GetInt("K", 0)) : 0,
            EndOfLine: ccitt && p.GetBool("EndOfLine"),
            EncodedByteAlign: ccitt && p.GetBool("EncodedByteAlign"),
            CcittColumns: ccitt ? Clamp(p.GetInt("Columns", 1728)) : 1728,
            Rows: ccitt ? Clamp(p.GetInt("Rows", 0)) : 0,
            EndOfBlock: !ccitt || p.GetBool("EndOfBlock", true),
            BlackIs1: ccitt && p.GetBool("BlackIs1"),
            DamagedRowsBeforeError: ccitt ? Clamp(p.GetInt("DamagedRowsBeforeError", 0)) : 0);
    }

    /// <summary>Parameters for every filter of a chain, parallel to <paramref name="filterNames"/>.</summary>
    public static FilterParameters?[] ForChain(PdfObject? decodeParms, IReadOnlyList<string> filterNames)
    {
        var result = new FilterParameters?[filterNames.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = From(DictionaryAt(decodeParms, i), filterNames[i]);
        }

        return result;
    }

    private static int Clamp(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);
}
