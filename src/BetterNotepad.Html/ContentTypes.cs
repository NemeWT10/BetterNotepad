using System.IO;

namespace BetterNotepad.Html;

/// <summary>MIME types of the files a local page typically loads.</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8", [".css"] = "text/css",
        [".js"] = "text/javascript", [".mjs"] = "text/javascript", [".json"] = "application/json", [".map"] = "application/json",
        [".xml"] = "application/xml", [".txt"] = "text/plain", [".csv"] = "text/csv", [".md"] = "text/markdown",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".avif"] = "image/avif", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg", [".wasm"] = "application/wasm", [".pdf"] = "application/pdf",
    };

    public static string Of(string path) => Map.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
}
