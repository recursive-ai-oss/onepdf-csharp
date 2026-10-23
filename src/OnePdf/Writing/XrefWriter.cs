using OnePdf.Internal;

namespace OnePdf.Writing;

/// <summary>Writes cross-reference sections (ISO 32000-2 7.5.4 tables and 7.5.8 streams) from a sorted list of entries.</summary>
internal static class XrefWriter
{
    // A classic table entry has ten digits of offset.
    private const long MaxTableOffset = 9_999_999_999;

    /// <summary>Runs of consecutive object numbers: (first, count).</summary>
    public static List<(int First, int Count)> Runs(List<(int Num, XrefEntry E)> entries)
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < entries.Count)
        {
            int j = i;
            while (j + 1 < entries.Count && entries[j + 1].Num == entries[j].Num + 1)
            {
                j++;
            }

            runs.Add((entries[i].Num, j - i + 1));
            i = j + 1;
        }

        return runs;
    }

    public static void WriteTable(ByteSink sink, List<(int Num, XrefEntry E)> entries)
    {
        sink.Ascii("xref\n");
        int at = 0;
        foreach ((int first, int count) in Runs(entries))
        {
            sink.Long(first);
            sink.Byte((byte)' ');
            sink.Long(count);
            sink.Byte((byte)'\n');
            for (int k = 0; k < count; k++, at++)
            {
                XrefEntry e = entries[at].E;
                if (e.Type == 2)
                {
                    throw new InvalidOperationException("A classic cross-reference table cannot hold a compressed object.");
                }

                if (e.A > MaxTableOffset)
                {
                    throw new PdfLimitException("The file is too large for a classic cross-reference table; save with PdfXrefFormat.Stream.");
                }

                sink.Padded(e.A, 10);
                sink.Byte((byte)' ');
                sink.Padded(e.B, 5);
                sink.Ascii(e.Type == 1 ? " n \n" : " f \n");
            }
        }
    }

    /// <summary>
    /// The rows of a cross-reference stream, fields of width 1, <paramref name="w2"/> and <paramref name="w3"/> bytes, each row
    /// stored with the PNG "Up" predictor (/Predictor 12), which makes the rows compress far better.
    /// </summary>
    public static byte[] StreamRows(List<(int Num, XrefEntry E)> entries, out int w2, out int w3)
    {
        long max2 = 0;
        long max3 = 0;
        foreach ((_, XrefEntry e) in entries)
        {
            max2 = Math.Max(max2, e.A);
            max3 = Math.Max(max3, e.B);
        }

        w2 = WidthFor(max2);
        w3 = WidthFor(max3);
        int row = 1 + w2 + w3;
        byte[] raw = new byte[checked(entries.Count * row)];
        for (int i = 0; i < entries.Count; i++)
        {
            XrefEntry e = entries[i].E;
            int p = i * row;
            raw[p++] = e.Type;
            for (int b = w2 - 1; b >= 0; b--)
            {
                raw[p++] = (byte)(e.A >> (8 * b));
            }

            for (int b = w3 - 1; b >= 0; b--)
            {
                raw[p++] = (byte)(e.B >> (8 * b));
            }
        }

        // Predictor rows are one tag byte longer: tag 2 (Up), then each byte minus the byte above it.
        byte[] predicted = new byte[checked(entries.Count * (row + 1))];
        for (int i = 0; i < entries.Count; i++)
        {
            int dst = i * (row + 1);
            predicted[dst] = 2;
            for (int c = 0; c < row; c++)
            {
                byte above = i == 0 ? (byte)0 : raw[((i - 1) * row) + c];
                predicted[dst + 1 + c] = (byte)(raw[(i * row) + c] - above);
            }
        }

        return predicted;
    }

    private static int WidthFor(long max)
    {
        int w = 1;
        while (w < 8 && (max >> (8 * w)) != 0)
        {
            w++;
        }

        return w;
    }
}
