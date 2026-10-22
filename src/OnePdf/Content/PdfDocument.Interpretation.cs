namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>Records a diagnostic from content interpretation; in strict mode a warning or worse throws.</summary>
    internal void ReportContent(PdfSeverity severity, string message, int objectNumber)
    {
        switch (severity)
        {
            case PdfSeverity.Info:
                _diag.Info(message, objectNumber);
                break;
            case PdfSeverity.Warning:
                _diag.Warn(message, objectNumber);
                break;
            default:
                _diag.Error(message, objectNumber);
                break;
        }
    }
}
