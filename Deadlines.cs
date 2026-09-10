using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LittleCalendar
{
    public sealed class DeadlineSpec
    {
        public string StartAt { get; set; }
        public int Amount { get; set; }
        public string Unit { get; set; }
        public bool Confirmed { get; set; }
        public string ManualEndAt { get; set; }
        public string OriginalText { get; set; }
        public DeadlineSpec Copy() { return (DeadlineSpec)MemberwiseClone(); }
    }

    public static class Deadlines
    {
        public static DateTimeOffset ParseInstant(string text)
        {
            DateTimeOffset result;
            if (text == null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out result) || result.Year < 1900 || result.Year > 9998)
                throw new InvalidDataException("请填写有效的起算／截止时间，并保留时区。");
            return result;
        }

        public static DateTimeOffset End(DeadlineSpec spec)
        {
            DateTimeOffset start = ParseInstant(spec.StartAt);
            if (spec.Amount < 1 || spec.Amount > (spec.Unit == "hours" ? 8760 : 365))
                throw new InvalidDataException("期限须为正整数：小时最多 8760，天数最多 365。");
            DateTimeOffset end;
            if (spec.Unit == "hours") end = start.AddHours(spec.Amount);
            else if (spec.Unit == "days") end = start.AddHours(spec.Amount * 24);
            else if (spec.Unit == "absolute") end = ParseInstant(spec.ManualEndAt);
            else if (spec.Unit == "workingDays") {
                if (!spec.Confirmed) throw new InvalidDataException("工作日可能涉及节假日，请人工核实截止时间并勾选确认。");
                end = ParseInstant(spec.ManualEndAt);
            } else throw new InvalidDataException("不支持的期限单位。");
            if (end <= start || end.Year > 9998) throw new InvalidDataException("截止时间必须晚于起算时间，且在有效日期范围内。");
            return end;
        }

        public static void Normalize(Todo item)
        {
            if (item.Deadline == null) return;
            DateTime end = End(item.Deadline).LocalDateTime;
            item.Date = Dates.Key(end); item.Time = end.ToString("HH:mm", CultureInfo.InvariantCulture);
            item.Deadline.OriginalText = item.Deadline.OriginalText ?? "";
        }

        public static bool OnDay(Todo item, DateTime day, DateTime today)
        {
            if (item.Deadline == null || item.Completed || item.Deleted) return item.Date == Dates.Key(day);
            DateTime start = ParseInstant(item.Deadline.StartAt).LocalDateTime.Date;
            DateTime end = End(item.Deadline).LocalDateTime.Date;
            return (day.Date >= start && day.Date <= end) || (day.Date == today.Date && end < today.Date);
        }

        public static string Status(Todo item, DateTime now)
        {
            if (item.Completed) return "已完成";
            DateTimeOffset current = new DateTimeOffset(now);
            DateTimeOffset end = End(item.Deadline);
            if (current < ParseInstant(item.Deadline.StartAt)) return "尚未开始";
            bool expired = current >= end;
            var span = TimeSpan.FromMinutes(Math.Ceiling(Math.Abs((end - current).TotalMinutes)));
            string duration = (span.Days > 0 ? span.Days + " 天 " : "") + (span.Hours > 0 ? span.Hours + " 小时 " : "") + span.Minutes + " 分钟";
            return (item.Deadline.Confirmed ? "" : "预估 · ") + (expired ? "已逾期 " : "剩余 ") + duration;
        }

        public static string Description(Todo item, DateTime now)
        {
            string end = End(item.Deadline).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            return (item.Deadline.Confirmed ? "截止 " : "预计截止 ") + end + "\n" + Status(item, now) +
                (item.Deadline.Confirmed ? "" : " · 待确认（按每自然日 24 小时估算）");
        }

        public static string Source(DeadlineSpec spec)
        {
            if (spec.Unit == "absolute")
                return "有效期 " + ParseInstant(spec.StartAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm") + " 至 " + End(spec).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            return "起算 " + ParseInstant(spec.StartAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm") + " · " + spec.Amount +
                (spec.Unit == "hours" ? " 小时内" : spec.Unit == "days" ? " 天内" : " 个工作日内（人工核实）");
        }
    }
}
