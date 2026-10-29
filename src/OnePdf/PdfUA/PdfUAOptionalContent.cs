namespace OnePdf.PdfUA;

// ISO 14289-1 7.10: every optional content configuration dictionary, the default one (/OCProperties /D) and each of /Configs, carries
// a /Name so assistive technology can offer the configuration by name (ISO 32000-1 8.11.4.3, Table 101).
internal sealed partial class PdfUAChecker
{
    private void CheckOptionalContentNames()
    {
        PdfDictionary? props = _doc.Catalog.GetDictionary("OCProperties");
        if (props is null)
        {
            return;
        }

        foreach ((PdfDictionary config, string where) in OptionalContentConfigurations(props))
        {
            if (string.IsNullOrWhiteSpace(config.GetString("Name")?.GetText()))
            {
                Add("20", "7.10", $"The optional content configuration {where} has no /Name.", config.Reference?.Number ?? -1);
            }

            if (config.ContainsKey("AS"))
            {
                Add("20", "7.10", $"The optional content configuration {where} has an /AS entry, which ISO 14289-1 7.10 does not allow.", config.Reference?.Number ?? -1);
            }
        }
    }

    internal static List<(PdfDictionary Config, string Where)> OptionalContentConfigurations(PdfDictionary props)
    {
        var all = new List<(PdfDictionary, string)>();
        if (props.GetDictionary("D") is { } d)
        {
            all.Add((d, "(/OCProperties /D)"));
        }

        if (props.GetArray("Configs") is { } configs)
        {
            for (int i = 0; i < configs.Count; i++)
            {
                if (configs[i] is PdfDictionary c)
                {
                    all.Add((c, $"(/OCProperties /Configs entry {i})"));
                }
            }
        }

        return all;
    }
}
