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

        /// <summary>
        /// True if this item is at the start of a new fiscal year group.
        /// Used for visual grouping in the ListView.
        /// </summary>
        public bool IsFiscalYearBoundary { get; set; }

        /// <summary>
        /// Display label for the fiscal year group (e.g. "2024年度").
        /// </summary>
        public string FiscalYearLabel { get; set; }

        public string Extension
        {
            get
            {
                if (IsDirectory) return "";
                int dot = Name.LastIndexOf('.');
                return dot >= 0 ? Name.Substring(dot) : "";
            }
        }

        /// <summary>
        /// Calculates the fiscal year (4月〜翌3月) from the Modified date.
        /// Example: 2024年4月〜2025年3月 → 2024年度
        /// </summary>
        public int FiscalYear
        {
            get
            {
                if (Modified.Month >= 4)
                    return Modified.Year;
                else
                    return Modified.Year - 1;
            }
        }
    }
}
