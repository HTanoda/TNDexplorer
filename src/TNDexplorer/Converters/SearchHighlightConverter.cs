using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace TNDexplorer.Converters
{
    /// <summary>
    /// Multi-value converter that creates a list of Inline elements for a TextBlock,
    /// highlighting portions of the file name that match the search query.
    ///
    /// Values[0] = Name (string)
    /// Values[1] = SearchText (string) from MainViewModel
    /// Values[2] = IsDirectory (bool)
    ///
    /// For keyword searches: matches the keyword in the Name.
    /// For ext: searches: matches the extension part in the Name.
    /// </summary>
    public class SearchHighlightConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return null;

            string name = values[0] as string;
            string searchText = values[1] as string;

            if (string.IsNullOrEmpty(name))
                return new List<Inline> { new Run("") };

            if (string.IsNullOrEmpty(searchText))
                return new List<Inline> { new Run(name) };

            // Parse the search text to find highlight targets
            var highlights = GetHighlightRanges(name, searchText);

            if (highlights.Count == 0)
                return new List<Inline> { new Run(name) };

            // Merge overlapping ranges and sort
            highlights.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = MergeRanges(highlights);

            // Build inlines
            var inlines = new List<Inline>();
            int pos = 0;
            foreach (var range in merged)
            {
                if (range.Start > pos)
                {
                    inlines.Add(new Run(name.Substring(pos, range.Start - pos)));
                }
                inlines.Add(new Run(name.Substring(range.Start, range.Length))
                {
                    FontWeight = FontWeights.Bold
                });
                pos = range.Start + range.Length;
            }
            if (pos < name.Length)
            {
                inlines.Add(new Run(name.Substring(pos)));
            }

            return inlines;
        }

        private List<HighlightRange> GetHighlightRanges(string name, string searchText)
        {
            var ranges = new List<HighlightRange>();

            // Split search text by spaces (AND tokens)
            var tokens = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                // Split by | for OR within each token
                var orParts = token.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var orPart in orParts)
                {
                    string part = orPart.Trim();
                    if (string.IsNullOrEmpty(part)) continue;

                    // Skip negated terms
                    if (part.StartsWith("!")) continue;

                    // Handle ext: prefix
                    if (part.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                    {
                        string extValue = part.Substring(4);
                        if (string.IsNullOrEmpty(extValue)) continue;

                        // Support semicolon-separated extensions
                        var extensions = extValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var ext in extensions)
                        {
                            string e = ext.Trim().TrimStart('*').TrimStart('.');
                            if (string.IsNullOrEmpty(e)) continue;

                            // Find the extension part in the name
                            int dotIdx = name.LastIndexOf('.');
                            if (dotIdx >= 0)
                            {
                                string nameExt = name.Substring(dotIdx + 1);
                                if (nameExt.Equals(e, StringComparison.OrdinalIgnoreCase))
                                {
                                    ranges.Add(new HighlightRange(dotIdx + 1, nameExt.Length));
                                }
                            }
                        }
                    }
                    else
                    {
                        // Plain keyword: find all occurrences in name
                        int startIdx = 0;
                        while (startIdx < name.Length)
                        {
                            int idx = name.IndexOf(part, startIdx, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) break;
                            ranges.Add(new HighlightRange(idx, part.Length));
                            startIdx = idx + 1;
                        }
                    }
                }
            }

            return ranges;
        }

        private List<HighlightRange> MergeRanges(List<HighlightRange> ranges)
        {
            if (ranges.Count <= 1) return ranges;

            var merged = new List<HighlightRange>();
            var current = ranges[0];

            for (int i = 1; i < ranges.Count; i++)
            {
                if (ranges[i].Start <= current.Start + current.Length)
                {
                    int end = Math.Max(current.Start + current.Length, ranges[i].Start + ranges[i].Length);
                    current = new HighlightRange(current.Start, end - current.Start);
                }
                else
                {
                    merged.Add(current);
                    current = ranges[i];
                }
            }
            merged.Add(current);
            return merged;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private struct HighlightRange
        {
            public int Start;
            public int Length;
            public HighlightRange(int start, int length)
            {
                Start = start;
                Length = length;
            }
        }
    }
}
