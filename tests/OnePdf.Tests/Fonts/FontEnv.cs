using OnePdf.FontPrograms;

namespace OnePdf.Tests.Fonts;

/// <summary>Where the font tests look for fonts that are not committed: the OFL set in a cache directory and the host's installed fonts.</summary>
internal static class FontEnv
{
    /// <summary>Directory holding downloaded OFL fonts (Liberation 2.x, Noto); set through ONEPDF_FONT_CACHE. Null when unset.</summary>
    public static string? CacheDir
    {
        get
        {
            string? d = Environment.GetEnvironmentVariable("ONEPDF_FONT_CACHE");
            return d is not null && Directory.Exists(d) ? d : null;
        }
    }

    /// <summary>Directory with the unpacked upstream data (AFM files, cmap-resources); set through ONEPDF_UPSTREAM. Null when unset.</summary>
    public static string? UpstreamDir
    {
        get
        {
            string? d = Environment.GetEnvironmentVariable("ONEPDF_UPSTREAM");
            return d is not null && Directory.Exists(d) ? d : null;
        }
    }

    public static IEnumerable<string> CachedFonts()
    {
        string? dir = CacheDir;
        if (dir is null)
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    public static IEnumerable<string> SystemFonts(params string[] extensions)
    {
        return SystemFontRoots()
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>The conventional font directories of the host platform.</summary>
    public static IEnumerable<string> SystemFontRoots()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
        }
    }

    /// <summary>Runs every glyph of a program through outline and width extraction and returns a summary of what failed.</summary>
    public static GlyphSweep Sweep(FontProgram font)
    {
        int empty = 0, failed = 0, noAdvance = 0;
        string? firstError = null;
        for (int g = 0; g < font.GlyphCount; g++)
        {
            try
            {
                GlyphOutline o = font.GetOutline(g);
                if (o.IsEmpty)
                {
                    empty++;
                }

                foreach (float c in o.Coords)
                {
                    if (!float.IsFinite(c))
                    {
                        throw new InvalidOperationException("non-finite coordinate in glyph " + g);
                    }
                }

                if (!font.TryGetAdvance(g, out _))
                {
                    noAdvance++;
                }
            }
            catch (FontProgramException ex)
            {
                failed++;
                firstError ??= "glyph " + g + ": " + ex.Message;
            }
        }

        return new GlyphSweep(font.GlyphCount, empty, failed, noAdvance, firstError);
    }
}

internal readonly record struct GlyphSweep(int Glyphs, int Empty, int Failed, int NoAdvance, string? FirstError);
