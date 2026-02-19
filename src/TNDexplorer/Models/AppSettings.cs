using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TNDexplorer.Models
{
    [DataContract]
    public class AppSettings
    {
        /// <summary>
        /// Called before deserialization to set default values for newly added properties.
        /// DataContractSerializer ignores property initializers for fields not present in the data.
        /// </summary>
        [OnDeserializing]
        private void SetDefaults(StreamingContext context)
        {
            ContentSearchIncludeFilename = true;
            MaxSearchResults = 100000;
            NetworkRescanIntervalMinutes = 5;
            ExcludeHiddenFiles = true;
            ExcludeSystemFiles = true;
            IsFirstRun = true;
            WindowWidth = 900;
            WindowHeight = 600;
            WindowLeft = -1;
            WindowTop = -1;
            FullTextMaxFileSizeMB = 50;
        }

        [DataMember]
        public List<DriveSettings> LocalDrives { get; set; } = new List<DriveSettings>();

        [DataMember]
        public List<NetworkDriveSettings> NetworkDrives { get; set; } = new List<NetworkDriveSettings>();

        [DataMember]
        public List<string> ExcludePatterns { get; set; } = new List<string>();

        [DataMember]
        public List<CustomFolderSettings> CustomFolders { get; set; } = new List<CustomFolderSettings>();

        // Display settings
        [DataMember]
        public bool AlternateRowColors { get; set; } = false;

        [DataMember]
        public bool GroupByFiscalYear { get; set; } = false;

        [DataMember]
        public int MaxSearchResults { get; set; } = 100000;

        [DataMember]
        public int NetworkRescanIntervalMinutes { get; set; } = 5;

        [DataMember]
        public bool ExcludeHiddenFiles { get; set; } = true;

        [DataMember]
        public bool ExcludeSystemFiles { get; set; } = true;

        [DataMember]
        public bool IsFirstRun { get; set; } = true;

        [DataMember]
        public double WindowWidth { get; set; } = 900;

        [DataMember]
        public double WindowHeight { get; set; } = 600;

        [DataMember]
        public double WindowLeft { get; set; } = -1;

        [DataMember]
        public double WindowTop { get; set; } = -1;

        [DataMember]
        public List<FavoriteFilter> FavoriteFilters { get; set; } = new List<FavoriteFilter>();

        // Content search settings
        [DataMember]
        public List<string> FullTextTargetExtensions { get; set; } = new List<string>();

        [DataMember]
        public long FullTextMaxFileSizeMB { get; set; } = 50;

        [DataMember]
        public bool ContentSearchIncludeFilename { get; set; } = true;
    }

    [DataContract]
    public class FavoriteFilter
    {
        [DataMember]
        public string Name { get; set; }

        /// <summary>Extension filter e.g. ".pdf", ".docx"</summary>
        [DataMember]
        public string Extension { get; set; }

        /// <summary>Folder path filter e.g. "C:\Users\Documents"</summary>
        [DataMember]
        public string FolderPath { get; set; }

        /// <summary>Search keyword</summary>
        [DataMember]
        public string Keyword { get; set; }
    }

    [DataContract]
    public class DriveSettings
    {
        [DataMember]
        public string DriveLetter { get; set; }

        [DataMember]
        public bool Enabled { get; set; } = true;
    }

    [DataContract]
    public class NetworkDriveSettings
    {
        [DataMember]
        public string DriveLetter { get; set; }

        [DataMember]
        public string UncPath { get; set; }

        [DataMember]
        public bool Enabled { get; set; } = true;
    }

    [DataContract]
    public class CustomFolderSettings
    {
        [DataMember]
        public string FolderPath { get; set; }

        [DataMember]
        public bool Enabled { get; set; } = true;
    }
}
