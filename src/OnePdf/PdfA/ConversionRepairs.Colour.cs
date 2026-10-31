using System.Linq;
using System.Text;

namespace OnePdf.PdfA;

// Output intent and device colour.
internal sealed partial class ConversionRepairs
{
    private bool _usesIccCmyk, _defaultRgbNeeded, _defaultCmykNeeded;

    /// <summary>
    /// Preparing the file for saving can create resource dictionaries (generated widget appearances, for one). The checker wants the default
    /// spaces in every resource dictionary, so they are added to the new ones too.
    /// </summary>
    public void CoverNewResources()
    {
        if (_defaultRgbNeeded || _defaultCmykNeeded)
        {
            AddDefaultSpaces(_defaultRgbNeeded, _defaultCmykNeeded);
        }
    }

    /// <summary>
    /// ISO 19005-2 6.2.3 and 6.2.4.3: DeviceRGB needs an RGB output intent (or a DefaultRGB space), DeviceCMYK a CMYK one (or DefaultCMYK), DeviceGray any.
    /// A file's own PDF/A intent is kept; else its first usable intent profile (a print-ready file's PDF/X intent) is reused as the PDF/A intent;
    /// else the intent is made from the colours the file uses: CMYK alone gets the generated CMYK profile, anything else the sRGB profile.
    /// Device spaces the intent does not cover are put under a default ICC space in every resource dictionary.
    /// </summary>
    public void ChooseColour(bool rgb, bool gray, bool cmyk, bool cmykNeedsIntent)
    {
        PdfDictionary catalog = _doc.Catalog;
        string? space = null;
        PdfDictionary? chosen = null;
        PdfStream? chosenProfile = null;
        var all = new List<PdfDictionary>();
        if (catalog.Get("OutputIntents") is PdfArray intents)
        {
            for (int i = 0; i < intents.Count; i++)
            {
                if (intents[i] is PdfDictionary oi)
                {
                    all.Add(oi);
                }
            }

            // A PDF/A intent that is usable is kept as it is; otherwise the first intent with a usable profile is the model for ours.
            foreach (PdfDictionary oi in all.OrderBy(static x => x.GetName("S") == "GTS_PDFA1" ? 0 : 1))
            {
                if (oi.Get("DestOutputProfile") is PdfStream ps && ProfileSpace(ps) is { } sp)
                {
                    chosen = oi;
                    chosenProfile = ps;
                    space = sp;
                    break;
                }
            }
        }

        if (chosen is not null && chosen.GetName("S") != "GTS_PDFA1")
        {
            var intent = new PdfDictionary();
            intent.SetEntry("Type", new PdfName("OutputIntent"));
            intent.SetEntry("S", new PdfName("GTS_PDFA1"));
            intent.SetEntry("OutputConditionIdentifier", chosen.GetString("OutputConditionIdentifier") ?? PdfString.FromText("Custom"));
            if (chosen.GetString("Info") is { } info)
            {
                intent.SetEntry("Info", info);
            }

            if (chosen.GetString("RegistryName") is { } reg)
            {
                intent.SetEntry("RegistryName", reg);
            }

            intent.SetEntry("DestOutputProfile", chosen.GetRaw("DestOutputProfile"));
            ((PdfArray)catalog.Get("OutputIntents")!).Append(intent);
            all.Add(intent);
            chosen = intent;
            _log.Add(PdfAChangeKind.OutputIntentAdded, "The file's existing output intent profile was reused as the PDF/A output intent.");
        }
        else if (chosen is null)
        {
            bool useCmyk = cmyk && (!rgb || cmykNeedsIntent);
            var profile = _doc.CreateStream(useCmyk ? CmykProfile.Bytes : SrgbProfile.Bytes, compress: true);
            profile.Dictionary.SetEntry("N", new PdfInteger(useCmyk ? 4 : 3));
            var intent = new PdfDictionary();
            intent.SetEntry("Type", new PdfName("OutputIntent"));
            intent.SetEntry("S", new PdfName("GTS_PDFA1"));
            string id = useCmyk ? CmykProfile.Identifier : SrgbProfile.Identifier;
            intent.SetEntry("OutputConditionIdentifier", PdfString.FromText(id));
            intent.SetEntry("Info", PdfString.FromText(id));
            intent.SetEntry("RegistryName", PdfString.FromText("http://www.color.org"));
            intent.SetEntry("DestOutputProfile", _doc.AddObject(profile));
            catalog.SetEntry("OutputIntents", new PdfArray());
            ((PdfArray)catalog.Get("OutputIntents")!).Append(intent);
            all = [intent];
            chosen = intent;
            space = useCmyk ? "CMYK" : "RGB ";
            _log.Add(PdfAChangeKind.OutputIntentAdded, useCmyk
                ? "A CMYK output intent was added because the file uses DeviceCMYK."
                : "An sRGB output intent was added.");
        }

        // Every intent must name the same profile object (6.2.3); unusable or different ones are dropped.
        if (catalog.Get("OutputIntents") is PdfArray list && chosen.GetRaw("DestOutputProfile") is PdfReference keep)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                PdfDictionary? other = list[i] as PdfDictionary;
                if (ReferenceEquals(other, chosen))
                {
                    continue;
                }

                if (other?.GetRaw("DestOutputProfile") is not PdfReference pr || pr.Number != keep.Number)
                {
                    list.RemoveAt(i);
                    _log.Add(PdfAChangeKind.OutputIntentAdded, "Output intents with another profile were removed; PDF/A allows one profile.");
                }
            }
        }

        if (chosenProfile is not null)
        {
            // The /N of the profile stream must agree with the profile (6.2.3).
            int n = space switch { "CMYK" => 4, "GRAY" => 1, _ => 3 };
            if (chosenProfile.Dictionary.GetInt("N") != n)
            {
                chosenProfile.Dictionary.SetEntry("N", new PdfInteger(n));
            }
        }

        bool needRgb = rgb && space != "RGB ";
        bool needCmyk = cmyk && space != "CMYK";
        _defaultRgbNeeded = needRgb;
        _defaultCmykNeeded = needCmyk;
        if (needRgb || needCmyk)
        {
            AddDefaultSpaces(needRgb, needCmyk);
        }

        _usesIccCmyk = space == "CMYK" || needCmyk || HasIccCmyk();
    }

    private PdfStream ProfileStream(int components)
    {
        PdfStream s = _doc.CreateStream(components == 4 ? CmykProfile.Bytes : SrgbProfile.Bytes, compress: true);
        s.Dictionary.SetEntry("N", new PdfInteger(components));
        return s;
    }

    private string? ProfileSpace(PdfStream profile)
    {
        ReadOnlyMemory<byte> data;
        try
        {
            data = profile.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            return null;
        }

        ReadOnlySpan<byte> h = data.Span;
        if (h.Length < 128 || !h.Slice(36, 4).SequenceEqual("acsp"u8))
        {
            return null;
        }

        string cls = Encoding.ASCII.GetString(h.Slice(12, 4));
        string space = Encoding.ASCII.GetString(h.Slice(16, 4));
        return cls is "prtr" or "mntr" && space is "RGB " or "CMYK" or "GRAY" && h[8] < 5 ? space : null;
    }

    // The default spaces carry the same ICC profiles as the intents would: sRGB for RGB, the generated CMYK profile for CMYK.
    private void AddDefaultSpaces(bool rgb, bool cmyk)
    {
        PdfReference? rgbProfile = null, cmykProfile = null;
        int added = 0;
        Walk((d, stream, parent, key) =>
        {
            bool isResources = key == "Resources" && stream is null;
            if (!isResources)
            {
                return;
            }

            PdfDictionary? cs = d.GetDictionary("ColorSpace");
            if (cs is null)
            {
                cs = new PdfDictionary();
                d.SetEntry("ColorSpace", cs);
            }

            if (rgb && !cs.ContainsKey("DefaultRGB"))
            {
                rgbProfile ??= _doc.AddObject(ProfileStream(3));
                cs.SetEntry("DefaultRGB", IccArray(rgbProfile));
                added++;
            }

            if (cmyk && !cs.ContainsKey("DefaultCMYK"))
            {
                cmykProfile ??= _doc.AddObject(ProfileStream(4));
                cs.SetEntry("DefaultCMYK", IccArray(cmykProfile));
                added++;
            }
        });
        if (added > 0)
        {
            _log.Add(PdfAChangeKind.ColourSpacesAdded, "DefaultRGB/DefaultCMYK colour spaces were added so device colours agree with the output intent.", added);
        }
    }

    private static PdfArray IccArray(PdfReference profile)
    {
        var a = new PdfArray();
        a.Append(new PdfName("ICCBased"));
        a.Append(profile);
        return a;
    }

    private bool HasIccCmyk()
    {
        bool found = false;
        Walk((d, stream, parent, key) =>
        {
            if (stream is not null && d.GetInt("N") == 4 && !d.ContainsKey("Subtype") && !d.ContainsKey("Type"))
            {
                found = true;
            }
        });
        return found;
    }

    /// <summary>
    /// ISO 19005-2 6.2.4.2: overprint mode 1 must not be set where an ICC-based CMYK space paints with overprint on. When the file has one,
    /// every graphics state with <c>/OPM 1</c> gets 0; the two modes differ only for overprinted CMYK tints.
    /// </summary>
    public void ResetOverprintMode()
    {
        if (!_usesIccCmyk)
        {
            return;
        }

        Walk((d, stream, parent, key) =>
        {
            if (stream is null && d.GetInt("OPM") == 1)
            {
                d.SetEntry("OPM", new PdfInteger(0));
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Overprint mode 1 was set to 0 (not allowed with ICC-based CMYK).");
            }
        });
    }
}
