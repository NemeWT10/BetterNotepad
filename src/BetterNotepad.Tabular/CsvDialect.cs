namespace BetterNotepad.Tabular;

/// <summary>How the file quotes fields. Used when new text is written (new rows, edited cells).</summary>
public enum CsvQuoteStyle
{
    /// <summary>Quote only when the value needs it (delimiter, quote, line break).</summary>
    Minimal,

    /// <summary>Every non-empty field is quoted.</summary>
    All,

    /// <summary>Text fields are quoted, numeric fields are not.</summary>
    NonNumeric,
}

/// <summary>
/// The shape of a delimited text file. Immutable; derive variants with <c>with</c> (e.g. the user toggles
/// <see cref="HasHeader"/> or picks another delimiter, then the table is parsed again with the new dialect).
/// </summary>
public sealed record CsvDialect
{
    public static CsvDialect Default { get; } = new();

    /// <summary>Delimiters considered by <see cref="Detect"/>.</summary>
    public static IReadOnlyList<char> CandidateDelimiters { get; } = [',', ';', '\t', '|'];

    public char Delimiter { get; init; } = ',';

    /// <summary>Quote character (normally <c>"</c>; <c>'</c> is detected too). A quote inside a quoted field is doubled.</summary>
    public char Quote { get; init; } = '"';

    /// <summary>The first record holds column names.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>"\r\n", "\n" or "\r": the terminator that ends records. Used for rows the engine writes.</summary>
    public string LineEnding { get; init; } = "\r\n";

    public CsvQuoteStyle QuoteStyle { get; init; } = CsvQuoteStyle.Minimal;

    /// <summary>0..1. How consistently the chosen delimiter splits the sample (0 = nothing tabular found).</summary>
    public double Confidence { get; init; }

    /// <summary>The sample splits into a consistent grid; below this the host may keep the plain-text view.</summary>
    public bool LooksTabular => Confidence >= 0.6;

    /// <summary>
    /// Detects the delimiter (consistency scoring over sample records, quote aware), quote character, header row,
    /// line ending and quoting style. <paramref name="fileName"/> (or just an extension) only breaks ties
    /// (.tsv leans to tab, .psv to pipe) and picks the default when the sample has no structure.
    /// A leading BOM character is ignored.
    /// </summary>
    public static CsvDialect Detect(string text, string? fileName = null) => CsvDialectDetector.Detect(text, fileName);

    internal void Validate()
    {
        if (Delimiter == Quote || Delimiter is '\r' or '\n' || Quote is '\r' or '\n' or '\0')
            throw new ArgumentException("Invalid CSV dialect: delimiter and quote must be distinct, non-line-break characters.");
    }
}
