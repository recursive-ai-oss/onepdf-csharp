using System;

namespace OnePdf.Filters;

/// <summary>Internal control flow inside the codecs; never leaves the filter layer.</summary>
internal abstract class FilterException : Exception
{
    protected FilterException(string message)
        : base(message)
    {
    }
}

/// <summary>The compressed data is invalid.</summary>
internal sealed class FilterDataException : FilterException
{
    public FilterDataException(string message)
        : base(message)
    {
    }
}

/// <summary>A decode limit was exceeded.</summary>
internal sealed class FilterLimitException : FilterException
{
    public FilterLimitException(string message)
        : base(message)
    {
    }
}
