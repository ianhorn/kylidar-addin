/*
 * Which points of each tile to keep when converting: by LAS classification and by return (all,
 * first or last). Pure logic with no ArcGIS dependency, so the rules can be tested on their own.
 *
 * Mirrors the QGIS plugin's point_filter.py (kentucky_stac), so both tools behave the same.
 *
 * A filter becomes two PDAL stages, which every conversion path (the in-pane Run, the generated
 * Python/PowerShell/shell scripts and the bundled downloader exe) appends before writers.las:
 *   - filters.range "limits" -- the classification runs (several limits on one dimension are ORed)
 *     and, when overlap is excluded, Overlap[0:0] (the LAS 1.4 overlap flag; limits on different
 *     dimensions are ANDed);
 *   - filters.expression -- the return groups, as an OR of conditions on ReturnNumber and
 *     NumberOfReturns, which a range can't express because it compares two dimensions.
 *
 * Returns are four disjoint groups that together cover every point, the same split as PDAL's own
 * filters.returns: a pulse with one return is "single", otherwise its first return is "first of
 * many", its last is "last of many" and any in between are "intermediate". Ticking Single + Last
 * gives "last returns, single ones included" (the usual choice for ground); ticking only Last gives
 * PDAL's strict "last" (last of a multi-return pulse).
 */
using System.Collections.Generic;
using System.Linq;

namespace KylidarAddin.Services
{
    /// <summary>Which returns to keep: any combination of the four disjoint groups.</summary>
    [System.Flags]
    public enum ReturnGroups
    {
        None = 0,
        Single = 1,
        First = 2,
        Intermediate = 4,
        Last = 8,
        All = Single | First | Intermediate | Last
    }

    public sealed class PointFilter
    {
        /// <summary>Stands for every code above 18 (19-63 reserved, 64-255 user-defined), so unticking
        /// the named classes can't silently keep or drop those.</summary>
        public const int OtherClass = 19;

        /// <summary>The ASPRS LAS classes. 12 is "reserved" in LAS 1.4 (it was "overlap" in 1.1).</summary>
        public static readonly IReadOnlyList<(int Code, string Label)> Classes = new (int, string)[]
        {
            (0, "Never classified"),
            (1, "Unclassified"),
            (2, "Ground"),
            (3, "Low vegetation"),
            (4, "Medium vegetation"),
            (5, "High vegetation"),
            (6, "Building"),
            (7, "Low point (noise)"),
            (8, "Model key / reserved"),
            (9, "Water"),
            (10, "Rail"),
            (11, "Road surface"),
            (12, "Overlap / reserved"),
            (13, "Wire guard"),
            (14, "Wire conductor"),
            (15, "Transmission tower"),
            (16, "Wire connector"),
            (17, "Bridge deck"),
            (18, "High noise"),
            (OtherClass, "Other (19 and up)")
        };

        public static readonly IReadOnlyCollection<int> AllClasses = Classes.Select(c => c.Code).ToArray();

        /// <summary>One-click choices for the class checkboxes.</summary>
        public static readonly IReadOnlyList<(string Name, IReadOnlyCollection<int> Codes)> Presets = new (string, IReadOnlyCollection<int>)[]
        {
            ("All", AllClasses),
            ("None", System.Array.Empty<int>()),
            ("Ground only", new[] { 2 }),
            ("Without noise", AllClasses.Where(c => c != 7 && c != 18).ToArray()),
            ("Vegetation", new[] { 3, 4, 5 }),
            ("Buildings", new[] { 6 })
        };

        /// <summary>One-click choices for the return checkboxes.</summary>
        public static readonly IReadOnlyList<(string Name, ReturnGroups Groups)> ReturnPresets = new (string, ReturnGroups)[]
        {
            ("All", ReturnGroups.All),
            ("First (incl. single)", ReturnGroups.Single | ReturnGroups.First),
            ("Last (incl. single)", ReturnGroups.Single | ReturnGroups.Last),
            ("Multiple only", ReturnGroups.First | ReturnGroups.Intermediate | ReturnGroups.Last)
        };

        private readonly HashSet<int> _classes;

        public PointFilter(IEnumerable<int> classes, ReturnGroups returns, bool excludeOverlap = false)
        {
            _classes = new HashSet<int>(classes ?? AllClasses);
            Returns = returns;
            ExcludeOverlap = excludeOverlap;
        }

        /// <summary>Keeps every point.</summary>
        public static PointFilter KeepAll => new PointFilter(AllClasses, ReturnGroups.All);

        public IReadOnlyCollection<int> SelectedClasses => _classes;
        public ReturnGroups Returns { get; }

        /// <summary>Drop points flagged as overlap (LAS 1.4: points from a swath overlap, flagged rather than
        /// classified; class 12 is the old LAS 1.1 overlap class and is empty in KyFromAbove).</summary>
        public bool ExcludeOverlap { get; }

        private bool AllClassesSelected => AllClasses.All(_classes.Contains);

        /// <summary>True when the filter drops anything at all.</summary>
        public bool IsActive => !AllClassesSelected || Returns != ReturnGroups.All || ExcludeOverlap;

        /// <summary>True when no class or no return group is ticked, so nothing would be left.</summary>
        public bool IsEmpty => _classes.Count == 0 || Returns == ReturnGroups.None;

        /// <summary>The "limits" option of PDAL's filters.range (the classification runs), or "" when every class is kept.</summary>
        public string PdalLimits()
        {
            if (IsEmpty) return "";
            var limits = new List<string>();
            if (!AllClassesSelected)
                limits.AddRange(Runs().Select(r => $"Classification[{r.Low}:{r.High}]"));
            if (ExcludeOverlap)
                limits.Add("Overlap[0:0]");
            return string.Join(",", limits);
        }

        /// <summary>The "expression" option of PDAL's filters.expression (the return groups), or "" when every
        /// return is kept.</summary>
        public string PdalExpression()
        {
            if (IsEmpty || Returns == ReturnGroups.All) return "";
            var clauses = new List<string>();
            if (Returns.HasFlag(ReturnGroups.Single)) clauses.Add("NumberOfReturns == 1");
            if (Returns.HasFlag(ReturnGroups.First)) clauses.Add("(ReturnNumber == 1 && NumberOfReturns > 1)");
            if (Returns.HasFlag(ReturnGroups.Intermediate)) clauses.Add("(ReturnNumber > 1 && ReturnNumber < NumberOfReturns)");
            if (Returns.HasFlag(ReturnGroups.Last)) clauses.Add("(ReturnNumber == NumberOfReturns && NumberOfReturns > 1)");
            return string.Join(" || ", clauses);
        }

        /// <summary>The return choice in words, for the pane header and the log.</summary>
        public static string DescribeReturns(ReturnGroups groups)
        {
            switch (groups)
            {
                case ReturnGroups.All: return "all returns";
                case ReturnGroups.Single | ReturnGroups.First: return "first returns (incl. single)";
                case ReturnGroups.Single | ReturnGroups.Last: return "last returns (incl. single)";
                case ReturnGroups.First | ReturnGroups.Intermediate | ReturnGroups.Last: return "multiple returns only";
                case ReturnGroups.None: return "no returns";
            }
            var names = new List<string>();
            if (groups.HasFlag(ReturnGroups.Single)) names.Add("single");
            if (groups.HasFlag(ReturnGroups.First)) names.Add("first of many");
            if (groups.HasFlag(ReturnGroups.Intermediate)) names.Add("intermediate");
            if (groups.HasFlag(ReturnGroups.Last)) names.Add("last of many");
            return string.Join(" + ", names) + " returns";
        }

        /// <summary>A short description for the pane header and the log: "Ground, Building; first return".</summary>
        public string Summary()
        {
            if (!IsActive) return "all points";
            var parts = new List<string>();
            if (!AllClassesSelected)
            {
                var names = Classes.Where(c => _classes.Contains(c.Code)).Select(c => c.Label).ToList();
                parts.Add(names.Count > 3 ? $"{names.Count} classes" : names.Count == 0 ? "no class" : string.Join(", ", names));
            }
            if (Returns != ReturnGroups.All)
                parts.Add(DescribeReturns(Returns));
            if (ExcludeOverlap)
                parts.Add("no overlap");
            return string.Join("; ", parts);
        }

        /// <summary>The chosen codes as (low, high) runs of neighbouring codes. The "other" box stands
        /// for everything from 19 up to 255.</summary>
        private List<(int Low, int High)> Runs()
        {
            var runs = new List<(int Low, int High)>();
            foreach (var code in _classes.OrderBy(c => c))
            {
                int high = code == OtherClass ? 255 : code;
                if (runs.Count > 0 && runs[^1].High + 1 == code)
                    runs[^1] = (runs[^1].Low, high);
                else
                    runs.Add((code, high));
            }
            return runs;
        }
    }
}
