using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Runtime.Serialization;
using TNDexplorer.Core;

namespace TNDexplorer.Services
{
    /// <summary>
    /// Caches network drive index to local disk for faster startup.
    /// Stored in %LOCALAPPDATA%\TNDexplorer\cache\
    /// </summary>
    public class NetworkIndexCache
    {
        private readonly string _cacheDir;

        public NetworkIndexCache()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDir = Path.Combine(appData, "TNDexplorer", "cache");
        }

        private string GetCachePath(string driveLetter)
        {
            string safeName = driveLetter.Replace(":", "").Replace("\\", "");
            return Path.Combine(_cacheDir, $"nw_index_{safeName}.json");
        }

        public List<CachedFileEntry> Load(string driveLetter)
        {
            string path = GetCachePath(driveLetter);
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] json = File.ReadAllBytes(path);
                using (var stream = new MemoryStream(json))
                {
                    var serializer = new DataContractJsonSerializer(typeof(NetworkIndexCacheData));
                    var data = (NetworkIndexCacheData)serializer.ReadObject(stream);
                    return data?.Entries;
                }
            }
            catch
            {
                return null;
            }
        }

        public DateTime? GetCacheTimestamp(string driveLetter)
        {
            string path = GetCachePath(driveLetter);
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

        public void Save(string driveLetter, List<FileEntry> entries)
        {
            try
            {
                if (!Directory.Exists(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);

                var data = new NetworkIndexCacheData
                {
                    Entries = new List<CachedFileEntry>()
                };

                foreach (var e in entries)
                {
                    data.Entries.Add(new CachedFileEntry
                    {
                        FRN = e.FileReferenceNumber,
                        ParentFRN = e.ParentFileReferenceNumber,
                        Name = e.Name,
                        Size = e.Size,
                        Modified = e.ModifiedDate,
                        IsDir = e.IsDirectory,
                    });
                }

                string path = GetCachePath(driveLetter);
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(NetworkIndexCacheData));
                    serializer.WriteObject(stream, data);
                    File.WriteAllBytes(path, stream.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save network cache: {ex.Message}");
            }
        }

        public void Delete(string driveLetter)
        {
            try
            {
                string path = GetCachePath(driveLetter);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    [DataContract]
    public class NetworkIndexCacheData
    {
        [DataMember]
        public List<CachedFileEntry> Entries { get; set; }
    }

    [DataContract]
    public class CachedFileEntry
    {
        [DataMember]
        public ulong FRN { get; set; }
        [DataMember]
        public ulong ParentFRN { get; set; }
        [DataMember]
        public string Name { get; set; }
        [DataMember]
        public long Size { get; set; }
        [DataMember]
        public DateTime Modified { get; set; }
        [DataMember]
        public bool IsDir { get; set; }
    }
}
