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

            // Try IFilter extraction
            if (_iFilterAvailable)
            {
                string text = ExtractViaIFilter(filePath, maxTextLength);
                if (text != null)
                    return text;
            }

            // Fallback: extract from Office OpenXML (.docx/.xlsx/.pptx) by unzipping
            if (IsOpenXmlExtension(ext))
            {
                string text = ExtractFromOpenXml(filePath, ext, maxTextLength);
                if (text != null)
                    return text;
            }

            // Fallback: extract text from PDF by parsing raw streams
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                string text = ExtractFromPdf(filePath, maxTextLength);
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
        /// Extract text from PDF files by parsing content streams.
        /// Handles basic text operators (Tj, TJ, ', ") for both ASCII and UTF-16BE encoded text.
        /// This is a lightweight fallback when IFilter/Adobe is not available.
        /// </summary>
        private string ExtractFromPdf(string filePath, int maxTextLength)
        {
            try
            {
                byte[] data;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Limit read size
                    long readSize = Math.Min(fs.Length, maxTextLength * 4L);
                    data = new byte[readSize];
                    fs.Read(data, 0, data.Length);
                }

                string raw = Encoding.GetEncoding(1252).GetString(data);
                var sb = new StringBuilder();

                // Find and decode text from stream content between BT...ET blocks
                int pos = 0;
                while (pos < raw.Length && sb.Length < maxTextLength)
                {
                    int btStart = raw.IndexOf("BT", pos, StringComparison.Ordinal);
                    if (btStart < 0) break;

                    int etEnd = raw.IndexOf("ET", btStart + 2, StringComparison.Ordinal);
                    if (etEnd < 0) break;

                    string block = raw.Substring(btStart, etEnd - btStart + 2);
                    ExtractPdfTextOperators(block, sb, maxTextLength);
                    pos = etEnd + 2;
                }

                if (sb.Length == 0) return null;

                // Clean up the result
                string result = sb.ToString();
                // Remove excessive whitespace
                result = Regex.Replace(result, @"\s{3,}", "  ");
                return result.Length > 0 ? result : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract text from PDF text operators within a BT...ET block.
        /// </summary>
        private static void ExtractPdfTextOperators(string block, StringBuilder sb, int maxTextLength)
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
                // Look for hex strings: <hex> Tj
                else if (block[i] == '<' && i + 1 < block.Length && block[i + 1] != '<')
                {
                    int end = block.IndexOf('>', i + 1);
                    if (end > i)
                    {
                        string hex = block.Substring(i + 1, end - i - 1).Trim();
                        if (hex.Length > 0 && hex.Length % 4 == 0)
                        {
                            // Likely UTF-16BE encoded
                            string decoded = DecodeHexUtf16(hex);
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
                else if (i + 2 < block.Length
                    && (block[i] == 'T' && (block[i + 1] == 'd' || block[i + 1] == 'D'))
                    && (i + 2 >= block.Length || !char.IsLetter(block[i + 2])))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n')
                        sb.Append(' ');
                    i += 2;
                }
                // T* (new line)
                else if (i + 2 < block.Length && block[i] == 'T' && block[i + 1] == '*')
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
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
