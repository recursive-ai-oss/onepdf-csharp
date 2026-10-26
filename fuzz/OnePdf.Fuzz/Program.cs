using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Fuzz;
using SharpFuzz;

// One fuzz executable, one target per input surface. The target is chosen by the ONEPDF_FUZZ_TARGET environment
// variable because libFuzzer's .NET host starts the program without command-line arguments.
//
// To add a target: write a static method `Run(ReadOnlySpan<byte> data)` that lets only the library's typed exceptions
// escape, then add one line to `Targets` below.
//
// Without a libFuzzer host there are two local modes:
//   mutate <target> <seconds> <seed file or directory>...   random byte-level mutation of the seeds, in process; a crashing
//                                                           input is saved under $ONEPDF_FUZZ_WORK/findings/mutate (printed as base64 when unset)
//   replay <target> <file>...                                runs saved inputs once each (reproduces a finding)
//   seeds <target> <out dir> [pdf dir] [font dir]            writes starting inputs for a target (used by run.sh)
//   interpret-dir <directory>                               opens every .pdf and interprets its first pages into display lists
//   open-dir <directory> [seconds-per-file]                 opens, walks and times every .pdf in a directory tree
//   fonts-dir <directory>...                                 loads and decodes every font of every page, reports counts by font type
internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> data);

    private static readonly Dictionary<string, Target> Targets = new(StringComparer.Ordinal)
    {
        ["noop"] = static data => _ = Library.Name.Length + data.Length,
        ["parser"] = ReaderTargets.Parser,
        ["document"] = ReaderTargets.Document,
        ["model"] = ReaderTargets.DocumentModel,
        ["filters"] = FilterTarget.Run,
        ["type4"] = Type4Target.Run,
        ["ccitt"] = CcittTarget.Run,
        ["fontprogram"] = FontProgramTarget.Run,
        ["cmap"] = CMapTarget.Run,
        ["security"] = SecurityTarget.Run,
        ["pdffont"] = FontSweep.Target,
        ["image"] = ImageTarget.Run,
        ["png"] = PngTarget.Run,
        ["content"] = ContentTarget.Run,
        ["appearance"] = AppearanceTarget.Run,
        ["shading"] = ShadingTarget.Run,
        ["extraction"] = ExtractionTarget.Run,
        ["save"] = SaveTarget.Run,
        ["redact"] = RedactTarget.Run,
        ["tag"] = TagTarget.Run,
        ["fontsubset"] = FontSubsetTarget.Run,
        ["signature"] = SignatureTarget.Run,
        ["pdfa"] = PdfATarget.Run,
    };

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "mutate")
        {
            return Mutate(args);
        }

        if (args.Length > 1 && args[0] == "fonts-dir")
        {
            return FontSweep.RunDirs(args);
        }

        if (args.Length > 2 && args[0] == "replay")
        {
            return Replay(args);
        }

        if (args.Length > 0 && args[0] == "seeds")
        {
            return Seeds.Run(args);
        }

        if (args.Length > 1 && args[0] == "interpret-dir")
        {
            return InterpretSweep.Run(args);
        }

        if (args.Length > 0 && args[0] == "open-dir")
        {
            return OpenDir(args);
        }

        string? name = Environment.GetEnvironmentVariable("ONEPDF_FUZZ_TARGET") ?? (args.Length > 0 ? args[0] : null);
        if (name is null || !Targets.TryGetValue(name, out Target? target))
        {
            Console.Error.WriteLine("usage: set ONEPDF_FUZZ_TARGET to one of: " + string.Join(", ", Targets.Keys));
            return 2;
        }

        Fuzzer.LibFuzzer.Run(data => target(data));
        return 0;
    }

    private static int Replay(string[] args)
    {
        if (!Targets.TryGetValue(args[1], out Target? target))
        {
            Console.Error.WriteLine("unknown target " + args[1]);
            return 2;
        }

        int failures = 0;
        foreach (string f in args.Skip(2))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                target(File.ReadAllBytes(f));
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

    private static int Mutate(string[] args)
    {
        if (args.Length < 4 || !Targets.TryGetValue(args[1], out Target? target))
        {
            Console.Error.WriteLine("usage: mutate <target> <seconds> <seed>...");
            return 2;
        }

        var seeds = new List<byte[]>();
        foreach (string p in args.Skip(3))
        {
            foreach (string f in Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : [p])
            {
                if (new FileInfo(f).Length < 1 << 20)
                {
                    seeds.Add(File.ReadAllBytes(f));
                }
            }
        }

        var rnd = new Random(12345);
        var clock = Stopwatch.StartNew();
        double limit = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        long runs = 0;
        double worst = 0;
        while (clock.Elapsed.TotalSeconds < limit)
        {
            var buf = new List<byte>(seeds[rnd.Next(seeds.Count)]);
            int edits = 1 + rnd.Next(8);
            for (int e = 0; e < edits && buf.Count > 0; e++)
            {
                int at = rnd.Next(buf.Count);
                switch (rnd.Next(5))
                {
                    case 0: buf[at] = (byte)rnd.Next(256); break;
                    case 1: buf[at] ^= (byte)(1 << rnd.Next(8)); break;
                    case 2: buf.RemoveRange(at, Math.Min(1 + rnd.Next(16), buf.Count - at)); break;
                    case 3:
                        int from = rnd.Next(buf.Count);
                        buf.InsertRange(at, buf.GetRange(from, Math.Min(64, buf.Count - from)));
                        break;
                    default:
                        int len = rnd.Next(1, 12);
                        for (int k = 0; k < len; k++)
                        {
                            buf.Insert(at, (byte)('0' + rnd.Next(10)));
                        }

                        break;
                }
            }

            byte[] input = [.. buf];

            double t0 = clock.Elapsed.TotalSeconds;
            try
            {
                target(input);
            }
#pragma warning disable CA1031 // A fuzz driver must record any escaping exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                string? work = Environment.GetEnvironmentVariable("ONEPDF_FUZZ_WORK");
                string saved;
                if (string.IsNullOrEmpty(work))
                {
                    saved = "not saved (set ONEPDF_FUZZ_WORK); base64: " + Convert.ToBase64String(input.AsSpan(0, Math.Min(input.Length, 4096)));
                }
                else
                {
                    string dir = Path.Combine(work, "findings", "mutate");
                    Directory.CreateDirectory(dir);
                    saved = Path.Combine(dir, $"crash-{args[1]}-{runs}.bin");
                    File.WriteAllBytes(saved, input);
                }

                Console.WriteLine($"CRASH {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\nsaved {saved}");
                return 1;
            }

            worst = Math.Max(worst, clock.Elapsed.TotalSeconds - t0);
            runs++;
        }

        Console.WriteLine($"target={args[1]} seeds={seeds.Count} runs={runs} slowest={worst:F3}s crashes=0");
        return 0;
    }

    private static int OpenDir(string[] args)
    {
        int ok = 0, failed = 0, slow = 0, crashes = 0, modelErrors = 0;
        var typed = new Dictionary<string, int>();
        foreach (string f in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using PdfDocument doc = PdfDocument.Open(f);
                _ = doc.PageCount;
                string fileName = Path.GetFileName(f);
                ReaderTargets.WalkModel(doc, (part, e) =>
                {
                    modelErrors++;
                    Console.WriteLine($"MODEL {fileName} [{part}]: {e.GetType().Name}: {e.Message}");
                });
                for (int i = 0; i < Math.Min(doc.ObjectCount, 5000); i++)
                {
                    try
                    {
                        if (doc.GetObject(i) is PdfStream s)
                        {
                            _ = s.GetDecodedBytes();
                        }
                    }
                    catch (PdfLimitException)
                    {
                    }
                    catch (PdfFormatException)
                    {
                    }
                }

                ok++;
            }
            catch (PdfException ex)
            {
                failed++;
                typed[ex.GetType().Name] = typed.GetValueOrDefault(ex.GetType().Name) + 1;
                Console.WriteLine($"{Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}");
            }
#pragma warning disable CA1031 // The sweep must report any other exception as a crash.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                crashes++;
                Console.WriteLine($"CRASH {f}: {ex}");
            }

            if (sw.Elapsed.TotalSeconds > 10)
            {
                slow++;
                Console.WriteLine($"SLOW {f}: {sw.Elapsed.TotalSeconds:F1}s");
            }
        }

        Console.WriteLine($"opened={ok} typed-errors={failed} ({string.Join(", ", typed.Select(kv => kv.Key + "=" + kv.Value))}) crashes={crashes} slow={slow} model-errors={modelErrors}");
        return crashes == 0 ? 0 : 1;
    }
}
