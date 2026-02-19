using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TNDexplorer.Native;

namespace TNDexplorer.Core
{
    /// <summary>
    /// Extracts text content from files using Windows IFilter COM interface.
    /// Supports Office documents, PDF, HTML, plain text, etc.
    /// Must be called from an STA thread.
    /// </summary>
    public class TextExtractor
    {
        // Binary extensions that should be skipped (no text content)
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // Executables / Libraries
            ".exe", ".dll", ".sys", ".ocx", ".com", ".scr",
            // Archives
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso",
            // Images
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff", ".tif",
            ".svg", ".webp", ".psd", ".raw", ".cr2", ".nef",
            // Audio / Video
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma",
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            // Databases
            ".mdb", ".accdb", ".sqlite", ".db",
            // Fonts
            ".ttf", ".otf", ".woff", ".woff2",
            // Other binary
            ".obj", ".lib", ".pdb", ".dat", ".bin",
        };

        /// <summary>
        /// Default maximum text length to extract (in characters).
        /// 1MB of text ≈ 1 million characters.
        /// </summary>
        public int MaxTextLength { get; set; } = 1024 * 1024;

        /// <summary>
        /// Maximum file size in bytes to attempt extraction.
        /// Files larger than this are skipped.
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;

        private readonly bool _iFilterAvailable;

        public TextExtractor()
        {
            _iFilterAvailable = IFilterNativeMethods.IsIFilterAvailable();
        }

        /// <summary>
        /// Returns true if IFilter system is available on this machine.
        /// </summary>
        public bool IsAvailable => _iFilterAvailable;

        /// <summary>
        /// Extract text content from a file.
        /// Returns null if extraction fails or the file type is not supported.
        /// </summary>
        public string ExtractText(string filePath)
        {
            return ExtractText(filePath, MaxTextLength);
        }

        /// <summary>
        /// Extract text content from a file with a custom max length.
        /// Returns null if extraction fails or the file type is not supported.
        /// </summary>
        public string ExtractText(string filePath, int maxTextLength)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            // Check binary extension
            string ext = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(ext) && BinaryExtensions.Contains(ext))
                return null;

            // Check file exists and size
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length == 0)
                    return null;
                if (MaxFileSizeBytes > 0 && fi.Length > MaxFileSizeBytes)
                    return null;
            }
            catch
            {
                return null;
            }

            // Try plain text extraction first for known text extensions
            if (IsPlainTextExtension(ext))
            {
                return ExtractPlainText(filePath, maxTextLength);
            }

            // For Office OpenXML formats, try direct ZIP extraction first (most reliable)
            if (IsOpenXmlExtension(ext))
            {
                string text = ExtractFromOpenXml(filePath, ext, maxTextLength);
                if (text != null)
                    return text;
            }

            // For PDF, try our built-in parser first (handles FlateDecode + ToUnicode)
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                string text = ExtractFromPdf(filePath, maxTextLength);
                if (text != null)
                    return text;
            }

            // Fallback: try IFilter extraction (COM-based, for other formats)
            if (_iFilterAvailable)
            {
                string text = ExtractViaIFilter(filePath, maxTextLength);
                if (text != null)
                    return text;
            }

            return null;
        }

        /// <summary>
        /// Check if a file extension is a known text-based format.
        /// </summary>
        public static bool IsPlainTextExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;

            switch (ext.ToLowerInvariant())
            {
                case ".txt":
                case ".csv":
                case ".tsv":
                case ".log":
                case ".md":
                case ".json":
                case ".xml":
                case ".yaml":
                case ".yml":
                case ".ini":
                case ".cfg":
                case ".conf":
                case ".properties":
                case ".bat":
                case ".cmd":
                case ".ps1":
                case ".sh":
                case ".py":
                case ".rb":
                case ".pl":
                case ".cs":
                case ".java":
                case ".js":
                case ".ts":
                case ".cpp":
                case ".c":
                case ".h":
                case ".hpp":
                case ".sql":
                case ".vb":
                case ".vbs":
                case ".css":
                case ".scss":
                case ".less":
                case ".htm":
                case ".html":
                case ".xhtml":
                case ".rtf":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Read plain text file content directly (for known text formats).
        /// Auto-detects encoding (BOM → UTF-8 → Shift-JIS fallback).
        /// </summary>
        private string ExtractPlainText(string filePath, int maxTextLength)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Detect encoding from BOM or heuristics
                    var encoding = DetectEncoding(fs);
                    fs.Position = 0;

                    using (var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true))
                    {
                        char[] buffer = new char[Math.Min(maxTextLength, 64 * 1024)];
                        var sb = new StringBuilder();

                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sb.Append(buffer, 0, read);
                            if (sb.Length >= maxTextLength)
                            {
                                sb.Length = maxTextLength;
                                break;
                            }
                        }

                        return sb.Length > 0 ? sb.ToString() : null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Detect file encoding by checking BOM and content heuristics.
        /// Falls back to Shift-JIS (932) for Japanese Windows environments.
        /// </summary>
        private static Encoding DetectEncoding(FileStream fs)
        {
            // Read first bytes for BOM detection
            byte[] bom = new byte[4];
            int bytesRead = fs.Read(bom, 0, 4);

            if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return Encoding.UTF8;
            if (bytesRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode; // UTF-16 LE
            if (bytesRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode; // UTF-16 BE
            if (bytesRead >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
                return Encoding.GetEncoding(12001); // UTF-32 BE

            // No BOM: heuristic detection
            // Read up to 8KB for analysis
            fs.Position = 0;
            int sampleSize = (int)Math.Min(fs.Length, 8192);
            byte[] sample = new byte[sampleSize];
            fs.Read(sample, 0, sampleSize);

            // Check if content is valid UTF-8
            if (IsValidUtf8(sample, sampleSize))
                return new UTF8Encoding(false); // UTF-8 without BOM

            // Fallback to system default (Shift-JIS on Japanese Windows)
            return Encoding.Default;
        }

        /// <summary>
        /// Checks if byte sequence is valid UTF-8.
        /// Returns true if the content contains multi-byte UTF-8 sequences
        /// (indicating non-ASCII content that was intentionally encoded as UTF-8),
        /// or if all bytes are ASCII (compatible with both UTF-8 and Shift-JIS).
        /// </summary>
        private static bool IsValidUtf8(byte[] data, int length)
        {
            int i = 0;

            while (i < length)
            {
                byte b = data[i];

                if (b <= 0x7F)
                {
                    // ASCII
                    i++;
                    continue;
                }

                int expectedBytes;
                if ((b & 0xE0) == 0xC0) expectedBytes = 2;
                else if ((b & 0xF0) == 0xE0) expectedBytes = 3;
                else if ((b & 0xF8) == 0xF0) expectedBytes = 4;
                else return false; // Invalid UTF-8 lead byte

                if (i + expectedBytes > length) return false;

                for (int j = 1; j < expectedBytes; j++)
                {
                    if ((data[i + j] & 0xC0) != 0x80) return false;
                }

                i += expectedBytes;
            }

            // Only report as UTF-8 if we found multi-byte sequences
            // (pure ASCII is ambiguous, default to UTF-8 as it's most common now)
            return true;
        }

        /// <summary>
        /// Check if extension is an Office OpenXML format (ZIP-based).
        /// </summary>
        private static bool IsOpenXmlExtension(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".docx": case ".xlsx": case ".pptx":
                case ".docm": case ".xlsm": case ".pptm":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Extract text from Office OpenXML files by reading XML entries from the ZIP archive.
        /// Works without Office/IFilter installed.
        /// </summary>
        private string ExtractFromOpenXml(string filePath, string ext, int maxTextLength)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(filePath))
                {
                    var sb = new StringBuilder();
                    string lowerExt = ext.ToLowerInvariant();

                    if (lowerExt == ".docx" || lowerExt == ".docm")
                    {
                        // Word: read word/document.xml
                        ExtractTextFromZipEntry(zip, "word/document.xml", sb, maxTextLength);
                    }
                    else if (lowerExt == ".xlsx" || lowerExt == ".xlsm")
                    {
                        // Excel: read xl/sharedStrings.xml (contains all cell text)
                        ExtractTextFromZipEntry(zip, "xl/sharedStrings.xml", sb, maxTextLength);
                        // Also try worksheet data
                        if (sb.Length < maxTextLength)
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                    && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                {
                                    ExtractTextFromZipEntry(zip, entry.FullName, sb, maxTextLength);
                                    if (sb.Length >= maxTextLength) break;
                                }
                            }
                        }
                    }
                    else if (lowerExt == ".pptx" || lowerExt == ".pptm")
                    {
                        // PowerPoint: read ppt/slides/slide*.xml
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                            {
                                ExtractTextFromZipEntry(zip, entry.FullName, sb, maxTextLength);
                                if (sb.Length >= maxTextLength) break;
                            }
                        }
                        // Also read from notes
                        if (sb.Length < maxTextLength)
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.StartsWith("ppt/notesSlides/", StringComparison.OrdinalIgnoreCase)
                                    && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                {
                                    ExtractTextFromZipEntry(zip, entry.FullName, sb, maxTextLength);
                                    if (sb.Length >= maxTextLength) break;
                                }
                            }
                        }
                    }

                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read a ZIP entry and extract text content by stripping XML tags.
        /// </summary>
        private static void ExtractTextFromZipEntry(ZipArchive zip, string entryName, StringBuilder sb, int maxTextLength)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null)
            {
                // Try case-insensitive match
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = e;
                        break;
                    }
                }
            }
            if (entry == null) return;

            try
            {
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string xml = reader.ReadToEnd();
                    // Strip XML tags and extract text content
                    string text = StripXmlTags(xml);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        int remaining = maxTextLength - sb.Length;
                        if (text.Length > remaining)
                            sb.Append(text, 0, remaining);
                        else
                            sb.Append(text);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Strip XML tags and return text content.
        /// Preserves spaces between elements.
        /// </summary>
        private static string StripXmlTags(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;

            // Remove XML tags, keeping text content
            var sb = new StringBuilder(xml.Length / 3);
            bool inTag = false;
            bool lastWasSpace = true;

            for (int i = 0; i < xml.Length; i++)
            {
                char c = xml[i];
                if (c == '<')
                {
                    inTag = true;
                    // Add space between elements if not already present
                    if (sb.Length > 0 && !lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else if (c == '>')
                {
                    inTag = false;
                }
                else if (!inTag)
                {
                    if (c == '&')
                    {
                        // Handle XML entities
                        int semicolonIdx = xml.IndexOf(';', i + 1);
                        if (semicolonIdx > i && semicolonIdx - i < 10)
                        {
                            string entity = xml.Substring(i + 1, semicolonIdx - i - 1);
                            switch (entity)
                            {
                                case "amp": sb.Append('&'); break;
                                case "lt": sb.Append('<'); break;
                                case "gt": sb.Append('>'); break;
                                case "quot": sb.Append('"'); break;
                                case "apos": sb.Append('\''); break;
                                default:
                                    if (entity.StartsWith("#"))
                                    {
                                        try
                                        {
                                            int code = entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                                                ? Convert.ToInt32(entity.Substring(2), 16)
                                                : int.Parse(entity.Substring(1));
                                            sb.Append((char)code);
                                        }
                                        catch { sb.Append(' '); }
                                    }
                                    else { sb.Append(' '); }
                                    break;
                            }
                            i = semicolonIdx;
                            lastWasSpace = false;
                        }
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        if (!lastWasSpace)
                        {
                            sb.Append(' ');
                            lastWasSpace = true;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        lastWasSpace = false;
                    }
                }
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        /// <summary>
        /// Extract text from PDF files by decompressing FlateDecode streams,
        /// parsing ToUnicode CMap tables for CID-to-Unicode mapping,
        /// and extracting text from BT...ET blocks.
        /// </summary>
        private string ExtractFromPdf(string filePath, int maxTextLength)
        {
            try
            {
                byte[] data;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    data = new byte[fs.Length];
                    fs.Read(data, 0, data.Length);
                }

                string raw = Encoding.GetEncoding(1252).GetString(data);

                // Step 1: Find and decompress all FlateDecode streams
                var decompressedStreams = DecompressPdfStreams(data, raw);

                // Step 2: Parse ToUnicode CMap tables to build CID→Unicode mappings
                var cmap = BuildToUnicodeCMap(raw, data, decompressedStreams);

                // Step 3: Extract text from BT...ET blocks in all decompressed streams
                var sb = new StringBuilder();
                foreach (var stream in decompressedStreams)
                {
                    if (sb.Length >= maxTextLength) break;
                    ExtractTextFromPdfStream(stream, cmap, sb, maxTextLength);
                }

                // Also try the raw data for uncompressed streams
                ExtractTextFromPdfStream(raw, cmap, sb, maxTextLength);

                if (sb.Length == 0) return null;

                // Clean up the result
                string result = sb.ToString();
                result = Regex.Replace(result, @"\s{3,}", "  ");
                return result.Trim().Length > 0 ? result.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Decompress all FlateDecode streams in a PDF.
        /// </summary>
        private static List<string> DecompressPdfStreams(byte[] data, string raw)
        {
            var streams = new List<string>();
            int pos = 0;
            while (pos < raw.Length)
            {
                int streamStart = raw.IndexOf("stream", pos, StringComparison.Ordinal);
                if (streamStart < 0) break;

                // Verify this looks like a real stream (check for preceding >>)
                int checkStart = Math.Max(0, streamStart - 200);
                string before = raw.Substring(checkStart, streamStart - checkStart);
                if (!before.Contains(">>"))
                {
                    pos = streamStart + 6;
                    continue;
                }

                int dataStart = streamStart + 6;
                if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
                if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;

                int endstream = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (endstream < 0) { pos = streamStart + 6; continue; }

                int compressedLen = endstream - dataStart;
                if (compressedLen <= 0 || dataStart + compressedLen > data.Length)
                {
                    pos = endstream + 9;
                    continue;
                }

                bool isFlate = before.Contains("FlateDecode");

                if (isFlate)
                {
                    try
                    {
                        byte[] compressedBytes = new byte[compressedLen];
                        Array.Copy(data, dataStart, compressedBytes, 0, compressedLen);

                        // Skip zlib header (2 bytes) and decompress
                        if (compressedBytes.Length > 2)
                        {
                            using (var ms = new MemoryStream(compressedBytes, 2, compressedBytes.Length - 2))
                            using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
                            using (var result = new MemoryStream())
                            {
                                deflate.CopyTo(result);
                                byte[] decompBytes = result.ToArray();
                                string decompressed = Encoding.GetEncoding(1252).GetString(decompBytes);
                                streams.Add(decompressed);
                            }
                        }
                    }
                    catch { }
                }

                pos = endstream + 9;
            }
            return streams;
        }

        /// <summary>
        /// Parse ToUnicode CMap tables to build a mapping from CID values to Unicode characters.
        /// </summary>
        private static Dictionary<int, string> BuildToUnicodeCMap(string raw, byte[] data, List<string> decompressedStreams)
        {
            var cmap = new Dictionary<int, string>();

            // Search for ToUnicode references and parse the associated CMap streams
            // CMaps can be in decompressed streams or inline
            var allContent = new List<string>(decompressedStreams);
            allContent.Add(raw);

            foreach (var content in allContent)
            {
                ParseCMapContent(content, cmap);
            }

            return cmap;
        }

        /// <summary>
        /// Parse CMap entries from content (beginbfchar/endbfchar and beginbfrange/endbfrange).
        /// </summary>
        private static void ParseCMapContent(string content, Dictionary<int, string> cmap)
        {
            // Parse beginbfchar...endbfchar blocks
            int pos = 0;
            while (pos < content.Length)
            {
                int bfcharStart = content.IndexOf("beginbfchar", pos, StringComparison.Ordinal);
                if (bfcharStart < 0) break;

                int bfcharEnd = content.IndexOf("endbfchar", bfcharStart, StringComparison.Ordinal);
                if (bfcharEnd < 0) break;

                string block = content.Substring(bfcharStart + 11, bfcharEnd - bfcharStart - 11);
                ParseBfCharEntries(block, cmap);
                pos = bfcharEnd + 9;
            }

            // Parse beginbfrange...endbfrange blocks
            pos = 0;
            while (pos < content.Length)
            {
                int bfrangeStart = content.IndexOf("beginbfrange", pos, StringComparison.Ordinal);
                if (bfrangeStart < 0) break;

                int bfrangeEnd = content.IndexOf("endbfrange", bfrangeStart, StringComparison.Ordinal);
                if (bfrangeEnd < 0) break;

                string block = content.Substring(bfrangeStart + 12, bfrangeEnd - bfrangeStart - 12);
                ParseBfRangeEntries(block, cmap);
                pos = bfrangeEnd + 10;
            }
        }

        /// <summary>
        /// Parse bfchar entries: &lt;srcCode&gt; &lt;dstUnicode&gt;
        /// </summary>
        private static void ParseBfCharEntries(string block, Dictionary<int, string> cmap)
        {
            // Match pairs of hex values: <XX> <XXXX> or <XXXX> <XXXX>
            var matches = Regex.Matches(block, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>");
            foreach (Match m in matches)
            {
                try
                {
                    int srcCode = Convert.ToInt32(m.Groups[1].Value, 16);
                    string dstHex = m.Groups[2].Value;
                    string unicode = HexToUnicodeString(dstHex);
                    if (unicode != null)
                        cmap[srcCode] = unicode;
                }
                catch { }
            }
        }

        /// <summary>
        /// Parse bfrange entries: &lt;start&gt; &lt;end&gt; &lt;startUnicode&gt;
        /// Maps a range of CID codes to sequential Unicode values.
        /// </summary>
        private static void ParseBfRangeEntries(string block, Dictionary<int, string> cmap)
        {
            var matches = Regex.Matches(block, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>");
            foreach (Match m in matches)
            {
                try
                {
                    int startCode = Convert.ToInt32(m.Groups[1].Value, 16);
                    int endCode = Convert.ToInt32(m.Groups[2].Value, 16);
                    int startUnicode = Convert.ToInt32(m.Groups[3].Value, 16);

                    for (int code = startCode; code <= endCode && code - startCode < 65536; code++)
                    {
                        int unicodeVal = startUnicode + (code - startCode);
                        cmap[code] = char.ConvertFromUtf32(unicodeVal);
                    }
                }
                catch { }
            }

            // Also handle array format: <start> <end> [<u1> <u2> ...]
            var arrayMatches = Regex.Matches(block,
                @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\[([^\]]+)\]");
            foreach (Match m in arrayMatches)
            {
                try
                {
                    int startCode = Convert.ToInt32(m.Groups[1].Value, 16);
                    string arrayContent = m.Groups[3].Value;
                    var hexValues = Regex.Matches(arrayContent, @"<([0-9A-Fa-f]+)>");
                    int code = startCode;
                    foreach (Match hv in hexValues)
                    {
                        string unicode = HexToUnicodeString(hv.Groups[1].Value);
                        if (unicode != null)
                            cmap[code] = unicode;
                        code++;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Convert hex string to Unicode string. Handles both 2-byte and 4-byte+ hex values.
        /// </summary>
        private static string HexToUnicodeString(string hex)
        {
            try
            {
                if (hex.Length <= 4)
                {
                    int codePoint = Convert.ToInt32(hex, 16);
                    if (codePoint > 0)
                        return char.ConvertFromUtf32(codePoint);
                }
                else
                {
                    // Multi-char mapping
                    var sb = new StringBuilder();
                    for (int i = 0; i + 3 < hex.Length; i += 4)
                    {
                        int codePoint = Convert.ToInt32(hex.Substring(i, 4), 16);
                        if (codePoint > 0)
                            sb.Append(char.ConvertFromUtf32(codePoint));
                    }
                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extract text from BT...ET blocks in a PDF content stream.
        /// Uses the ToUnicode CMap for character mapping.
        /// </summary>
        private static void ExtractTextFromPdfStream(string stream, Dictionary<int, string> cmap, StringBuilder sb, int maxTextLength)
        {
            int pos = 0;
            while (pos < stream.Length && sb.Length < maxTextLength)
            {
                // Find BT marker (must be preceded by whitespace/newline or at start)
                int btStart = stream.IndexOf("BT", pos, StringComparison.Ordinal);
                if (btStart < 0) break;

                // Verify BT is a real operator (preceded by whitespace or newline)
                if (btStart > 0 && !char.IsWhiteSpace(stream[btStart - 1]))
                {
                    pos = btStart + 2;
                    continue;
                }

                int etEnd = stream.IndexOf("ET", btStart + 2, StringComparison.Ordinal);
                if (etEnd < 0) break;

                string block = stream.Substring(btStart, etEnd - btStart + 2);
                ExtractPdfTextOperators(block, cmap, sb, maxTextLength);
                pos = etEnd + 2;
            }
        }

        /// <summary>
        /// Extract text from PDF text operators within a BT...ET block.
        /// Handles both parenthesized strings and hex-encoded CID values.
        /// </summary>
        private static void ExtractPdfTextOperators(string block, Dictionary<int, string> cmap, StringBuilder sb, int maxTextLength)
        {
            int i = 0;
            while (i < block.Length && sb.Length < maxTextLength)
            {
                // Look for parenthesized strings: (text) Tj or (text) '
                if (block[i] == '(')
                {
                    int depth = 1;
                    int start = i + 1;
                    i++;
                    while (i < block.Length && depth > 0)
                    {
                        if (block[i] == '\\') { i += 2; continue; }
                        if (block[i] == '(') depth++;
                        if (block[i] == ')') depth--;
                        if (depth > 0) i++;
                    }
                    if (depth == 0)
                    {
                        string text = block.Substring(start, i - start);
                        string decoded = DecodePdfString(text);
                        if (!string.IsNullOrEmpty(decoded))
                            sb.Append(decoded);
                    }
                    i++;
                }
                // Look for hex strings: <hex> Tj or within TJ array
                else if (block[i] == '<' && i + 1 < block.Length && block[i + 1] != '<')
                {
                    int end = block.IndexOf('>', i + 1);
                    if (end > i)
                    {
                        string hex = block.Substring(i + 1, end - i - 1)
                            .Replace(" ", "").Replace("\n", "").Replace("\r", "");
                        if (hex.Length > 0)
                        {
                            // Try CMap lookup first (CID-encoded)
                            string decoded = DecodeHexWithCMap(hex, cmap);
                            if (!string.IsNullOrEmpty(decoded))
                                sb.Append(decoded);
                        }
                        i = end + 1;
                    }
                    else
                    {
                        i++;
                    }
                }
                // Td/TD (text positioning) - add space
                else if (i + 2 <= block.Length
                    && block[i] == 'T' && (block[i + 1] == 'd' || block[i + 1] == 'D')
                    && (i + 2 >= block.Length || !char.IsLetter(block[i + 2])))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n')
                        sb.Append(' ');
                    i += 2;
                }
                // T* (new line)
                else if (i + 2 <= block.Length && block[i] == 'T' && block[i + 1] == '*')
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    i += 2;
                }
                // TJ array with negative number spacing (large negative = word space)
                else if (block[i] == '-' && i + 1 < block.Length && char.IsDigit(block[i + 1]))
                {
                    // Read the number
                    int numStart = i;
                    i++;
                    while (i < block.Length && (char.IsDigit(block[i]) || block[i] == '.')) i++;
                    string numStr = block.Substring(numStart, i - numStart);
                    double val;
                    if (double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out val))
                    {
                        // Large negative values indicate word spacing
                        if (val < -100 && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            sb.Append(' ');
                    }
                }
                else
                {
                    i++;
                }
            }
        }

        /// <summary>
        /// Decode hex string using ToUnicode CMap. Falls back to direct Unicode / UTF-16BE.
        /// Many CID fonts in Japanese PDFs use Identity-H encoding where CID = Unicode codepoint.
        /// </summary>
        private static string DecodeHexWithCMap(string hex, Dictionary<int, string> cmap)
        {
            if (hex.Length == 0) return null;

            // Try 2-byte (4 hex char) codes first, then 1-byte (2 hex char) codes
            if (hex.Length >= 4 && hex.Length % 4 == 0)
            {
                var sb = new StringBuilder();
                bool success = true;
                for (int j = 0; j + 4 <= hex.Length; j += 4)
                {
                    try
                    {
                        int code = Convert.ToInt32(hex.Substring(j, 4), 16);
                        string unicode;
                        if (cmap != null && cmap.TryGetValue(code, out unicode))
                        {
                            sb.Append(unicode);
                        }
                        else if (code == 0 || code == 0xFFFF)
                        {
                            // Skip null / sentinel
                        }
                        else if (IsLikelyUnicodeCodePoint(code))
                        {
                            // Identity-H: CID is the Unicode codepoint directly
                            sb.Append(char.ConvertFromUtf32(code));
                        }
                        else
                        {
                            success = false;
                            break;
                        }
                    }
                    catch
                    {
                        success = false;
                        break;
                    }
                }

                if (success && sb.Length > 0)
                    return sb.ToString();
            }

            // Try 1-byte (2 hex char) codes
            if (hex.Length >= 2 && hex.Length % 2 == 0 && hex.Length < 4)
            {
                var sb = new StringBuilder();
                bool success = true;
                for (int j = 0; j + 2 <= hex.Length; j += 2)
                {
                    try
                    {
                        int code = Convert.ToInt32(hex.Substring(j, 2), 16);
                        string unicode;
                        if (cmap != null && cmap.TryGetValue(code, out unicode))
                        {
                            sb.Append(unicode);
                        }
                        else if (code == 0)
                        {
                            // Skip null
                        }
                        else if (code >= 0x20 && code < 0x7F)
                        {
                            // ASCII range
                            sb.Append((char)code);
                        }
                        else
                        {
                            success = false;
                            break;
                        }
                    }
                    catch
                    {
                        success = false;
                        break;
                    }
                }

                if (success && sb.Length > 0)
                    return sb.ToString();
            }

            // Fallback: try UTF-16BE interpretation
            if (hex.Length >= 4 && hex.Length % 4 == 0)
            {
                return DecodeHexUtf16(hex);
            }

            return null;
        }

        /// <summary>
        /// Check if a code value is likely a direct Unicode code point
        /// (common in Identity-H encoded CID fonts).
        /// </summary>
        private static bool IsLikelyUnicodeCodePoint(int code)
        {
            // Basic Latin (ASCII printable)
            if (code >= 0x0020 && code <= 0x007E) return true;
            // Latin Extended / Symbols
            if (code >= 0x00A0 && code <= 0x024F) return true;
            // General Punctuation
            if (code >= 0x2000 && code <= 0x206F) return true;
            // Letterlike Symbols, Number Forms, Arrows
            if (code >= 0x2100 && code <= 0x27FF) return true;
            // CJK Symbols and Punctuation
            if (code >= 0x3000 && code <= 0x303F) return true;
            // Hiragana
            if (code >= 0x3040 && code <= 0x309F) return true;
            // Katakana
            if (code >= 0x30A0 && code <= 0x30FF) return true;
            // CJK Unified Ideographs
            if (code >= 0x4E00 && code <= 0x9FFF) return true;
            // CJK Compatibility Ideographs
            if (code >= 0xF900 && code <= 0xFAFF) return true;
            // Halfwidth and Fullwidth Forms
            if (code >= 0xFF00 && code <= 0xFFEF) return true;
            // CJK Unified Ideographs Extension A
            if (code >= 0x3400 && code <= 0x4DBF) return true;
            // Enclosed CJK Letters
            if (code >= 0x3200 && code <= 0x32FF) return true;
            // Katakana Phonetic Extensions
            if (code >= 0x31F0 && code <= 0x31FF) return true;
            // Miscellaneous Symbols
            if (code >= 0x25A0 && code <= 0x25FF) return true;
            // Box Drawing / Block Elements / Geometric Shapes
            if (code >= 0x2500 && code <= 0x259F) return true;

            return false;
        }

        /// <summary>
        /// Decode a PDF parenthesized string, handling escape sequences.
        /// </summary>
        private static string DecodePdfString(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case '(': sb.Append('('); i++; break;
                        case ')': sb.Append(')'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        default:
                            // Octal escape
                            if (next >= '0' && next <= '7')
                            {
                                string octal = "" + next;
                                if (i + 2 < text.Length && text[i + 2] >= '0' && text[i + 2] <= '7')
                                {
                                    octal += text[i + 2];
                                    if (i + 3 < text.Length && text[i + 3] >= '0' && text[i + 3] <= '7')
                                        octal += text[i + 3];
                                }
                                int code = Convert.ToInt32(octal, 8);
                                sb.Append((char)code);
                                i += octal.Length;
                            }
                            else
                            {
                                sb.Append(next);
                                i++;
                            }
                            break;
                    }
                }
                else
                {
                    sb.Append(text[i]);
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Decode hex-encoded UTF-16BE string from PDF.
        /// </summary>
        private static string DecodeHexUtf16(string hex)
        {
            try
            {
                // Remove whitespace
                hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
                if (hex.Length < 4 || hex.Length % 4 != 0) return null;

                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < hex.Length; i += 2)
                {
                    bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
                }

                string result = Encoding.BigEndianUnicode.GetString(bytes);
                // Filter out control characters
                var sb = new StringBuilder(result.Length);
                foreach (char c in result)
                {
                    if (c >= 32 || c == '\n' || c == '\r' || c == '\t')
                        sb.Append(c);
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract text using the IFilter COM interface.
        /// Handles initialization, chunk enumeration, and text retrieval.
        /// </summary>
        private string ExtractViaIFilter(string filePath, int maxTextLength)
        {
            IFilter filter = null;
            try
            {
                int hr = IFilterNativeMethods.LoadIFilter(filePath, IntPtr.Zero, out filter);
                if (hr != IFilterNativeMethods.S_OK || filter == null)
                    return null;

                // Initialize the filter
                uint flags;
                hr = filter.Init(
                    IFILTER_INIT.CANON_PARAGRAPHS
                    | IFILTER_INIT.CANON_HYPHENS
                    | IFILTER_INIT.CANON_SPACES
                    | IFILTER_INIT.APPLY_INDEX_ATTRIBUTES,
                    0,
                    IntPtr.Zero,
                    out flags);

                if (hr != IFilterNativeMethods.S_OK)
                    return null;

                var sb = new StringBuilder();
                var textBuffer = new StringBuilder(8192);

                // Enumerate chunks
                while (true)
                {
                    STAT_CHUNK chunk;
                    hr = filter.GetChunk(out chunk);

                    if (hr == IFilterNativeMethods.FILTER_E_END_OF_CHUNKS)
                        break;

                    if (hr != IFilterNativeMethods.S_OK)
                        break;

                    // Only process text chunks
                    if ((chunk.flags & CHUNKSTATE.CHUNK_TEXT) == 0)
                        continue;

                    // Read text from this chunk
                    while (true)
                    {
                        uint bufLen = 8192;
                        textBuffer.Length = 0;
                        textBuffer.Capacity = (int)bufLen;

                        hr = filter.GetText(ref bufLen, textBuffer);

                        if (bufLen > 0 && textBuffer.Length > 0)
                        {
                            sb.Append(textBuffer.ToString(0, Math.Min(textBuffer.Length, (int)bufLen)));
                        }

                        if (sb.Length >= maxTextLength)
                        {
                            sb.Length = maxTextLength;
                            return sb.ToString();
                        }

                        if (hr == IFilterNativeMethods.FILTER_E_NO_MORE_TEXT)
                            break;

                        if (hr != IFilterNativeMethods.S_OK)
                            break;
                    }
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch (COMException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (filter != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(filter);
                    }
                    catch { }
                }
            }
        }
    }
}
