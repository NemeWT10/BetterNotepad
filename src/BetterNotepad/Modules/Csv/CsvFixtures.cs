using System.Globalization;
using System.IO;
using System.Text;

namespace BetterNotepad.Modules.Csv;

/// <summary>Our own CSV / TSV test files (self-test and snapshots); written under the test data folder only.</summary>
internal static class CsvFixtures
{
    private static readonly string[] Cities = ["Kraków", "Warszawa", "Gdańsk", "Wrocław", "Poznań", "Łódź", "Lublin", "Szczecin"];
    private static readonly string[] Names = ["Anna Nowak", "Jan Kowalski", "Ola Wiśniewska", "Piotr Zieliński", "Kasia Lewandowska", "Marek Wójcik", "Zofia Mazur", "Tomasz Krawczyk"];
    private static readonly string[] Status = ["dostarczone", "w drodze", "anulowane", "zwrot"];

    public static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Orders with a header, semicolons, Polish decimals, dates, quoted notes (one with a doubled quote).</summary>
    public static string Orders(int rows, int seed = 7)
    {
        var rnd = new Random(seed);
        var pl = CultureInfo.GetCultureInfo("pl-PL");
        var sb = new StringBuilder(rows * 80);
        sb.Append("Id;Klient;Miasto;Data;Kwota;Status;Uwagi\r\n");
        for (var i = 1; i <= rows; i++)
        {
            var amount = (rnd.Next(100, 2_000_000) / 100.0).ToString("0.00", pl);
            var date = new DateTime(2025, 1, 1).AddDays(rnd.Next(0, 640)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var status = (i % 9) switch { 0 => Status[2], 4 => Status[3], 1 or 2 or 6 => Status[1], _ => Status[0] };
            var note = (i % 11) switch
            {
                0 => "\"Pilne; zadzwonić przed dostawą\"",
                3 => "\"Prosi o \"\"fakturę\"\" na firmę\"",
                7 => "Paczkomat",
                _ => "",
            };
            sb.Append(i).Append(';').Append(Names[rnd.Next(Names.Length)]).Append(';').Append(Cities[rnd.Next(Cities.Length)]).Append(';')
              .Append(date).Append(';').Append(amount).Append(';').Append(status).Append(';').Append(note).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Comma separated with a header; quoted cells with commas, an embedded line break and a doubled quote.</summary>
    public const string Contacts =
        "name,email,city,score\r\n" +
        "\"Kowalski, Jan\",jan@example.com,Kraków,87\r\n" +
        "Anna Nowak,anna@example.com,Gdańsk,92.5\r\n" +
        "\"Wiśniewska, Ola\",ola@example.com,\"Warszawa\",78\r\n" +
        "Piotr Zieliński,piotr@example.com,Łódź,\"100\"\r\n" +
        "\"Zofia \"\"Zosia\"\" Mazur\",zofia@example.com,Poznań,64\r\n" +
        "Marek Wójcik,marek@example.com,\"Lublin\r\n(centrum)\",71\r\n";

    /// <summary>Tab separated measurements.</summary>
    public const string Sensors =
        "sensor\ttimestamp\ttemp_c\thumidity\tok\n" +
        "A-01\t2026-10-01 08:00\t21.4\t45\ttrue\n" +
        "A-02\t2026-10-01 08:00\t19.8\t51\ttrue\n" +
        "B-07\t2026-10-01 08:05\t23.1\t39\tfalse\n" +
        "B-10\t2026-10-01 08:05\t22.0\t40\ttrue\n" +
        "C-03\t2026-10-01 08:10\t18.6\t58\ttrue\n";

    /// <summary>Pipe separated.</summary>
    public const string Inventory =
        "sku|product|qty|price\r\n" +
        "KB-100|Keyboard|14|249.00\r\n" +
        "MS-210|Mouse|31|89.90\r\n" +
        "HD-001|Headset|7|399.00\r\n" +
        "CB-USB|Cable USB-C|120|19.99\r\n";

    /// <summary>No header: every row is data (numbers in the first row).</summary>
    public const string NoHeader =
        "1,2026-01-05,12.50\r\n" +
        "2,2026-01-06,8.00\r\n" +
        "3,2026-01-09,23.75\r\n" +
        "4,2026-01-12,4.20\r\n";

    /// <summary>Ragged: two rows with a different number of cells.</summary>
    public const string Ragged =
        "kod,nazwa,ilość,cena\r\n" +
        "A1,Śruba M4,200,0.12\r\n" +
        "A2,Nakrętka M4,180\r\n" +
        "A3,Podkładka,500,0.03\r\n" +
        "A4,Wkręt 3x20,120,0.08,promocja\r\n" +
        "A5,Kołek 6,90,0.15\r\n";

    /// <summary>A quote opened in line 4 and never closed.</summary>
    public const string Unclosed =
        "id,opis,wartość\r\n" +
        "1,pierwszy,10\r\n" +
        "2,drugi,20\r\n" +
        "3,\"trzeci bez końca,30\r\n" +
        "4,czwarty,40\r\n";

    /// <summary>A wide table (many columns) for horizontal virtualization.</summary>
    public static string Wide(int columns, int rows)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < columns; c++) sb.Append(c == 0 ? "" : ",").Append("col").Append(c + 1);
        sb.Append("\r\n");
        for (var r = 1; r <= rows; r++)
        {
            for (var c = 0; c < columns; c++) sb.Append(c == 0 ? "" : ",").Append(r * 100 + c);
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Write(string dir, string name, string text)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text, Utf8);
        return path;
    }
}
