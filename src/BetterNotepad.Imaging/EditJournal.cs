using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Imaging;

/// <summary>
/// A captured edit state (<see cref="EditDocument.Capture"/>): undo snapshots (bottom → top), the current list, redo snapshots
/// (bottom → top) and the index of the snapshot the file holds (in that order; null = none of them). Serialized compactly:
/// every distinct edit once, snapshots as lists of indexes into that pool.
/// </summary>
public sealed record EditJournal(IReadOnlyList<List<EditOp>> Undo, List<EditOp> Current, IReadOnlyList<List<EditOp>> Redo, int? Saved)
{
    private const int FormatVersion = 1;

    public string ToJson()
    {
        var pool = new List<EditOp>();
        var index = new Dictionary<EditOp, int>(ReferenceEqualityComparer.Instance);
        int Id(EditOp op)
        {
            if (index.TryGetValue(op, out var i)) return i;
            index[op] = pool.Count;
            pool.Add(op);
            return pool.Count - 1;
        }
        var undo = Undo.Select(s => s.Select(Id).ToArray()).ToList();
        var current = Current.Select(Id).ToArray();
        var redo = Redo.Select(s => s.Select(Id).ToArray()).ToList();

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", FormatVersion);
            w.WriteStartArray("ops");
            foreach (var op in pool) WriteOp(w, op);
            w.WriteEndArray();
            WriteLists(w, "undo", undo);
            w.WritePropertyName("current");
            WriteInts(w, current);
            WriteLists(w, "redo", redo);
            if (Saved is { } s) w.WriteNumber("saved", s);
            else w.WriteNull("saved");
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Reads <see cref="ToJson"/> output; throws <see cref="FormatException"/> (or a JSON exception) on anything else.</summary>
    public static EditJournal FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("v").GetInt32() != FormatVersion) throw new FormatException("unknown edit journal version");
        var pool = root.GetProperty("ops").EnumerateArray().Select(ReadOp).ToList();
        List<EditOp> List(JsonElement e) => e.EnumerateArray().Select(i => pool[i.GetInt32()]).ToList();
        var undo = root.GetProperty("undo").EnumerateArray().Select(List).ToList();
        var current = List(root.GetProperty("current"));
        var redo = root.GetProperty("redo").EnumerateArray().Select(List).ToList();
        int? saved = root.TryGetProperty("saved", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
        return new EditJournal(undo, current, redo, saved);
    }

    private static void WriteLists(Utf8JsonWriter w, string name, List<int[]> lists)
    {
        w.WriteStartArray(name);
        foreach (var l in lists) WriteInts(w, l);
        w.WriteEndArray();
    }

    private static void WriteInts(Utf8JsonWriter w, int[] values)
    {
        w.WriteStartArray();
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static void Num(Utf8JsonWriter w, double v) => w.WriteNumberValue(Math.Round(v, 3));

    private static void WriteRect(Utf8JsonWriter w, Rect r)
    {
        w.WriteStartArray("r");
        Num(w, r.X);
        Num(w, r.Y);
        Num(w, r.Width);
        Num(w, r.Height);
        w.WriteEndArray();
    }

    private static void WriteOp(Utf8JsonWriter w, EditOp op)
    {
        w.WriteStartObject();
        switch (op)
        {
            case CropOp c:
                w.WriteString("t", "crop");
                WriteRect(w, c.Area);
                break;
            case RotateOp r:
                w.WriteString("t", "rotate");
                w.WriteNumber("q", r.Quarters);
                break;
            case FlipOp f:
                w.WriteString("t", "flip");
                w.WriteBoolean("h", f.Horizontal);
                break;
            case ResizeOp z:
                w.WriteString("t", "resize");
                w.WriteNumber("w", z.Width);
                w.WriteNumber("h", z.Height);
                break;
            case RegionOp g:
                w.WriteString("t", "region");
                WriteRect(w, g.Area);
                w.WriteBoolean("px", g.Pixelate);
                break;
            case ShapeOp s:
                var a = s.Annotation;
                w.WriteString("t", "shape");
                w.WriteString("k", a.Kind.ToString());
                w.WriteString("c", $"#{a.Color.A:X2}{a.Color.R:X2}{a.Color.G:X2}{a.Color.B:X2}");
                w.WritePropertyName("s");
                Num(w, a.Size);
                w.WriteStartArray("p");
                foreach (var p in a.Points)
                {
                    Num(w, p.X);
                    Num(w, p.Y);
                }
                w.WriteEndArray();
                if (a.Text is not null) w.WriteString("x", a.Text);
                break;
            default:
                throw new NotSupportedException("edit " + op.GetType().Name);
        }
        w.WriteEndObject();
    }

    private static Rect ReadRect(JsonElement e)
    {
        var r = e.GetProperty("r");
        return new Rect(r[0].GetDouble(), r[1].GetDouble(), Math.Max(0, r[2].GetDouble()), Math.Max(0, r[3].GetDouble()));
    }

    private static EditOp ReadOp(JsonElement e)
    {
        switch (e.GetProperty("t").GetString())
        {
            case "crop":
                return new CropOp(ReadRect(e));
            case "rotate":
                return new RotateOp(e.GetProperty("q").GetInt32());
            case "flip":
                return new FlipOp(e.GetProperty("h").GetBoolean());
            case "resize":
                return new ResizeOp(e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32());
            case "region":
                return new RegionOp(ReadRect(e), e.GetProperty("px").GetBoolean());
            case "shape":
                if (!Enum.TryParse<EditTool>(e.GetProperty("k").GetString(), out var kind)) throw new FormatException("unknown drawing");
                var hex = e.GetProperty("c").GetString() ?? "#FF000000";
                var color = Color.FromArgb(Hex(hex, 1), Hex(hex, 3), Hex(hex, 5), Hex(hex, 7));
                var flat = e.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var points = new Point[flat.Length / 2];
                for (var i = 0; i < points.Length; i++) points[i] = new Point(flat[i * 2], flat[i * 2 + 1]);
                var text = e.TryGetProperty("x", out var x) ? x.GetString() : null;
                return new ShapeOp(new Annotation(kind, color, e.GetProperty("s").GetDouble(), points, text));
            default:
                throw new FormatException("unknown edit");
        }
    }

    private static byte Hex(string s, int at) => s.Length >= at + 2 ? byte.Parse(s.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0xFF;
}
