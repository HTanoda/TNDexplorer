using System;
using System.Collections.Generic;
using System.Linq;

namespace TNDexplorer.Core
{
    /// <summary>
    /// Parsed search query supporting AND/OR/NOT/ext: syntax.
    ///
    /// Syntax:
    ///   AND:  space-separated terms    e.g. "ext:txt テスト"
    ///   OR:   pipe-separated terms     e.g. "ext:txt|ext:doc"
    ///   NOT:  ! prefix                 e.g. "!否定"
    ///   EXT:  ext: prefix              e.g. "ext:txt", "ext:jpg;gif"
    ///
    /// Example: "ext:txt|ext:doc テスト !否定"
    ///   => (ext is txt OR ext is doc) AND (name contains テスト) AND (name does NOT contain 否定)
    /// </summary>
    public class ParsedSearchQuery
    {
        /// <summary>OR groups: each group is a list of AND clauses.
        /// The top level is OR, each element is ANDed together within its group.
        /// Overall: ORGroup1 results | ORGroup2 results
        /// But in the user syntax, | splits individual tokens, and space groups them.
        ///
        /// Actual structure:
        /// The query "ext:txt|ext:doc テスト !否定" is parsed as:
        ///   OR-split first: ["ext:txt", "ext:doc テスト !否定"]
        ///   Each OR branch is AND-split by space.
        ///   So final structure = OR of AND groups:
        ///     Branch 1: [ext:txt]
        ///     Branch 2: [ext:doc, テスト, !否定]
        ///
        /// Wait - re-reading the spec more carefully:
        ///   "ext:txt|ext:doc テスト" means:
        ///   (ext:txt OR ext:doc) AND テスト
        ///
        /// The OR binds tighter than AND (OR only applies between adjacent | tokens).
        /// So "A|B C" means "(A OR B) AND C"
        /// And "A B|C" means "A AND (B OR C)"
        /// </summary>
        public List<SearchClause> AndClauses { get; set; } = new List<SearchClause>();

        public bool IsEmpty => AndClauses.Count == 0;

        /// <summary>
        /// Get all ext: filters merged from all clauses.
        /// Returns null if no ext: filters found.
        /// </summary>
        public string[] GetExtensionFilters()
        {
            var exts = new List<string>();
            foreach (var clause in AndClauses)
            {
                foreach (var term in clause.OrTerms)
                {
                    if (term.Type == TermType.Extension && !term.IsNegated)
                    {
                        exts.AddRange(term.Extensions);
                    }
                }
            }
            return exts.Count > 0 ? exts.ToArray() : null;
        }

        /// <summary>
        /// Get negated extension filters.
        /// </summary>
        public string[] GetNegatedExtensionFilters()
        {
            var exts = new List<string>();
            foreach (var clause in AndClauses)
            {
                foreach (var term in clause.OrTerms)
                {
                    if (term.Type == TermType.Extension && term.IsNegated)
                    {
                        exts.AddRange(term.Extensions);
                    }
                }
            }
            return exts.Count > 0 ? exts.ToArray() : null;
        }
    }

    /// <summary>
    /// An AND clause that may contain multiple OR terms.
    /// All OR terms are alternatives - at least one must match.
    /// </summary>
    public class SearchClause
    {
        public List<SearchTerm> OrTerms { get; set; } = new List<SearchTerm>();
    }

    public enum TermType
    {
        Keyword,    // plain text match
        Extension,  // ext:xxx
    }

    public class SearchTerm
    {
        public TermType Type { get; set; }
        public bool IsNegated { get; set; }
        public string Value { get; set; }           // for Keyword type
        public string[] Extensions { get; set; }    // for Extension type (normalized with dots)
    }

    public static class SearchQueryParser
    {
        /// <summary>
        /// Parse a search query string into structured form.
        /// </summary>
        public static ParsedSearchQuery Parse(string query)
        {
            var result = new ParsedSearchQuery();

            if (string.IsNullOrWhiteSpace(query))
                return result;

            // Split by spaces first (AND tokens), but preserve | within tokens
            var andTokens = SplitBySpace(query);

            foreach (var andToken in andTokens)
            {
                if (string.IsNullOrWhiteSpace(andToken)) continue;

                var clause = new SearchClause();

                // Split by | for OR
                var orParts = andToken.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var orPart in orParts)
                {
                    string part = orPart.Trim();
                    if (string.IsNullOrEmpty(part)) continue;

                    var term = ParseTerm(part);
                    if (term != null)
                    {
                        clause.OrTerms.Add(term);
                    }
                }

                if (clause.OrTerms.Count > 0)
                {
                    result.AndClauses.Add(clause);
                }
            }

            return result;
        }

        private static string[] SplitBySpace(string query)
        {
            // Simple space split, respecting that | should stay with tokens
            return query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static SearchTerm ParseTerm(string token)
        {
            bool negated = false;
            string working = token;

            // Check for negation
            if (working.StartsWith("!") && working.Length > 1)
            {
                negated = true;
                working = working.Substring(1);
            }

            // Check for ext: prefix
            if (working.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                string extValue = working.Substring(4);
                if (string.IsNullOrEmpty(extValue)) return null;

                // Support semicolon-separated extensions: ext:jpg;gif;png
                var extensions = extValue
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e =>
                    {
                        string ext = e.Trim().TrimStart('*');
                        if (!ext.StartsWith("."))
                            ext = "." + ext;
                        return ext;
                    })
                    .ToArray();

                return new SearchTerm
                {
                    Type = TermType.Extension,
                    IsNegated = negated,
                    Extensions = extensions,
                };
            }

            // Plain keyword
            return new SearchTerm
            {
                Type = TermType.Keyword,
                IsNegated = negated,
                Value = working,
            };
        }
    }
}
