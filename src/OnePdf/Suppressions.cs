using System.Diagnostics.CodeAnalysis;

// The COS type names are fixed by the public API design (and by the PDF specification's own vocabulary): a PDF dictionary
// is a "Dictionary" and a PDF stream is a "Stream", although neither derives from the BCL type of that name.
[assembly: SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Names mandated by the public API design and the PDF specification.", Scope = "type", Target = "~T:OnePdf.PdfDictionary")]
[assembly: SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Names mandated by the public API design and the PDF specification.", Scope = "type", Target = "~T:OnePdf.PdfStream")]
