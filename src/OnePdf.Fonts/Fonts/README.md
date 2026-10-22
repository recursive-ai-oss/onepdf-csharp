# Substitute fonts

These are the unmodified release files of the following fonts, all under the SIL Open Font License 1.1 (each font's
license text and copyright lines are in its `*.OFL.txt`). They are embedded as resources byte for byte: nothing is subset,
converted or rebuilt, because the Liberation fonts carry Reserved Font Names.

| Files | Font | Release | License text |
|---|---|---|---|
| `LiberationSans-*`, `LiberationSerif-*`, `LiberationMono-*` (12 faces) | Liberation Fonts 2.1.5 (TTF) | https://github.com/liberationfonts/liberation-fonts/files/7261482/liberation-fonts-ttf-2.1.5.tar.gz | `Liberation.OFL.txt` (the release's `LICENSE`) |
| `NotoSansMath-Regular.ttf` | Noto Sans Math 3.000, `unhinted/ttf` | https://github.com/notofonts/math/releases/tag/NotoSansMath-v3.000 | `NotoSansMath.OFL.txt` |
| `NotoSansSymbols2-Regular.ttf` | Noto Sans Symbols 2 2.008, `unhinted/ttf` | https://github.com/notofonts/symbols/releases/tag/NotoSansSymbols2-v2.008 | `NotoSansSymbols2.OFL.txt` |
| `NotoSansSymbols-Regular.ttf` | Noto Sans Symbols 2.003, `unhinted/ttf` | https://github.com/notofonts/symbols/releases/tag/NotoSansSymbols-v2.003 | `NotoSansSymbols.OFL.txt` |

`SHA256SUMS` lists the files; `tools/fonts/fetch.sh CACHE_DIR --verify-repo` downloads the pinned releases (URL and SHA-256 of
each archive are in the script) and checks that these files are identical to them.
