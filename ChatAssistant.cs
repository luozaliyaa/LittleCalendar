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
        internal List<TaskQueryRow> Rows;

        public TaskQueryResult()
        {
            Overdue = new List<Todo>();
            Due = new List<Todo>();
            DeterministicText = "";
            Rows = new List<TaskQueryRow>();
        }
    }

    internal sealed class TaskQueryRow
    {
        public string TodoId;
        public string Text;
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
            result.Rows = selected.Select(item => new TaskQueryRow { TodoId = item.Todo.Id, Text = Describe(new[] { item }) }).ToList();
            result.DeterministicText = String.Join("\n", result.Rows.Select(row => row.Text));
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
                text.Append(Regex.Replace(item.Todo.Title ?? "", @"[\r\n]+", " "));
            }
            return text.ToString();
        }
    }

    public sealed class ChatAssistantService
    {
        private readonly CalendarController controller;
        private readonly SecretStore secrets;
        private readonly IChatLanguageAgent languageAgent;
        private readonly MailSyncCoordinator mailSync;
        private readonly MailStateStore mailState;
        private readonly ChatHistoryStore history;
        private readonly object gate = new object();

        public ChatAssistantService(CalendarController controller, SecretStore secrets, IChatLanguageAgent languageAgent,
            MailSyncCoordinator mailSync, MailStateStore mailState, ChatHistoryStore history)
        {
            this.controller = controller; this.secrets = secrets; this.languageAgent = languageAgent;
            this.mailSync = mailSync; this.mailState = mailState; this.history = history;
        }

        public ChatMessage Handle(string text, DateTime now)
        {
            lock (gate) {
                string apiKey = LoadSecret(secrets);
                string mailKey = secrets == null ? "" : LoadSecret(new MailSecretStore(System.IO.Path.GetDirectoryName(secrets.FilePath)));
                var privateValues = new List<string> { apiKey, mailKey };
                CalendarData snapshot = controller.Store.Copy(controller.Data);
                foreach (Todo item in snapshot.Items.Where(item => item.EmailSource != null)) {
                    privateValues.Add(item.EmailSource.Subject); privateValues.Add(item.EmailSource.Sender);
                    privateValues.Add(item.Notes);
                }
                Func<string, int, string> clean = (value, limit) => CleanKnown(value, privateValues, limit);
                string question = clean(text, 2000);
                ChatIntent intent = ChatIntentRouter.Parse(question);
                string intentName = intent.Kind == ChatIntentKind.SyncMailIncremental ? "sync_mail_incremental" : intent.Kind == ChatIntentKind.ListTasks ? "list_tasks" : "chat";
                string timestamp = new DateTimeOffset(now).ToString("o", CultureInfo.InvariantCulture);
                history.Append(ChatMessage.FromDisplay("user", ChatDisplay.UserInput(question), timestamp, intentName));
                ChatMessage response;
                if (intent.Kind == ChatIntentKind.SyncMailIncremental) response = Sync(timestamp);
                else {
                    foreach (Todo item in snapshot.Items) {
                        item.Title = clean(item.Title, 4000);
                        item.Notes = item.EmailSource == null ? clean(item.Notes, 500) : "";
                        item.EmailSource = null;
                    }
                    TaskQueryRange range = intent.Kind == ChatIntentKind.ListTasks ? intent.Range : TaskQueryRange.FourteenDays;
                    TaskQueryResult facts = TaskQueryService.Query(snapshot, now, range);
                    List<string> ids;
                    string local = BoundedLocalText(facts, intent.Kind == ChatIntentKind.Chat, out ids);
                    ChatDisplay display = ChatDisplay.LocalSummary(local);
                    List<string> queryIds = facts.Overdue.Concat(facts.Due).Select(item => item.Id).ToList();
                    if (languageAgent != null && !String.IsNullOrWhiteSpace(apiKey)) {
                        try {
                            ChatLanguageRequest request = CreateRequest(facts, question, now, range);
                            ChatLanguageReply reply = languageAgent.Reply(request, apiKey, snapshot.Agent.Model);
                            if (reply == null || String.IsNullOrWhiteSpace(reply.Answer)) throw new System.IO.InvalidDataException();
                            display = ChatDisplay.AssistantAnswer(clean(reply.Answer, 4000));
                            ids = reply.TodoIds.Where(id => queryIds.Contains(id)).Distinct(StringComparer.Ordinal).Take(50).ToList();
                        } catch { /* Deterministic facts remain available; never persist exception text. */ }
                    }
                    response = ChatMessage.FromDisplay("assistant", display, timestamp, intentName, ids);
                }
                history.Append(response);
                return response;
            }
        }

        private static string BoundedLocalText(TaskQueryResult facts, bool generalChat, out List<string> ids)
        {
            const int budget = 4000;
            ids = new List<string>();
            var text = new StringBuilder();
            if (generalChat) text.Append("可以询问今天、明天或未来七天的待办，也可以输入“读取新邮件”。\n");
            if (facts.Rows.Count == 0) return text.Append("当前范围内暂无待办。").ToString();
            foreach (TaskQueryRow row in facts.Rows) {
                int remaining = facts.Rows.Count - ids.Count - 1;
                string separator = ids.Count == 0 ? "" : "\n";
                string notice = remaining == 0 ? "" : "\n还有 " + remaining + " 项未展开";
                if (text.Length + separator.Length + row.Text.Length + notice.Length > budget) break;
                text.Append(separator).Append(row.Text); ids.Add(row.TodoId);
            }
            int omitted = facts.Rows.Count - ids.Count;
            if (omitted > 0) {
                if (ids.Count > 0) text.Append('\n');
                text.Append("还有 " + omitted + " 项未展开");
            }
            return text.ToString();
        }

        private ChatMessage Sync(string timestamp)
        {
            var summary = new ChatSyncSummary { StartedAt = timestamp };
            string text;
            try {
                if (mailSync == null || mailState == null) throw new InvalidOperationException();
                summary.PreviousCompletedAt = mailState.Load().LastCompletedAt;
                MailSyncResult result = mailSync.RunIncremental();
                MailSyncState state = mailState.Load();
                summary.StartedAt = state.LastStartedAt; summary.CompletedAt = state.LastCompletedAt;
                summary.ScannedCount = result.ScannedCount; summary.CreatedCount = result.CreatedCount;
                summary.NoticeCount = result.NoticeCount; summary.ErrorCount = result.Errors.Count;
                // The sync result does not distinguish ignored mail from duplicates or failures.
                // Leave the legacy ignored-count field unset rather than inventing a classification.
                int number = 0;
                foreach (MailFolderSyncSummary folder in result.Folders.Take(50)) {
                    number++;
                    summary.Folders.Add(new ChatFolderCursorSummary {
                        DisplayName = "文件夹 " + number, PreviousScannedAt = folder.PreviousScannedAt, CompletedAt = folder.CompletedAt,
                        PreviousUid = folder.PreviousUid, RequestedMinimumUid = folder.RequestedMinimumUid, FinalUid = folder.FinalUid,
                        FetchedCount = folder.FetchedCount, Error = String.IsNullOrWhiteSpace(folder.Error) ? "" : "同步出错"
                    });
                }
                var lines = new StringBuilder();
                lines.Append(summary.ErrorCount == 0 && summary.Folders.All(folder => folder.Error.Length == 0) ? "邮箱增量同步完成。" : "邮箱增量同步部分完成，请重试失败的文件夹。");
                lines.Append("\n上次完成：" + TimestampLabel(summary.PreviousCompletedAt));
                lines.Append("\n开始：" + TimestampLabel(summary.StartedAt) + "；完成：" + TimestampLabel(summary.CompletedAt));
                lines.Append("\n扫描 " + summary.ScannedCount + " 封；新增待办 " + summary.CreatedCount + " 项；通知 " + summary.NoticeCount + " 条；错误 " + summary.ErrorCount + " 项。");
                foreach (ChatFolderCursorSummary folder in summary.Folders) {
                    lines.Append("\n" + folder.DisplayName + "：上次扫描 " + TimestampLabel(folder.PreviousScannedAt) + "；完成 " + TimestampLabel(folder.CompletedAt));
                    lines.Append("；UID " + folder.PreviousUid + " → 请求 " + folder.RequestedMinimumUid + " → " + folder.FinalUid + "；读取 " + folder.FetchedCount + " 封");
                    if (folder.Error.Length > 0) lines.Append("；同步出错，请重试");
                }
                text = lines.ToString();
            } catch {
                summary.ErrorCount = Math.Max(1, summary.ErrorCount);
                text = "邮箱增量同步未完成，请检查邮箱设置、授权码和网络后重试。\n开始：" + timestamp;
            }
            return ChatMessage.FromDisplay("assistant", ChatDisplay.LocalSummary(text), timestamp, "sync_mail_incremental", sync: summary);
        }

        private static string TimestampLabel(string value) { return String.IsNullOrWhiteSpace(value) ? "无记录" : value; }
        private static string LoadSecret(ProtectedSecretStore store) { try { return store == null ? "" : store.Load(); } catch { return ""; } }
        private static string CleanKnown(string value, IEnumerable<string> privateValues, int limit)
        {
            value = value ?? "";
            foreach (string secret in privateValues.Where(item => !String.IsNullOrWhiteSpace(item)).OrderByDescending(item => item.Length))
                value = value.Replace(secret, "[REDACTED]");
            return ChatDisplay.Clean(value, limit);
        }
        private static ChatLanguageRequest CreateRequest(TaskQueryResult facts, string question, DateTime now, TaskQueryRange range)
        {
            var request = new ChatLanguageRequest {
                CurrentTime = new DateTimeOffset(now).ToString("o", CultureInfo.InvariantCulture), Timezone = TimeZoneInfo.Local.Id,
                Question = question, RangeLabel = range == TaskQueryRange.Today ? "今天（含逾期）" : range == TaskQueryRange.Tomorrow ? "明天（含逾期）" : range == TaskQueryRange.SevenDays ? "未来七天（含逾期）" : "未来十四天（含逾期）"
            };
            foreach (Todo item in facts.Overdue.Concat(facts.Due).Take(50)) {
                DateTimeOffset due = item.Deadline != null ? Deadlines.End(item.Deadline) :
                    new DateTimeOffset(Dates.Parse(item.Date).Add(Dates.IsTime(item.Time) ? Dates.Time(item.Time) : TimeSpan.Zero));
                request.Items.Add(new ChatLanguageItem { Id = item.Id, Title = ChatDisplay.Clean(item.Title, 200), DueInstant = due.ToString("o", CultureInfo.InvariantCulture),
                    Important = item.Important, DeadlineConfirmed = item.Deadline == null || item.Deadline.Confirmed, NoteExcerpt = item.Notes });
            }
            return request;
        }
    }
}
