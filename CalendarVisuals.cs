using System;
using System.Collections.Generic;
using System.Linq;

namespace LittleCalendar
{
    public sealed class CalendarBarSegment
    {
        public Todo Item { get; set; }
        public int Week { get; set; }
        public int StartColumn { get; set; }
        public int EndColumn { get; set; }
        public int Lane { get; set; }
        public bool ContinuesBefore { get; set; }
        public bool ContinuesAfter { get; set; }
    }

    public sealed class CalendarVisualLayout
    {
        public List<CalendarBarSegment> Segments { get; private set; }
        public Dictionary<string, int> Overflow { get; private set; }
        public CalendarVisualLayout() { Segments = new List<CalendarBarSegment>(); Overflow = new Dictionary<string, int>(); }
    }

    public static class CalendarVisuals
    {
        private sealed class Range
        {
            public Todo Item;
            public DateTime Start;
            public DateTime End;
        }

        private sealed class DayLane
        {
            public Range Range;
            public DateTime Day;
            public int Lane;
        }

        public static CalendarVisualLayout Build(IEnumerable<Todo> items, DateTime gridStart, DateTime today, int maxLanes)
        {
            var result = new CalendarVisualLayout();
            gridStart = gridStart.Date;
            DateTime gridEnd = gridStart.AddDays(41);
            int laneCount = Math.Max(1, maxLanes);
            var ranges = items.Where(x => x != null && !x.Deleted && x.Deadline != null).Select(x => {
                DateTime start = Deadlines.ParseInstant(x.Deadline.StartAt).LocalDateTime.Date;
                DateTime end = Deadlines.End(x.Deadline).LocalDateTime.Date;
                if (x.Completed) start = end;
                return new Range { Item = x, Start = start, End = end };
            }).Where(x => x.End >= gridStart && x.Start <= gridEnd && x.End >= x.Start)
              .OrderBy(x => x.Start).ThenByDescending(x => x.End).ThenBy(x => x.Item.Id).ToList();

            var laneRanges = new Range[laneCount];
            var visible = new List<DayLane>();
            for (DateTime day = gridStart; day <= gridEnd; day = day.AddDays(1)) {
                for (int lane = 0; lane < laneCount; lane++) {
                    if (laneRanges[lane] != null && laneRanges[lane].End < day) laneRanges[lane] = null;
                }
                var assigned = new HashSet<Range>(laneRanges.Where(x => x != null));
                foreach (Range waiting in ranges.Where(x => x.Start <= day && x.End >= day && !assigned.Contains(x))) {
                    int free = Array.FindIndex(laneRanges, x => x == null);
                    if (free < 0) {
                        string key = Dates.Key(day); int count; result.Overflow.TryGetValue(key, out count); result.Overflow[key] = count + 1;
                    } else {
                        laneRanges[free] = waiting;
                        assigned.Add(waiting);
                    }
                }
                for (int lane = 0; lane < laneCount; lane++) if (laneRanges[lane] != null)
                    visible.Add(new DayLane { Range = laneRanges[lane], Day = day, Lane = lane });
            }

            foreach (var group in visible.GroupBy(x => new { x.Range, x.Lane })) {
                List<DateTime> days = group.Select(x => x.Day).OrderBy(x => x).ToList();
                int offset = 0;
                while (offset < days.Count) {
                    DateTime segmentStart = days[offset], segmentEnd = segmentStart;
                    int week = (segmentStart - gridStart).Days / 7;
                    while (offset + 1 < days.Count && days[offset + 1] == segmentEnd.AddDays(1) && (days[offset + 1] - gridStart).Days / 7 == week) {
                        offset++; segmentEnd = days[offset];
                    }
                    result.Segments.Add(new CalendarBarSegment {
                        Item = group.Key.Range.Item, Week = week, Lane = group.Key.Lane,
                        StartColumn = (segmentStart - gridStart).Days % 7,
                        EndColumn = (segmentEnd - gridStart).Days % 7,
                        ContinuesBefore = segmentStart > group.Key.Range.Start,
                        ContinuesAfter = segmentEnd < group.Key.Range.End
                    });
                    offset++;
                }
            }
            List<CalendarBarSegment> ordered = result.Segments.OrderBy(x => x.Week).ThenBy(x => x.Lane).ThenBy(x => x.StartColumn).ToList();
            result.Segments.Clear(); result.Segments.AddRange(ordered);
            return result;
        }
    }

    public static class CalendarPalette
    {
        public static string DeadlineBackground(Todo item, DateTime now)
        {
            if (item == null || item.Deadline == null) return "#DCEFE8";
            if (item.Completed) return "#E1E7E4";
            DateTimeOffset instant = new DateTimeOffset(now), end = Deadlines.End(item.Deadline);
            if (instant >= end) return "#F3D7D2";
            if (!item.Deadline.Confirmed) return "#F7E7C4";
            return end - instant <= TimeSpan.FromHours(24) ? "#F5DEB7" : "#DCEFE8";
        }
        public static string DeadlineForeground(Todo item, DateTime now)
        {
            if (item == null || item.Deadline == null) return "#216E5B";
            if (item.Completed) return "#7B8C85";
            DateTimeOffset instant = new DateTimeOffset(now), end = Deadlines.End(item.Deadline);
            if (instant >= end) return "#9E4338";
            if (!item.Deadline.Confirmed || end - instant <= TimeSpan.FromHours(24)) return "#825A22";
            return "#216E5B";
        }
    }
}
