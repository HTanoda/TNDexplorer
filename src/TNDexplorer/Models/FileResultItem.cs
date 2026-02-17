using System;

namespace TNDexplorer.Models
{
    public class FileResultItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public bool IsDirectory { get; set; }

        public string Extension
        {
            get
            {
                if (IsDirectory) return "";
                int dot = Name.LastIndexOf('.');
                return dot >= 0 ? Name.Substring(dot) : "";
            }
        }
    }
}
