using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using TNDexplorer.Core;

namespace TNDexplorer.Services
{
    /// <summary>
    /// Caches network drive index to local disk for faster startup.
    /// Stored in %LOCALAPPDATA%\TNDexplorer\cache\ as compact binary files.
    ///
    /// Binary format (version 1):
    ///   Header (16 bytes):
    ///     [Magic: 4 bytes] "TNDc"
    ///     [Version: 2 bytes] uint16 LE = 1
    ///     [EntryCount: 4 bytes] uint32 LE
    ///     [Reserved: 6 bytes] zeros
    ///   Per-entry (35 + nameLen bytes):
    ///     [FRN: 8 bytes] uint64 LE
    ///     [ParentFRN: 8 bytes] uint64 LE
    ///     [Size: 8 bytes] int64 LE
    ///     [Modified: 8 bytes] int64 LE (DateTime.ToBinary())
    ///     [Flags: 1 byte] (bit 0 = IsDir)
    ///     [NameLen: 2 bytes] uint16 LE (UTF-8 byte count)
    ///     [Name: NameLen bytes] UTF-8
    /// </summary>
    public class NetworkIndexCache
    {
        private readonly string _cacheDir;

        private static readonly byte[] Magic = { (byte)'T', (byte)'N', (byte)'D', (byte)'c' };
        private const ushort FormatVersion = 1;

        /// <summary>
        /// Per-key lock objects to prevent concurrent writes to the same cache file.
        /// Without this, two threads saving the same cacheKey simultaneously would
        /// both try to create .tmp files and race on File.Move → IOException.
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> _keyLocks
            = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maximum cache file path length allowed (leave margin below MAX_PATH 260).
        /// </summary>
        private const int MaxCachePathLength = 240;

        public NetworkIndexCache()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDir = Path.Combine(appData, "TNDexplorer", "cache");
        }

        private string GetCachePath(string cacheKey)
        {
            string safeName = cacheKey.Replace(":", "").Replace("\\", "").Replace("/", "");
            string candidate = Path.Combine(_cacheDir, $"nw_index_{safeName}.bin");

            if (candidate.Length > MaxCachePathLength)
            {
                string hash = ComputeHash(cacheKey);
                string prefix = safeName.Length > 30 ? safeName.Substring(0, 30) : safeName;
                candidate = Path.Combine(_cacheDir, $"nw_index_{prefix}_{hash}.bin");
            }

            return candidate;
        }

        private static string ComputeHash(string input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(24);
                for (int i = 0; i < 12; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public List<CachedFileEntry> Load(string cacheKey)
        {
            string path = GetCachePath(cacheKey);
            if (!File.Exists(path))
                return null;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new BinaryReader(fs, Encoding.UTF8))
                {
                    // Read and validate header
                    byte[] magic = reader.ReadBytes(4);
                    if (magic.Length < 4 || magic[0] != Magic[0] || magic[1] != Magic[1]
                        || magic[2] != Magic[2] || magic[3] != Magic[3])
                    {
                        throw new InvalidDataException("Invalid cache file: bad magic bytes");
                    }

                    ushort version = reader.ReadUInt16();
                    if (version != FormatVersion)
                    {
                        throw new InvalidDataException($"Unsupported cache version: {version}");
                    }

                    uint entryCount = reader.ReadUInt32();
                    reader.ReadBytes(6); // Reserved

                    if (entryCount > 10_000_000)
                    {
                        throw new InvalidDataException($"Entry count too large: {entryCount}");
                    }

                    var entries = new List<CachedFileEntry>((int)entryCount);

                    for (uint i = 0; i < entryCount; i++)
                    {
                        ulong frn = reader.ReadUInt64();
                        ulong parentFrn = reader.ReadUInt64();
                        long size = reader.ReadInt64();
                        long modifiedBinary = reader.ReadInt64();
                        byte flags = reader.ReadByte();
                        ushort nameLen = reader.ReadUInt16();

                        byte[] nameBytes = reader.ReadBytes(nameLen);
                        string name = Encoding.UTF8.GetString(nameBytes);

                        entries.Add(new CachedFileEntry
                        {
                            FRN = frn,
                            ParentFRN = parentFrn,
                            Name = name,
                            Size = size,
                            Modified = DateTime.FromBinary(modifiedBinary),
                            IsDir = (flags & 0x01) != 0,
                        });
                    }

                    return entries;
                }
            }
            catch (Exception ex)
            {
                LogError("Load", cacheKey, ex);
                return null;
            }
        }

        public DateTime? GetCacheTimestamp(string cacheKey)
        {
            string path = GetCachePath(cacheKey);
            if (!File.Exists(path))
                return null;

            try
            {
                return File.GetLastWriteTime(path);
            }
            catch
            {
                return null;
            }
        }

        public void Save(string cacheKey, List<FileEntry> entries)
        {
            // Acquire per-key lock to prevent concurrent writes to the same cache file.
            // This fixes the race condition where initial scan and missed-rescan could
            // both try to save the same cache simultaneously, causing IOException.
            var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new object());
            lock (keyLock)
            {
                SaveInternal(cacheKey, entries);
            }
        }

        private void SaveInternal(string cacheKey, List<FileEntry> entries)
        {
            string tempPath = null;
            try
            {
                if (!Directory.Exists(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);

                string path = GetCachePath(cacheKey);
                // Use GUID-based temp file name to avoid collision even if lock is bypassed
                tempPath = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    using (var writer = new BinaryWriter(fs, Encoding.UTF8))
                    {
                        // Write header
                        writer.Write(Magic);
                        writer.Write(FormatVersion);
                        writer.Write((uint)entries.Count);
                        writer.Write(new byte[6]); // Reserved

                        // Write entries
                        foreach (var e in entries)
                        {
                            writer.Write(e.FileReferenceNumber);
                            writer.Write(e.ParentFileReferenceNumber);
                            writer.Write(e.Size);
                            writer.Write(e.ModifiedDate.ToBinary());

                            byte flags = 0;
                            if (e.IsDirectory) flags |= 0x01;
                            writer.Write(flags);

                            byte[] nameBytes = Encoding.UTF8.GetBytes(e.Name ?? string.Empty);
                            writer.Write((ushort)nameBytes.Length);
                            writer.Write(nameBytes);
                        }

                        writer.Flush();
                        fs.Flush(true);
                    }

                    // Atomic replace
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(tempPath, path);
                }
                finally
                {
                    try { if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogError("Save", cacheKey, ex);
            }
        }

        public void Delete(string cacheKey)
        {
            try
            {
                string path = GetCachePath(cacheKey);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void LogError(string operation, string cacheKey, Exception ex)
        {
            AppLog.Error("NetworkIndexCache", $"{operation} failed for '{cacheKey}'", ex);
        }
    }

    /// <summary>
    /// Cached file entry for binary format.
    /// </summary>
    public class CachedFileEntry
    {
        public ulong FRN { get; set; }
        public ulong ParentFRN { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public bool IsDir { get; set; }
    }
}
