using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LittleCalendar
{
    public enum ChatIntentKind { SyncMailIncremental, ListTasks, Chat }
    public enum TaskQueryRange { Today, Tomorrow, SevenDays, FourteenDays }

    public sealed class ChatIntent
    {
        public ChatIntentKind Kind;
        public TaskQueryRange Range;
    }

    public sealed class TaskQueryResult
    {
        public List<Todo> Overdue;
        public List<Todo> Due;
        public string DeterministicText;

        public TaskQueryResult()
        {
            Overdue = new List<Todo>();
            Due = new List<Todo>();
            DeterministicText = "";
        }
    }

    public static class ChatIntentRouter
    {
        public static ChatIntent Parse(string text)
        {
            string input = Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", "");
            if (ContainsAny(input, "读取新邮件", "读新邮件", "查新邮件", "收取一下新邮件", "同步一下邮箱", "同步一下收件箱", "同步邮箱", "邮箱同步"))
                return new ChatIntent { Kind = ChatIntentKind.SyncMailIncremental, Range = TaskQueryRange.Today };
            if (ContainsAny(input, "明天截止", "明天要做什么", "明天有什么任务", "明天有哪些截止事项", "明天任务"))
                return new ChatIntent { Kind = ChatIntentKind.ListTasks, Range = TaskQueryRange.Tomorrow };
            if (ContainsAny(input, "未来七天", "未来7天", "七天内", "7天内", "最近七天", "接下来一周"))
                return new ChatIntent { Kind = ChatIntentKind.ListTasks, Range = TaskQueryRange.SevenDays };
            if (ContainsAny(input, "今天要做什么", "今天有什么任务", "今天有哪些安排", "今天任务", "今天截止"))
                return new ChatIntent { Kind = ChatIntentKind.ListTasks, Range = TaskQueryRange.Today };
            return new ChatIntent { Kind = ChatIntentKind.Chat, Range = TaskQueryRange.Today };
        }

        private static bool ContainsAny(string input, params string[] phrases)
        {
            return phrases.Any(phrase => input.Contains(phrase));
        }
    }

    public static class TaskQueryService
    {
        private sealed class QueryItem
        {
            public Todo Todo;
            public DateTime DueDate;
            public DateTimeOffset DueInstant;
            public bool IsOverdue;
        }

        public static TaskQueryResult Query(CalendarData data, DateTime now, TaskQueryRange range)
        {
            DateTimeOffset current = new DateTimeOffset(now);
            DateTime start;
            DateTime end;
            RangeBounds(now.Date, range, out start, out end);
            var overdue = new List<QueryItem>();
            var due = new List<QueryItem>();
            foreach (Todo todo in (data == null ? new List<Todo>() : data.Items ?? new List<Todo>())) {
                QueryItem item;
                if (todo == null || todo.Completed || todo.Deleted || !TryCreate(todo, current, out item)) continue;
                if (item.IsOverdue) overdue.Add(item);
                else if (item.DueDate >= start && item.DueDate <= end) due.Add(item);
            }
            var orderedOverdue = Sort(overdue);
            var orderedDue = Sort(due);
            var selected = orderedOverdue.Concat(orderedDue).Take(50).ToList();
            var result = new TaskQueryResult {
                Overdue = selected.Where(item => item.IsOverdue).Select(item => item.Todo).ToList(),
                Due = selected.Where(item => !item.IsOverdue).Select(item => item.Todo).ToList()
            };
            result.DeterministicText = Describe(selected);
            return result;
        }

        private static bool TryCreate(Todo todo, DateTimeOffset current, out QueryItem result)
        {
            result = null;
            try {
                DateTime date;
                DateTimeOffset instant;
                bool overdue;
                if (todo.Deadline != null) {
                    instant = Deadlines.End(todo.Deadline);
                    date = instant.LocalDateTime.Date;
                    overdue = instant < current;
                } else {
                    date = Dates.Parse(todo.Date).Date;
                    bool hasTime = Dates.IsTime(todo.Time);
                    DateTime scheduled = hasTime ? date.Add(Dates.Time(todo.Time)) : date;
                    instant = new DateTimeOffset(scheduled);
                    overdue = hasTime ? instant < current : date < current.LocalDateTime.Date;
                }
                result = new QueryItem { Todo = todo, DueDate = date, DueInstant = instant, IsOverdue = overdue };
                return true;
            } catch (ArgumentException) { return false; }
              catch (FormatException) { return false; }
              catch (System.IO.InvalidDataException) { return false; }
        }

        private static List<QueryItem> Sort(IEnumerable<QueryItem> items)
        {
            return items.OrderBy(item => item.DueInstant)
                .ThenByDescending(item => item.Todo.Important)
                .ThenBy(item => item.Todo.Title ?? "", StringComparer.Ordinal)
                .ThenBy(item => item.Todo.Id ?? "", StringComparer.Ordinal)
                .ToList();
        }

        private static void RangeBounds(DateTime today, TaskQueryRange range, out DateTime start, out DateTime end)
        {
            start = today.Date;
            if (range == TaskQueryRange.Tomorrow) start = start.AddDays(1);
            end = range == TaskQueryRange.Today || range == TaskQueryRange.Tomorrow ? start :
                start.AddDays(range == TaskQueryRange.FourteenDays ? 13 : 6);
        }

        private static string Describe(IEnumerable<QueryItem> items)
        {
            var text = new StringBuilder();
            foreach (QueryItem item in items) {
                if (text.Length > 0) text.Append('\n');
                text.Append(item.IsOverdue ? "逾期" : "待办");
                text.Append(" · ");
                text.Append(item.DueInstant.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                text.Append(" · ");
                text.Append(item.Todo.Title ?? "");
            }
            return text.ToString();
        }
    }
}
