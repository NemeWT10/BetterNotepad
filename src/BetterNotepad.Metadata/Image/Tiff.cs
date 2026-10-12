using System.Globalization;
using System.Text;

namespace BetterNotepad.Metadata;

internal enum IfdKind { Ifd0, Exif, Gps, Interop, Ifd1 }

internal sealed class TiffIfd
{
    public IfdKind Kind;
    public int Offset;
    public int Count;
}

internal readonly record struct TiffEntry(IfdKind Kind, ushort Tag, ushort Type, uint Count, int EntryPos, int ValuePos, int ValueLen);

/// <summary>Defensive TIFF / EXIF structure parser over a private byte[] (header at offset 0).</summary>
internal sealed class TiffBlock
{
    private static readonly int[] TypeSize = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4 };

    public byte[] Buf = Array.Empty<byte>();
    public bool LE;
    public List<TiffIfd> Ifds = new();
    public List<TiffEntry> Entries = new();

    public static TiffBlock? Parse(byte[] buf)
    {
        if (buf.Length < 8) return null;
        bool le;
        if (buf[0] == 'I' && buf[1] == 'I') le = true;
        else if (buf[0] == 'M' && buf[1] == 'M') le = false;
        else return null;
        if (Bin.U16(buf, 2, le) != 42) return null;
        var t = new TiffBlock { Buf = buf, LE = le };
        t.ParseIfd(IfdKind.Ifd0, Bin.U32(buf, 4, le), new HashSet<long>(), 0);
        return t.Ifds.Count > 0 ? t : null;
    }

    private void ParseIfd(IfdKind kind, long off, HashSet<long> visited, int depth)
    {
        if (off < 8 || off + 2 > Buf.Length || depth > 5 || !visited.Add(off)) return;
        int n = Bin.U16(Buf, (int)off, LE);
        if (off + 2 + (long)n * 12 > Buf.Length) n = (int)((Buf.Length - off - 2) / 12);
        if (n <= 0) return;
        long end = off + 2 + (long)n * 12;
        Ifds.Add(new TiffIfd { Kind = kind, Offset = (int)off, Count = n });
        var next = new List<(IfdKind, long)>();
        for (int i = 0; i < n; i++)
        {
            int pos = (int)off + 2 + i * 12;
            ushort tag = Bin.U16(Buf, pos, LE), type = Bin.U16(Buf, pos + 2, LE);
            uint count = Bin.U32(Buf, pos + 4, LE);
            int size = type < TypeSize.Length ? TypeSize[type] : 0;
            long total = (long)size * count;
            int vpos = -1;
            if (size > 0)
            {
                if (total <= 4) vpos = pos + 8;
                else
                {
                    long vo = Bin.U32(Buf, pos + 8, LE);
                    if (vo >= 8 && vo + total <= Buf.Length) vpos = (int)vo;
                }
            }
            int vlen = vpos >= 0 ? (int)Math.Min(total, int.MaxValue) : 0;
            Entries.Add(new TiffEntry(kind, tag, type, count, pos, vpos, vlen));
            if (kind == IfdKind.Ifd0 && tag == 0x8769) next.Add((IfdKind.Exif, Bin.U32(Buf, pos + 8, LE)));
            else if (kind == IfdKind.Ifd0 && tag == 0x8825) next.Add((IfdKind.Gps, Bin.U32(Buf, pos + 8, LE)));
            else if (kind == IfdKind.Exif && tag == 0xA005) next.Add((IfdKind.Interop, Bin.U32(Buf, pos + 8, LE)));
        }
        if (kind == IfdKind.Ifd0 && end + 4 <= Buf.Length)
        {
            uint nx = Bin.U32(Buf, (int)end, LE);
            if (nx != 0) next.Add((IfdKind.Ifd1, nx));
        }
        foreach (var (k, o) in next) ParseIfd(k, o, visited, depth + 1);
    }

    public ReadOnlySpan<byte> Raw(in TiffEntry e) => e.ValuePos < 0 ? default : new ReadOnlySpan<byte>(Buf, e.ValuePos, e.ValueLen);

    public string ReadAscii(in TiffEntry e)
    {
        var raw = Raw(e);
        int nul = raw.IndexOf((byte)0);
        if (nul >= 0) raw = raw.Slice(0, nul);
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    public (long Num, long Den)[] ReadRationals(in TiffEntry e)
    {
        if (e.ValuePos < 0 || (e.Type != 5 && e.Type != 10)) return Array.Empty<(long, long)>();
        int n = (int)Math.Min(e.Count, 64);
        var r = new (long, long)[n];
        for (int i = 0; i < n; i++)
        {
            int o = e.ValuePos + i * 8;
            if (e.Type == 5) r[i] = (Bin.U32(Buf, o, LE), Bin.U32(Buf, o + 4, LE));
            else r[i] = ((int)Bin.U32(Buf, o, LE), (int)Bin.U32(Buf, o + 4, LE));
        }
        return r;
    }

    public long[] ReadInts(in TiffEntry e)
    {
        if (e.ValuePos < 0) return Array.Empty<long>();
        int n = (int)Math.Min(e.Count, 64);
        var r = new long[n];
        for (int i = 0; i < n; i++)
        {
            switch (e.Type)
            {
                case 1: case 7: r[i] = Buf[e.ValuePos + i]; break;
                case 6: r[i] = (sbyte)Buf[e.ValuePos + i]; break;
                case 3: r[i] = Bin.U16(Buf, e.ValuePos + i * 2, LE); break;
                case 8: r[i] = (short)Bin.U16(Buf, e.ValuePos + i * 2, LE); break;
                case 4: case 13: r[i] = Bin.U32(Buf, e.ValuePos + i * 4, LE); break;
                case 9: r[i] = (int)Bin.U32(Buf, e.ValuePos + i * 4, LE); break;
                default: return Array.Empty<long>();
            }
        }
        return r;
    }

    public double[] ReadFloats(in TiffEntry e)
    {
        if (e.ValuePos < 0 || (e.Type != 11 && e.Type != 12)) return Array.Empty<double>();
        int n = (int)Math.Min(e.Count, 32);
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (e.Type == 11) r[i] = BitConverter.Int32BitsToSingle((int)Bin.U32(Buf, e.ValuePos + i * 4, LE));
            else
            {
                ulong hi = Bin.U32(Buf, e.ValuePos + i * 8 + (LE ? 4 : 0), LE), lo = Bin.U32(Buf, e.ValuePos + i * 8 + (LE ? 0 : 4), LE);
                r[i] = BitConverter.Int64BitsToDouble((long)(hi << 32 | lo));
            }
        }
        return r;
    }

    public int Orientation()
    {
        foreach (var e in Entries)
            if (e.Kind == IfdKind.Ifd0 && e.Tag == 0x0112 && e.Type == 3 && e.ValuePos >= 0)
            {
                int v = Bin.U16(Buf, e.ValuePos, LE);
                return v is >= 1 and <= 8 ? v : 1;
            }
        return 1;
    }

    /// <summary>Minimal big-endian TIFF block holding only the Orientation tag.</summary>
    public static byte[] BuildOrientationOnly(int orientation)
    {
        var b = new byte[8 + 2 + 12 + 4];
        b[0] = (byte)'M'; b[1] = (byte)'M'; b[3] = 42; b[7] = 8;
        b[9] = 1;                              // one entry
        b[10] = 0x01; b[11] = 0x12;            // tag 0x0112
        b[13] = 3;                             // SHORT
        b[17] = 1;                             // count 1
        b[19] = (byte)orientation;             // value (left-justified big-endian short)
        return b;
    }
}

/// <summary>In-place, structure-preserving EXIF edits (no re-serialisation: MakerNote offsets stay valid).</summary>
internal static class TiffEditor
{
    /// <summary>Rewrites the value of an ASCII / BYTE entry. Fits in place, otherwise the new value is appended at the end of the block and the old one is zeroed.</summary>
    public static byte[] SetEntryBytes(byte[] buf, in TiffEntry e, byte[] data)
    {
        bool le = buf[0] == 'I';
        int cap = e.ValueLen;
        if (data.Length <= cap)
        {
            Array.Clear(buf, e.ValuePos, cap);
            Buffer.BlockCopy(data, 0, buf, e.ValuePos, data.Length);
            return buf;
        }
        int newPos = buf.Length + (buf.Length & 1);
        var nb = new byte[newPos + data.Length];
        Buffer.BlockCopy(buf, 0, nb, 0, buf.Length);
        Buffer.BlockCopy(data, 0, nb, newPos, data.Length);
        if (e.ValuePos != e.EntryPos + 8) Array.Clear(nb, e.ValuePos, cap);   // old out-of-line value: do not leave it behind
        else Array.Clear(nb, e.EntryPos + 8, 4);
        Bin.PutU32(nb, e.EntryPos + 4, (uint)data.Length, le);
        Bin.PutU32(nb, e.EntryPos + 8, (uint)newPos, le);
        return nb;
    }

    /// <summary>Removes the GPS IFD (pointer in IFD0 + the IFD and its values are zeroed). Returns false if there was none.</summary>
    public static bool RemoveGps(byte[] buf)
    {
        var t = TiffBlock.Parse(buf);
        if (t == null) return false;
        int idx0 = -1;
        for (int i = 0; i < t.Entries.Count; i++) if (t.Entries[i].Kind == IfdKind.Ifd0 && t.Entries[i].Tag == 0x8825) { idx0 = i; break; }
        if (idx0 < 0) return false;
        var ptr = t.Entries[idx0];

        var gps = t.Ifds.FirstOrDefault(i => i.Kind == IfdKind.Gps);
        if (gps != null)
        {
            foreach (var e in t.Entries)
                if (e.Kind == IfdKind.Gps && e.ValuePos >= 0 && e.ValueLen > 4) Array.Clear(buf, e.ValuePos, e.ValueLen);
            int len = Math.Min(2 + gps.Count * 12 + 4, buf.Length - gps.Offset);
            Array.Clear(buf, gps.Offset, len);
        }

        var ifd0 = t.Ifds[0];
        int entriesStart = ifd0.Offset + 2;
        int idx = (ptr.EntryPos - entriesStart) / 12;
        int tail = ifd0.Count - idx - 1;
        int copyLen = Math.Min(tail * 12 + 4, buf.Length - (ptr.EntryPos + 12));
        if (copyLen > 0) Buffer.BlockCopy(buf, ptr.EntryPos + 12, buf, ptr.EntryPos, copyLen);
        int clearAt = entriesStart + (ifd0.Count - 1) * 12 + 4;
        if (clearAt + 12 <= buf.Length) Array.Clear(buf, clearAt, 12);
        Bin.PutU16(buf, ifd0.Offset, (ushort)(ifd0.Count - 1), t.LE);
        return true;
    }
}

internal static class ExifNames
{
    private static readonly Dictionary<ushort, string> Main = Load(
        "0100:ImageWidth,0101:ImageLength,0102:BitsPerSample,0103:Compression,0106:PhotometricInterpretation," +
        "010E:ImageDescription,010F:Make,0110:Model,0112:Orientation,0115:SamplesPerPixel,011A:XResolution,011B:YResolution," +
        "011C:PlanarConfiguration,0128:ResolutionUnit,0131:Software,0132:DateTime,013B:Artist,013C:HostComputer,013E:WhitePoint," +
        "013F:PrimaryChromaticities,0211:YCbCrCoefficients,0212:YCbCrSubSampling,0213:YCbCrPositioning,0214:ReferenceBlackWhite," +
        "8298:Copyright,829A:ExposureTime,829D:FNumber,8822:ExposureProgram,8824:SpectralSensitivity,8827:ISO," +
        "8828:OECF,8830:SensitivityType,8831:StandardOutputSensitivity,8832:RecommendedExposureIndex,8833:ISOSpeed," +
        "9000:ExifVersion,9003:DateTimeOriginal,9004:DateTimeDigitized,9010:OffsetTime,9011:OffsetTimeOriginal,9012:OffsetTimeDigitized," +
        "9101:ComponentsConfiguration,9102:CompressedBitsPerPixel,9201:ShutterSpeedValue,9202:ApertureValue,9203:BrightnessValue," +
        "9204:ExposureBiasValue,9205:MaxApertureValue,9206:SubjectDistance,9207:MeteringMode,9208:LightSource,9209:Flash," +
        "920A:FocalLength,9214:SubjectArea,927C:MakerNote,9286:UserComment,9290:SubSecTime,9291:SubSecTimeOriginal,9292:SubSecTimeDigitized," +
        "9C9B:XPTitle,9C9C:XPComment,9C9D:XPAuthor,9C9E:XPKeywords,9C9F:XPSubject," +
        "A000:FlashpixVersion,A001:ColorSpace,A002:PixelXDimension,A003:PixelYDimension,A20E:FocalPlaneXResolution," +
        "A20F:FocalPlaneYResolution,A210:FocalPlaneResolutionUnit,A214:SubjectLocation,A215:ExposureIndex,A217:SensingMethod," +
        "A300:FileSource,A301:SceneType,A302:CFAPattern,A401:CustomRendered,A402:ExposureMode,A403:WhiteBalance,A404:DigitalZoomRatio," +
        "A405:FocalLengthIn35mmFilm,A406:SceneCaptureType,A407:GainControl,A408:Contrast,A409:Saturation,A40A:Sharpness," +
        "A40B:DeviceSettingDescription,A40C:SubjectDistanceRange,A420:ImageUniqueID,A430:CameraOwnerName,A431:BodySerialNumber," +
        "A432:LensSpecification,A433:LensMake,A434:LensModel,A435:LensSerialNumber,A460:CompositeImage,A500:Gamma,C4A5:PrintImageMatching");

    private static readonly Dictionary<ushort, string> Gps = Load(
        "0000:VersionID,0001:LatitudeRef,0002:Latitude,0003:LongitudeRef,0004:Longitude,0005:AltitudeRef,0006:Altitude,0007:TimeStamp," +
        "0008:Satellites,0009:Status,000A:MeasureMode,000B:DOP,000C:SpeedRef,000D:Speed,000E:TrackRef,000F:Track,0010:ImgDirectionRef," +
        "0011:ImgDirection,0012:MapDatum,0013:DestLatitudeRef,0014:DestLatitude,0015:DestLongitudeRef,0016:DestLongitude,0017:DestBearingRef," +
        "0018:DestBearing,0019:DestDistanceRef,001A:DestDistance,001B:ProcessingMethod,001C:AreaInformation,001D:DateStamp,001E:Differential," +
        "001F:HPositioningError");

    private static Dictionary<ushort, string> Load(string s)
    {
        var d = new Dictionary<ushort, string>();
        foreach (var p in s.Split(','))
        {
            int c = p.IndexOf(':');
            d[ushort.Parse(p.AsSpan(0, c), NumberStyles.HexNumber)] = p.Substring(c + 1);
        }
        return d;
    }

    public static string? Name(IfdKind kind, ushort tag) =>
        kind == IfdKind.Gps ? (Gps.TryGetValue(tag, out var g) ? g : null)
        : kind == IfdKind.Interop ? (tag == 0x0001 ? "InteropIndex" : tag == 0x0002 ? "InteropVersion" : null)
        : (Main.TryGetValue(tag, out var m) ? m : null);

    /// <summary>Tags rendered without a value row (structure pointers).</summary>
    public static bool IsPointer(ushort tag, IfdKind kind) => kind != IfdKind.Gps && (tag == 0x8769 || tag == 0x8825 || tag == 0xA005);

    /// <summary>ASCII (or UTF-16 XP*) tags that can be rewritten in place.</summary>
    public static readonly HashSet<string> EditableText = new()
    {
        "ImageDescription", "Make", "Model", "Software", "DateTime", "Artist", "Copyright", "HostComputer",
        "DateTimeOriginal", "DateTimeDigitized", "CameraOwnerName", "BodySerialNumber", "LensMake", "LensModel", "LensSerialNumber",
        "ImageUniqueID", "XPTitle", "XPComment", "XPAuthor", "XPKeywords", "XPSubject",
    };

    public static readonly HashSet<string> DateTags = new() { "DateTime", "DateTimeOriginal", "DateTimeDigitized" };

    public static readonly HashSet<string> SensitiveNames = new()
    {
        "Software", "Artist", "Copyright", "ImageDescription", "HostComputer", "CameraOwnerName", "BodySerialNumber", "LensSerialNumber",
        "ImageUniqueID", "UserComment", "MakerNote", "XPTitle", "XPComment", "XPAuthor", "XPKeywords", "XPSubject", "Thumbnail",
    };
}

internal static class ExifReader
{
    public static void Build(MetadataReport report, TiffBlock t, bool editable)
    {
        var exif = report.Group(MetadataGroupId.Exif);
        var seen = new HashSet<string>();

        // GPS composites first.
        var gps = t.Entries.Where(e => e.Kind == IfdKind.Gps).ToList();
        if (gps.Count > 0) BuildGps(report.Group(MetadataGroupId.Gps), t, gps);

        foreach (var e in t.Entries)
        {
            if (e.Kind == IfdKind.Gps) continue;
            if (ExifNames.IsPointer(e.Tag, e.Kind)) continue;
            if (e.Kind == IfdKind.Ifd1)
            {
                if (e.Tag == 0x0201 && e.ValuePos >= 0 && !seen.Contains("exif.Thumbnail"))
                {
                    var len = t.Entries.FirstOrDefault(x => x.Kind == IfdKind.Ifd1 && x.Tag == 0x0202);
                    var lv = len.ValuePos >= 0 ? t.ReadInts(len) : Array.Empty<long>();
                    seen.Add("exif.Thumbnail");
                    exif.Entries.Add(new MetadataEntry("exif.Thumbnail", "meta.exif.Thumbnail", lv.Length > 0 ? lv[0].ToString(CultureInfo.InvariantCulture) : "?", MetadataValueKind.Number, false, true, "Thumbnail"));
                }
                continue;
            }
            string? name = ExifNames.Name(e.Kind, e.Tag);
            string raw = name ?? "0x" + e.Tag.ToString("X4");
            string key = "exif." + (e.Kind == IfdKind.Interop ? "Interop." : "") + raw;
            if (!seen.Add(key)) continue;
            if (e.ValuePos < 0) continue;

            var (val, kind) = Format(t, e, name);
            bool ed = editable && name != null && e.Kind is IfdKind.Ifd0 or IfdKind.Exif
                      && ExifNames.EditableText.Contains(name)
                      && ((e.Type == 2) || (name.StartsWith("XP", StringComparison.Ordinal) && e.Type == 1));
            bool sens = name != null && ExifNames.SensitiveNames.Contains(name);
            exif.Entries.Add(new MetadataEntry(key, "meta." + key, Txt.Clip(val), ed && ExifNames.DateTags.Contains(name!) ? MetadataValueKind.DateTime : kind, ed, sens, raw));
        }
        if (exif.Entries.Count == 0) report.Groups.Remove(exif);
    }

    private static (string, MetadataValueKind) Format(TiffBlock t, in TiffEntry e, string? name)
    {
        switch (e.Type)
        {
            case 2:
            {
                string s = t.ReadAscii(e);
                if (name != null && ExifNames.DateTags.Contains(name) && DateTime.TryParseExact(s.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return (Txt.Iso(dt), MetadataValueKind.DateTime);
                return (s, MetadataValueKind.Text);
            }
            case 5:
            case 10:
            {
                var r = t.ReadRationals(e);
                if (name == "ExposureTime" && r.Length == 1 && r[0].Den != 0)
                {
                    double v = (double)r[0].Num / r[0].Den;
                    if (v > 0 && v < 1) return ("1/" + Math.Round(1 / v).ToString(CultureInfo.InvariantCulture), MetadataValueKind.Text);
                }
                return (string.Join(", ", r.Take(16).Select(x => x.Den == 0 ? "0" : Txt.Num((double)x.Num / x.Den))), MetadataValueKind.Number);
            }
            case 3: case 4: case 8: case 9: case 13:
            {
                var v = t.ReadInts(e);
                var s = string.Join(", ", v.Take(16).Select(x => x.ToString(CultureInfo.InvariantCulture)));
                if (e.Count > 16) s += ", …";
                return (s, v.Length == 1 ? MetadataValueKind.Number : MetadataValueKind.Text);
            }
            case 11: case 12:
                return (string.Join(", ", t.ReadFloats(e).Take(16).Select(Txt.Num)), MetadataValueKind.Number);
            default:
                break;
        }
        // BYTE / UNDEFINED
        var raw = t.Raw(e);
        if (name != null && name.StartsWith("XP", StringComparison.Ordinal))
            return (Encoding.Unicode.GetString(raw).TrimEnd('\0'), MetadataValueKind.Text);
        if (name == "UserComment") return (DecodeUserComment(raw), MetadataValueKind.Text);
        if (name is "ExifVersion" or "FlashpixVersion" or "InteropVersion" && raw.Length == 4 && raw.ToArray().All(b => b >= 0x20 && b < 0x7F))
            return (Encoding.ASCII.GetString(raw), MetadataValueKind.Text);
        if (name == "MakerNote") return ($"({raw.Length} B)", MetadataValueKind.Text);
        if (raw.Length <= 16) return (string.Join(" ", raw.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))), MetadataValueKind.Text);
        return ($"({raw.Length} B)", MetadataValueKind.Text);
    }

    private static string DecodeUserComment(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) return "";
        var id = Encoding.ASCII.GetString(raw.Slice(0, 8)).TrimEnd('\0', ' ');
        var body = raw.Slice(8);
        if (id == "UNICODE")
        {
            // Byte order is not recorded explicitly; most writers use the TIFF order, but BOMs / zero bytes tell us.
            bool be = body.Length >= 2 && body[0] == 0 && body[1] != 0;
            return (be ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(body).TrimEnd('\0');
        }
        try { return new UTF8Encoding(false, true).GetString(body).TrimEnd('\0', ' '); }
        catch { return Encoding.Latin1.GetString(body).TrimEnd('\0', ' '); }
    }

    private static void BuildGps(MetadataGroup group, TiffBlock t, List<TiffEntry> gps)
    {
        TiffEntry? Get(ushort tag) { foreach (var e in gps) if (e.Tag == tag) return e; return null; }
        string Ref(ushort tag) { var e = Get(tag); return e is { Type: 2 } ? t.ReadAscii(e.Value).Trim() : ""; }

        double? Coord(ushort tag, ushort refTag, string neg)
        {
            var e = Get(tag);
            if (e == null || e.Value.ValuePos < 0) return null;
            var r = t.ReadRationals(e.Value);
            if (r.Length < 1) return null;
            double D(int i) => i < r.Length && r[i].Den != 0 ? (double)r[i].Num / r[i].Den : 0;
            double v = D(0) + D(1) / 60.0 + D(2) / 3600.0;
            return Ref(refTag).StartsWith(neg, StringComparison.OrdinalIgnoreCase) ? -v : v;
        }

        void Add(string name, string value, MetadataValueKind kind = MetadataValueKind.Text) =>
            group.Entries.Add(new MetadataEntry("gps." + name, "meta.gps." + name, value, kind, false, true, name));

        var lat = Coord(0x0002, 0x0001, "S");
        var lon = Coord(0x0004, 0x0003, "W");
        if (lat != null) Add("Latitude", lat.Value.ToString("0.000000", CultureInfo.InvariantCulture), MetadataValueKind.Number);
        if (lon != null) Add("Longitude", lon.Value.ToString("0.000000", CultureInfo.InvariantCulture), MetadataValueKind.Number);

        var alt = Get(0x0006);
        if (alt != null && alt.Value.ValuePos >= 0)
        {
            var r = t.ReadRationals(alt.Value);
            if (r.Length > 0 && r[0].Den != 0)
            {
                double a = (double)r[0].Num / r[0].Den;
                var ar = Get(0x0005);
                if (ar != null && ar.Value.ValuePos >= 0 && t.ReadInts(ar.Value) is { Length: > 0 } ai && ai[0] == 1) a = -a;
                Add("Altitude", Txt.Num(a), MetadataValueKind.Number);
            }
        }

        var ts = Get(0x0007);
        if (ts != null && ts.Value.ValuePos >= 0)
        {
            var r = t.ReadRationals(ts.Value);
            if (r.Length >= 3 && r.All(x => x.Den != 0))
                Add("TimeStamp", $"{(int)((double)r[0].Num / r[0].Den):00}:{(int)((double)r[1].Num / r[1].Den):00}:{(int)((double)r[2].Num / r[2].Den):00} UTC");
        }
        var ds = Get(0x001D);
        if (ds is { Type: 2 }) Add("DateStamp", t.ReadAscii(ds.Value).Replace(':', '-'));

        foreach (var e in gps)
        {
            if (e.Tag is 0x0001 or 0x0002 or 0x0003 or 0x0004 or 0x0005 or 0x0006 or 0x0007 or 0x001D or 0x0000) continue;
            if (e.ValuePos < 0) continue;
            string? nm = ExifNames.Name(IfdKind.Gps, e.Tag);
            if (nm == null) continue;
            var (v, kind) = Format(t, e, nm);
            Add(nm, Txt.Clip(v), kind);
        }
    }
}
