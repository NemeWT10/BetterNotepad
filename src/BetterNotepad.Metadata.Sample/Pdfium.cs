using System.Runtime.InteropServices;
using System.Text;

namespace BetterNotepad.Metadata.Sample;

/// <summary>Test-only independent PDF parser (PDFium) used to prove the produced files still open.</summary>
internal static unsafe class Pdfium
{
    private const string Lib = "pdfium";
    [DllImport(Lib)] private static extern void FPDF_InitLibrary();
    [DllImport(Lib)] private static extern nint FPDF_LoadMemDocument(byte* data, int size, byte* password);
    [DllImport(Lib)] private static extern int FPDF_GetPageCount(nint doc);
    [DllImport(Lib)] private static extern uint FPDF_GetMetaText(nint doc, byte* tag, void* buffer, uint len);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(nint doc);
    [DllImport(Lib)] private static extern uint FPDF_GetLastError();

    private static bool _init;

    public sealed record Info(int Pages, Dictionary<string, string> Meta);

    public static Info Open(string path) => Open(File.ReadAllBytes(path));

    public static Info Open(byte[] bytes)
    {
        if (!_init) { FPDF_InitLibrary(); _init = true; }
        fixed (byte* p = bytes)
        {
            nint doc = FPDF_LoadMemDocument(p, bytes.Length, null);
            if (doc == 0) throw new InvalidOperationException("PDFium could not open the file (error " + FPDF_GetLastError() + ")");
            try
            {
                var meta = new Dictionary<string, string>();
                foreach (var tag in new[] { "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate" })
                {
                    var t = Encoding.ASCII.GetBytes(tag + "\0");
                    var buf = new byte[4096];
                    uint n;
                    fixed (byte* tp = t) fixed (byte* bp = buf) n = FPDF_GetMetaText(doc, tp, bp, (uint)buf.Length);
                    meta[tag] = n >= 2 ? Encoding.Unicode.GetString(buf, 0, (int)n - 2) : "";
                }
                return new Info(FPDF_GetPageCount(doc), meta);
            }
            finally { FPDF_CloseDocument(doc); }
        }
    }
}
