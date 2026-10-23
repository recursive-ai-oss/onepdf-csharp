# Platform checks (osx-arm64, linux-arm64)

Run before a release, from a Linux x64 machine with the .NET 10 SDK. Both checks must give the same results as the x64 run:
a clean build, a full test run with only the documented skips, the same snapshots, an AOT smoke that prints `ok`, and the same
extraction scores (generated set: OnePDF 0.3 / 1.2 % content / order). A difference in a snapshot or score is a determinism
bug; fix the code, do not regenerate the snapshot.

Expected skips: the qpdf comparison tests (no qpdf for the platform), and on a Mac the tests that need Type 1 fonts, the
Noto Sans CJK collection or `NimbusSans-Regular.otf`. The Unicode conformance test runs when `ONEPDF_UNICODE` points at the
Unicode data files.

## macOS (arm64)

Needs an arm64 Mac you can ssh to, with a user-local .NET 10 SDK (`dotnet-install.sh --channel 10.0 --install-dir $MAC_ROOT/dotnet`).
Everything lives under one directory; nothing needs sudo.

```
export MAC=user@host MAC_KEY=~/.ssh/id_ed25519
rsync -a --delete -e "ssh -i $MAC_KEY" --exclude bin/ --exclude obj/ --exclude artifacts/ --exclude TestResults/ --exclude .git ./ $MAC:onepdf/tree/
scp -i $MAC_KEY tools/platform/mac.sh $MAC:onepdf/mac.sh
ssh -i $MAC_KEY $MAC 'bash ~/onepdf/mac.sh'      # build, test, AOT smoke, quality scores, font lookup; logs in ~/onepdf/logs
```

`mac.sh` expects the SDK in `~/onepdf/dotnet` and optional test data under `~/onepdf/data/{fonts,unicode,upstream}`
(`ONEPDF_FONT_CACHE`, `ONEPDF_UNICODE`, `ONEPDF_UPSTREAM`). The test log has a line `Arial -> ... Times New Roman Bold -> ...` naming the installed files the font provider chose;
the provider scans `/System/Library/Fonts`, `/Library/Fonts` and `~/Library/Fonts`.

## linux-arm64 under qemu-user

Needs `qemu-aarch64`, an arm64 sysroot (glibc, libstdc++, libz; set `QEMU_LD_PREFIX` to it) and, for the AOT smoke, an
aarch64 cross gcc behind a small wrapper that drops clang-style `--target`/`-fuse-ld`/`--sysroot` options.

```
export QEMU_LD_PREFIX=/path/to/arm64/sysroot DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
dotnet publish tests/OnePdf.Tests -r linux-arm64 --self-contained -o out/armtests
(cd out/armtests && qemu-aarch64 ./OnePdf.Tests)            # about 2 minutes; keep it under a memory cap
dotnet publish tests/OnePdf.AotSmoke -r linux-arm64 -warnaserror -p:CppCompilerAndLinker=/path/to/cross-gcc-wrapper \
    -p:ObjCopyName=true -p:StripSymbols=false -p:SysRoot=/path/to/arm64/sysroot -o out/armaot
(cd out/armaot && qemu-aarch64 ./OnePdf.AotSmoke)            # prints ok
```

## What to look at

- Machine-dependent output is a bug in the test or the code: snapshot tests use the bundled substitute fonts rather than
  whatever the machine has installed, so glyph ids are the same everywhere.
- The generated extraction set embeds its fonts, so system fonts must not change its scores.
