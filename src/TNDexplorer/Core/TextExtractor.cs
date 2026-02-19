using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
