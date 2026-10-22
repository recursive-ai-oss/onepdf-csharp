namespace OnePdf;

/// <summary>Parsing of PDF date strings.</summary>
public static class PdfDate
{
    /// <summary>
    /// Parses a date string of the form <c>D:YYYYMMDDHHmmSSOHH'mm'</c> (ISO 32000-2 7.9.4). Everything after the year is optional
    /// (month and day default to 1, the rest to 0), the <c>D:</c> prefix may be missing, the zone may be <c>Z</c>, <c>+HH'mm'</c>,
    /// <c>+HHmm</c> or <c>+HH</c>, and a missing zone means UTC. Leading white space and trailing text are ignored.
    /// A second of 60 becomes 59. Returns false when the year or any given field is out of range.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        int pos = 0;
        if (!Digits(s, ref pos, 4, out int year) || year < 1)
        {
            return false;
        }

        int month = 1, day = 1, hour = 0, minute = 0, second = 0;
        if (Digits(s, ref pos, 2, out int v))
        {
            month = v;
            if (Digits(s, ref pos, 2, out v))
            {
                day = v;
                if (Digits(s, ref pos, 2, out v))
                {
                    hour = v;
                    if (Digits(s, ref pos, 2, out v))
                    {
                        minute = v;
                        if (Digits(s, ref pos, 2, out v))
                        {
                            second = Math.Min(v, 59);
                        }
                    }
                }
            }
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
        {
            return false;
        }

        TimeSpan offset = TimeSpan.Zero;
        if (pos < s.Length && s[pos] is '+' or '-')
        {
            int sign = s[pos++] == '-' ? -1 : 1;
            int oh = 0, om = 0;
            if (Digits(s, ref pos, 2, out int h))
            {
                oh = h;
                if (pos < s.Length && s[pos] == '\'')
                {
                    pos++;
                }

                if (Digits(s, ref pos, 2, out int m))
                {
                    om = m;
                }
            }

            if (oh <= 14 && om <= 59)
            {
                offset = new TimeSpan(sign * oh, sign * om, 0);
            }
        }

        if (offset > TimeSpan.FromHours(14) || offset < TimeSpan.FromHours(-14))
        {
            offset = TimeSpan.Zero;
        }

        // The constructor throws for instants outside year 1..9999 once the offset is applied; keep clear of the edges.
        if (year > 9998 || year < 2)
        {
            offset = TimeSpan.Zero;
        }

        value = new DateTimeOffset(year, month, day, hour, minute, second, offset);
        return true;
    }

    private static bool Digits(ReadOnlySpan<char> s, ref int pos, int count, out int value)
    {
        value = 0;
        if (pos + count > s.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            char c = s[pos + i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        pos += count;
        return true;
    }
}
