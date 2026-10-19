using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Globalization",
    "CA1309:Use ordinal string comparison",
    Justification = "Artist, album, title, genre, location, and station matching is user-facing and intentionally follows the current Windows culture.")]

[assembly: SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Window lifetime; MainWindow cancels and disposes every owned service in its Closing handler.",
    Scope = "type",
    Target = "~T:Chistara.MainWindow")]
