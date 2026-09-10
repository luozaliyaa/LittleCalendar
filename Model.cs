using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LittleCalendar
{
    public sealed class Todo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public string Date { get; set; }
        public string Time { get; set; }
        public bool Important { get; set; }
        public bool Completed { get; set; }
        public bool Deleted { get; set; }
        public bool Remind { get; set; }
        public string DeliveredKey { get; set; }
        public string SnoozedUntil { get; set; }
        public string CompletedAt { get; set; }
        public DeadlineSpec Deadline { get; set; }
        public EmailSource EmailSource { get; set; }
        public Todo()
        {
            Id = Guid.NewGuid().ToString("N"); Title = ""; Notes = "";
            Date = Dates.Key(DateTime.Today); Time = ""; Remind = true;
            DeliveredKey = ""; SnoozedUntil = ""; CompletedAt = "";
        }
        public Todo Copy()
        {
            var copy = (Todo)MemberwiseClone();
            copy.Deadline = Deadline == null ? null : Deadline.Copy();
            copy.EmailSource = EmailSource == null ? null : EmailSource.Copy();
            return copy;
        }
    }

    public sealed class CalendarData
    {
        public int Version { get; set; }
        public string ReminderTime { get; set; }
        public bool Sound { get; set; }
        public List<Todo> Items { get; set; }
        public List<OpportunityNotice> Notices { get; set; }
        public AgentSettings Agent { get; set; }
        public CalendarData() { Version = 3; ReminderTime = "19:00"; Sound = true; Items = new List<Todo>(); Notices = new List<OpportunityNotice>(); Agent = new AgentSettings(); }
    }

    public static class Dates
    {
        public static string Key(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static DateTime Parse(string date) { return DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static bool IsDate(string date)
        {
            DateTime parsed;
            return DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }
        public static bool IsTime(string text)
        {
            DateTime parsed;
            return DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }
        public static TimeSpan Time(string time) { return DateTime.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture).TimeOfDay; }
        public static DateTime GridStart(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        }
        public static string Relative(DateTime date, DateTime today)
        {
            int days = (date.Date - today.Date).Days;
            return days == 0 ? "今天" : days == 1 ? "明天" : days == -1 ? "昨天" : date.ToString("M月d日");
        }
    }

    public static class Reminders
    {
        public static string Key(CalendarData data, Todo item) { return item.Deadline == null ? item.Date + "@" + data.ReminderTime : "deadline@" + Deadlines.End(item.Deadline).UtcTicks + "@" + item.Deadline.Confirmed; }
        public static List<Todo> Due(CalendarData data, DateTime now)
        {
            return data.Items.Where(item => {
                if (item.Completed || item.Deleted || !item.Remind || !Dates.IsDate(item.Date)) return false;
                DateTime date = Dates.Parse(item.Date);
                if (item.Deadline != null) {
                    DateTimeOffset instant = new DateTimeOffset(now), end = Deadlines.End(item.Deadline);
                    if (instant >= end || instant < end.AddHours(-24) || instant < Deadlines.ParseInstant(item.Deadline.StartAt)) return false;
                } else if (now.Date > date || now < date.AddDays(-1).Add(Dates.Time(data.ReminderTime))) return false;
                if (!String.IsNullOrEmpty(item.SnoozedUntil)) {
                    DateTime until;
                    return DateTime.TryParse(item.SnoozedUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until) && now >= until;
                }
                return item.DeliveredKey != Key(data, item);
            }).OrderBy(item => item.Date).ThenBy(item => item.Time).ToList();
        }
        public static void MarkShown(CalendarData data, IEnumerable<Todo> items)
        {
            foreach (Todo item in items) { item.DeliveredKey = Key(data, item); item.SnoozedUntil = ""; }
        }
        public static void Snooze(IEnumerable<Todo> items, DateTime now)
        {
            foreach (Todo item in items) item.SnoozedUntil = now.AddMinutes(10).ToString("o", CultureInfo.InvariantCulture);
        }
    }

    public sealed class CalendarController
    {
        public CalendarStore Store { get; private set; }
        public CalendarData Data { get; private set; }
        public event Action Changed;
        public CalendarController(CalendarStore store) { Store = store; Data = store.Load(); }
        public void Commit(Action<CalendarData> change)
        {
            CalendarData next = Store.Copy(Data);
            change(next);
            Store.Save(next);
            Data = next;
            if (Changed != null) Changed();
        }
        public void SaveTodo(Todo edited)
        {
            edited = edited.Copy();
            Deadlines.Normalize(edited);
            if (String.IsNullOrWhiteSpace(edited.Title)) throw new ArgumentException("请先填写待办内容。");
            if (edited.Title.Trim().Length > 200) throw new ArgumentException("标题请控制在 200 字以内，详细内容可写在备注里。");
            if (!Dates.IsDate(edited.Date)) throw new ArgumentException("请选择有效的日期。");
            if (!String.IsNullOrEmpty(edited.Time) && !Dates.IsTime(edited.Time)) throw new ArgumentException("时间请填写为 09:30 这样的格式，也可以留空。");
            Commit(next => {
                Todo item = edited.Copy(); item.Title = item.Title.Trim();
                Todo old = next.Items.Find(x => x.Id == item.Id);
                if (old != null) {
                    if (Reminders.Key(next, old) != Reminders.Key(next, item) || old.Remind != item.Remind) { item.DeliveredKey = ""; item.SnoozedUntil = ""; }
                    next.Items.Remove(old);
                }
                next.Items.Add(item);
            });
        }
        public void Complete(string id, bool complete) { Complete(id, complete, DateTime.Now); }
        public void Complete(string id, bool complete, DateTime now)
        {
            Commit(next => {
                Todo item = next.Items.First(x => x.Id == id);
                item.Completed = complete;
                item.CompletedAt = complete ? new DateTimeOffset(now).ToString("o", CultureInfo.InvariantCulture) : "";
                if (complete) item.SnoozedUntil = "";
                if (item.EmailSource != null) {
                    item.EmailSource.CompletionSyncState = complete ? "pending" : "none";
                    item.EmailSource.CompletionSyncedAt = "";
                    item.EmailSource.CompletionSyncError = "";
                }
            });
        }
        public Todo FindByEmailKey(string key)
        {
            Todo item = Data.Items.FirstOrDefault(x => x.EmailSource != null && String.Equals(x.EmailSource.EmailKey, key, StringComparison.Ordinal));
            return item == null ? null : item.Copy();
        }
        public bool AddMailTodo(Todo todo)
        {
            if (todo == null || todo.EmailSource == null || String.IsNullOrWhiteSpace(todo.EmailSource.EmailKey))
                throw new ArgumentException("邮件待办缺少来源标识。");
            bool added = false;
            Todo candidate = todo.Copy();
            Deadlines.Normalize(candidate);
            if (String.IsNullOrWhiteSpace(candidate.Title) || !Dates.IsDate(candidate.Date))
                throw new ArgumentException("邮件待办内容无效。");
            Commit(next => {
                if (next.Items.Any(x => x.EmailSource != null && String.Equals(x.EmailSource.EmailKey, candidate.EmailSource.EmailKey, StringComparison.Ordinal))) return;
                if (next.Items.Any(x => MailTodoDeduplication.IsDuplicate(x, candidate))) return;
                next.Items.Add(candidate.Copy());
                added = true;
            });
            return added;
        }
        public bool AddOpportunityNotice(OpportunityNotice notice)
        {
            if (notice == null || notice.EmailSource == null || String.IsNullOrWhiteSpace(notice.EmailSource.EmailKey) || String.IsNullOrWhiteSpace(notice.Title))
                throw new ArgumentException("机会通知缺少有效的邮件来源。");
            bool added = false; OpportunityNotice candidate = notice.Copy(); candidate.Title = candidate.Title.Trim();
            Commit(next => {
                if (next.Notices.Any(x => x.EmailSource != null && String.Equals(x.EmailSource.EmailKey, candidate.EmailSource.EmailKey, StringComparison.Ordinal))) return;
                string normalized = NormalizeNoticeTitle(candidate.Title);
                if (next.Notices.Any(x => String.Equals(NormalizeNoticeTitle(x.Title), normalized, StringComparison.Ordinal))) return;
                next.Notices.Add(candidate.Copy()); added = true;
            });
            return added;
        }
        public void ReadOpportunity(string id, bool read) { Commit(next => next.Notices.First(x => x.Id == id).Read = read); }
        public void DeleteOpportunity(string id) { Commit(next => next.Notices.RemoveAll(x => x.Id == id)); }
        private static string NormalizeNoticeTitle(string value)
        {
            string normalized = MailTodoDeduplication.Normalize(value);
            foreach (string term in new[] { "智联推荐", "邀请投递", "邀您投递", "诚邀投递", "一键投递", "职位推荐", "岗位推荐" }) normalized = normalized.Replace(term.ToLowerInvariant(), "");
            return normalized;
        }
        public void Trash(string id, bool deleted) { Commit(next => { Todo item = next.Items.First(x => x.Id == id); item.Deleted = deleted; }); }
        public void RestoreBackup(CalendarData imported) { Store.Save(imported); Data = imported; if (Changed != null) Changed(); }
    }

    public sealed class CalendarStore
    {
        public string FilePath { get; private set; }
        public string LoadWarning { get; private set; }
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public CalendarStore(string directory) { FilePath = Path.Combine(directory, "calendar.json"); LoadWarning = ""; }
        public CalendarData Load()
        {
            if (!File.Exists(FilePath)) return new CalendarData();
            try { return Decode(File.ReadAllText(FilePath, Encoding.UTF8)); }
            catch (Exception primaryError) {
                string backup = FilePath + ".bak";
                if (File.Exists(backup)) {
                    try {
                        CalendarData recovered = Decode(File.ReadAllText(backup, Encoding.UTF8));
                        File.Copy(FilePath, FilePath + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmss"), false);
                        LoadWarning = "主数据文件异常，已读取上次备份；原文件已保留。";
                        return recovered;
                    } catch (Exception backupError) { throw new IOException("主数据和备份均无法读取。请保留数据目录，不要覆盖原文件。", new AggregateException(primaryError, backupError)); }
                }
                throw new IOException("无法读取日历资料，原文件未改动：" + FilePath, primaryError);
            }
        }
        public CalendarData Decode(string json)
        {
            CalendarData data = serializer.Deserialize<CalendarData>(json);
            if (data == null || (data.Version != 1 && data.Version != 2 && data.Version != 3) || data.Items == null || !Dates.IsTime(data.ReminderTime)) throw new InvalidDataException("不支持或损坏的数据格式。");
            data.Version = 3;
            if (data.Agent == null) data.Agent = new AgentSettings();
            if (data.Notices == null) data.Notices = new List<OpportunityNotice>();
            data.Agent.DailyTime = String.IsNullOrWhiteSpace(data.Agent.DailyTime) ? "09:00" : data.Agent.DailyTime;
            data.Agent.Model = String.IsNullOrWhiteSpace(data.Agent.Model) ? "deepseek-v4-flash" : data.Agent.Model.Trim();
            data.Agent.LastAutomaticDate = data.Agent.LastAutomaticDate ?? ""; data.Agent.LastSummaryAt = data.Agent.LastSummaryAt ?? "";
            if (!Dates.IsTime(data.Agent.DailyTime) || (!String.IsNullOrEmpty(data.Agent.LastAutomaticDate) && !Dates.IsDate(data.Agent.LastAutomaticDate))) throw new InvalidDataException("智能整理设置格式无效。");
            var ids = new HashSet<string>();
            foreach (Todo item in data.Items) {
                if (item != null) Deadlines.Normalize(item);
                if (item == null || String.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || String.IsNullOrWhiteSpace(item.Title) || !Dates.IsDate(item.Date) || (!String.IsNullOrEmpty(item.Time) && !Dates.IsTime(item.Time))) throw new InvalidDataException("待办条目格式无效。");
                item.Notes = item.Notes ?? ""; item.Time = item.Time ?? ""; item.CompletedAt = item.CompletedAt ?? "";
                DateTimeOffset completedAt;
                if (!String.IsNullOrEmpty(item.CompletedAt) && !DateTimeOffset.TryParse(item.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out completedAt))
                    throw new InvalidDataException("待办完成时间格式无效。");
                NormalizeEmailSource(item.EmailSource);
            }
            var noticeIds = new HashSet<string>();
            foreach (OpportunityNotice notice in data.Notices) {
                if (notice == null || String.IsNullOrWhiteSpace(notice.Id) || !noticeIds.Add(notice.Id) || String.IsNullOrWhiteSpace(notice.Title)) throw new InvalidDataException("机会通知格式无效。");
                notice.Summary = notice.Summary ?? ""; notice.ReceivedAt = notice.ReceivedAt ?? "";
                NormalizeEmailSource(notice.EmailSource);
            }
            return data;
        }
        private static void NormalizeEmailSource(EmailSource source)
        {
            if (source == null) return;
            source.EmailKey = source.EmailKey ?? ""; source.AccountId = source.AccountId ?? ""; source.FolderId = source.FolderId ?? "";
            source.MessageId = source.MessageId ?? ""; source.Fingerprint = source.Fingerprint ?? ""; source.Subject = source.Subject ?? "";
            source.Sender = source.Sender ?? ""; source.SentAt = source.SentAt ?? ""; source.DeadlineOriginalText = source.DeadlineOriginalText ?? ""; source.ActionCategory = source.ActionCategory ?? "";
            source.ImportSyncState = String.IsNullOrWhiteSpace(source.ImportSyncState) ? "pending" : source.ImportSyncState;
            source.ImportSyncError = source.ImportSyncError ?? "";
            source.CompletionSyncState = String.IsNullOrWhiteSpace(source.CompletionSyncState) ? "none" : source.CompletionSyncState;
            source.CompletionSyncedAt = source.CompletionSyncedAt ?? ""; source.CompletionSyncError = source.CompletionSyncError ?? "";
            string[] allowed = { "none", "pending", "synced", "source-not-found", "failed" };
            if (String.IsNullOrWhiteSpace(source.EmailKey) || !allowed.Contains(source.CompletionSyncState) ||
                !new[] { "pending", "synced", "failed" }.Contains(source.ImportSyncState))
                throw new InvalidDataException("邮件来源格式无效。");
        }
        public void Save(CalendarData data)
        {
            string json = serializer.Serialize(Decode(serializer.Serialize(data)));
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            string temporary = FilePath + ".tmp";
            WriteFlushed(temporary, json);
            if (File.Exists(FilePath)) {
                CalendarData upgradedPrevious = null;
                bool previousValid = false;
                string priorJson = File.ReadAllText(FilePath, Encoding.UTF8);
                try {
                    CalendarData raw = serializer.Deserialize<CalendarData>(priorJson);
                    CalendarData previous = Decode(priorJson);
                    previousValid = true;
                    if (raw.Version == 1 || raw.Version == 2) upgradedPrevious = previous;
                } catch (Exception error) {
                    // Only parse/validation failures are recoverable here. File I/O
                    // stays outside this catch so failed access never looks like success.
                    if (!(error is ArgumentException || error is InvalidDataException || error is InvalidOperationException || error is FormatException || error is OverflowException)) throw;
                }
                if (upgradedPrevious != null) {
                    // Old executables recover a v1 .bak after rejecting v2. Upgrade that
                    // fallback BEFORE replacing the primary, and archive v1 separately.
                    File.Copy(FilePath, FilePath + ".pre-v3-" + Guid.NewGuid().ToString("N"), false);
                    string backup = FilePath + ".bak", backupTemporary = backup + ".tmp";
                    WriteFlushed(backupTemporary, serializer.Serialize(upgradedPrevious));
                    if (File.Exists(backup)) File.Replace(backupTemporary, backup, null, true);
                    else File.Move(backupTemporary, backup);
                    File.Replace(temporary, FilePath, null, true);
                } else if (!previousValid) {
                    File.Copy(FilePath, FilePath + ".damaged-" + Guid.NewGuid().ToString("N"), false);
                    UpgradeFallback();
                    File.Replace(temporary, FilePath, null, true);
                } else File.Replace(temporary, FilePath, FilePath + ".bak", true);
            }
            else { UpgradeFallback(); File.Move(temporary, FilePath); }
        }
        private void UpgradeFallback()
        {
            string backup = FilePath + ".bak";
            if (!File.Exists(backup)) return;
            string json = File.ReadAllText(backup, Encoding.UTF8);
            CalendarData upgraded;
            try {
                var raw = serializer.Deserialize<CalendarData>(json);
                if (raw == null || (raw.Version != 1 && raw.Version != 2)) return;
                upgraded = Decode(json);
            } catch (Exception error) {
                if (!(error is ArgumentException || error is InvalidDataException || error is InvalidOperationException || error is FormatException || error is OverflowException)) throw;
                // A damaged fallback must not remain accessible to an older reader.
                // Preserve its bytes separately; use the validated pending v2 primary
                // as the new fallback in this exceptional recovery case.
                File.Copy(backup, backup + ".damaged-" + Guid.NewGuid().ToString("N"), false);
                upgraded = Decode(File.ReadAllText(FilePath + ".tmp", Encoding.UTF8));
            }
            File.Copy(backup, backup + ".pre-v3-" + Guid.NewGuid().ToString("N"), false);
            WriteFlushed(backup + ".tmp", serializer.Serialize(upgraded));
            File.Replace(backup + ".tmp", backup, null, true);
        }
        private static void WriteFlushed(string path, string json)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
                byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
        }
        public void Export(CalendarData data, string destination) { File.WriteAllText(destination, serializer.Serialize(data), new UTF8Encoding(false)); }
        public CalendarData Copy(CalendarData data) { return Decode(serializer.Serialize(data)); }
    }
}
