using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Layout;
using SharpFuzz;

namespace OnePdf.Layout.Fuzz;

// Fuzz harness for the layout package. Two targets: `svg` (see SvgTarget; selected by ONEPDF_FUZZ_TARGET=svg or `replay svg`) and `layout`:
//
//   byte 0       flags: bit 0 a hostile font follows the script, bit 1 right-to-left, bit 2 untagged, bit 3 justified text,
//                       bit 4 landscape, bit 5 debug overlay
//   bytes 1-2    length of the script
//   script       bytes that choose a random element tree, its text and its settings (read in a loop, so every input is valid)
//   rest         with bit 6 (64): a colour test font built in code, whose COLR and CPAL tables are these bytes (the first byte
//                       places the split between the two; bit 7 makes the valid default table version 1)
//                with flag 0: a font file (TrueType/OpenType) registered as a family and used as the first fallback
//
// Invariants: only the library's typed exceptions come out; laying the same tree out twice gives the same bytes; the file opens;
// every text leaf's token appears exactly once in the content-order text.
internal static class Program
{
    private static int Main(string[] args)
    {
        bool svg = Environment.GetEnvironmentVariable("ONEPDF_FUZZ_TARGET") == "svg" || (args.Length > 1 && args[0] == "replay" && args[1] == "svg");
        if (args.Length > 1 && args[0] == "replay")
        {
            int failures = 0;
            foreach (string f in args.Skip(1).Where(a => a is not ("layout" or "svg")))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (svg)
                    {
                        SvgTarget.Run(File.ReadAllBytes(f));
                    }
                    else
                    {
                        LayoutTarget.Run(File.ReadAllBytes(f));
                    }

                    Console.WriteLine($"ok {f} ({sw.Elapsed.TotalSeconds:F2}s)");
                }
#pragma warning disable CA1031 // Replay reports any escaping exception as a failure.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failures++;
                    Console.WriteLine($"FAIL {f}: {ex}");
                }
            }

            return failures == 0 ? 0 : 1;
        }

        if (args.Length > 1 && args[0] == "seeds" && svg)
        {
            return SvgTarget.Seeds(args[1], args.Length > 2 ? args[2] : string.Empty);
        }

        if (args.Length > 1 && args[0] == "seeds")
        {
            return Seeds.Run(args[1], args.Length > 2 ? args[2] : null);
        }

        if (args.Length > 2 && args[0] == "mutate")
        {
            return Seeds.Mutate(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args.Skip(2));
        }

        if (svg)
        {
            Fuzzer.LibFuzzer.Run(data => SvgTarget.Run(data));
            return 0;
        }

        Fuzzer.LibFuzzer.Run(data => LayoutTarget.Run(data));
        return 0;
    }
}
