namespace OnePdf;

/// <summary>Base type of every exception OnePDF raises for a problem with a document or a configured bound.</summary>
public class PdfException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>The indirect object number the problem was found in, or -1 when not known.</summary>
    public int ObjectNumber { get; init; } = -1;

    /// <summary>The byte offset in the file the problem was found at, or -1 when not known.</summary>
    public long Offset { get; init; } = -1;
}

/// <summary>The file is not a PDF, or damage could not be repaired (or, in strict mode, any repair was needed).</summary>
public sealed class PdfFormatException : PdfException
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfFormatException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfFormatException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The document needs a password and none, or a wrong one, was supplied.</summary>
public sealed class PdfPasswordException : PdfException
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfPasswordException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfPasswordException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The document uses an encryption scheme that cannot be handled.</summary>
public sealed class PdfEncryptionException : PdfException
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfEncryptionException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfEncryptionException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>A <see cref="PdfLimits"/> bound was exceeded. Raised before the work or allocation the bound guards.</summary>
public sealed class PdfLimitException : PdfException
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfLimitException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfLimitException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The file or stream changed after the document was opened, so positions recorded at open are no longer valid.</summary>
public sealed class PdfSourceChangedException : PdfException
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfSourceChangedException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PdfSourceChangedException(string message, Exception? innerException) : base(message, innerException) { }
}
