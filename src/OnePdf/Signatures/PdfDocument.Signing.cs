using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OnePdf.Internal;

namespace OnePdf;

// Signing (ISO 32000-2 12.8, ETSI EN 319 142-1). A signature is always written as an incremental update: the objects for the
// signature, its field and the form changes are saved in memory with a fixed-size placeholder for /ByteRange and /Contents, the
// placeholder is patched with the real byte range, the signed digest is computed over the finished bytes, and the CMS is
// written into the reserved space. The original file is an exact prefix of the output, so earlier signatures stay valid.
public sealed partial class PdfDocument
{
    private bool _signingUsed;

    /// <summary>The placeholder for <c>/ByteRange</c> as the writer emits it: three ten-digit numbers that are replaced in place.</summary>
    private const string ByteRangePlaceholder = "/ByteRange[ 0 9999999999 9999999999 9999999999]";

    private const int MaxContentsReserve = 4 << 20;

    private const int DefaultTimestampReserve = 12 << 10;

    /// <summary>
    /// Signs the document with an approval or certification signature and writes the result: the original bytes unchanged, followed
    /// by one incremental update holding the signature. The signature is PAdES baseline B-B (B-T when
    /// <see cref="PdfSigningOptions.Timestamp"/> is set) or <c>adbe.pkcs7.detached</c>, as <see cref="PdfSigningOptions.Format"/> says.
    /// </summary>
    /// <remarks>
    /// <para>The document must be opened with <see cref="PdfOpenOptions.AllowEditing"/>. Changes made through the mutable view before
    /// the call (a filled field, say) are part of the signed update. After this method returns, or throws once it has started, the
    /// document holds the pending signature objects: open the output (or the file again) to sign a further time.</para>
    /// <para>Nothing is written to <paramref name="output"/> unless the whole signature succeeds. An encrypted document keeps its
    /// encryption; the <c>/Contents</c> string is not encrypted (ISO 32000-2 7.6.2). OnePDF contacts no network: the time-stamp
    /// authority and any remote signer are reached only through the callbacks.</para>
    /// <para>Output is held in memory until the signature is complete, so the memory used is about the size of the file.</para>
    /// </remarks>
    /// <param name="output">A writable stream that does not have to seek; left open. It must not be the file the document was read from.</param>
    /// <param name="credential">The signer's certificate and key (or external signer).</param>
    /// <param name="options">Null means an invisible PAdES B-B approval signature with SHA-256 in a new field.</param>
    /// <param name="cancellationToken">Checked between steps.</param>
    /// <returns>The field name and the sizes, useful for tuning <see cref="PdfSigningOptions.ContentsReserve"/>.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="PdfSigningException">The credential, field, timestamp or reserved space is unusable; see <see cref="PdfSigningException.Reason"/>.</exception>
    /// <exception cref="ArgumentException">An option is out of range (page index, reserve, rectangle, field name), or the stream is not writable.</exception>
    /// <exception cref="PdfFormatException">The file was rebuilt by scanning on open, so there is nothing to chain an update to.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfSigningResult Sign(Stream output, PdfSigningCredential credential, PdfSigningOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(output));
        }

        (byte[] buffer, int length, PdfSigningResult result) = SignToMemory(credential, options, cancellationToken);
        output.Write(buffer, 0, length);
        return result;
    }

    /// <summary>Like <see cref="Sign(Stream, PdfSigningCredential, PdfSigningOptions?, CancellationToken)"/>, to a file created or replaced only once the signature is complete.</summary>
    /// <param name="path">The output file. Not the file the document was read from.</param>
    /// <param name="credential">The signer's certificate and key (or external signer).</param>
    /// <param name="options">Null means an invisible PAdES B-B approval signature.</param>
    /// <param name="cancellationToken">Checked between steps.</param>
    /// <returns>The field name and the sizes.</returns>
    public PdfSigningResult Sign(string path, PdfSigningCredential credential, PdfSigningOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        (byte[] buffer, int length, PdfSigningResult result) = SignToMemory(credential, options, cancellationToken);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        file.Write(buffer, 0, length);
        return result;
    }

    /// <summary>
    /// Adds a document time-stamp (<c>/Type /DocTimeStamp</c>, sub-filter <c>ETSI.RFC3161</c>; ETSI EN 319 142-1 5.4) as an incremental
    /// update. The token covers the whole file up to the token itself. The authority is reached only through
    /// <paramref name="timestamp"/>; the returned token is checked (status, imprint, nonce) before it is embedded.
    /// </summary>
    /// <remarks>The same rules as <see cref="Sign(Stream, PdfSigningCredential, PdfSigningOptions?, CancellationToken)"/> apply to the document, the memory use and failure.</remarks>
    /// <param name="output">A writable stream; left open.</param>
    /// <param name="timestamp">Gets the token from an authority.</param>
    /// <param name="options">Null means field <c>Timestamp1</c> (or the next free), SHA-256 and a 12 KiB reserve.</param>
    /// <param name="cancellationToken">Checked between steps.</param>
    /// <returns>The field name and the sizes.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="PdfSigningException">The token was refused or does not fit; see <see cref="PdfSigningException.Reason"/>.</exception>
    public PdfSigningResult AddDocumentTimestamp(Stream output, PdfTimestampCallback timestamp, PdfTimestampOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(timestamp);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(output));
        }

        options ??= new PdfTimestampOptions();
        var job = new SigningJob
        {
            Timestamp = timestamp,
            FieldName = options.FieldName,
            DefaultFieldPrefix = "Timestamp",
            PageIndex = options.PageIndex,
            Hash = options.Hash,
            Reserve = options.ContentsReserve ?? DefaultTimestampReserve,
        };
        (byte[] buffer, int length, PdfSigningResult result) = Execute(job, cancellationToken);
        output.Write(buffer, 0, length);
        return result;
    }

    private sealed class SigningJob
    {
        public CmsSigner? Signer { get; init; }

        public PdfSigningOptions? Options { get; init; }

        public PdfTimestampCallback? Timestamp { get; init; }

        public string? FieldName { get; init; }

        public string DefaultFieldPrefix { get; init; } = "Signature";

        public int PageIndex { get; init; }

        public HashAlgorithmName Hash { get; init; }

        public int Reserve { get; init; }

        public bool IsDocumentTimestamp => Signer is null;
    }

    private (byte[] Buffer, int Length, PdfSigningResult Result) SignToMemory(PdfSigningCredential credential, PdfSigningOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        options ??= new PdfSigningOptions();
        bool cades = options.Format == PdfSignatureFormat.PadesBaseline;
        if (!Enum.IsDefined(options.Format) || !Enum.IsDefined(options.RsaPadding))
        {
            throw new ArgumentException("The format or the RSA padding is not a defined value.", nameof(options));
        }

        CmsSigner signer = CmsSigner.Create(credential, options.Hash, options.RsaPadding, cades);
        int reserve = options.ContentsReserve ?? signer.EstimateBytes(options.Timestamp is not null);
        var job = new SigningJob
        {
            Signer = signer,
            Options = options,
            Timestamp = options.Timestamp,
            FieldName = options.FieldName,
            PageIndex = options.PageIndex,
            Hash = options.Hash,
            Reserve = reserve,
        };
        return Execute(job, ct);
    }

    private (byte[] Buffer, int Length, PdfSigningResult Result) Execute(SigningJob job, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsEditable)
        {
            throw new InvalidOperationException("Signing writes an update to the document, so it must be opened with PdfOpenOptions.AllowEditing.");
        }

        if (_signingUsed)
        {
            throw new PdfSigningException(PdfSigningError.DocumentUsed, "This document was already used for a signing attempt; open the signed output (or the file) again to sign once more.");
        }

        if (job.Reserve is < 64 or > MaxContentsReserve)
        {
            throw new ArgumentOutOfRangeException(null, "The reserved /Contents size must be between 64 bytes and 4 MiB.");
        }

        if (job.PageIndex < 0 || job.PageIndex >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(null, "The page index is outside the document.");
        }

        if (Revisions.Count == 0 || Revisions[^1].XrefOffset < 0)
        {
            throw new PdfFormatException("The file was rebuilt by scanning, so there is no cross-reference section to chain a signature update to; save it with Save and sign the result.");
        }

        IReadOnlyList<PdfSignature> existing = GetSignatures();
        PdfSigningOptions? so = job.Options;
        (PdfFormField? field, string resolvedName) = ResolveField(job, existing.Count);
        if (so?.Certify is { } level)
        {
            if (!Enum.IsDefined(level))
            {
                throw new ArgumentException("The certification level must be 1, 2 or 3.");
            }

            if (existing.Count > 0)
            {
                throw new PdfSigningException(PdfSigningError.CertificationNotFirst, "Only the first signature of a document can certify it; this document already has signatures.");
            }
        }

        if (so?.Rectangle is { } r && (!double.IsFinite(r.Left + r.Bottom + r.Right + r.Top)))
        {
            throw new ArgumentException("The signature rectangle must be finite.");
        }

        ct.ThrowIfCancellationRequested();
        _signingUsed = true;
        DateTimeOffset when = so?.SigningTime ?? DateTimeOffset.UtcNow;
        when = new DateTimeOffset(when.UtcDateTime.Ticks - (when.UtcDateTime.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

        string fieldName = PrepareUpdate(job, field, resolvedName, when, existing.Count);
        ResetEditViews();

        long originalLength = _source.Length;
        if (originalLength > int.MaxValue / 2)
        {
            throw new PdfLimitException("The file is too large to sign in memory (more than 1 GiB).");
        }

        var ms = new MemoryStream(checked((int)Math.Min(int.MaxValue - 1, originalLength + (1 << 16) + (job.Reserve * 2L))));
        SaveIncremental(ms, null, ct);
        ct.ThrowIfCancellationRequested();
        if (!ms.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
        {
            throw new InvalidOperationException("The output buffer is not accessible.");
        }

        byte[] buffer = segment.Array;
        int length = checked((int)ms.Length);

        // Locate the placeholders in the update; the original bytes before it can hold anything.
        Span<byte> update = buffer.AsSpan((int)originalLength, length - (int)originalLength);
        ReadOnlySpan<byte> marker = Encoding.ASCII.GetBytes(ByteRangePlaceholder);
        int rangeAt = update.IndexOf(marker);
        if (rangeAt < 0 || update[(rangeAt + marker.Length)..].IndexOf(marker) >= 0)
        {
            throw new InvalidOperationException("The signature placeholder was not found exactly once in the update.");
        }

        rangeAt += (int)originalLength;
        int hexAt = buffer.AsSpan(rangeAt, length - rangeAt).IndexOf("/Contents<"u8);
        if (hexAt < 0)
        {
            throw new InvalidOperationException("The /Contents placeholder was not found.");
        }

        int lt = rangeAt + hexAt + "/Contents".Length;
        int hexDigits = job.Reserve * 2;
        int gt = lt + 1 + hexDigits;
        if (gt >= length || buffer[gt] != (byte)'>')
        {
            throw new InvalidOperationException("The /Contents placeholder has an unexpected size.");
        }

        long covered = length;
        long second = covered - (gt + 1);
        string tail = string.Create(CultureInfo.InvariantCulture, $"{lt,-10} {gt + 1,-10} {second,-10}]");
        Encoding.ASCII.GetBytes(tail, buffer.AsSpan(rangeAt + marker.Length - 33, tail.Length));

        byte[] digest = HashRanges(buffer, lt, gt + 1, length, job.Hash);
        ct.ThrowIfCancellationRequested();
        byte[] cms;
        if (job.Signer is { } signer)
        {
            cms = signer.Build(digest, when, job.Timestamp, ct);
        }
        else
        {
            cms = CmsSigner.RequestToken(job.Timestamp!, digest, job.Hash);
        }

        if (cms.Length > job.Reserve)
        {
            throw new PdfSigningException(PdfSigningError.ContentsTooSmall,
                $"The signature needs {cms.Length} bytes but {job.Reserve} were reserved; raise ContentsReserve to at least {cms.Length}.")
            {
                RequiredBytes = cms.Length,
            };
        }

        const string digits = "0123456789ABCDEF";
        for (int i = 0; i < cms.Length; i++)
        {
            buffer[lt + 1 + (2 * i)] = (byte)digits[cms[i] >> 4];
            buffer[lt + 2 + (2 * i)] = (byte)digits[cms[i] & 15];
        }

        return (buffer, length, new PdfSigningResult(fieldName, job.Reserve, cms.Length, length, job.Timestamp is not null));
    }

    private static byte[] HashRanges(byte[] buffer, int holeStart, int holeEnd, int length, HashAlgorithmName name)
    {
        using IncrementalHash h = IncrementalHash.CreateHash(name);
        h.AppendData(buffer, 0, holeStart);
        h.AppendData(buffer, holeEnd, length - holeEnd);
        return h.GetHashAndReset();
    }

    // The existing unsigned signature field to use (or null with the name of a new one). Checks the name before anything is changed.
    private (PdfFormField? Field, string Name) ResolveField(SigningJob job, int existingSignatures)
    {
        string? name = job.FieldName;
        PdfAcroForm? form = Form;
        if (name is null)
        {
            for (int i = 1; ; i++)
            {
                string candidate = job.DefaultFieldPrefix + i.ToString(CultureInfo.InvariantCulture);
                if (form?.FindField(candidate) is null)
                {
                    return (null, candidate);
                }
            }
        }

        if (name.Length == 0)
        {
            throw new PdfSigningException(PdfSigningError.BadField, "The field name is empty.");
        }

        if (form?.FindField(name) is { } found)
        {
            if (found.Kind != PdfFieldKind.Signature || found.IsSigned || found.Children.Count > 0 || found.Widgets.Count == 0)
            {
                throw new PdfSigningException(PdfSigningError.BadField, $"The field '{name}' exists but is not an unsigned signature field with a widget.");
            }

            return (found, found.FullName);
        }

        if (name.Contains('.', StringComparison.Ordinal))
        {
            throw new PdfSigningException(PdfSigningError.BadField, "A new field's name must not contain '.'; choose a plain name.");
        }

        return (null, name);
    }

    // Changes the document: the signature dictionary with placeholders, its field and widget, the form, the page's annotations and,
    // for certification, /Perms. Returns the field's full name.
    private string PrepareUpdate(SigningJob job, PdfFormField? field, string newName, DateTimeOffset when, int existingSignatures)
    {
        PdfSigningOptions? so = job.Options;
        bool timestampOnly = job.IsDocumentTimestamp;

        var sig = new PdfDictionary();
        sig.SetEntry("Type", new PdfName(timestampOnly ? "DocTimeStamp" : "Sig"));
        sig.SetEntry("Filter", new PdfName("Adobe.PPKLite"));
        sig.SetEntry("SubFilter", new PdfName(timestampOnly ? "ETSI.RFC3161" : so!.Format == PdfSignatureFormat.PadesBaseline ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached"));
        var range = new PdfArray();
        range.Append(new PdfInteger(0));
        for (int i = 0; i < 3; i++)
        {
            range.Append(new PdfInteger(9_999_999_999));
        }

        sig.SetEntry("ByteRange", range);
        sig.SetEntry("Contents", new PdfString(new byte[job.Reserve]));
        if (!timestampOnly)
        {
            string date = "D:" + when.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "+00'00'";
            sig.SetEntry("M", PdfString.FromText(date));
            string? name = so!.SignerName ?? job.Signer!.Certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrEmpty(name))
            {
                sig.SetEntry("Name", PdfString.FromText(name));
            }

            if (so.Reason is not null)
            {
                sig.SetEntry("Reason", PdfString.FromText(so.Reason));
            }

            if (so.Location is not null)
            {
                sig.SetEntry("Location", PdfString.FromText(so.Location));
            }

            if (so.ContactInfo is not null)
            {
                sig.SetEntry("ContactInfo", PdfString.FromText(so.ContactInfo));
            }

            if (so.Certify is { } level)
            {
                var parms = new PdfDictionary();
                parms.SetEntry("Type", new PdfName("TransformParams"));
                parms.SetEntry("P", new PdfInteger((int)level));
                parms.SetEntry("V", new PdfName("1.2"));
                var reference = new PdfDictionary();
                reference.SetEntry("Type", new PdfName("SigRef"));
                reference.SetEntry("TransformMethod", new PdfName("DocMDP"));
                reference.SetEntry("TransformParams", parms);
                var refs = new PdfArray();
                refs.Append(reference);
                sig.SetEntry("Reference", refs);
            }
        }

        PdfReference sigRef = AddObject(sig);
        PdfDictionary acro = EnsureAcroForm();
        acro.SetEntry("SigFlags", new PdfInteger(Math.Max(acro.GetInt("SigFlags"), 0) | 3));
        if (existingSignatures == 0)
        {
            DeclareExtensions(timestampOnly || so!.Format == PdfSignatureFormat.PadesBaseline, timestampOnly ? 1 : 2);
        }

        PdfDictionary widget;
        string fullName;
        if (field is not null)
        {
            widget = field.Widgets[0];
            field.Dictionary.SetEntry("V", sigRef);
            fullName = field.FullName;
        }
        else
        {
            fullName = newName;
            PdfPage page = Pages[job.PageIndex];
            widget = new PdfDictionary();
            widget.SetEntry("Type", new PdfName("Annot"));
            widget.SetEntry("Subtype", new PdfName("Widget"));
            widget.SetEntry("FT", new PdfName("Sig"));
            widget.SetEntry("T", PdfString.FromText(fullName));
            widget.SetEntry("F", new PdfInteger(132));
            PdfRectangle rect = so?.Rectangle is { IsEmpty: false } rr ? Normalize(rr) : new PdfRectangle(0, 0, 0, 0);
            widget.SetEntry("Rect", RectArray(rect));
            if (page.Dictionary.Reference is { } pageRef)
            {
                widget.SetEntry("P", pageRef);
            }

            widget.SetEntry("V", sigRef);
            PdfReference widgetRef = AddObject(widget);
            AppendTo(acro, "Fields", widgetRef);
            AppendTo(page.Dictionary, "Annots", widgetRef);
        }

        if (so?.FieldLock is { } fieldLock)
        {
            (field?.Dictionary ?? widget).SetEntry("Lock", LockDictionary(fieldLock));
        }

        if (!timestampOnly)
        {
            PdfRectangle visible = PdfRectangle.From(widget.Get("Rect")) ?? new PdfRectangle(0, 0, 0, 0);
            if (!visible.IsEmpty && (so!.Appearance is not null || widget.GetDictionary("AP") is null))
            {
                BuildSignatureAppearance(widget, visible, so, job.Signer!.Certificate, when);
            }

            if (so!.Certify is not null)
            {
                PdfDictionary perms = Catalog.GetDictionary("Perms") ?? new PdfDictionary();
                perms.SetEntry("DocMDP", sigRef);
                Catalog.SetEntry("Perms", perms);
            }
        }

        return fullName;
    }

    private static PdfRectangle Normalize(PdfRectangle r) =>
        new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));

    private static PdfArray RectArray(PdfRectangle r)
    {
        var a = new PdfArray();
        foreach (double v in (double[])[r.Left, r.Bottom, r.Right, r.Top])
        {
            a.Append(new PdfReal(v));
        }

        return a;
    }

    private static void AppendTo(PdfDictionary owner, string key, PdfObject item)
    {
        if (owner.Get(key) is PdfArray existing)
        {
            existing.Append(item);
        }
        else
        {
            var fresh = new PdfArray();
            fresh.Append(item);
            owner.SetEntry(key, fresh);
        }
    }

    private PdfDictionary EnsureAcroForm()
    {
        if (Catalog.GetDictionary("AcroForm") is { } existing)
        {
            return existing;
        }

        var created = new PdfDictionary();
        created.SetEntry("Fields", new PdfArray());
        Catalog.SetEntry("AcroForm", AddObject(created));
        return created;
    }

    // ETSI EN 319 142-1 5.6: an extensions dictionary entry names the ETSI extensions (level 1: document time-stamps, level 2: the
    // CAdES sub-filter) for documents below PDF 2.0. It is added only to a document with no signatures yet, since changing the
    // catalog later would count as a change after an earlier signature.
    private void DeclareExtensions(bool needed, int level)
    {
        if (!needed || (Version is { } v && string.CompareOrdinal(v, "2.0") >= 0))
        {
            return;
        }

        PdfDictionary extensions = Catalog.GetDictionary("Extensions") ?? new PdfDictionary();
        if (extensions.GetDictionary("ESIC") is not null)
        {
            return;
        }

        var esic = new PdfDictionary();
        esic.SetEntry("BaseVersion", new PdfName("1.7"));
        esic.SetEntry("ExtensionLevel", new PdfInteger(level));
        extensions.SetEntry("ESIC", esic);
        Catalog.SetEntry("Extensions", extensions);
    }

    private static PdfDictionary LockDictionary(PdfSignatureLock l)
    {
        var d = new PdfDictionary();
        d.SetEntry("Type", new PdfName("SigFieldLock"));
        d.SetEntry("Action", new PdfName(l.Action.ToString()));
        if (l.Action != PdfLockAction.All)
        {
            var names = new PdfArray();
            foreach (string f in l.Fields)
            {
                names.Append(PdfString.FromText(f));
            }

            d.SetEntry("Fields", names);
        }

        if (l.Permission is { } p)
        {
            d.SetEntry("P", new PdfInteger(Math.Clamp(p, 1, 3)));
        }

        return d;
    }

    // The visible look: an optional image on the left and wrapped text lines, in a Form XObject with the box's size.
    private void BuildSignatureAppearance(PdfDictionary widget, PdfRectangle rect, PdfSigningOptions so, X509Certificate2 cert, DateTimeOffset when)
    {
        PdfSignatureAppearance look = so.Appearance ?? new PdfSignatureAppearance();
        double w = rect.Width;
        double h = rect.Height;
        PdfCreatedFont font = AddStandardFont("Helvetica");
        using PdfFormContentBuilder b = BeginForm(new PdfRectangle(0, 0, w, h));
        if (look.Border)
        {
            b.SetStrokeGray(0.4);
            b.SetLineWidth(0.5);
            b.Rectangle(0.25, 0.25, Math.Max(0, w - 0.5), Math.Max(0, h - 0.5));
            b.Stroke();
        }

        double left = 3;
        if (look.Image is { } image && image.Height > 0)
        {
            double area = Math.Max(1, (w - 6) * 0.4);
            double aspect = (double)image.Width / image.Height;
            double iw = Math.Min(area, Math.Max(1, h - 6) * aspect);
            double ih = iw / aspect;
            b.DrawImage(image, 3, (h - ih) / 2, iw, ih);
            left = 3 + area + 3;
        }

        string signer = so.SignerName ?? cert.GetNameInfo(X509NameType.SimpleName, false);
        IReadOnlyList<string> lines = look.Lines ?? DefaultLines(signer, so, when);
        string text = string.Join('\n', lines);
        double boxWidth = Math.Max(1, w - left - 3);
        double boxHeight = Math.Max(1, h - 6);
        double size = look.FontSize;
        if (size <= 0)
        {
            size = 10;
            for (; size > 4; size -= 0.5)
            {
                b.SetFont(font, size);
                if (b.WrapText(text, boxWidth).Count * size * 1.2 <= boxHeight)
                {
                    break;
                }
            }
        }

        b.SetFont(font, size);
        b.SetFillGray(0);
        b.DrawTextBox(text, left, 3, boxWidth, boxHeight);
        PdfFormXObject form = b.Commit();
        var states = new PdfDictionary();
        states.SetEntry("N", form.Reference);
        widget.SetEntry("AP", states);
    }

    private static List<string> DefaultLines(string signer, PdfSigningOptions so, DateTimeOffset when)
    {
        var lines = new List<string>
        {
            "Digitally signed by " + signer,
            "Date: " + when.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(so.Reason))
        {
            lines.Add("Reason: " + so.Reason);
        }

        if (!string.IsNullOrEmpty(so.Location))
        {
            lines.Add("Location: " + so.Location);
        }

        return lines;
    }
}
