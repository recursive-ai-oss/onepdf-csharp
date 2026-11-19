#if !LAYOUT_CORE
using System.Runtime.CompilerServices;

// IHyphenator moved to the core assembly so that the hyphenation package works with both layout packages. Code built against an
// earlier version of this assembly still finds it here.
[assembly: TypeForwardedTo(typeof(OnePdf.Layout.IHyphenator))]
#endif
