using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BetterNotepad.Pdf;

// Minimal P/Invoke surface of PDFium (pdfium.dll from bblanchon.PDFium.Win32). Windows LLP64: C "unsigned long" is 32 bit.
// Every call must be made on the engine thread (PDFium is not thread-safe) - see PdfEngine.
internal static unsafe partial class Pdfium
{
    private const string Lib = "pdfium";

    public const int BitmapBgra = 4;            // FPDFBitmap_BGRA
    public const int RenderAnnot = 0x01;        // FPDF_ANNOT
    public const int RenderLcdText = 0x02;      // FPDF_LCD_TEXT
    public const int RenderGrayscale = 0x08;    // FPDF_GRAYSCALE
    public const int RenderLimitedImageCache = 0x200;
    public const int RenderPrinting = 0x800;    // FPDF_PRINTING
    public const uint MatchCase = 0x1;          // FPDF_MATCHCASE
    public const uint MatchWholeWord = 0x2;     // FPDF_MATCHWHOLEWORD
    public const int PageObjImage = 3;          // FPDF_PAGEOBJ_IMAGE

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int Version;
        public byte** UserFontPaths;
        public void* Isolate;
        public uint V8EmbedderSlot;
        public void* Platform;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public uint FileLen;
        public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, int> GetBlock;
        public void* Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF { public float Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix { public float A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageMetadata
    {
        public uint Width, Height;
        public float HorizontalDpi, VerticalDpi;
        public uint BitsPerPixel;
        public int Colorspace;
        public int MarkedContentId;
    }

    // ---- library / document ----
    [LibraryImport(Lib)] public static partial void FPDF_InitLibraryWithConfig(LibraryConfig* config);
    [LibraryImport(Lib)] public static partial void FPDF_DestroyLibrary();
    [LibraryImport(Lib)] public static partial nint FPDF_LoadCustomDocument(FileAccess* access, byte* password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, SizeF* size);
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(nint document, int* version);
    [LibraryImport(Lib)] public static partial uint FPDF_GetMetaText(nint document, byte* tag, void* buffer, uint bufLen);

    // ---- pages / rendering ----
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int pageIndex);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void* FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetWidth(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetHeight(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    // ---- text ----
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial double FPDFText_GetFontSize(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetFontWeight(nint textPage, int index);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetFontInfo(nint textPage, int index, void* buffer, uint bufLen, int* flags);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial nint FPDFText_FindStart(nint textPage, char* findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);

    // ---- page objects (embedded images) ----
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(nint pageObject);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(nint pageObject, Matrix* matrix);
    [LibraryImport(Lib)] public static partial nint FPDFImageObj_GetRenderedBitmap(nint document, nint page, nint imageObject);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImageFilterCount(nint imageObject);
    [LibraryImport(Lib)] public static partial uint FPDFImageObj_GetImageFilter(nint imageObject, int index, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDFImageObj_GetImageDataRaw(nint imageObject, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImageMetadata(nint imageObject, nint page, ImageMetadata* metadata);

    // Optional override of the native library location (set before first use). By default the .NET host finds
    // pdfium.dll next to the exe or under runtimes\win-x64\native.
    public static string? NativeLibraryPath { get; set; }

    [ModuleInitializer]
    internal static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, static (name, asm, path) =>
        {
            if (!string.Equals(name, Lib, StringComparison.OrdinalIgnoreCase)) return 0;
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            string[] candidates =
            [
                NativeLibraryPath ?? "",
                Path.Combine(AppContext.BaseDirectory, "pdfium.dll"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "pdfium.dll"),
            ];
            foreach (var c in candidates)
                if (c.Length > 0 && File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
            return 0; // default probing
        });
    }
}
