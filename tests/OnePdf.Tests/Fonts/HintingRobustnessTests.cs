using System.Diagnostics;
using OnePdf.FontPrograms;

namespace OnePdf.Tests.Fonts;

/// <summary>
/// Hostile and unusual glyph programs run directly on the interpreter: arithmetic on extreme values, the twilight zone across
/// programs, and the instruction budget shared by the components of one glyph. Kept in step with the same tests in OneImage.Text.
/// </summary>
public class HintingRobustnessTests
{
    private static readonly byte[] Nop = [0x00];
    private static readonly byte[] Loop = [0x00, 0xB8, 0xFF, 0xFC, 0x1C];

    private const byte SvtcaY = 0x00, SvtcaX = 0x01, Szps = 0x16, Wcvtp = 0x44, Miap1 = 0x3F, Miap0 = 0x3E;

    private static byte[] Maxp()
    {
        var m = new byte[32];
        m[17] = 16;
        m[19] = 16;
        m[21] = 16;
        m[25] = 64;
        return m;
    }

    private static TrueTypeHinter Hinter(byte[] prep) =>
        TrueTypeHinter.Create(1000, 12, false, Array.Empty<byte>(), prep, new byte[16], Maxp(), out _)!;

    private static bool Hint(TrueTypeHinter h, byte[] program, out float[] y)
    {
        float[] x = [100, 600, 600, 100];
        y = [0, 0, 700, 700];
        return h.TryHint(x, y, [true, true, true, true], 0, 4, [3], program, 0, 600, new TrueTypeHinter.HintBudget());
    }

    private static byte[] Push(int v) => v is >= 0 and < 256 ? [0xB0, (byte)v] : [0xB8, (byte)(v >> 8), (byte)v];

    // Builds any 32-bit value from 16-bit pieces: hi, sixteen doublings (ADD wraps), then lo.
    private static byte[] Const(int v)
    {
        short lo = (short)v;
        short hi = (short)((v - lo) >> 16);
        var code = new List<byte> { 0xB8, (byte)(hi >> 8), (byte)hi };
        for (int i = 0; i < 16; i++)
        {
            code.Add(0x20); // DUP
            code.Add(0x60); // ADD
        }

        code.AddRange([0xB8, (byte)(lo >> 8), (byte)lo, 0x60]);
        return [.. code];
    }

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    [Fact]
    public void ExtremeValuesInArithmeticAndDistancesDoNotThrow()
    {
        TrueTypeHinter h = Hinter(Nop);
        (string Name, byte[] Program)[] programs =
        [
            ("abs", Cat(Const(int.MinValue), [0x64])),
            ("miap", Cat([SvtcaY], Push(0), Const(int.MinValue), [Wcvtp], Push(0), Push(0), [Miap1])),
            ("mirp", Cat([SvtcaY], Push(0), Const(int.MinValue), [Wcvtp], Push(1), Push(0), [0xE4])),
            ("mdrp", Cat([SvtcaX], Push(0), [Szps], Push(0), Const(int.MinValue), [Wcvtp], Push(0), Push(0), [Miap0], Push(1), [0xC0])),
        ];
        var failures = new List<string>();
        foreach ((string name, byte[] program) in programs)
        {
            bool ran = false;
            Exception? thrown = Record.Exception(() => ran = Hint(h, program, out _));
            if (thrown is not null || !ran)
            {
                failures.Add($"{name}: {(thrown is null ? "the program did not run to its end" : thrown.GetType().Name)}");
            }
        }

        Assert.Empty(failures);
    }

    // Twilight points a0 (0,0), a1 (int.MinValue,3), b0 (2,-6), b1 (-715827881,-5): the discriminant is -1 and the numerator's
    // product wraps to long.MinValue, so a 64-bit division overflows.
    private static byte[] IsectProgram()
    {
        var code = new List<byte>();
        void Set(int point, int cvtX, int cvtY)
        {
            code.Add(SvtcaX);
            code.AddRange(Push(0));
            code.AddRange(Const(cvtX));
            code.Add(Wcvtp);
            code.AddRange(Push(point));
            code.AddRange(Push(0));
            code.Add(Miap0);
            code.Add(SvtcaY);
            code.AddRange(Push(point));
            code.AddRange(Const(cvtY));
            code.Add(0x48); // SCFS keeps x
        }

        code.AddRange(Push(0));
        code.Add(Szps);
        Set(1, int.MinValue, 3);
        Set(2, 2, -6);
        Set(3, -715827881, -5);
        foreach (int p in new[] { 4, 0, 1, 2, 3 })
        {
            code.AddRange(Push(p));
        }

        code.Add(0x0F); // ISECT
        return [.. code];
    }

    [Fact]
    public void IsectWithAnOverflowingDivisionDoesNotThrow()
    {
        TrueTypeHinter h = Hinter(Nop);
        bool ran = false;
        Exception? thrown = Record.Exception(() => ran = Hint(h, IsectProgram(), out _));
        Assert.Null(thrown);
        Assert.True(ran, "the program did not run to its end");
    }

    private static readonly byte[] PrepSetsTwilight = Cat([SvtcaY], Push(0), [0x13], Push(0), Push(640), [Wcvtp], Push(0), Push(0), [Miap0]);
    private static readonly byte[] GlyphReadsTwilight = Cat([SvtcaY], Push(3), Push(0), [0x15], Push(0), [0x46], Push(1), [0x15, 0x48]);
    private static readonly byte[] GlyphClearsTwilight = Cat([SvtcaY], Push(0), [0x13], Push(0), Push(1), [Miap0]);

    [Fact]
    public void TwilightPointsSetByTheControlValueProgramReachGlyphPrograms()
    {
        TrueTypeHinter h = Hinter(PrepSetsTwilight);
        Assert.True(Hint(h, GlyphReadsTwilight, out float[] y));
        Assert.Equal(10f * 1000 / 12, y[3], 0.01f);
    }

    [Fact]
    public void EachGlyphStartsFromItsOwnCopyOfTheTwilightZone()
    {
        TrueTypeHinter h = Hinter(PrepSetsTwilight);
        Assert.True(Hint(h, GlyphClearsTwilight, out _));
        Assert.True(Hint(h, GlyphReadsTwilight, out float[] y));
        Assert.Equal(10f * 1000 / 12, y[3], 0.01f);
    }

    // ---- the budget shared by the programs of one glyph ----

    // Counts 30000 loop iterations (about 180k instructions), then rounds point 3 so the glyph visibly moves.
    private static readonly byte[] SlowProgram =
    [
        0xB8, 0x75, 0x30,
        0xB0, 0x01, 0x61, 0x20,
        0xB8, 0xFF, 0xF8, 0x23, 0x78,
        0x00, 0xB0, 0x03, 0x2F,
    ];

    private static byte[] Square(byte[] instructions)
    {
        var d = new List<byte>();
        void W(int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        W(1); W(100); W(0); W(600); W(700); W(3); W(instructions.Length);
        d.AddRange(instructions);
        d.AddRange([1, 1, 1, 1]);
        foreach (int v in new[] { 100, 500, 0, -500, 0, 0, 700, 0 })
        {
            W(v);
        }

        return [.. d];
    }

    private static byte[] Composite(int glyph, int count)
    {
        var d = new List<byte>();
        void W(int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        W(-1); W(0); W(0); W(0); W(0);
        for (int i = 0; i < count; i++)
        {
            W(i == count - 1 ? 0x0003 : 0x0023);
            W(glyph);
            W(0);
            W(0);
        }

        return [.. d];
    }

    // Glyphs: 0 empty, 1 a square with <leaf>, 2 a composite of <components> copies of glyph 1.
    private static TrueTypeGlyphs Glyphs(byte[] leaf, int components, bool hinted)
    {
        byte[][] glyphs = [[], Square(leaf), Composite(1, components)];
        var glyf = new List<byte>();
        byte[] loca = new byte[4 * (glyphs.Length + 1)];
        for (int i = 0; i < glyphs.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * i), (uint)glyf.Count);
            glyf.AddRange(glyphs[i]);
            if (glyphs[i].Length % 2 != 0)
            {
                glyf.Add(0);
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * glyphs.Length), (uint)glyf.Count);
        return new TrueTypeGlyphs(glyf.ToArray(), loca, true, glyphs.Length, hinted ? Hinter(Nop) : null);
    }

    [Fact]
    public void ACompositeWithinTheSharedBudgetIsHinted()
    {
        var hinted = Glyphs(SlowProgram, 4, true);
        var plain = Glyphs(SlowProgram, 4, false);
        Assert.NotEqual(plain.Load(1).Coords, hinted.Load(1).Coords);
        Assert.NotEqual(plain.Load(2).Coords, hinted.Load(2).Coords);
    }

    [Fact]
    public void ACompositeWhoseComponentsTogetherExceedTheBudgetIsDrawnUnhinted()
    {
        // Eight components of 180k instructions each pass the per-program limit but not the per-glyph total.
        var hinted = Glyphs(SlowProgram, 8, true);
        var plain = Glyphs(SlowProgram, 8, false);
        Assert.Equal(plain.Load(2).Coords, hinted.Load(2).Coords);
        Assert.NotNull(hinted.HintFailure);
    }

    [Fact]
    public void ManyHostileComponentsStayBounded()
    {
        var hinted = Glyphs(Loop, 500, true);
        var plain = Glyphs(Loop, 500, false);
        var sw = Stopwatch.StartNew();
        Assert.Equal(plain.Load(2).Coords, hinted.Load(2).Coords);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AComponentWhoseOwnProgramFailsStaysUnhintedAlone()
    {
        // Glyph 1 loops until its own limit; glyph 2 is a single component of it. The budget is not spent, so nothing else changes:
        // the failing leaf keeps its plain points and the composite is not redrawn from scratch (the failure is still reported).
        var hinted = Glyphs(Loop, 1, true);
        var plain = Glyphs(Loop, 1, false);
        Assert.Equal(plain.Load(2).Coords, hinted.Load(2).Coords);
        Assert.NotNull(hinted.HintFailure);
    }
}
