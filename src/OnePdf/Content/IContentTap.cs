namespace OnePdf.Content;

/// <summary>
/// Observes where the interpreter is in the bytes of the content streams it runs. Content rewriting (redaction) uses it to
/// pair the device calls an operator caused with the operator's bytes. Calls come from the interpreting thread only.
/// </summary>
internal interface IContentTap
{
    /// <summary>A content stream starts: the page's (joined) content, a form XObject or an annotation appearance. <paramref name="resources"/> is what its names resolve against.</summary>
    void StreamBegin(ReadOnlyMemory<byte> data, PdfDictionary? resources);

    /// <summary>The operator that occupies <c>[start, end)</c> of the current stream, with its operands, has been executed (its device calls have been made).</summary>
    void Operator(ContentOp op, int start, int end);

    /// <summary>The current stream ended.</summary>
    void StreamEnd();

    /// <summary>Content was skipped by the interpreter (undecodable stream, limit), so it cannot have been checked.</summary>
    void CannotVerify(string reason);
}
