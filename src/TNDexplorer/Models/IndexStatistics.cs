namespace TNDexplorer.Models
{
    public class IndexStatistics
    {
        public int TotalFiles { get; set; }
        public int TotalFolders { get; set; }
        public int TotalEntries => TotalFiles + TotalFolders;
        public bool IsIndexing { get; set; }
        public string CurrentOperation { get; set; }
    }
}
