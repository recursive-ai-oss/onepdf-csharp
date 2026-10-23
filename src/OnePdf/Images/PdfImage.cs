using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace OnePdf;

/// <summary>
/// Decodes the image codecs OnePDF does not implement itself: DCTDecode (JPEG), JPXDecode (JPEG 2000) and JBIG2Decode.
/// Register one with <see cref="PdfImageDecoderRegistry"/>. A decoder returns raw samples; OnePDF applies every PDF rule
/// (<c>/Decode</c>, colour spaces, masks, colour-key masking) afterwards, so a decoder must not.
/// </summary>
/// <remarks>
/// <para>Decoders must be thread-safe: documents are read concurrently.</para>
/// <para>Failure contract: return null when the data cannot be decoded, or throw <see cref="PdfFormatException"/>. Either
/// way OnePDF records a diagnostic and hands the device the encoded bytes. Other exceptions are bugs and propagate.</para>
/// </remarks>
public interface IPdfImageDecoder
{
    /// <summary>Decodes one image.</summary>
    /// <param name="request">The encoded data and what the PDF dictionary says about it.</param>
    /// <param name="cancellationToken">Cancellation for long decodes.</param>
    /// <returns>The samples, or null when the data cannot be decoded.</returns>
    PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken);
}

/// <summary>The input of <see cref="IPdfImageDecoder.Decode"/>.</summary>
public readonly struct PdfImageDecodeRequest
{
    /// <summary>Creates a request.</summary>
    public PdfImageDecodeRequest(string filter, ReadOnlyMemory<byte> data, int declaredWidth, int declaredHeight)
    {
        Filter = filter;
        Data = data;
        DeclaredWidth = declaredWidth;
        DeclaredHeight = declaredHeight;
    }

    /// <summary>The filter name: <c>DCTDecode</c>, <c>JPXDecode</c> or <c>JBIG2Decode</c> (abbreviations are expanded).</summary>
    public string Filter { get; }

    /// <summary>The encoded bytes after any filters that precede the image codec (for example Flate) were undone.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The image dictionary's <c>/Width</c> (0 if absent). A hint: decoders report the real size in the result.</summary>
    public int DeclaredWidth { get; }

    /// <summary>The image dictionary's <c>/Height</c> (0 if absent).</summary>
    public int DeclaredHeight { get; }

    /// <summary>JBIG2Decode: the bytes of the <c>/JBIG2Globals</c> stream, or empty.</summary>
    public ReadOnlyMemory<byte> Globals { get; init; }

    /// <summary>DCTDecode: the <c>/ColorTransform</c> decode parameter (0 or 1), or null when absent (ISO 32000-2 7.4.8).</summary>
    public int? ColorTransform { get; init; }

    /// <summary>The image dictionary's <c>/BitsPerComponent</c>, or 0 when absent.</summary>
    public int DeclaredBitsPerComponent { get; init; }

    /// <summary>
    /// The width in device pixels the image is drawn at on the page, or 0 when unknown (an image decoded outside a page). It is the
    /// axis-aligned extent of the image's unit square under the CTM and output transform, rounded up and never above
    /// <see cref="DeclaredWidth"/>. A hint only: a decoder that can decode at reduced resolution (JPEG 2000 resolution levels)
    /// may return an image of this size or larger, or ignore the hint and return full size.
    /// </summary>
    public int TargetWidth { get; init; }

    /// <summary>The height in device pixels the image is drawn at on the page, or 0 when unknown; see <see cref="TargetWidth"/>.</summary>
    public int TargetHeight { get; init; }
}

/// <summary>
/// The raw output of an <see cref="IPdfImageDecoder"/>: interleaved samples, rows padded to a byte boundary.
/// </summary>
/// <remarks>
/// <para><b>DCT.</b> Return the components as the JPEG stores them after the component transform: YCbCr to RGB when the
/// transform applies (three components, or Adobe APP14 transform 1; none for transform 0 or when
/// <see cref="PdfImageDecodeRequest.ColorTransform"/> is 0), YCCK to CMYK for Adobe transform 2. Do <b>not</b> invert Adobe CMYK:
/// PDF writers state the inversion with <c>/Decode [1 0 1 0 1 0 1 0]</c> (ISO 32000-2 8.9.5.2) and OnePDF applies it.
/// Report the APP14 transform value in <see cref="AdobeTransform"/>.</para>
/// <para><b>JPX.</b> Return colour channels in the JP2/JPX colour space converted to a PDF-expressible one (sYCC to RGB),
/// with <see cref="ColorSpace"/> set when the file states one, and the opacity channel, if any, last with
/// <see cref="HasAlpha"/> set.</para>
/// <para><b>Reduced size.</b> A decoder may return fewer pixels than <see cref="PdfImageDecodeRequest.DeclaredWidth"/> by
/// <see cref="PdfImageDecodeRequest.DeclaredHeight"/>, typically after using the request's target size. OnePDF then passes the
/// reduced dimensions through (<see cref="PdfImageData.Width"/> and <see cref="PdfImageData.Height"/> are the buffer's); it records a
/// warning unless the request carried a target size and the result is not larger than declared. The image still fills the same unit square, so placement is unchanged. Masks and soft masks are scaled to the
/// image by the device as usual. Nothing is resampled by OnePDF.</para>
/// <para><b>JBIG2.</b> One component, one bit, in JBIG2's convention: 1 is black. OnePDF inverts it as ISO 32000-2 7.4.7
/// requires.</para>
/// </remarks>
public sealed class PdfDecodedImage
{
    /// <summary>Creates a decoded image.</summary>
    /// <param name="width">Pixels per row, at least 1.</param>
    /// <param name="height">Rows, at least 1.</param>
    /// <param name="components">Colour components per pixel, 1 to 32.</param>
    /// <param name="bitsPerComponent">1, 2, 4, 8 or 16 (16-bit samples are big-endian).</param>
    /// <param name="samples">Interleaved samples, each row starting on a byte boundary.</param>
    /// <exception cref="ArgumentException">A size is out of range, or <paramref name="samples"/> is too short.</exception>
    public PdfDecodedImage(int width, int height, int components, int bitsPerComponent, ReadOnlyMemory<byte> samples)
    {
        if (width < 1 || height < 1 || components is < 1 or > 32 || bitsPerComponent is not (1 or 2 or 4 or 8 or 16))
        {
            throw new ArgumentException("Image size, component count or bit depth is out of range.");
        }

        Width = width;
        Height = height;
        Components = components;
        BitsPerComponent = bitsPerComponent;
        Samples = samples;
    }

    /// <summary>Pixels per row.</summary>
    public int Width { get; }

    /// <summary>Number of rows.</summary>
    public int Height { get; }

    /// <summary>Colour components per pixel, not counting the opacity channel.</summary>
    public int Components { get; }

    /// <summary>Bits per sample: 1, 2, 4, 8 or 16.</summary>
    public int BitsPerComponent { get; }

    /// <summary>Interleaved samples, <c>Height</c> rows of <c>ceil(Width × (Components + alpha) × BitsPerComponent / 8)</c> bytes. Not modified by OnePDF.</summary>
    public ReadOnlyMemory<byte> Samples { get; }

    /// <summary>True when each pixel carries one more sample after the colour components: an opacity channel (JPX).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>JPX: the colour space the file states, if any. Used when the image dictionary has no <c>/ColorSpace</c>.</summary>
    public PdfColorSpace? ColorSpace { get; init; }

    /// <summary>DCT: the Adobe APP14 transform byte (0, 1 or 2) when the marker is present, else null. Informational.</summary>
    public int? AdobeTransform { get; init; }
}

/// <summary>
/// The decoders OnePDF calls for image codecs. <see cref="Default"/> is shared by every document; a copy can be passed in
/// <see cref="PdfImageOptions.Decoders"/>. Registration is thread-safe.
/// </summary>
public sealed class PdfImageDecoderRegistry
{
    private ImmutableDictionary<string, IPdfImageDecoder> _decoders = ImmutableDictionary<string, IPdfImageDecoder>.Empty;

    /// <summary>The shared registry. It starts empty: OnePDF ships no JPEG, JPEG 2000 or JBIG2 decoder.</summary>
    public static PdfImageDecoderRegistry Default { get; } = new();

    /// <summary>Registers (or replaces) the decoder for a filter.</summary>
    /// <param name="filter"><c>DCTDecode</c>, <c>JPXDecode</c> or <c>JBIG2Decode</c>.</param>
    /// <param name="decoder">The decoder.</param>
    /// <exception cref="ArgumentException">The filter is not one of the three image codecs.</exception>
    public void Register(string filter, IPdfImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(decoder);
        if (filter is not ("DCTDecode" or "JPXDecode" or "JBIG2Decode"))
        {
            throw new ArgumentException("Only DCTDecode, JPXDecode and JBIG2Decode can be registered.", nameof(filter));
        }

        ImmutableInterlocked.AddOrUpdate(ref _decoders, filter, decoder, (_, _) => decoder);
    }

    /// <summary>Removes the decoder for a filter; false when none was registered.</summary>
    public bool Unregister(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return ImmutableInterlocked.TryRemove(ref _decoders, filter, out _);
    }

    /// <summary>Looks up the decoder for a filter.</summary>
    public bool TryGet(string filter, [NotNullWhen(true)] out IPdfImageDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return _decoders.TryGetValue(filter, out decoder);
    }

    /// <summary>A new registry holding the same registrations, for per-call customisation.</summary>
    public PdfImageDecoderRegistry Clone() => new() { _decoders = _decoders };
}

/// <summary>Options for turning an image stream into a <see cref="PdfImageData"/>.</summary>
public sealed record PdfImageOptions
{
    /// <summary>Options with the defaults.</summary>
    public static PdfImageOptions Default { get; } = new();

    /// <summary>The decoders to use. Default: <see cref="PdfImageDecoderRegistry.Default"/>.</summary>
    public PdfImageDecoderRegistry Decoders { get; init; } = PdfImageDecoderRegistry.Default;

    /// <summary>
    /// When true, DCTDecode and JPXDecode images are not decoded: the result carries the original bytes in
    /// <see cref="PdfImageData.EncodedData"/> and the PDF semantics as data in <see cref="PdfImageData.Semantics"/>. Masks that are not
    /// themselves DCT/JPX are still produced. Default false.
    /// </summary>
    public bool PreferEncoded { get; init; }
}

/// <summary>
/// An alpha plane: one byte per pixel, 255 opaque. For stencil masks it is the paint coverage. It may have a different size
/// from the image it belongs to; the device scales it over the image's unit square.
/// </summary>
public sealed class PdfImageAlpha
{
    internal PdfImageAlpha(int width, int height, byte[] values)
    {
        Width = width;
        Height = height;
        Values = values;
    }

    /// <summary>Pixels per row.</summary>
    public int Width { get; }

    /// <summary>Rows, top to bottom.</summary>
    public int Height { get; }

    /// <summary>Row-major values, <c>Width × Height</c> bytes. Do not modify.</summary>
    public ReadOnlyMemory<byte> Values { get; }
}

/// <summary>The image dictionary's colour semantics as plain data, for devices that decode DCT/JPX themselves.</summary>
public sealed class PdfImageSemantics
{
    internal PdfImageSemantics() { }

    /// <summary>The declared <c>/ColorSpace</c>, or null (JPX, stencil masks, or unresolvable).</summary>
    public PdfColorSpace? ColorSpace { get; internal set; }

    /// <summary>The declared <c>/BitsPerComponent</c>, or 0.</summary>
    public int BitsPerComponent { get; internal set; }

    /// <summary>The <c>/Decode</c> array (min/max pairs), or null for the default.</summary>
    public IReadOnlyList<double>? Decode { get; internal set; }

    /// <summary>The colour-key <c>/Mask</c> array (min/max pairs of raw sample values), or null.</summary>
    public IReadOnlyList<int>? ColorKeyMask { get; internal set; }

    /// <summary>The <c>/Matte</c> colour of the soft mask, or null.</summary>
    public IReadOnlyList<double>? Matte { get; internal set; }

    /// <summary>JPX: <c>/SMaskInData</c> (0 none, 1 opacity channel, 2 premultiplied opacity channel).</summary>
    public int SMaskInData { get; internal set; }

    /// <summary>DCT: <c>/ColorTransform</c> decode parameter, or null.</summary>
    public int? ColorTransform { get; internal set; }
}

/// <summary>
/// An image ready for a device: either finished samples in a final colour space (<see cref="IsDecoded"/>), or, when no
/// decoder is registered or <see cref="PdfImageOptions.PreferEncoded"/> asked for it, the encoded bytes with the
/// semantics as data. Create with <see cref="FromXObject"/> or <see cref="FromInline"/>.
/// </summary>
public sealed class PdfImageData
{
    internal PdfImageData() { }

    /// <summary>Pixels per row of the image data.</summary>
    public int Width { get; internal set; }

    /// <summary>Rows.</summary>
    public int Height { get; internal set; }

    /// <summary>
    /// True for an <c>/ImageMask</c> stencil: no colour samples, <see cref="Alpha"/> holds the coverage and the device paints
    /// it with the current fill colour.
    /// </summary>
    public bool IsStencil { get; internal set; }

    /// <summary>True when <see cref="Samples"/> (or, for stencils, <see cref="Alpha"/>) is filled in.</summary>
    public bool IsDecoded { get; internal set; }

    /// <summary>The final colour space of <see cref="Samples"/>; null for stencils and undecoded images.</summary>
    public PdfColorSpace? ColorSpace { get; internal set; }

    /// <summary>
    /// Interleaved 8-bit samples in <see cref="ColorSpace"/>, <c>Width × Height × ColorSpace.ComponentCount</c> bytes, rows top to
    /// bottom with no padding. Sample 0..255 maps linearly onto <see cref="PdfColorSpace.GetComponentRange"/> (0..1 for device
    /// spaces). Indexed images are expanded through their palette, Separation/DeviceN through their tint transform, so
    /// this is always a final space. 16-bit sources keep their high byte. Empty when not decoded or for stencils.
    /// </summary>
    public ReadOnlyMemory<byte> Samples { get; internal set; }

    /// <summary>The array behind <see cref="Samples"/> (for mask conversion without a copy).</summary>
    internal byte[]? SamplesBuffer { get; set; }

    /// <summary>
    /// Opacity: the soft mask, the explicit or colour-key mask, the JPX opacity channel, or for stencils the coverage. Null when
    /// the image is fully opaque. Its size may differ from the image's.
    /// </summary>
    public PdfImageAlpha? Alpha { get; internal set; }

    /// <summary>The image dictionary's <c>/Interpolate</c>, default false.</summary>
    public bool Interpolate { get; internal set; }

    /// <summary>The image's <c>/Intent</c> rendering intent name (for example <c>Perceptual</c>), or null.</summary>
    public string? RenderingIntent { get; internal set; }

    /// <summary>
    /// The image data as stored after the filters that precede the image codec: the original JPEG/JPEG 2000/JBIG2 stream, or,
    /// for images without a codec, the packed sample bytes. Always available.
    /// </summary>
    public ReadOnlyMemory<byte> EncodedData { get; internal set; }

    /// <summary>The image codec of <see cref="EncodedData"/> (<c>DCTDecode</c>, <c>JPXDecode</c>, <c>JBIG2Decode</c>) or null when the data are plain samples.</summary>
    public string? EncodedFilter { get; internal set; }

    /// <summary>The bytes of the JBIG2 globals stream, or empty.</summary>
    public ReadOnlyMemory<byte> EncodedGlobals { get; internal set; }

    /// <summary>The colour semantics from the dictionary, as data.</summary>
    public PdfImageSemantics Semantics { get; internal set; } = new();

    /// <summary>
    /// Builds the image for an image XObject. Problems are repaired where possible and recorded in the document's diagnostics.
    /// </summary>
    /// <param name="document">The owning document.</param>
    /// <param name="stream">The image XObject stream.</param>
    /// <param name="resources">The resources that name colour spaces for the image (page or form XObject resources), or null.</param>
    /// <param name="options">Decoder registry and preferences; null for the defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The image, or null when the dictionary is unusable (missing or non-positive size, for instance), with a diagnostic.</returns>
    /// <exception cref="PdfLimitException">The image exceeds <see cref="PdfLimits.MaxImagePixels"/> or the decoded-size limit; nothing was allocated for it.</exception>
    public static PdfImageData? FromXObject(PdfDocument document, PdfStream stream, PdfDictionary? resources = null, PdfImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        return ImageLoader.LoadXObject(document, stream, resources, options ?? PdfImageOptions.Default, allowMasks: true, ct: cancellationToken);
    }

    /// <summary>
    /// Builds the image for an inline image (<c>BI … ID … EI</c>), given its dictionary (abbreviated or full keys) and the data
    /// between <c>ID</c> and <c>EI</c> as stored (still filtered).
    /// </summary>
    /// <inheritdoc cref="FromXObject"/>
    public static PdfImageData? FromInline(PdfDocument document, PdfDictionary dictionary, ReadOnlyMemory<byte> data, PdfDictionary? resources = null, PdfImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(dictionary);
        return ImageLoader.LoadInline(document, dictionary, data, resources, options ?? PdfImageOptions.Default, cancellationToken);
    }
}
