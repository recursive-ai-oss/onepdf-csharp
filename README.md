# OnePDF

A pure-managed .NET PDF library: parse, repair, decrypt, extract text and read document structure, with no dependencies beyond
the base class library. It targets `net10.0` and is built to be NativeAOT- and trim-compatible.

OnePDF is read-only for now. It contains no `unsafe` code, and it never runs code or touches the network: JavaScript, actions
and URIs in a document are data, never executed or fetched.

> Early development: the repository is a skeleton and has no PDF functionality yet.

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OnePdf` | The library (assembly and namespace `OnePdf`). |
| `RecursiveAI.OnePdf.Fonts` | Substitute fonts for documents that don't embed the standard 14. |

## Building

```
dotnet build
dotnet test
dotnet publish tests/OnePdf.AotSmoke -c Release -r linux-x64 -warnaserror
```

Build output goes outside the checkout: set `ONEPDF_ARTIFACTS` to choose where (default: `../OnePdf-artifacts`).

## License

`MIT OR Apache-2.0`, Copyright (c) Recursive AI, LLC. See `LICENSE-MIT`, `LICENSE-APACHE` and `THIRD-PARTY-NOTICES.md`.
