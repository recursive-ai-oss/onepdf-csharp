namespace OnePdf;

/// <summary>Content interpretation for pages.</summary>
public static class PdfPageInterpretExtensions
{
    /// <summary>
    /// Runs the page's content streams, then its annotation appearances, against <paramref name="device"/>: BeginPage, the
    /// drawing calls in content order, EndPage. Damaged content is skipped operator by operator and recorded in
    /// <see cref="PdfDocument.Diagnostics"/>; EndPage and every bracket close even when a limit stops the page or the token is
    /// cancelled. The output space comes from <paramref name="options"/> (crop box, rotation and user unit applied by default).
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="device">Receives the calls, on the calling thread.</param>
    /// <param name="options">Output space and behavior; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the interpretation with <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="PdfLimitException">A stream is larger than the decoded-size limit.</exception>
    public static void Interpret(this PdfPage page, IPdfDevice device, PdfInterpretOptions? options = null, CancellationToken cancellationToken = default) =>
        Content.ContentInterpreter.Run(page, device, options, cancellationToken);
}
