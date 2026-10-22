using System;
using OnePdf;
using OnePdf.Fonts;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (Library.Name != "OnePDF" || FontsPackage.Name != "OnePdf.Fonts")
{
    Console.Error.WriteLine("library identity missing");
    return 1;
}

if (OnePdf.AotSmoke.FontSmoke.Run() is { } failure)
{
    Console.Error.WriteLine("font data: " + failure);
    return 1;
}

Console.WriteLine("ok");
return 0;
