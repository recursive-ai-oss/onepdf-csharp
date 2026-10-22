namespace OnePdf.Internal;

/// <summary>Thread-safe diagnostic collection. In strict mode a warning or worse throws instead of being recorded.</summary>
internal sealed class DiagnosticBag
{
    private const int MaxRecorded = 10_000;
    private readonly bool _strict;
    private readonly List<PdfDiagnostic> _items = [];
    private bool _overflowed;

    public DiagnosticBag(bool strict) => _strict = strict;

    public void Info(string message, int obj = -1, long offset = -1) => Add(PdfSeverity.Info, message, obj, offset);

    public void Warn(string message, int obj = -1, long offset = -1) => Add(PdfSeverity.Warning, message, obj, offset);

    public void Error(string message, int obj = -1, long offset = -1) => Add(PdfSeverity.Error, message, obj, offset);

    private void Add(PdfSeverity severity, string message, int obj, long offset)
    {
        if (_strict && severity >= PdfSeverity.Warning)
        {
            throw new PdfFormatException(message) { ObjectNumber = obj, Offset = offset };
        }

        lock (_items)
        {
            if (_items.Count < MaxRecorded)
            {
                _items.Add(new PdfDiagnostic(severity, obj, offset, message));
            }
            else if (!_overflowed)
            {
                _overflowed = true;
                _items.Add(new PdfDiagnostic(PdfSeverity.Info, -1, -1, "Further diagnostics were dropped."));
            }
        }
    }

    public PdfDiagnostic[] Snapshot()
    {
        lock (_items)
        {
            return [.. _items];
        }
    }
}
