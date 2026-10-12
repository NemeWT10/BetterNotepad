namespace BetterNotepad.Tabular;

internal static class CsvDialectDetector
{
    private const int SampleChars = 256 * 1024;
    private const int SampleRecords = 300;
    private const int HeaderDataRows = 60;

    /// <summary>Delimiters closer than this in score are decided by the priority order below.</summary>
    private const double TieEpsilon = 0.03;

    /// <summary>Tie-break order: a tab or semicolon is rarely there by accident, a comma is in every sentence and every Polish decimal.</summary>
    private static readonly char[] Priority = ['\t', ';', '|', ','];

    private readonly record struct Rec(int Start, int End, int Next);

    private sealed class Scan
    {
        public char Delimiter;
        public char Quote;
        public List<Rec> Records = [];
        public double Score;
        public int ModalColumns;
        public int QuotedFields;
    }

    public static CsvDialect Detect(string text, string? fileName)
    {
        var start = CsvScanner.ContentStart(text);
        var limit = Math.Min(text.Length, start + SampleChars);
        var truncated = limit < text.Length;
        var sample = text.AsSpan(0, limit);
        var ext = ExtensionOf(fileName);

        Scan? best = null;
        foreach (var d in CsvDialect.CandidateDelimiters)
        {
            var scan = Run(sample, start, d, '"', truncated);
            scan.Score += ExtensionBonus(ext, d);
            best = Better(best, scan);
        }

        best ??= Run(sample, start, ',', '"', truncated);

        // single quote as the quote character: only when it clearly wraps fields and the double quote does not
        if (best.QuotedFields == 0 && sample.IndexOf('\'') >= 0)
        {
            var alt = Run(sample, start, best.Delimiter, '\'', truncated);
            if (alt.QuotedFields >= 2 && alt.Score >= best.Score - TieEpsilon) best = alt;
        }

        var found = best.Score >= 0.5 && best.ModalColumns >= 2;
        var delimiter = best.Delimiter;
        if (!found) delimiter = ext is ".tsv" or ".tab" ? '\t' : ext == ".psv" ? '|' : ',';
        if (!found) best = Run(sample, start, delimiter, '"', truncated);

        var header = DetectHeader(text, best, delimiter);
        var style = DetectQuoteStyle(text, best);
        return new CsvDialect
        {
            Delimiter = delimiter,
            Quote = best.Quote,
            HasHeader = header,
            LineEnding = DetectLineEnding(text, best),
            QuoteStyle = style,
            Confidence = found ? Math.Min(1.0, best.Score) : 0,
        };
    }

    private static Scan Better(Scan? current, Scan candidate)
    {
        if (current is null) return candidate;
        // within the epsilon the higher priority wins, outside it the higher score
        if (candidate.Score > current.Score + TieEpsilon) return candidate;
        if (current.Score > candidate.Score + TieEpsilon) return current;
        return Array.IndexOf(Priority, candidate.Delimiter) < Array.IndexOf(Priority, current.Delimiter) ? candidate : current;
    }

    private static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        var dot = fileName.LastIndexOf('.');
        return (dot >= 0 ? fileName[dot..] : "." + fileName).ToLowerInvariant();
    }

    private static double ExtensionBonus(string ext, char d) => (ext, d) switch
    {
        (".tsv" or ".tab", '\t') => 0.04,
        (".psv", '|') => 0.04,
        _ => 0,
    };

    private static Scan Run(ReadOnlySpan<char> sample, int start, char delimiter, char quote, bool truncated)
    {
        var scan = new Scan { Delimiter = delimiter, Quote = quote };
        var pos = start;
        var counts = new Dictionary<int, int>();
        var fields = new CsvField[64];
        var used = 0;
        while (used < SampleRecords && CsvScanner.ReadRecord(sample, pos, delimiter, quote, out var rec))
        {
            pos = rec.Next;
            // a record cut by the end of the sample is incomplete
            if (rec.UnterminatedQuote || (truncated && rec.End == sample.Length && rec.Next == sample.Length)) break;
            scan.Records.Add(new Rec(rec.Start, rec.End, rec.Next));
            if (rec.Start == rec.End) continue; // blank line: no information about the columns
            var n = CsvScanner.SplitFields(sample, rec.Start, rec.End, delimiter, quote, fields);
            if (n > fields.Length) fields = new CsvField[n * 2];
            var written = Math.Min(n, fields.Length);
            for (var i = 0; i < written; i++) if (fields[i].Quoted) scan.QuotedFields++;
            counts[n] = counts.GetValueOrDefault(n) + 1;
            used++;
        }

        if (used == 0) return scan;
        var modal = 0;
        var freq = 0;
        foreach (var (cols, f) in counts)
        {
            if (f > freq || (f == freq && cols > modal))
            {
                modal = cols;
                freq = f;
            }
        }

        scan.ModalColumns = modal;
        if (modal >= 2)
        {
            var consistency = (double)freq / used;
            scan.Score = consistency * (0.8 + 0.2 * Math.Min(1.0, (modal - 1) / 3.0));
            if (used < 2) scan.Score *= 0.9;
        }

        return scan;
    }

    private static string DetectLineEnding(string text, Scan scan)
    {
        int crlf = 0, lf = 0, cr = 0;
        foreach (var r in scan.Records)
        {
            if (r.Next == r.End) continue;
            if (text[r.End] == '\n') lf++;
            else if (r.Next - r.End == 2) crlf++;
            else cr++;
        }

        if (crlf == 0 && lf == 0 && cr == 0) return "\r\n";
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    private static CsvQuoteStyle DetectQuoteStyle(string text, Scan scan)
    {
        long quotedText = 0, quotedNumber = 0, plainText = 0, plainNumber = 0;
        var fields = new CsvField[64];
        foreach (var r in scan.Records)
        {
            if (r.Start == r.End) continue;
            var n = CsvScanner.SplitFields(text, r.Start, r.End, scan.Delimiter, scan.Quote, fields);
            if (n > fields.Length) fields = new CsvField[n * 2];
            for (var i = 0; i < Math.Min(n, fields.Length); i++)
            {
                var f = fields[i];
                var value = CsvScanner.Decode(text, f, scan.Quote);
                if (value.Length == 0) continue;
                var number = CsvValue.TryParseNumber(value, scan.Delimiter == ',' ? CsvNumberStyle.DotDecimal : CsvNumberStyle.Auto, out _);
                if (f.Quoted)
                {
                    if (number) quotedNumber++;
                    else quotedText++;
                }
                else if (number) plainNumber++;
                else plainText++;
            }
        }

        var quoted = quotedText + quotedNumber;
        var plain = plainText + plainNumber;
        if (quoted >= 2 && plain == 0) return CsvQuoteStyle.All;
        if (quotedText >= 2 && plainText == 0 && plainNumber >= 1 && quotedNumber == 0) return CsvQuoteStyle.NonNumeric;
        return CsvQuoteStyle.Minimal;
    }

    // ------------------------------------------------------------------ header

    private static bool DetectHeader(string text, Scan scan, char delimiter)
    {
        var style = delimiter == ',' ? CsvNumberStyle.DotDecimal : CsvNumberStyle.Auto;
        var rows = new List<string[]>();
        var fields = new CsvField[64];
        foreach (var r in scan.Records)
        {
            if (r.Start == r.End) continue;
            var n = CsvScanner.SplitFields(text, r.Start, r.End, delimiter, scan.Quote, fields);
            if (n > fields.Length) fields = new CsvField[n * 2];
            var cells = new string[Math.Min(n, fields.Length)];
            for (var i = 0; i < cells.Length; i++) cells[i] = CsvScanner.Decode(text, fields[i], scan.Quote);
            rows.Add(cells);
            if (rows.Count > HeaderDataRows) break;
        }

        if (rows.Count == 0) return true;
        var cols = rows[0].Length;
        var header = rows[0];

        if (rows.Count == 1)
        {
            // a lone line: names unless something in it is a number / date
            foreach (var h in header)
            {
                var k = CsvValue.Classify(h, style);
                if (k is CsvValueKind.Number or CsvValueKind.Date) return false;
            }

            return true;
        }

        var data = rows.Skip(1).Where(r => r.Length == cols).ToList();
        if (data.Count == 0) data = rows.Skip(1).ToList();

        double evidence = 0;
        for (var c = 0; c < cols; c++)
        {
            int nNum = 0, nDate = 0, nText = 0;
            var textValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in data)
            {
                if (c >= row.Length) continue;
                switch (CsvValue.Classify(row[c], style))
                {
                    case CsvValueKind.Number: nNum++; break;
                    case CsvValueKind.Date: nDate++; break;
                    case CsvValueKind.Text:
                        nText++;
                        textValues.Add(row[c].Trim());
                        break;
                }
            }

            var nonEmpty = nNum + nDate + nText;
            if (nonEmpty == 0) continue;
            var hk = CsvValue.Classify(header[c], style);
            if (nNum >= 0.8 * nonEmpty || nDate >= 0.8 * nonEmpty)
            {
                var dom = nNum >= 0.8 * nonEmpty ? CsvValueKind.Number : CsvValueKind.Date;
                if (hk == CsvValueKind.Text) evidence += 2;
                else if (hk == dom) evidence -= 2;
                else if (hk == CsvValueKind.Empty) evidence += 0.5;
                else evidence -= 1;
            }
            else if (nText >= 0.8 * nonEmpty)
            {
                if (hk is CsvValueKind.Number or CsvValueKind.Date) evidence -= 1.5;
                else if (hk == CsvValueKind.Text && textValues.Contains(header[c].Trim())) evidence -= 1;
            }
        }

        var nonEmptyHeaders = header.Where(h => h.Trim().Length > 0).Select(h => h.Trim()).ToList();
        if (nonEmptyHeaders.Count == header.Length)
            evidence += nonEmptyHeaders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == nonEmptyHeaders.Count ? 1 : -1;
        else if (nonEmptyHeaders.Count > 0) evidence -= 0.5;

        return evidence >= 1;
    }
}
