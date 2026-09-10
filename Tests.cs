using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using LittleCalendar;

internal static class CalendarTests
{
    private static int passed, failed;
    private static readonly List<string> results = new List<string>();
    private static string root;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; results.Add("PASS " + name); }
        catch (Exception error) { failed++; results.Add("FAIL " + name + ": " + error); }
        Console.WriteLine(results.Last());
    }
    private static CalendarData Plan(string date = "2026-09-04") { var data = new CalendarData(); data.Items.Add(new Todo { Title = "准备面试材料", Date = date }); return data; }
    private static CalendarStore Store(string name) { return new CalendarStore(Path.Combine(root, name)); }
    private static Todo DeadlineTask(string start = "2026-09-03T15:00:00+08:00", int amount = 48, string unit = "hours", bool confirmed = true, string manualEnd = "")
    {
        var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
            Version = 1, ReminderTime = "19:00", Items = new[] { new {
                Id = Guid.NewGuid().ToString("N"), Title = "完成笔试", Date = "2026-09-05", Time = "15:00", Remind = true,
                Deadline = new { StartAt = start, Amount = amount, Unit = unit, Confirmed = confirmed, ManualEndAt = manualEnd, OriginalText = "请在邮件发出后两天内完成" }
            }}
        });
        return Store("fixture").Decode(json).Items[0];
    }
    private static void Throws(Action action) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, "Expected a validation failure."); }
    private sealed class FakeAgent : IWorkAgent
    {
        public int Calls;
        public AgentSummary Summarize(CalendarData data, DateTime now, string apiKey, string model)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            return new AgentSummary { Overview = "测试总结", Today = new List<string> { "先完成笔试" }, Upcoming = new List<string>(), Risks = new List<string>() };
        }
        public void Test(string apiKey, string model) { }
    }
    private sealed class FakeMailFactory : IMailboxClientFactory
    {
        public FakeMailboxClient Client = new FakeMailboxClient();
        public IMailboxClient Create() { return Client; }
    }
    private sealed class FakeMailboxClient : IMailboxClient
    {
        public MailboxCapabilities Capabilities { get; private set; }
        public List<MailboxFolder> Folders = new List<MailboxFolder>();
        public Dictionary<string, List<MailMessageSnapshot>> Messages = new Dictionary<string, List<MailMessageSnapshot>>();
        public List<MailFetchRequest> Requests = new List<MailFetchRequest>();
        public List<string> Imported = new List<string>();
        public List<string> Completed = new List<string>();
        public bool FailNextImport;
        public string FailFolder;
        public int KeepAliveCalls;
        public bool Connected;
        public FakeMailboxClient() { Capabilities = new MailboxCapabilities(); }
        public void Connect(MailConnectionOptions options, string authorizationCode) { Connected = options.Address.EndsWith("@163.com") && authorizationCode == "mail-secret"; }
        public IList<MailboxFolder> ListFolders() { return Folders.ToList(); }
        public IList<MailMessageSnapshot> Fetch(MailboxFolder folder, MailFetchRequest request)
        {
            Requests.Add(new MailFetchRequest { Since = request.Since, MinimumUid = request.MinimumUid });
            if (folder.FullName == FailFolder) throw new IOException("folder unavailable");
            List<MailMessageSnapshot> values;
            return Messages.TryGetValue(folder.FullName, out values) ? values.ToList() : new List<MailMessageSnapshot>();
        }
        public MailMessageSnapshot FindByMessageId(IEnumerable<MailboxFolder> folders, string messageId)
        {
            return Messages.Values.SelectMany(x => x).FirstOrDefault(x => x.MessageId == messageId);
        }
        public void MarkImported(MailMessageLocator message)
        {
            if (FailNextImport) { FailNextImport = false; throw new IOException("temporary flag failure"); }
            Imported.Add(message.FolderId + ":" + message.Uid);
        }
        public void MarkCompleted(MailMessageLocator message) { Completed.Add(message.FolderId + ":" + message.Uid); }
        public void KeepAlive() { KeepAliveCalls++; }
        public void Dispose() { }
    }
    private sealed class FakeMailAnalyzer : IMailActionAnalyzer
    {
        public MailAction Analyze(NormalizedMail mail, DateTime now, string apiKey, string model)
        {
            return new MailAction {
                Disposition = mail.Subject.Contains("笔试") ? "todo" : "ignore", Actionable = mail.Subject.Contains("笔试"), Category = "assessment", Title = "完成 " + mail.Subject,
                Notes = mail.Text, Important = true, DeadlineKind = "relative", DeadlineAmount = 48,
                DeadlineUnit = "hours", DeadlineOriginalText = "48小时内", Confidence = 0.95, Reason = "明确笔试要求"
            };
        }
    }
    private sealed class FakeStructuredMailAgent : IStructuredMailAgent
    {
        public readonly Queue<string> Results = new Queue<string>();
        public int Calls;
        public string LastInstructions = "";
        public string CompleteMailAction(string apiKey, string model, string instructions, string input)
        {
            Calls++; LastInstructions = instructions;
            return Results.Dequeue();
        }
    }
    private sealed class OrderedAgent : IWorkAgent
    {
        private readonly List<string> order;
        public OrderedAgent(List<string> order) { this.order = order; }
        public AgentSummary Summarize(CalendarData data, DateTime now, string apiKey, string model)
        {
            lock (order) order.Add("summary");
            return new AgentSummary { Overview = "已整理", Today = new List<string>(), Upcoming = new List<string>(), Risks = new List<string>() };
        }
        public void Test(string apiKey, string model) { }
    }
    private sealed class FakeDailyMailSync : IMailSyncService
    {
        private readonly List<string> order;
        public bool Due = true;
        public int Runs;
        public FakeDailyMailSync(List<string> order) { this.order = order; }
        public bool IsDue(DateTime now) { return Due && Runs == 0; }
        public MailSyncResult Run(bool automatic, int days = 0)
        {
            lock (order) order.Add("mail");
            Runs++;
            return new MailSyncResult();
        }
    }

    [STAThread]
    public static int Main()
    {
        root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-results", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        Test("v2 calendar upgrades without treating legacy completed work as newly completed", delegate {
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                Version = 2, ReminderTime = "19:00", Sound = true,
                Items = new[] { new { Id = "legacy-done", Title = "旧完成项", Date = "2026-09-07", Time = "", Completed = true, Rem = true } }
            });
            CalendarData data = Store("mail-v2").Decode(json);
            Check(data.Version == 3, "Calendar data did not upgrade to v3");
            Check(String.IsNullOrEmpty(data.Items.Single().CompletedAt), "Legacy completion incorrectly received a new completion timestamp");
        });
        Test("completion time and email source survive storage while duplicate mail stays singular", delegate {
            var store = Store("mail-source");
            var controller = new CalendarController(store);
            var todo = new Todo {
                Id = "mail-todo", Title = "完成在线测评", Date = "2026-09-10",
                EmailSource = new EmailSource {
                    EmailKey = "message:abc@example.com", AccountId = "account-hash", FolderId = "INBOX",
                    UidValidity = 7, Uid = 42, MessageId = "<abc@example.com>", Fingerprint = "",
                    Subject = "在线测评通知", Sender = "campus@example.com", SentAt = "2026-09-08T08:00:00+08:00",
                    DeadlineOriginalText = "两天内完成", DeadlineInferred = false, CompletionSyncState = "none"
                }
            };
            Check(controller.AddMailTodo(todo), "First mail todo was not added");
            Check(!controller.AddMailTodo(todo.Copy()), "Duplicate mail created a second todo");
            DateTime completedAt = new DateTime(2026, 9, 9, 10, 30, 0, DateTimeKind.Local);
            controller.Complete(todo.Id, true, completedAt);
            CalendarData loaded = store.Load();
            Check(loaded.Items.Count == 1 && loaded.Items[0].EmailSource.MessageId == "<abc@example.com>", "Email source did not round-trip");
            Check(loaded.Items[0].CompletedAt.StartsWith("2026-09-09T10:30:00"), "Completion timestamp was not recorded");
            Check(loaded.Items[0].EmailSource.CompletionSyncState == "pending", "Mail completion was not queued for reconciliation");
            controller.Complete(todo.Id, false, completedAt.AddMinutes(1));
            Check(String.IsNullOrEmpty(controller.Data.Items.Single().CompletedAt), "Undo completion kept a stale completion timestamp");
        });
        Test("same hiring task from invitation and reminder mail is stored only once", delegate {
            var controller = new CalendarController(Store("mail-semantic-dedup"));
            Todo invited = DeadlineTask("2026-09-08T18:56:00+08:00", 48); invited.Title = "参加锐捷网络AI面试";
            invited.EmailSource = new EmailSource { EmailKey = "message:invite@example.com", MessageId = "<invite@example.com>", Subject = "AI面试邀请", Sender = "campus@example.com", SentAt = "2026-09-08T18:56:00+08:00" };
            Todo reminded = DeadlineTask("2026-09-08T18:56:00+08:00", 48); reminded.Title = "参加锐捷网络 AI 面试";
            reminded.EmailSource = new EmailSource { EmailKey = "message:reminder@example.com", MessageId = "<reminder@example.com>", Subject = "AI面试到期提醒", Sender = "campus@example.com", SentAt = "2026-09-09T08:00:00+08:00" };
            Check(controller.AddMailTodo(invited), "Initial invitation was not stored");
            Check(!controller.AddMailTodo(reminded), "Reminder with a different Message-ID duplicated the same task");
            Check(controller.Data.Items.Count == 1, "Semantic duplicate left two calendar records");
            controller.Complete(invited.Id, true, new DateTime(2026, 9, 9, 9, 0, 0));
            Todo secondReminder = reminded.Copy(); secondReminder.Id = Guid.NewGuid().ToString("N"); secondReminder.EmailSource.EmailKey = "message:second-reminder@example.com";
            Check(!controller.AddMailTodo(secondReminder), "A reminder recreated a task that was already completed");
        });
        Test("same task title on a materially different deadline remains a new todo", delegate {
            var controller = new CalendarController(Store("mail-dedup-deadline-boundary"));
            Todo first = DeadlineTask("2026-09-08T10:00:00+08:00", 24); first.Title = "参加校园招聘面试";
            first.EmailSource = new EmailSource { EmailKey = "message:first-slot@example.com", Subject = "面试通知", Sender = "campus@example.com" };
            Todo later = DeadlineTask("2026-09-12T10:00:00+08:00", 24); later.Title = "参加校园招聘面试";
            later.EmailSource = new EmailSource { EmailKey = "message:later-slot@example.com", Subject = "第二轮面试通知", Sender = "campus@example.com" };
            Check(controller.AddMailTodo(first) && controller.AddMailTodo(later), "A genuinely different interview date was incorrectly deduplicated");
            Check(controller.Data.Items.Count == 2, "Different interview dates did not remain separate");
        });
        Test("calendar data exposes a persistent opportunity notification collection", delegate {
            PropertyInfo notices = typeof(CalendarData).GetProperty("Notices");
            Check(notices != null, "Calendar data has no opportunity notification collection");
            object value = notices.GetValue(new CalendarData(), null);
            Check(value is System.Collections.IList, "Opportunity notification collection is not initialized");
        });
        Test("chat history retains the newest fifty messages in chronological order", delegate {
            var store = new ChatHistoryStore(Path.Combine(root, "chat-retention"));
            var expectedIds = new List<string>();
            for (int index = 1; index <= 55; index++) {
                string id = Guid.NewGuid().ToString("N"); expectedIds.Add(id);
                store.Append(ChatMessage.CreateSafeDisplay(id, index % 2 == 0 ? "assistant" : "user", "message " + index,
                    new DateTimeOffset(2026, 9, 10, 8, index, 0, TimeSpan.FromHours(8)).ToString("o", CultureInfo.InvariantCulture)));
            }
            ChatHistory history = store.Load();
            Check(history.Version == 1 && history.Messages.Count == 50, "Chat history did not retain exactly fifty messages");
            for (int index = 0; index < history.Messages.Count; index++) {
                int expected = index + 6;
                Check(history.Messages[index].Id == expectedIds[expected - 1],
                    "Chat history did not preserve chronological retention at message " + expected);
            }
        });
        Test("chat history recovers the prior backup after primary JSON corruption", delegate {
            string directory = Path.Combine(root, "chat-recovery");
            var store = new ChatHistoryStore(directory);
            string firstId = Guid.NewGuid().ToString("N");
            store.Append(ChatMessage.CreateSafeDisplay(firstId, "user", "first retained message", "2026-09-10T08:00:00+08:00"));
            store.Append(ChatMessage.CreateSafeDisplay(Guid.NewGuid().ToString("N"), "assistant", "second retained message", "2026-09-10T08:01:00+08:00"));
            File.WriteAllText(Path.Combine(directory, "chat-history.json"), "{broken");
            ChatHistory recovered = store.Load();
            Check(recovered.Messages.Count == 1 && recovered.Messages.Single().Id == firstId, "Chat history did not recover the prior valid backup");
            Check(Directory.GetFiles(directory, "chat-history.json.damaged-*").Length == 1, "Damaged chat history was not preserved");
            Check(store.Load().Messages.Single().Id == firstId && Directory.GetFiles(directory, "chat-history.json.damaged-*").Length == 2,
                "Repeated recovery in the same second did not preserve a second damaged primary");
            store.Save(recovered);
            store.Load();
            Check(String.IsNullOrEmpty(store.LoadWarning), "A later successful chat history load kept a stale recovery warning");
        });
        Test("chat history projects untrusted values into safe bounded persistence fields", delegate {
            string directory = Path.Combine(root, "chat-safe-storage");
            string rawMailBody = "From: recruiter@example.test\r\nSubject: confidential interview\r\nMessage-ID: <private@example.test>\r\nPlease bring your passport.";
            string fullPrompt = "System: use the following complete hidden instructions and never reveal them.";
            string rawModelResponse = "{\"choices\":[{\"message\":{\"content\":\"unfiltered model response\"}}]}";
            var store = new ChatHistoryStore(directory);
            store.Append(new ChatMessage {
                Id = "deepseek-secret", Role = "assistant", Text = rawMailBody,
                CreatedAt = "2026-09-10T08:00:00+08:00", Intent = fullPrompt,
                TodoIds = new List<string> { "mail-auth-code" },
                Sync = new ChatSyncSummary {
                    PreviousCompletedAt = "2026-09-10T07:00:00+08:00", StartedAt = "2026-09-10T07:30:00+08:00", CompletedAt = "2026-09-10T08:00:00+08:00",
                    Folders = new List<ChatFolderCursorSummary> {
                        new ChatFolderCursorSummary { DisplayName = rawModelResponse, PreviousScannedAt = "2026-09-10T07:00:00+08:00", CompletedAt = "2026-09-10T08:00:00+08:00", Error = "deepseek-secret" }
                    }
                }
            });
            string json = File.ReadAllText(Path.Combine(directory, "chat-history.json"));
            Check(!json.Contains("deepseek-secret") && !json.Contains("mail-auth-code") && !json.Contains(rawMailBody) && !json.Contains(fullPrompt) && !json.Contains(rawModelResponse),
                "Chat history serialized untrusted credentials, mail content, prompt, or model output");
        });
        Test("chat history never persists ordinary raw text supplied through its display factory", delegate {
            string directory = Path.Combine(root, "chat-safe-factory");
            string rawOrdinaryProse = "The complete unfiltered reply says that the applicant should contact the coordinator before Friday.";
            var store = new ChatHistoryStore(directory);
            store.Append(ChatMessage.CreateSafeDisplay(Guid.NewGuid().ToString("N"), "assistant", rawOrdinaryProse, "2026-09-10T08:00:00+08:00", "chat"));
            Check(!File.ReadAllText(Path.Combine(directory, "chat-history.json")).Contains(rawOrdinaryProse),
                "Chat display factory allowed ordinary raw prose into persistence");
        });
        Test("chat history reprojects legacy raw JSON before returning or saving it", delegate {
            string directory = Path.Combine(root, "chat-legacy-projection");
            string rawOrdinaryProse = "The complete ordinary mail prose asks the applicant to join a private interview meeting on Friday.";
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "chat-history.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                Version = 1,
                Messages = new[] { new { Id = Guid.NewGuid().ToString("N"), Role = "assistant", Text = rawOrdinaryProse, CreatedAt = "2026-09-10T08:00:00+08:00", Intent = "chat" } }
            }));
            var store = new ChatHistoryStore(directory);
            ChatHistory history = store.Load();
            Check(history.Messages.Single().Text != rawOrdinaryProse, "Legacy raw JSON was trusted as display content during load");
            store.Save(history);
            Check(!File.ReadAllText(Path.Combine(directory, "chat-history.json")).Contains(rawOrdinaryProse),
                "Legacy raw JSON was written back as trusted display content");
        });
        Test("chat history rejects invalid roles, normalizes timestamps, and clears stored messages", delegate {
            string directory = Path.Combine(root, "chat-validation");
            var store = new ChatHistoryStore(directory);
            Throws(delegate { store.Append(new ChatMessage { Id = "invalid", Role = "tool", Text = "not allowed", CreatedAt = "2026-09-10T08:00:00+08:00" }); });
            store.Append(ChatMessage.CreateSafeDisplay(Guid.NewGuid().ToString("N"), "system", "allowed", "2026-09-10 08:00:00 +08:00"));
            ChatMessage message = store.Load().Messages.Single();
            DateTimeOffset timestamp;
            Check(DateTimeOffset.TryParse(message.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) && message.CreatedAt == timestamp.ToString("o", CultureInfo.InvariantCulture),
                "Chat timestamp was not normalized to an ISO 8601 offset value");
            store.Clear();
            Check(store.Load().Messages.Count == 0, "Chat history clear did not persist an empty history");
        });
        Test("chat history normalizes sync cursor timestamps with offsets", delegate {
            var store = new ChatHistoryStore(Path.Combine(root, "chat-sync-timestamps"));
            store.Append(ChatMessage.CreateSafeDisplay(Guid.NewGuid().ToString("N"), "assistant", "同步完成", "2026-09-10T08:00:00+08:00", sync: new ChatSyncSummary {
                    PreviousCompletedAt = "2026-09-10 07:00:00 +08:00", StartedAt = "2026-09-10 07:55:00 +08:00", CompletedAt = "2026-09-10 08:00:00 +08:00",
                    Folders = new List<ChatFolderCursorSummary> {
                        new ChatFolderCursorSummary { DisplayName = "收件箱", PreviousScannedAt = "2026-09-10 07:30:00 +08:00", CompletedAt = "2026-09-10 08:00:00 +08:00" }
                    }
                }));
            ChatSyncSummary sync = store.Load().Messages.Single().Sync;
            Func<string, bool> isNormalizedTimestamp = delegate(string value) {
                DateTimeOffset timestamp;
                return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) && value == timestamp.ToString("o", CultureInfo.InvariantCulture);
            };
            Check(isNormalizedTimestamp(sync.PreviousCompletedAt) && isNormalizedTimestamp(sync.StartedAt) && isNormalizedTimestamp(sync.CompletedAt) &&
                isNormalizedTimestamp(sync.Folders.Single().PreviousScannedAt) && isNormalizedTimestamp(sync.Folders.Single().CompletedAt),
                "Chat sync timestamps were not normalized to ISO 8601 offset values");
        });
        Test("chat intent router recognizes exact and natural sync and task requests", delegate {
            ChatIntent readMail = ChatIntentRouter.Parse("读取新邮件");
            ChatIntent naturalReadMail = ChatIntentRouter.Parse("收取一下新邮件");
            ChatIntent syncMailbox = ChatIntentRouter.Parse("  同步 一下 邮箱  ");
            ChatIntent naturalMailbox = ChatIntentRouter.Parse("帮我同步一下收件箱");
            ChatIntent today = ChatIntentRouter.Parse("今天要做什么");
            ChatIntent naturalToday = ChatIntentRouter.Parse("今天有哪些安排");
            ChatIntent tomorrow = ChatIntentRouter.Parse("明天截止");
            ChatIntent naturalTomorrow = ChatIntentRouter.Parse("明天有哪些截止事项");
            ChatIntent sevenDays = ChatIntentRouter.Parse("未来 七 天");
            ChatIntent naturalSevenDays = ChatIntentRouter.Parse("接下来一周");
            ChatIntent mixed = ChatIntentRouter.Parse("读取新邮件，然后今天要做什么");
            ChatIntent unrelated = ChatIntentRouter.Parse("帮我写一段自我介绍");
            Check(readMail.Kind == ChatIntentKind.SyncMailIncremental && naturalReadMail.Kind == ChatIntentKind.SyncMailIncremental &&
                syncMailbox.Kind == ChatIntentKind.SyncMailIncremental && naturalMailbox.Kind == ChatIntentKind.SyncMailIncremental,
                "Explicit mailbox sync phrases were not routed to local incremental sync");
            Check(today.Kind == ChatIntentKind.ListTasks && today.Range == TaskQueryRange.Today &&
                naturalToday.Kind == ChatIntentKind.ListTasks && naturalToday.Range == TaskQueryRange.Today,
                "Today's task request was not routed with the today range");
            Check(tomorrow.Kind == ChatIntentKind.ListTasks && tomorrow.Range == TaskQueryRange.Tomorrow &&
                naturalTomorrow.Kind == ChatIntentKind.ListTasks && naturalTomorrow.Range == TaskQueryRange.Tomorrow,
                "Tomorrow deadline request was not routed with the tomorrow range");
            Check(sevenDays.Kind == ChatIntentKind.ListTasks && sevenDays.Range == TaskQueryRange.SevenDays &&
                naturalSevenDays.Kind == ChatIntentKind.ListTasks && naturalSevenDays.Range == TaskQueryRange.SevenDays,
                "Future seven-day request was not routed with the seven-day range");
            Check(mixed.Kind == ChatIntentKind.SyncMailIncremental,
                "Explicit mailbox sync did not take precedence over a task phrase");
            Check(unrelated.Kind == ChatIntentKind.Chat, "Unrelated text did not remain chat");
        });
        Test("task query uses deadline instants and excludes inactive calendar items", delegate {
            DateTime now = new DateTime(2026, 9, 10, 12, 0, 0);
            var data = new CalendarData();
            data.Items.Add(new Todo { Title = "逾期截止", Date = "2026-09-10", Important = false,
                Deadline = new DeadlineSpec { StartAt = "2026-09-09T08:00:00+08:00", Amount = 24, Unit = "hours", Confirmed = true } });
            data.Items.Add(new Todo { Title = "今日截止", Date = "2026-09-10", Important = true,
                Deadline = new DeadlineSpec { StartAt = "2026-09-10T06:00:00+08:00", Amount = 12, Unit = "hours", Confirmed = true } });
            data.Items.Add(new Todo { Title = "明日普通待办", Date = "2026-09-11", Time = "09:00", Important = true });
            data.Items.Add(new Todo { Title = "七日内跨日截止", Date = "2026-09-16", Important = false,
                Deadline = new DeadlineSpec { StartAt = "2026-09-09T23:00:00+08:00", Amount = 168, Unit = "hours", Confirmed = true } });
            data.Items.Add(new Todo { Title = "已完成", Date = "2026-09-10", Completed = true });
            data.Items.Add(new Todo { Title = "已删除", Date = "2026-09-10", Deleted = true });
            TaskQueryResult today = TaskQueryService.Query(data, now, TaskQueryRange.Today);
            TaskQueryResult tomorrow = TaskQueryService.Query(data, now, TaskQueryRange.Tomorrow);
            TaskQueryResult sevenDays = TaskQueryService.Query(data, now, TaskQueryRange.SevenDays);
            Check(today.Overdue.Select(item => item.Title).SequenceEqual(new[] { "逾期截止" }),
                "A past deadline instant was not classified as overdue");
            Check(today.Due.Select(item => item.Title).SequenceEqual(new[] { "今日截止" }),
                "Today's deadline instant was not included in today's due list");
            Check(tomorrow.Due.Select(item => item.Title).SequenceEqual(new[] { "明日普通待办" }),
                "Tomorrow's ordinary todo was not included in tomorrow's due list");
            Check(sevenDays.Due.Select(item => item.Title).SequenceEqual(new[] { "今日截止", "明日普通待办", "七日内跨日截止" }),
                "Seven-day query did not use the deadline end date or deterministic due ordering");
            Check(!sevenDays.Overdue.Concat(sevenDays.Due).Any(item => item.Title == "已完成" || item.Title == "已删除"),
                "Completed or deleted items leaked into the local task query");
            Check(sevenDays.DeterministicText.Contains("逾期截止") && sevenDays.DeterministicText.Contains("七日内跨日截止"),
                "Task query did not create deterministic local result text");
        });
        Test("task query keeps a deadline at the current instant in the due list", delegate {
            DateTime now = new DateTime(2026, 9, 10, 12, 0, 0);
            var data = new CalendarData();
            data.Items.Add(new Todo { Title = "恰好截止", Date = "2026-09-10",
                Deadline = new DeadlineSpec { StartAt = "2026-09-10T11:00:00+08:00", Amount = 1, Unit = "hours", Confirmed = true } });
            TaskQueryResult result = TaskQueryService.Query(data, now, TaskQueryRange.Today);
            Check(result.Overdue.Count == 0 && result.Due.Select(item => item.Title).SequenceEqual(new[] { "恰好截止" }),
                "A deadline equal to the current instant was incorrectly classified as overdue");
        });
        Test("task query uses ordinary todo times without expiring all-day today items", delegate {
            DateTime now = new DateTime(2026, 9, 10, 12, 0, 0);
            var data = new CalendarData();
            data.Items.Add(new Todo { Title = "今天较早定时", Date = "2026-09-10", Time = "09:00" });
            data.Items.Add(new Todo { Title = "今天较晚定时", Date = "2026-09-10", Time = "15:00" });
            data.Items.Add(new Todo { Title = "今天全天", Date = "2026-09-10", Time = "" });
            TaskQueryResult result = TaskQueryService.Query(data, now, TaskQueryRange.Today);
            Check(result.Overdue.Select(item => item.Title).SequenceEqual(new[] { "今天较早定时" }),
                "A timed ordinary todo before now was not classified as overdue");
            Check(result.Due.Select(item => item.Title).SequenceEqual(new[] { "今天全天", "今天较晚定时" }),
                "Future timed or all-day ordinary todos today were not kept in the due list");
        });
        Test("task query caps model-facing items after deterministic overdue ordering", delegate {
            DateTime now = new DateTime(2026, 9, 10, 12, 0, 0);
            var data = new CalendarData();
            for (int index = 0; index < 51; index++)
                data.Items.Add(new Todo { Title = "普通任务 " + index.ToString("D2"), Date = "2026-09-10", Important = index == 50 });
            data.Items.Add(new Todo { Title = "逾期任务", Date = "2026-09-10",
                Deadline = new DeadlineSpec { StartAt = "2026-09-09T08:00:00+08:00", Amount = 24, Unit = "hours", Confirmed = true } });
            TaskQueryResult result = TaskQueryService.Query(data, now, TaskQueryRange.Today);
            Check(result.Overdue.Count == 1 && result.Due.Count == 49, "Task query did not cap combined model-facing items at fifty");
            Check(result.Overdue.Single().Title == "逾期任务" && result.Due.First().Title == "普通任务 50",
                "Task query did not keep overdue status, importance, and title ordering deterministic");
        });
        Test("task query caps tied items by ordinal todo identifier instead of storage order", delegate {
            DateTime now = new DateTime(2026, 9, 10, 12, 0, 0);
            var data = new CalendarData();
            for (int index = 50; index >= 0; index--)
                data.Items.Add(new Todo { Id = "task-" + index.ToString("D2"), Title = "同名同日任务", Date = "2026-09-10" });
            TaskQueryResult result = TaskQueryService.Query(data, now, TaskQueryRange.Today);
            Check(result.Due.Count == 50 && result.Due.First().Id == "task-00" && result.Due.Last().Id == "task-49",
                "The fifty-item cap depended on input storage order when due, importance, and title tied");
        });
        Test("mail sync state recovers its backup and never contains the authorization code", delegate {
            string directory = Path.Combine(root, "mail-state");
            var store = new MailStateStore(directory);
            var first = new MailSyncState();
            first.Account.Enabled = true; first.Account.Address = "student@163.com";
            first.Folders.Add(new MailFolderState { FolderId = "INBOX", DisplayName = "收件箱", LastUid = 12, UidValidity = 7 });
            store.Save(first);
            var second = store.Load(); second.Folders[0].LastUid = 18; second.LastCompletedAt = "2026-09-08T09:00:00+08:00";
            store.Save(second);
            Check(store.Load().Folders.Single().LastUid == 18, "Mail state did not round-trip");
            string json = File.ReadAllText(store.FilePath);
            Check(!json.Contains("mail-auth-code"), "Mail authorization code leaked into sync state");
            File.WriteAllText(store.FilePath, "{broken");
            MailSyncState recovered = store.Load();
            Check(recovered.Folders.Single().LastUid == 12, "Mail state did not recover the prior valid backup");
            Check(File.Exists(Directory.GetFiles(directory, "mail-sync.json.damaged-*").Single()), "Damaged mail state was not preserved");
        });
        Test("mail authorization and DeepSeek key use separate current-user encrypted files", delegate {
            string directory = Path.Combine(root, "mail-secrets");
            var deepSeek = new SecretStore(directory);
            var mail = new MailSecretStore(directory);
            deepSeek.Save("deepseek-secret");
            mail.Save("mail-auth-code");
            Check(deepSeek.Load() == "deepseek-secret" && mail.Load() == "mail-auth-code", "Encrypted secrets did not round-trip independently");
            Check(deepSeek.FilePath != mail.FilePath, "Mail and DeepSeek secrets share one file");
            Check(!File.ReadAllText(mail.FilePath).Contains("mail-auth-code"), "Mail authorization code was stored as plaintext");
            mail.Clear();
            Check(deepSeek.HasKey && !mail.HasKey, "Clearing mail authorization removed the DeepSeek key");
        });
        Test("mail folder policy includes incoming and custom folders but excludes outgoing stores", delegate {
            Check(MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "INBOX", Attributes = MailFolderAttributes.Inbox }), "Inbox was excluded");
            Check(MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "垃圾邮件", Attributes = MailFolderAttributes.Junk }), "Junk folder was excluded");
            Check(MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "订阅邮件", Attributes = MailFolderAttributes.None }), "Subscription folder was excluded");
            Check(MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "校招", Attributes = MailFolderAttributes.None }), "Custom folder was excluded");
            Check(!MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "已发送", Attributes = MailFolderAttributes.Sent }), "Sent folder was included");
            Check(!MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "草稿箱", Attributes = MailFolderAttributes.Drafts }), "Drafts folder was included");
            Check(!MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "已删除", Attributes = MailFolderAttributes.Trash }), "Trash folder was included");
            Check(!MailFolderPolicy.ShouldInclude(new MailboxFolder { FullName = "Sent Messages", Attributes = MailFolderAttributes.None }), "Localized sent-name fallback was included");
        });
        Test("NetEase IMAP connection sends a generic RFC2971 client identity", delegate {
            Check(NetEaseImapIdentity.ShouldIdentify("imap.163.com"), "163 IMAP host was not recognized");
            Check(!NetEaseImapIdentity.ShouldIdentify("imap.example.com"), "Unrelated IMAP host should not require NetEase identity");
            MailKit.Net.Imap.ImapImplementation identity = NetEaseImapIdentity.Create();
            Check(identity.Name == "LittleCalendar" && !String.IsNullOrWhiteSpace(identity.Version), "Client identity is incomplete");
            Check(String.IsNullOrWhiteSpace(identity.OSVersion) && String.IsNullOrWhiteSpace(identity.Address), "Client identity contains tracking details");
        });
        Test("mail diagnostics create separate connection and read logs without secrets or bodies", delegate {
            string directory = Path.Combine(root, "mail-diagnostics");
            var diagnostics = new MailDiagnosticLog(directory);
            diagnostics.Connection("AUTHENTICATED", "host=imap.163.com authorization=must-not-appear");
            diagnostics.Read("SEARCH_RESULT", "folder=INBOX count=1");
            diagnostics.Ai("LLM_RESULT", "uid=23 actionable=true title=完成笔试");
            diagnostics.ReadMessage(new MailMessageSnapshot {
                FolderId = "INBOX", Uid = 23, SentAt = "2026-09-09T09:00:00+08:00",
                Sender = "campus@example.com", Subject = "笔试通知\r\n伪造日志", PlainText = "正文不应写入"
            });
            string connection = File.ReadAllText(diagnostics.ConnectionPath);
            string read = File.ReadAllText(diagnostics.ReadPath);
            string ai = File.ReadAllText(diagnostics.AiPath);
            Check(connection.Contains("AUTHENTICATED") && connection.Contains("imap.163.com"), "Connection stages were not logged");
            Check(!connection.Contains("must-not-appear"), "Authorization code leaked into connection log");
            Check(read.Contains("SEARCH_RESULT") && read.Contains("uid=23") && read.Contains("笔试通知  伪造日志"), "Mail search metadata was not logged as one line");
            Check(!read.Contains("正文不应写入"), "Mail body leaked into diagnostic log");
            Check(ai.Contains("LLM_RESULT") && ai.Contains("actionable=true") && !ai.Contains("正文不应写入"), "AI decision log is missing or unsafe");
        });
        Test("mail diagnostics rotate oversized logs before appending new events", delegate {
            string directory = Path.Combine(root, "mail-diagnostics-rotation");
            var diagnostics = new MailDiagnosticLog(directory, 4096);
            diagnostics.Connection("LARGE", new String('x', 5000));
            diagnostics.Connection("AFTER_ROTATION", "fresh=true");
            string previous = Path.Combine(diagnostics.DirectoryPath, "mail-connection.previous.log");
            Check(File.Exists(previous), "Oversized connection log was not rotated");
            Check(File.ReadAllText(previous).Contains("LARGE") && File.ReadAllText(diagnostics.ConnectionPath).Contains("AFTER_ROTATION"), "Rotated and current log contents are incorrect");
        });
        Test("mail status changes use only standard flags by default", delegate {
            MailFlagChange imported = MailStatusPolicy.Imported();
            Check(imported.AddSeen == false && imported.AddFlagged && !imported.RemoveFlagged, "Imported mail should only be starred");
            MailFlagChange completed = MailStatusPolicy.Completed();
            Check(completed.AddSeen && !completed.AddFlagged && completed.RemoveFlagged, "Completed mail should be read and unstarred");
            Check(String.IsNullOrEmpty(imported.CustomKeyword) && String.IsNullOrEmpty(completed.CustomKeyword), "Unverified private NetEase keyword was used");
        });
        Test("mail content normalization removes active HTML and keeps useful recruiting text", delegate {
            var message = new MailMessageSnapshot {
                Sender = "campus@example.com", Subject = "线上笔试通知",
                SentAt = "2026-09-08T08:00:00+08:00",
                HtmlText = "<style>.x{display:none}</style><script>steal()</script><p>请在 48 小时内完成&nbsp;笔试 &amp; 测评。</p><img src='https://tracker.example/pixel'>"
            };
            NormalizedMail mail = MailContent.Normalize(message);
            Check(mail.Text.Contains("48 小时内完成") && mail.Text.Contains("笔试 & 测评"), "Useful HTML text was lost");
            Check(!mail.Text.Contains("steal") && !mail.Text.Contains("tracker.example"), "Active or remote HTML leaked into normalized content");
        });
        Test("mail prefilter blocks security and receipt noise without blocking recruiting actions", delegate {
            Check(MailPrefilter.ShouldSkip(new NormalizedMail { Subject = "一次性验证码", Text = "验证码 123456，请勿泄露" }), "OTP mail was not filtered");
            Check(MailPrefilter.ShouldSkip(new NormalizedMail { Subject = "新设备登录提醒", Text = "如非本人操作请修改密码" }), "Login alert was not filtered");
            Check(MailPrefilter.ShouldSkip(new NormalizedMail { Subject = "申请已收到", Text = "感谢投递，我们会尽快处理" }), "Receipt-only mail was not filtered");
            Check(!MailPrefilter.ShouldSkip(new NormalizedMail { Subject = "线上笔试通知", Text = "请在两天内完成测评" }), "Assessment action was incorrectly filtered");
            Check(!MailPrefilter.ShouldSkip(new NormalizedMail { Subject = "面试时间确认", Text = "请在今日回复是否参加" }), "Interview confirmation was incorrectly filtered");
        });
        Test("mail prompt separates todos opportunities and irrelevant messages", delegate {
            var agent = new FakeStructuredMailAgent();
            agent.Results.Enqueue("{\"disposition\":\"notice\",\"actionable\":false,\"category\":\"other\",\"title\":\"贝泰妮集团校招岗位\",\"notes\":\"邀请投递\",\"important\":false,\"deadlineKind\":\"none\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":0,\"deadlineUnit\":\"\",\"deadlineOriginalText\":\"\",\"confidence\":0.95,\"reason\":\"岗位推荐\"}");
            var analyzer = new DeepSeekMailActionAnalyzer(agent);
            analyzer.Analyze(new NormalizedMail { Subject = "【智联推荐】贝泰妮集团27届校招岗位", Text = "选择心仪岗位一键投递" }, new DateTime(2026, 9, 10, 9, 0, 0), "key", "deepseek-v4-flash");
            Check(agent.LastInstructions.Contains("disposition") && agent.LastInstructions.Contains("notice") && agent.LastInstructions.Contains("邀请投递"), "Prompt does not define the three-way mail routing contract");
            Check(agent.LastInstructions.Contains("已经投递") || agent.LastInstructions.Contains("招聘流程"), "Prompt does not require evidence of an existing application before creating a todo");
        });
        Test("mail action parser is strict and deadline mapping preserves exact and fallback ranges", delegate {
            MailAction parsed = MailActionParser.Parse("{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"assessment\",\"title\":\"完成在线测评\",\"notes\":\"使用邮件内链接\",\"important\":true,\"deadlineKind\":\"relative\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":48,\"deadlineUnit\":\"hours\",\"deadlineOriginalText\":\"邮件发送后48小时内\",\"confidence\":0.96,\"reason\":\"明确要求完成测评\"}");
            var source = new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 7, Uid = 42, MessageId = "<a@example.com>", Subject = "测评通知", Sender = "campus@example.com", SentAt = "2026-09-08T08:00:00+08:00" };
            Todo relative = MailDeadlineMapper.ToTodo(parsed, source, new DateTime(2026, 9, 8, 9, 0, 0));
            Check(relative.Date == "2026-09-10" && relative.Time == "08:00", "48-hour deadline was calculated from the wrong instant");
            Check(relative.Deadline.Unit == "hours" && relative.Deadline.Amount == 48 && relative.Deadline.Confirmed, "Exact relative deadline lost its semantics");
            MailAction none = MailActionParser.Parse("{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"confirmation\",\"title\":\"确认参加宣讲\",\"notes\":\"\",\"important\":false,\"deadlineKind\":\"none\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":0,\"deadlineUnit\":\"\",\"deadlineOriginalText\":\"\",\"confidence\":0.72,\"reason\":\"需要确认\"}");
            Todo fallback = MailDeadlineMapper.ToTodo(none, source, new DateTime(2026, 9, 8, 14, 0, 0));
            Check(fallback.Date == "2026-09-15" && fallback.Time == "23:59", "No-deadline mail did not receive the one-week fallback");
            Check(!fallback.Deadline.Confirmed && fallback.EmailSource.DeadlineInferred, "Fallback deadline was not marked inferred");
            Throws(delegate { MailActionParser.Parse("{\"actionable\":true,\"title\":\"missing fields\"}"); });
        });
        Test("mail classification request disables DeepSeek thinking while summaries keep it enabled", delegate {
            string mailJson = DeepSeekRequestPayload.Build("deepseek-v4-flash", "system", "mail", 1200, true);
            string summaryJson = DeepSeekRequestPayload.Build("deepseek-v4-flash", "system", "summary", 1000, false);
            Check(mailJson.Contains("\"thinking\":{\"type\":\"disabled\"}"), "Mail classification still uses slow thinking mode");
            Check(summaryJson.Contains("\"thinking\":{\"type\":\"enabled\"}"), "Summary thinking mode changed unexpectedly");
        });
        Test("structured mail request requires every action field and disables reasoning", delegate {
            string payload = DeepSeekStructuredPayload.BuildMailAction("deepseek-v4-flash", "extract", "mail input", 1200);
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var rootShape = json.Deserialize<Dictionary<string, object>>(payload);
            var reasoning = (Dictionary<string, object>)rootShape["reasoning"];
            var textShape = (Dictionary<string, object>)rootShape["text"];
            var format = (Dictionary<string, object>)textShape["format"];
            var schema = (Dictionary<string, object>)format["schema"];
            var required = ((System.Collections.ArrayList)schema["required"]).Cast<string>().ToList();
            Check((string)format["type"] == "json_schema" && (string)reasoning["effort"] == "none", "Mail request is not strict structured output without reasoning");
            Check(required.Count == 14 && required.Contains("disposition") && required.Contains("actionable") && required.Contains("deadlineAmount") && required.Contains("reason"), "Mail action schema does not require the three-way routing contract");
            Check((bool)schema["additionalProperties"] == false, "Mail action schema permits unknown fields");
        });
        Test("Responses API output text is extracted and incomplete responses are rejected", delegate {
            string body = "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"actionable\\\":false}\"}]}]}";
            Check(DeepSeekResponses.ExtractOutputText(body) == "{\"actionable\":false}", "Structured output text was not extracted");
            Throws(delegate { DeepSeekResponses.ExtractOutputText("{\"status\":\"incomplete\",\"output\":[]}"); });
        });
        Test("mail analyzer retries one schema-invalid response and returns the corrected action", delegate {
            var agent = new FakeStructuredMailAgent();
            agent.Results.Enqueue("{\"actionable\":true}");
            agent.Results.Enqueue("{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"assessment\",\"title\":\"完成京东测评\",\"notes\":\"使用邮件链接\",\"important\":true,\"deadlineKind\":\"relative\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":48,\"deadlineUnit\":\"hours\",\"deadlineOriginalText\":\"48小时内\",\"confidence\":0.96,\"reason\":\"邮件要求完成测评\"}");
            string directory = Path.Combine(root, "mail-schema-retry"); var diagnostics = new MailDiagnosticLog(directory);
            var analyzer = new DeepSeekMailActionAnalyzer(agent, diagnostics);
            MailAction action = analyzer.Analyze(new NormalizedMail { Subject = "京东测评通知", Text = "请在48小时内完成" }, new DateTime(2026, 9, 10, 9, 0, 0), "key", "deepseek-v4-flash");
            Check(action.Actionable && action.Title == "完成京东测评" && agent.Calls == 2, "Invalid structured output was not corrected exactly once");
            string log = File.ReadAllText(diagnostics.AiPath);
            Check(log.Contains("JSON_RETRY") && log.Contains("缺少字段") && log.Contains("JSON_RETRY_OK"), "Schema correction was not explained in the safe AI log");
        });
        Test("fixed mail JSON flows through validation into a calendar todo", delegate {
            string directory = Path.Combine(root, "mail-structured-flow");
            var controller = new CalendarController(new CalendarStore(directory)); var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret"); var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory(); factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", Attributes = MailFolderAttributes.Inbox, UidValidity = 1 });
            factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> { new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 1, Uid = 77, MessageId = "<structured@example.com>", Subject = "京东测评通知", SentAt = "2026-09-10T08:00:00+08:00", PlainText = "请在48小时内完成" } };
            var agent = new FakeStructuredMailAgent(); agent.Results.Enqueue("{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"assessment\",\"title\":\"完成京东测评\",\"notes\":\"使用邮件链接\",\"important\":true,\"deadlineKind\":\"relative\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":48,\"deadlineUnit\":\"hours\",\"deadlineOriginalText\":\"48小时内\",\"confidence\":0.96,\"reason\":\"邮件要求完成测评\"}");
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new DeepSeekMailActionAnalyzer(agent), () => new DateTime(2026, 9, 10, 9, 0, 0));
            MailSyncResult result = sync.Run(false, 1);
            Check(result.CreatedCount == 1 && controller.Data.Items.Single().Title == "完成京东测评" && controller.Data.Items.Single().Date == "2026-09-12", "Validated mail JSON did not become the expected calendar todo");
        });
        Test("unsolicited application invitations become deduplicated opportunity notices instead of todos", delegate {
            string directory = Path.Combine(root, "mail-opportunity-flow");
            var controller = new CalendarController(new CalendarStore(directory)); var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret"); var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory(); factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", Attributes = MailFolderAttributes.Inbox, UidValidity = 1 });
            factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> {
                new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 1, Uid = 80, MessageId = "<opportunity-a@example.com>", Subject = "【智联推荐】贝泰妮集团27届校招岗位", Sender = "智联招聘 <campus@example.com>", SentAt = "2026-09-10T08:00:00+08:00", PlainText = "查看岗位详情" },
                new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 1, Uid = 81, MessageId = "<opportunity-b@example.com>", Subject = "贝泰妮集团27届校招岗位邀请投递", Sender = "智联招聘 <campus@example.com>", SentAt = "2026-09-10T08:30:00+08:00", PlainText = "职位推荐，一键投递" }
            };
            string wrongTodo = "{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"other\",\"title\":\"投递贝泰妮集团27届校招岗位\",\"notes\":\"一键投递\",\"important\":false,\"deadlineKind\":\"none\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":0,\"deadlineUnit\":\"\",\"deadlineOriginalText\":\"\",\"confidence\":0.91,\"reason\":\"邀请投递\"}";
            var agent = new FakeStructuredMailAgent(); agent.Results.Enqueue(wrongTodo); agent.Results.Enqueue(wrongTodo);
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new DeepSeekMailActionAnalyzer(agent), () => new DateTime(2026, 9, 10, 9, 0, 0));
            MailSyncResult result = sync.Run(false, 1);
            Check(result.CreatedCount == 0 && controller.Data.Items.Count == 0, "Unsolicited invitation leaked into the calendar");
            Check(result.NoticeCount == 1 && controller.Data.Notices.Count == 1, "Repeated opportunity invitation was not stored once in the notification center");
            Check(controller.Data.Notices[0].EmailSource.Sender.Contains("campus@example.com"), "Opportunity notice lost its source email address");
        });
        Test("manual sync honors its day window and reanalyzes prior non-actionable mail", delegate {
            string directory = Path.Combine(root, "mail-manual-window");
            var controller = new CalendarController(new CalendarStore(directory));
            var stateStore = new MailStateStore(directory);
            var message = new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 7, Uid = 9, MessageId = "<retry-window@example.com>", Subject = "线上笔试通知", Sender = "campus@example.com", SentAt = "2026-09-09T08:00:00+08:00", PlainText = "请完成" };
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; state.Account.ManualSyncDays = 3; state.ProcessedKeys.Add(MailIdentity.Key(message)); state.Folders.Add(new MailFolderState { FolderId = "INBOX", DisplayName = "收件箱", UidValidity = 7, LastUid = 0, LastScannedAt = "2026-09-08T09:00:00+08:00" }); stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret"); var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory(); factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", DisplayName = "收件箱", Attributes = MailFolderAttributes.Inbox, UidValidity = 7 }); factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> { message };
            DateTime now = new DateTime(2026, 9, 9, 17, 0, 0);
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new FakeMailAnalyzer(), () => now);
            MailSyncResult result = sync.Run(false, 3);
            Check(factory.Client.Requests.Single().Since == now.AddDays(-3) && factory.Client.Requests.Single().MinimumUid == 0, "Manual window leaked into all historical UIDs");
            Check(result.CreatedCount == 1 && factory.Client.KeepAliveCalls > 0, "Manual rescan did not reanalyze or keep the mailbox alive");
        });
        Test("one folder failure preserves successful mail work and reports a partial result", delegate {
            string directory = Path.Combine(root, "mail-partial-folder"); var controller = new CalendarController(new CalendarStore(directory)); var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret"); var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory(); factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", Attributes = MailFolderAttributes.Inbox, UidValidity = 7 }); factory.Client.Folders.Add(new MailboxFolder { FullName = "垃圾邮件", Attributes = MailFolderAttributes.Junk, UidValidity = 8 }); factory.Client.FailFolder = "垃圾邮件";
            factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> { new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 7, Uid = 5, MessageId = "<partial@example.com>", Subject = "线上笔试通知", SentAt = "2026-09-09T08:00:00+08:00", PlainText = "请完成" } };
            var diagnostics = new MailDiagnosticLog(directory); var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new FakeMailAnalyzer(), () => new DateTime(2026, 9, 9, 17, 0, 0), diagnostics);
            MailSyncResult result = sync.Run(false, 1); MailSyncState saved = stateStore.Load();
            Check(result.CreatedCount == 1 && !String.IsNullOrEmpty(saved.LastCompletedAt), "Successful folder work was discarded");
            Check(saved.LastError.Contains("部分") && File.ReadAllText(diagnostics.AiPath).Contains("SYNC_PARTIAL"), "Partial failure was not exposed in state and AI log");
        });
        Test("mail sync reads seven days first, advances per-folder cursor and never duplicates a todo", delegate {
            string directory = Path.Combine(root, "mail-sync-flow");
            var controller = new CalendarController(new CalendarStore(directory));
            var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com";
            stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret");
            var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory();
            factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", DisplayName = "收件箱", Attributes = MailFolderAttributes.Inbox, UidValidity = 7 });
            factory.Client.Folders.Add(new MailboxFolder { FullName = "已发送", DisplayName = "已发送", Attributes = MailFolderAttributes.Sent, UidValidity = 4 });
            factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> {
                new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 7, Uid = 42, MessageId = "<exam@example.com>", Subject = "线上笔试通知", Sender = "campus@example.com", SentAt = "2026-09-08T08:00:00+08:00", PlainText = "请按时完成" }
            };
            DateTime now = new DateTime(2026, 9, 8, 9, 0, 0);
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new FakeMailAnalyzer(), () => now);
            MailSyncResult first = sync.Run(false);
            Check(first.CreatedCount == 1 && controller.Data.Items.Count == 1, "First mail sync did not create one todo");
            Check(factory.Client.Requests.Count == 1 && factory.Client.Requests[0].Since == now.AddDays(-7), "First sync did not start seven days back");
            Check(factory.Client.Imported.Single() == "INBOX:42", "Created mail was not starred");
            Check(stateStore.Load().Folders.Single(x => x.FolderId == "INBOX").LastUid == 42, "Folder cursor did not advance");
            MailSyncResult second = sync.Run(true, 2);
            Check(second.CreatedCount == 0 && controller.Data.Items.Count == 1, "Repeated sync duplicated a todo");
            Check(factory.Client.Requests.Last().MinimumUid == 43 && factory.Client.Requests.Last().Since == now.AddDays(-2), "Incremental sync did not combine UID and overlap windows");
            Check(factory.Client.Requests.Count == 2, "Excluded sent folder was fetched");
        });
        Test("mail sync reconciles newly completed source mail before reading new messages", delegate {
            string directory = Path.Combine(root, "mail-completion-flow");
            var controller = new CalendarController(new CalendarStore(directory));
            var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret");
            var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory();
            factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", DisplayName = "收件箱", Attributes = MailFolderAttributes.Inbox, UidValidity = 7 });
            var todo = new Todo { Id = "linked", Title = "完成测评", Date = "2026-09-09", EmailSource = new EmailSource { EmailKey = "message:done@example.com", FolderId = "INBOX", UidValidity = 7, Uid = 9, MessageId = "<done@example.com>" } };
            controller.AddMailTodo(todo); controller.Complete(todo.Id, true, new DateTime(2026, 9, 8, 18, 0, 0));
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new FakeMailAnalyzer(), () => new DateTime(2026, 9, 9, 9, 0, 0));
            MailSyncResult result = sync.Run(false);
            Check(result.ReconciledCount == 1 && factory.Client.Completed.Single() == "INBOX:9", "Completed source mail was not reconciled");
            Check(controller.Data.Items.Single().EmailSource.CompletionSyncState == "synced", "Completion sync state was not persisted");
            Check(result.NewlyCompletedItems.Single().Id == "linked", "Completed item was not supplied to the daily result");
        });
        Test("mail sync retries a failed source star even when the message is no longer fetched", delegate {
            string directory = Path.Combine(root, "mail-import-retry");
            var controller = new CalendarController(new CalendarStore(directory));
            var stateStore = new MailStateStore(directory);
            var state = new MailSyncState(); state.Account.Enabled = true; state.Account.Address = "student@163.com"; stateStore.Save(state);
            var mailSecret = new MailSecretStore(directory); mailSecret.Save("mail-secret");
            var deepSecret = new SecretStore(directory); deepSecret.Save("deep-secret");
            var factory = new FakeMailFactory(); factory.Client.FailNextImport = true;
            factory.Client.Folders.Add(new MailboxFolder { FullName = "INBOX", DisplayName = "收件箱", Attributes = MailFolderAttributes.Inbox, UidValidity = 7 });
            factory.Client.Messages["INBOX"] = new List<MailMessageSnapshot> {
                new MailMessageSnapshot { FolderId = "INBOX", UidValidity = 7, Uid = 12, MessageId = "<retry@example.com>", Subject = "线上笔试通知", Sender = "campus@example.com", SentAt = "2026-09-08T08:00:00+08:00", PlainText = "请完成" }
            };
            var sync = new MailSyncCoordinator(controller, stateStore, mailSecret, deepSecret, factory, new FakeMailAnalyzer(), () => new DateTime(2026, 9, 8, 9, 0, 0));
            sync.Run(false);
            Check(controller.Data.Items.Single().EmailSource.ImportSyncState == "failed", "Failed star was not queued");
            factory.Client.Messages["INBOX"].Clear();
            sync.Run(false);
            Check(factory.Client.Imported.Single() == "INBOX:12" && controller.Data.Items.Single().EmailSource.ImportSyncState == "synced", "Queued star was not retried independently of fetch");
        });
        Test("runtime completes due mail sync before starting the daily summary", delegate {
            string directory = Path.Combine(root, "daily-workflow");
            var controller = new CalendarController(new CalendarStore(directory));
            controller.Commit(data => { data.Agent.Enabled = true; data.Agent.DailyTime = "09:00"; });
            new SecretStore(directory).Save("deep-secret");
            var order = new List<string>();
            var mail = new FakeDailyMailSync(order);
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 8, 9, 0, 0), new OrderedAgent(order), mail)) {
                runtime.Tick();
                WaitUntil(delegate { lock (order) return order.Count == 2; });
                lock (order) Check(order.SequenceEqual(new[] { "mail", "summary" }), "Daily workflow ran summary before mail sync");
                runtime.Tick();
                Check(mail.Runs == 1, "Daily mail sync ran twice on the same day");
            }
        });
        Test("agent prompt includes only actionable nearby work with notes and exact deadlines", delegate {
            var data = new CalendarData();
            data.Items.Add(new Todo { Id = "today", Title = "准备群面", Notes = "复习项目难点", Date = "2026-09-07", Time = "19:30", Important = true });
            data.Items.Add(new Todo { Id = "done", Title = "已经完成", Notes = "不应发送", Date = "2026-09-07", Completed = true });
            data.Items.Add(new Todo { Id = "deleted", Title = "回收站内容", Date = "2026-09-08", Deleted = true });
            data.Items.Add(new Todo { Id = "far", Title = "很久以后的安排", Date = "2026-10-20" });
            Todo deadline = DeadlineTask("2026-09-06T15:00:00+08:00", 48); deadline.Id = "deadline"; deadline.Notes = "邮件要求两天内"; data.Items.Add(deadline);
            string prompt = AgentPrompts.Build(data, new DateTime(2026, 9, 7, 9, 0, 0));
            Check(prompt.Contains("准备群面") && prompt.Contains("复习项目难点"), "Actionable note missing from prompt");
            Check(prompt.Contains("2026-09-08T15:00:00") && prompt.Contains("邮件要求两天内"), "Exact deadline missing from prompt");
            Check(!prompt.Contains("已经完成") && !prompt.Contains("回收站内容") && !prompt.Contains("很久以后的安排"), "Prompt leaked irrelevant work");
        });
        Test("agent prompt includes newly completed work but excludes legacy completion without a timestamp", delegate {
            var data = new CalendarData();
            data.Agent.LastSummaryAt = "2026-09-07T09:00:00+08:00";
            data.Items.Add(new Todo { Title = "刚完成的笔试", Date = "2026-09-08", Completed = true, CompletedAt = "2026-09-08T08:30:00+08:00" });
            data.Items.Add(new Todo { Title = "历史完成项", Date = "2026-08-01", Completed = true, CompletedAt = "" });
            string prompt = AgentPrompts.Build(data, new DateTime(2026, 9, 8, 9, 0, 0));
            Check(prompt.Contains("recentlyCompleted") && prompt.Contains("刚完成的笔试"), "New completion was not sent to the daily summary");
            Check(!prompt.Contains("历史完成项"), "Legacy completion leaked into the daily summary");
        });
        Test("agent summary parser accepts structured JSON and rejects incomplete output", delegate {
            AgentSummary parsed = AgentSummaries.Parse("{\"overview\":\"今天先完成笔试\",\"today\":[\"完成 A\"],\"upcoming\":[\"准备 B\"],\"risks\":[\"A 即将截止\"]}");
            Check(parsed.Overview == "今天先完成笔试" && parsed.Today.Single() == "完成 A" && parsed.Risks.Single().Contains("截止"), "Structured summary was not preserved");
            Throws(delegate { AgentSummaries.Parse("{\"overview\":\"缺少列表\"}"); });
            Throws(delegate { AgentSummaries.Parse("not json"); });
        });
        Test("daily agent runs once after configured time and never when disabled", delegate {
            var settings = new AgentSettings { Enabled = true, DailyTime = "09:00", LastAutomaticDate = "" };
            Check(!AgentSchedule.IsDue(settings, new DateTime(2026, 9, 7, 8, 59, 59)), "Agent ran before configured time");
            Check(AgentSchedule.IsDue(settings, new DateTime(2026, 9, 7, 9, 0, 0)), "Agent missed configured time");
            settings.LastAutomaticDate = "2026-09-07";
            Check(!AgentSchedule.IsDue(settings, new DateTime(2026, 9, 7, 18, 0, 0)), "Agent ran twice in one day");
            settings.Enabled = false; settings.LastAutomaticDate = "";
            Check(!AgentSchedule.IsDue(settings, new DateTime(2026, 9, 7, 18, 0, 0)), "Disabled agent still ran");
        });
        Test("DeepSeek key is current-user encrypted and excluded from calendar backup", delegate {
            string directory = Path.Combine(root, "agent-secret");
            var secrets = new SecretStore(directory); secrets.Save("deepseek-secret-value");
            Check(secrets.Load() == "deepseek-secret-value", "Encrypted key cannot round-trip");
            Check(!File.ReadAllText(secrets.FilePath).Contains("deepseek-secret-value"), "API key was stored as plaintext");
            var controller = new CalendarController(new CalendarStore(directory));
            controller.Commit(data => { data.Agent.Enabled = true; data.Agent.Model = "deepseek-v4-flash"; });
            string export = Path.Combine(root, "agent-export.json"); controller.Store.Export(controller.Data, export);
            Check(!File.ReadAllText(export).Contains("deepseek-secret-value"), "API key leaked into calendar export");
            secrets.Clear(); Check(secrets.Load() == "", "Cleared key remains available");
        });
        Test("agent settings and cached summary survive calendar storage", delegate {
            var controller = new CalendarController(Store("agent-state"));
            controller.Commit(data => {
                data.Agent.Enabled = true; data.Agent.DailyTime = "09:00"; data.Agent.Model = "deepseek-v4-flash";
                data.Agent.LastSummaryAt = "2026-09-07T09:00:00+08:00";
                data.Agent.LastSummary = new AgentSummary { Overview = "今天先完成笔试", Today = new List<string> { "完成笔试" }, Upcoming = new List<string>(), Risks = new List<string>() };
            });
            CalendarData loaded = controller.Store.Load();
            Check(loaded.Agent.Enabled && loaded.Agent.DailyTime == "09:00" && loaded.Agent.LastSummary.Today.Single() == "完成笔试", "Agent settings or cache did not round-trip");
        });
        Test("agent coordinator prepares encrypted jobs and records automatic completion once", delegate {
            string directory = Path.Combine(root, "agent-coordinator"); var controller = new CalendarController(new CalendarStore(directory));
            controller.Commit(data => { data.Agent.Enabled = true; data.Agent.DailyTime = "09:00"; });
            var secrets = new SecretStore(directory); var coordinator = new AgentCoordinator(controller, secrets, () => new DateTime(2026, 9, 7, 9, 0, 0));
            Check(coordinator.Prepare(true) == null, "Automatic job started without an API key");
            secrets.Save("sk-test"); AgentJob job = coordinator.Prepare(true);
            Check(job != null && job.ApiKey == "sk-test" && job.Model == "deepseek-v4-flash", "Automatic job did not use protected settings");
            coordinator.Complete(job, new AgentSummary { Overview = "先准备面试", Today = new List<string>(), Upcoming = new List<string>(), Risks = new List<string>() });
            Check(controller.Data.Agent.LastAutomaticDate == "2026-09-07" && controller.Data.Agent.LastSummary.Overview == "先准备面试", "Automatic completion was not persisted");
            Check(coordinator.Prepare(true) == null, "Completed automatic summary was scheduled again");
        });
        Test("main calendar exposes a compact cached agent card and manual refresh", delegate {
            var controller = new CalendarController(Store("agent-card"));
            controller.Commit(data => {
                data.Agent.LastSummaryAt = "2026-09-07T09:00:00+08:00";
                data.Agent.LastSummary = new AgentSummary { Overview = "今天先完成笔试", Today = new List<string> { "完成笔试" }, Upcoming = new List<string> { "准备群面" }, Risks = new List<string>() };
            });
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 7, 10, 0, 0))) {
                runtime.ShowMain(); Pump();
                Check(Descendants(runtime.Window).OfType<Border>().Any(x => AutomationProperties.GetName(x) == "智能整理卡片"), "Main agent card is missing");
                Check(Descendants(runtime.Window).OfType<TextBlock>().Any(x => x.Text.Contains("今天先完成笔试")), "Cached summary is not visible");
                Check(Descendants(runtime.Window).OfType<Button>().Any(x => Equals(x.Content, "立即总结")), "Manual summary action is missing");
                Capture(runtime.Window, "agent-card.png");
            }
        });
        Test("header search is one compact control aligned with the action buttons", delegate {
            var controller = new CalendarController(Store("compact-search"));
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 7, 10, 0, 0))) {
                runtime.ShowMain(); Pump();
                TextBox input = Descendants(runtime.Window).OfType<TextBox>().First(x => Equals(x.ToolTip, "搜索所有日期的标题和备注"));
                DependencyObject parent = VisualTreeHelper.GetParent(input); Border shell = parent == null ? null : VisualTreeHelper.GetParent(parent) as Border;
                Check(shell != null && shell.CornerRadius.TopLeft >= 9, "Search label and input are still visually separate");
                Check(shell.ActualHeight >= 38 && shell.ActualHeight <= 44 && shell.ActualWidth >= 200 && shell.ActualWidth <= 240, "Search control is not proportioned with header buttons");
            }
        });
        Test("month calendar fits six weeks without its own vertical scrollbar", delegate {
            var controller = new CalendarController(Store("calendar-no-scroll"));
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 7, 10, 0, 0))) {
                runtime.ShowMain(); Pump();
                ScrollViewer calendarScroll = Descendants(runtime.Window).OfType<ScrollViewer>().FirstOrDefault(x => x.Content is Grid && Descendants((Grid)x.Content).OfType<Button>().Count() >= 28);
                Check(calendarScroll == null, "Month grid still uses a vertical scrollbar");
                Check(Descendants(runtime.Window).OfType<Button>().Count(x => (AutomationProperties.GetName(x) ?? "").StartsWith("2026年")) == 42, "Six-week month grid is not fully rendered");
            }
        });
        Test("main calendar uses compact outer and inter-panel spacing", delegate {
            var controller = new CalendarController(Store("compact-gutters"));
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 7, 10, 0, 0))) {
                runtime.ShowMain(); Pump(); Grid rootPanel = (Grid)runtime.Window.Content;
                Grid columns = rootPanel.Children.OfType<Grid>().First(x => Grid.GetRow(x) == 1);
                Border calendarCard = columns.Children.OfType<Border>().First(x => Grid.GetColumn(x) == 0);
                Border sideCard = columns.Children.OfType<Border>().First(x => Grid.GetColumn(x) == 1);
                Point left = calendarCard.TranslatePoint(new Point(), runtime.Window), right = sideCard.TranslatePoint(new Point(), runtime.Window);
                double gap = right.X - left.X - calendarCard.ActualWidth;
                Check(rootPanel.Margin.Left <= 18 && rootPanel.Margin.Right <= 18, "Window gutters are still oversized");
                Check(gap >= 10 && gap <= 16 && calendarCard.Padding.Left <= 15, "Calendar spacing is too wide or cramped");
            }
        });
        Test("settings expose protected DeepSeek configuration without revealing saved key", delegate {
            string directory = Path.Combine(root, "agent-settings-ui"); var controller = new CalendarController(new CalendarStore(directory)); var secrets = new SecretStore(directory); secrets.Save("sk-never-show");
            var settings = new SettingsWindow(controller, delegate { }, secrets, new DeepSeekAgent()); string uiError = null;
            settings.Loaded += delegate {
                try {
                    Check(Find<CheckBox>(settings, x => AutomationProperties.GetName(x) == "启用每日智能整理").IsChecked == false, "Agent enabled state mismatch");
                    Check(Find<TextBox>(settings, x => AutomationProperties.GetName(x) == "每日总结时间").Text == "09:00", "Daily summary time missing");
                    Check(Find<TextBox>(settings, x => AutomationProperties.GetName(x) == "DeepSeek 模型").Text == "deepseek-v4-flash", "Default model missing");
                    Check(Find<PasswordBox>(settings, x => AutomationProperties.GetName(x) == "DeepSeek API Key").Password == "", "Saved API key was revealed in the UI");
                    Check(Descendants(settings).OfType<Button>().Any(x => Equals(x.Content, "测试连接")) && Descendants(settings).OfType<Button>().Any(x => Equals(x.Content, "清除 Key")), "Key management actions are missing");
                    Capture(settings, "agent-settings.png");
                    settings.Close();
                } catch (Exception e) { uiError = e.ToString(); settings.Close(); }
            };
            settings.ShowDialog(); Check(uiError == null, "Agent settings UI failed: " + uiError);
        });
        Test("settings expose protected NetEase mail configuration and manual sync controls", delegate {
            string directory = Path.Combine(root, "mail-settings-ui");
            var controller = new CalendarController(new CalendarStore(directory));
            var secrets = new SecretStore(directory);
            var mailStateStore = new MailStateStore(directory);
            mailStateStore.Save(new MailSyncState {
                LastCompletedAt = "2026-09-08T09:15:00+08:00", LastScannedCount = 12,
                LastCreatedCount = 3, LastReconciledCount = 1, LastError = ""
            });
            var settings = new SettingsWindow(controller, delegate { }, secrets, new DeepSeekAgent(), mailStateStore);
            string uiError = null;
            settings.ContentRendered += delegate {
                try {
                    Check(Find<TextBox>(settings, x => AutomationProperties.GetName(x) == "网易邮箱地址") != null, "Mail address field missing");
                    Check(Find<PasswordBox>(settings, x => AutomationProperties.GetName(x) == "网易邮箱授权码").Password == "", "Saved mail authorization code was revealed");
                    Check(Find<CheckBox>(settings, x => AutomationProperties.GetName(x) == "启用网易邮箱每日同步") != null, "Mail sync toggle missing");
                    Check(Find<Button>(settings, x => (x.Content as string) == "测试邮箱连接") != null, "Mail connection test missing");
                    Check(Find<Button>(settings, x => (x.Content as string) == "立即同步邮件") != null, "Manual mail sync action missing");
                    Check(Find<Button>(settings, x => (x.Content as string) == "打开邮箱日志目录") != null, "Mail log folder action missing");
                    ComboBox days = Find<ComboBox>(settings, x => AutomationProperties.GetName(x) == "手动同步邮件范围");
                    Check(days != null && (days.SelectedItem as ComboBoxItem).Tag.ToString() == "7", "Manual mail day selector missing or has the wrong default");
                    TextBlock status = Find<TextBlock>(settings, x => AutomationProperties.GetName(x) == "邮箱同步状态");
                    Check(status != null && status.Text.Contains("扫描 12") && status.Text.Contains("新增 3") && status.Text.Contains("回写 1"), "Last mail sync status missing");
                } catch (Exception e) { uiError = e.ToString(); }
                settings.Close();
            };
            settings.ShowDialog(); Check(uiError == null, "Mail settings UI failed: " + uiError);
        });
        Test("email-linked todo card identifies its source and inferred deadline", delegate {
            string directory = Path.Combine(root, "mail-source-ui");
            var controller = new CalendarController(new CalendarStore(directory));
            var todo = new Todo {
                Title = "完成校招测评", Date = "2026-09-15",
                Deadline = new DeadlineSpec { StartAt = "2026-09-08T00:00:00+08:00", Amount = 1, Unit = "absolute", ManualEndAt = "2026-09-15T23:59:00+08:00", Confirmed = false, OriginalText = "邮件未给出期限，系统按一周生成" },
                EmailSource = new EmailSource { EmailKey = "message:ui@example.com", FolderId = "INBOX", Uid = 2, MessageId = "<ui@example.com>", Sender = "校园招聘 <campus@example.com>", Subject = "测评邀请", SentAt = "2026-09-08T08:00:00+08:00", DeadlineInferred = true }
            };
            controller.AddMailTodo(todo);
            var window = new CalendarWindow(controller, () => new DateTime(2026, 9, 8, 9, 0, 0));
            window.Show(); window.SelectDate(new DateTime(2026, 9, 8)); Pump();
            string visible = String.Join("\n", Descendants(window).OfType<TextBlock>().Select(x => x.Text));
            Check(visible.Contains("来自邮件") && visible.Contains("校园招聘") && visible.Contains("期限由系统推断"), "Mail source metadata was not visible on the todo card");
            Button sourceButton = Descendants(window).OfType<Button>().FirstOrDefault(x => Equals(x.Content, "打开原邮件"));
            Check(sourceButton != null, "Email-linked todo has no open-original-mail action");
            window.Close();
        });
        Test("mail source navigation extracts the address and supplies a stable webmail search handoff", delegate {
            Type navigation = Type.GetType("LittleCalendar.MailSourceNavigation, LittleCalendar");
            Check(navigation != null, "Mail source navigation helper is missing");
            string address = (string)navigation.GetMethod("SenderAddress").Invoke(null, new object[] { "校园招聘 <campus@example.com>" });
            string search = (string)navigation.GetMethod("SearchText").Invoke(null, new object[] { new EmailSource { Subject = "锐捷网络 AI 面试邀请", MessageId = "<mail-42@example.com>" } });
            string url = (string)navigation.GetMethod("WebmailUrl").Invoke(null, new object[0]);
            Check(address == "campus@example.com", "Sender address was not extracted from the display name");
            Check(search.Contains("锐捷网络") && url.StartsWith("https://mail.163.com", StringComparison.Ordinal), "Open-mail handoff lacks the subject search key or NetEase URL");
        });
        Test("main window exposes unread opportunity notices outside the calendar", delegate {
            var controller = new CalendarController(Store("opportunity-ui"));
            controller.Data.Notices.Add(new OpportunityNotice { Title = "贝泰妮集团校招岗位", Summary = "邀请投递", ReceivedAt = "2026-09-10T08:00:00+08:00", EmailSource = new EmailSource { EmailKey = "message:notice-ui@example.com", Sender = "campus@example.com", Subject = "贝泰妮校招岗位" } });
            controller.Store.Save(controller.Data);
            var window = new CalendarWindow(controller, () => new DateTime(2026, 9, 10, 9, 0, 0));
            window.Show(); Pump();
            Check(Descendants(window).OfType<Button>().Any(x => (x.Content as string ?? "").Contains("机会通知") && (x.Content as string ?? "").Contains("1")), "Header has no unread opportunity badge");
            Check(!Descendants(window).OfType<TextBlock>().Any(x => x.Text == "贝泰妮集团校招岗位"), "Opportunity notice was rendered as a calendar todo");
            window.Close();
        });
        Test("calendar refresh marshals background mail changes onto the UI dispatcher", delegate {
            var controller = new CalendarController(Store("background-refresh"));
            var window = new CalendarWindow(controller, () => new DateTime(2026, 9, 10, 9, 0, 0)); window.Show(); Pump();
            Exception workerError = null;
            var thread = new System.Threading.Thread(new System.Threading.ThreadStart(delegate {
                try { controller.SaveTodo(new Todo { Title = "后台同步写入", Date = "2026-09-10" }); }
                catch (Exception error) { workerError = error; }
            }));
            thread.Start(); thread.Join(); Pump();
            Check(workerError == null, "Background sync triggered cross-thread UI access: " + (workerError == null ? "" : workerError.Message));
            Check(Descendants(window).OfType<TextBlock>().Any(x => x.Text == "后台同步写入"), "Marshaled background refresh did not reach the calendar");
            window.Close();
        });
        Test("runtime updates only the summary and automatic execution is deduplicated", delegate {
            string directory = Path.Combine(root, "agent-runtime"); var controller = new CalendarController(new CalendarStore(directory));
            controller.SaveTodo(new Todo { Title = "原有待办", Date = "2026-09-07" });
            controller.Commit(data => { data.Agent.Enabled = true; data.Agent.DailyTime = "09:00"; }); new SecretStore(directory).Save("sk-test");
            var fake = new FakeAgent();
            using (var runtime = new CalendarRuntime(controller, () => new DateTime(2026, 9, 7, 10, 0, 0), fake)) {
                runtime.StartAgentSummary(false); WaitUntil(delegate { return controller.Data.Agent.LastSummary != null; });
                Check(fake.Calls == 1 && controller.Data.Items.Single().Title == "原有待办", "Manual summary mutated todos or did not run once");
                runtime.Tick(); WaitUntil(delegate { return controller.Data.Agent.LastAutomaticDate == "2026-09-07"; });
                runtime.Tick(); Pump();
                Check(fake.Calls == 2 && controller.Data.Items.Count == 1, "Automatic summary repeated or wrote a todo");
            }
        });
        Test("deadline reminder uses exact 24 hours before cutoff, not previous evening", delegate {
            var data = new CalendarData(); data.Items.Add(DeadlineTask());
            DateTime boundary = new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(Reminders.Due(data, boundary.AddSeconds(-1)).Count == 0, "Early deadline reminder");
            Check(Reminders.Due(data, boundary).Count == 1, "Deadline missed its 24-hour boundary");
        });
        Test("expired deadline stops notifying at the precise cutoff", delegate {
            var data = new CalendarData(); data.Items.Add(DeadlineTask());
            DateTime end = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(Reminders.Due(data, end.AddSeconds(-1)).Count == 1, "Missed late catch-up");
            Check(Reminders.Due(data, end).Count == 0, "Already expired notification");
        });
        Test("deadline metadata survives storage and ambiguous days remain unconfirmed", delegate {
            var controller = new CalendarController(Store("deadline-roundtrip")); controller.SaveTodo(DeadlineTask(amount: 2, unit: "days", confirmed: false));
            string json = File.ReadAllText(controller.Store.FilePath);
            Check(json.Contains("StartAt") && json.Contains("OriginalText") && json.Contains("\"Confirmed\":false"), "Lost deadline source or uncertainty");
            Check(new CalendarController(controller.Store).Data.Items.Count == 1, "Deadline cannot reload");
        });
        Test("relative deadline rolls over leap day and year and overrides stale display date", delegate {
            var leap = DeadlineTask("2028-02-28T23:30:00+08:00");
            var expected = new DateTimeOffset(2028, 3, 1, 23, 30, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(leap.Date == Dates.Key(expected) && leap.Time == expected.ToString("HH:mm"), "Leap deadline wrong");
            var year = DeadlineTask("2026-12-31T15:00:00+08:00");
            expected = new DateTimeOffset(2027, 1, 2, 15, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(year.Date == Dates.Key(expected), "Year rollover wrong");
        });
        Test("deadline rejects invalid durations, missing timezone and unverified working days", delegate {
            Throws(delegate { DeadlineTask(amount: 0); });
            Throws(delegate { DeadlineTask(amount: -1); });
            Throws(delegate { DeadlineTask(start: "2026-09-03T15:00:00"); });
            Throws(delegate { DeadlineTask(unit: "workingDays", amount: 2, confirmed: false); });
            Throws(delegate { DeadlineTask(unit: "unknown"); });
        });
        Test("working-day cutoff is explicitly entered, not guessed by adding calendar days", delegate {
            var item = DeadlineTask(unit: "workingDays", amount: 2, manualEnd: "2026-09-07T18:00:00+08:00");
            Check(item.Date == Dates.Key(new DateTimeOffset(2026, 9, 7, 18, 0, 0, TimeSpan.FromHours(8)).LocalDateTime), "Manual working-day cutoff ignored");
        });
        Test("short deadlines catch up after start but never before the validity window", delegate {
            var data = new CalendarData(); data.Items.Add(DeadlineTask(amount: 2));
            DateTime start = new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(Reminders.Due(data, start.AddMinutes(-1)).Count == 0, "Reminded before email was sent");
            Check(Reminders.Due(data, start.AddMinutes(1)).Count == 1, "Short deadline catch-up missed");
            Reminders.MarkShown(data, data.Items); Reminders.Snooze(data.Items, start.AddMinutes(115));
            Check(Reminders.Due(data, start.AddMinutes(125)).Count == 0, "Snooze revived expired deadline");
        });
        Test("deadline day visibility spans all active days and carries overdue work to today", delegate {
            var item = DeadlineTask();
            DateTime today = new DateTime(2026, 9, 6);
            Check(Deadlines.OnDay(item, new DateTime(2026, 9, 4), today), "Active middle day missing");
            Check(!Deadlines.OnDay(item, new DateTime(2026, 9, 2), today), "Shown before start");
            Check(Deadlines.OnDay(item, today, today), "Overdue task vanished");
            item.Completed = true;
            Check(!Deadlines.OnDay(item, today, today), "Completed deadline carried over");
            Check(Deadlines.OnDay(item, new DateTime(2026, 9, 5), today), "Completed history disappeared");
        });
        Test("countdown, completed status and uncertainty are visible without losing source", delegate {
            var item = DeadlineTask(amount: 2, unit: "days", confirmed: false);
            DateTime now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            string text = Deadlines.Description(item, now);
            Check(text.Contains("待确认") && text.Contains("剩余 1 天 6 小时"), "Uncertainty/countdown missing");
            Check(Deadlines.Description(item, now.AddHours(31)).Contains("已逾期"), "No overdue status");
            item.Completed = true;
            Check(!Deadlines.Status(item, now.AddHours(31)).Contains("逾期"), "Completed task looks overdue");
        });
        Test("same-day cutoff changes reschedule reminders and nested edits cannot mutate stored data", delegate {
            var controller = new CalendarController(Store("deadline-edit")); var item = DeadlineTask(); controller.SaveTodo(item);
            controller.Commit(data => Reminders.MarkShown(data, data.Items));
            var edit = controller.Data.Items[0].Copy(); edit.Deadline.Amount = 49;
            Check(controller.Data.Items[0].Deadline.Amount == 48, "Editing leaked to stored task");
            controller.SaveTodo(edit);
            DateTime now = new DateTimeOffset(2026, 9, 4, 16, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            Check(Reminders.Due(controller.Data, now).Count == 1, "Same-day change never reminds");
            controller.Commit(data => Reminders.MarkShown(data, data.Items));
            Check(Reminders.Due(new CalendarController(controller.Store).Data, now).Count == 0, "Restart duplicates deadline reminder");
        });
        Test("timezone offsets represent instants rather than shifting relative durations", delegate {
            var one = DeadlineTask(start: "2026-09-03T15:00:00+08:00");
            var two = DeadlineTask(start: "2026-09-03T07:00:00Z");
            Check(Deadlines.End(one.Deadline) == Deadlines.End(two.Deadline), "Equivalent mail dates diverge");
            Check(Deadlines.End(one.Deadline).UtcDateTime == new DateTime(2026, 9, 5, 7, 0, 0, DateTimeKind.Utc), "Wrong UTC cutoff");
        });
        Test("v1 upgrade archives original bytes and prevents old-version fallback to stale data", delegate {
            var store = Store("upgrade-v1"); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath));
            string original = "{\"Version\":1,\"ReminderTime\":\"19:00\",\"Items\":[{\"Id\":\"old\",\"Title\":\"旧待办\",\"Date\":\"2026-09-06\",\"Time\":\"\",\"Remind\":true}]}";
            File.WriteAllText(store.FilePath, original); File.WriteAllText(store.FilePath + ".bak", original);
            var controller = new CalendarController(store); controller.SaveTodo(DeadlineTask());
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            Check(json.Deserialize<CalendarData>(File.ReadAllText(store.FilePath + ".bak")).Version == 3, "Current executable can recover upgraded v1 fallback");
            Check(store.Decode(File.ReadAllText(store.FilePath + ".bak")).Items.Single().Title == "旧待办", "Backup is not migrated previous state");
            string archive = Directory.GetFiles(Path.GetDirectoryName(store.FilePath), "calendar.json.pre-v3-*").Single();
            Check(File.ReadAllText(archive) == original, "Original upgrade archive changed");
            Check(new CalendarController(store).Data.Items.Count == 2, "Upgrade lost data");
        });
        Test("reminder fires on previous day at exactly the configured time", delegate {
            var data = Plan(); Check(Reminders.Due(data, new DateTime(2026, 9, 3, 18, 59, 59)).Count == 0, "Early notification");
            Check(Reminders.Due(data, new DateTime(2026, 9, 3, 19, 0, 0)).Count == 1, "Missed the boundary");
        });
        Test("tomorrow and today catch-up, expired tasks stay silent", delegate {
            var data = Plan(); Check(Reminders.Due(data, new DateTime(2026, 9, 4, 9, 0, 0)).Count == 1, "Missed catch-up");
            Check(Reminders.Due(data, new DateTime(2026, 9, 5)).Count == 0, "Expired reminder");
            Check(Reminders.Due(data, new DateTime(2026, 9, 2, 23, 0, 0)).Count == 0, "Two days early");
        });
        Test("completed, recycled and muted tasks do not notify", delegate {
            var data = Plan(); data.Items[0].Completed = true; Check(Reminders.Due(data, new DateTime(2026, 9, 3, 20, 0, 0)).Count == 0, "Completed notified");
            data.Items[0].Completed = false; data.Items[0].Deleted = true; Check(Reminders.Due(data, new DateTime(2026, 9, 3, 20, 0, 0)).Count == 0, "Deleted notified");
            data.Items[0].Deleted = false; data.Items[0].Remind = false; Check(Reminders.Due(data, new DateTime(2026, 9, 3, 20, 0, 0)).Count == 0, "Muted notified");
        });
        Test("one notification per schedule survives restarting the store", delegate {
            var data = Plan(); Reminders.MarkShown(data, data.Items); var store = Store("dedupe"); store.Save(data);
            Check(Reminders.Due(new CalendarStore(Path.GetDirectoryName(store.FilePath)).Load(), new DateTime(2026, 9, 3, 20, 0, 0)).Count == 0, "Repeated reminder");
        });
        Test("snooze reopens at ten minutes and persists", delegate {
            var data = Plan(); var now = new DateTime(2026, 9, 3, 23, 55, 0); Reminders.MarkShown(data, data.Items); Reminders.Snooze(data.Items, now);
            var store = Store("snooze"); store.Save(data); data = store.Load();
            Check(Reminders.Due(data, now.AddMinutes(9)).Count == 0, "Early snooze"); Check(Reminders.Due(data, now.AddMinutes(10)).Count == 1, "Missed midnight snooze");
            Reminders.MarkShown(data, data.Items); Check(Reminders.Due(data, now.AddMinutes(11)).Count == 0, "Repeated snooze");
        });
        Test("leap day, new year and Monday-based month grid", delegate {
            Check(Reminders.Due(Plan("2028-03-01"), new DateTime(2028, 2, 29, 19, 0, 0)).Count == 1, "Leap day");
            Check(Reminders.Due(Plan("2027-01-01"), new DateTime(2026, 12, 31, 19, 0, 0)).Count == 1, "New year");
            Check(Dates.GridStart(new DateTime(2026, 9, 1)) == new DateTime(2026, 8, 31), "Grid offset");
        });
        Test("changing the reminder time schedules the new boundary", delegate {
            var data = Plan(); Reminders.MarkShown(data, data.Items); data.ReminderTime = "20:30";
            Check(Reminders.Due(data, new DateTime(2026, 9, 3, 20, 0, 0)).Count == 0, "Early updated schedule");
            Check(Reminders.Due(data, new DateTime(2026, 9, 3, 20, 30, 0)).Count == 1, "No updated reminder");
        });
        Test("multiple tasks, editing, completion and recycling round-trip", delegate {
            var controller = new CalendarController(Store("roundtrip"));
            var one = new Todo { Title = "一份待办", Date = "2026-09-04", Notes = "多行\n内容", Time = "09:30" };
            var two = new Todo { Title = "第二份", Date = "2026-09-04" }; controller.SaveTodo(one); controller.SaveTodo(two);
            one.Title = "修改后的待办"; controller.SaveTodo(one); controller.Complete(one.Id, true); controller.Trash(two.Id, true);
            var restored = new CalendarController(controller.Store); Check(restored.Data.Items.Count == 2, "Missing task");
            Check(restored.Data.Items.First(x => x.Id == one.Id).Completed, "Completion not saved");
            Check(restored.Data.Items.First(x => x.Id == one.Id).Notes == "多行\n内容", "Notes changed");
            restored.Trash(two.Id, false); Check(!restored.Store.Load().Items.First(x => x.Id == two.Id).Deleted, "Cannot restore");
        });
        Test("rescheduling clears the old delivered flag", delegate {
            var controller = new CalendarController(Store("reschedule")); var task = new Todo { Title = "可改期", Date = "2026-09-04", DeliveredKey = "2026-09-04@19:00" }; controller.SaveTodo(task);
            task.Date = "2026-09-06"; controller.SaveTodo(task); Check(Reminders.Due(controller.Data, new DateTime(2026, 9, 5, 19, 0, 0)).Count == 1, "Rescheduled task never reminds");
        });
        Test("validation rejects blank titles, invalid dates and time", delegate {
            var controller = new CalendarController(Store("validation"));
            Throws(delegate { controller.SaveTodo(new Todo { Title = " " }); });
            Throws(delegate { controller.SaveTodo(new Todo { Title = "日期错误", Date = "2026-02-30" }); });
            Throws(delegate { controller.SaveTodo(new Todo { Title = "时间错误", Time = "25:00" }); });
            Check(controller.Data.Items.Count == 0, "Invalid task saved");
        });
        Test("failed writes preserve existing in-memory data", delegate {
            string file = Path.Combine(root, "not-a-directory"); File.WriteAllText(file, "fixture");
            var controller = new CalendarController(new CalendarStore(file));
            Throws(delegate { controller.SaveTodo(new Todo { Title = "无法写入" }); }); Check(controller.Data.Items.Count == 0, "Failed write changed data");
        });
        Test("atomic updates keep a usable previous-version backup", delegate {
            var store = Store("backup"); var data = Plan(); store.Save(data); data.Items.Add(new Todo { Title = "新增条目" }); store.Save(data);
            Check(store.Decode(File.ReadAllText(store.FilePath + ".bak")).Items.Count == 1, "No prior backup");
            Check(store.Load().Items.Count == 2, "Primary write failed");
        });
        Test("damaged primary is preserved while a valid backup is recovered", delegate {
            var store = Store("recover"); var data = Plan(); store.Save(data); store.Save(data); File.WriteAllText(store.FilePath, "broken");
            Check(store.Load().Items.Count == 1, "Recovery failed"); Check(store.LoadWarning.Length > 0, "Silent recovery");
            Check(Directory.GetFiles(Path.GetDirectoryName(store.FilePath), "*.damaged-*").Length == 1, "Damaged original lost");
        });
        Test("recovered wrong-shaped primary can be saved without destroying the valid fallback", delegate {
            var store = Store("recover-shape"); var data = Plan(); store.Save(data); store.Save(data);
            data.Version = 1;
            File.WriteAllText(store.FilePath + ".bak", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data));
            File.WriteAllText(store.FilePath, "{\"Version\":1,\"Items\":\"bad\"}");
            var controller = new CalendarController(store);
            Check(controller.Data.Items.Count == 1, "Did not recover fallback");
            var edit = controller.Data.Items[0].Copy(); edit.Notes = "恢复后编辑"; controller.SaveTodo(edit);
            Check(store.Load().Items[0].Notes == "恢复后编辑", "Cannot save recovered data");
            Check(store.Decode(File.ReadAllText(store.FilePath + ".bak")).Items.Count == 1, "Recovery save destroyed good backup");
            Check(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<CalendarData>(File.ReadAllText(store.FilePath + ".bak")).Version == 3, "Recovery retained unsafe v1 fallback");
        });
        Test("backup import rejects invalid schemas and duplicate IDs", delegate {
            var store = Store("decode"); Throws(delegate { store.Decode("{\"Version\":99,\"Items\":[]}"); });
            var data = Plan(); data.Items.Add(data.Items[0].Copy()); Throws(delegate { store.Save(data); });
        });
        Test("exported JSON restores dates, settings and notes", delegate {
            var store = Store("export"); var data = Plan(); data.Items[0].Notes = "中文备注\n第二行"; data.ReminderTime = "08:00";
            string export = Path.Combine(root, "export.json"); store.Export(data, export); var restored = store.Decode(File.ReadAllText(export));
            Check(restored.ReminderTime == "08:00" && restored.Items[0].Notes == "中文备注\n第二行", "Export loss");
        });

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream("Theme.xaml")) app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        Test("month view renders connected deadline bars and compact todo bubbles", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-calendar"));
            controller.SaveTodo(new Todo { Title = "准备项目介绍", Date = "2026-09-04", Important = true });
            controller.SaveTodo(DeadlineTask("2026-09-03T15:00:00+08:00", 144));
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                var deadlineBars = Descendants(runtime.Window).OfType<Button>().Where(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 ")).ToList();
                Check(deadlineBars.Count == 2, "Cross-week deadline was not split into two visual segments");
                Border bubble = Descendants(runtime.Window).OfType<Border>().FirstOrDefault(x => (AutomationProperties.GetName(x) ?? "") == "待办气泡 准备项目介绍");
                Check(bubble != null, "Fixed todo has no compact bubble");
                Point bubbleTop = bubble.TranslatePoint(new Point(), runtime.Window), barTop = deadlineBars[0].TranslatePoint(new Point(), runtime.Window);
                Check(bubbleTop.Y + bubble.ActualHeight + 2 <= barTop.Y || barTop.Y + deadlineBars[0].ActualHeight + 2 <= bubbleTop.Y,
                    "Todo bubble overlaps deadline bar: bubble " + bubbleTop.Y + "+" + bubble.ActualHeight + ", bar " + barTop.Y);
                Button day = Find<Button>(runtime.Window, x => AutomationProperties.GetName(x) == "2026年9月4日");
                TextBlock dayNumber = Descendants(day).OfType<TextBlock>().First(x => x.Text == "4");
                Point numberTop = dayNumber.TranslatePoint(new Point(), runtime.Window);
                Check(numberTop.Y + dayNumber.ActualHeight <= barTop.Y + 0.5,
                    "Date number overlaps deadline bars: number " + numberTop.Y.ToString("F1") + "+" + dayNumber.ActualHeight.ToString("F1") + ", bar " + barTop.Y.ToString("F1"));
                Capture(runtime.Window, "visual-calendar.png");
            }
        });
        Test("normal deadline bars keep edge spacing and receive distinct stable colors", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-palette"));
            Todo first = DeadlineTask("2026-09-03T10:00:00+08:00", 96); first.Id = "palette-first"; first.Title = "蓝色期限";
            Todo second = DeadlineTask("2026-09-03T11:00:00+08:00", 96); second.Id = "palette-second"; second.Title = "紫色期限";
            controller.SaveTodo(first); controller.SaveTodo(second);
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                var bars = Descendants(runtime.Window).OfType<Button>().Where(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 ")).ToList();
                Check(bars.Count == 4 && bars.All(x => x.Margin.Left >= 8 && x.Margin.Right >= 8), "Deadline bar touches a date-cell edge");
                var colors = bars.Select(x => ((SolidColorBrush)Descendants(x).OfType<Border>().First(y => y.CornerRadius.TopLeft == 5).Background).Color.ToString()).Distinct().ToList();
                Check(colors.Count == 2, "Different normal deadlines share the same visual color");
            }
        });
        Test("busy day exposes a clear overflow action and opens every item in the side panel", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            DateTime busyDay = new DateTime(2026, 9, 5);
            var controller = new CalendarController(Store("visual-day-overflow"));
            for (int i = 1; i <= 5; i++) controller.SaveTodo(new Todo {
                Id = "busy-" + i,
                Title = "第" + i + "项安排",
                Date = Dates.Key(busyDay),
                Important = i == 5
            });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); Pump();
                Button more = Find<Button>(runtime.Window, x => AutomationProperties.GetName(x) == "查看2026年9月5日全部5项待办");
                Check(Equals(more.Content, "+2 条"), "Busy-day overflow does not show the hidden count");
                Check((more.ToolTip ?? "").ToString().Contains("第1项安排") && (more.ToolTip ?? "").ToString().Contains("第5项安排"), "Busy-day overflow has no complete hover preview");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(runtime.Window.SelectedDate == busyDay, "Busy-day overflow did not select its date");
                Check(Descendants(runtime.Window).OfType<Border>().Count(x => (AutomationProperties.GetName(x) ?? "").StartsWith("待办卡片 ")) == 5, "Side panel did not open every busy-day item");
            }
        });
        Test("busy-day overflow action stays clear of stacked deadline bars", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-day-overflow-lanes"));
            for (int i = 0; i < 3; i++) {
                Todo deadline = DeadlineTask("2026-09-05T1" + i + ":00:00+08:00", 72);
                deadline.Id = "overflow-lane-" + i; controller.SaveTodo(deadline);
            }
            controller.SaveTodo(new Todo { Id = "overflow-fixed", Title = "准备面试资料", Date = "2026-09-05" });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); Pump();
                Button more = Find<Button>(runtime.Window, x => AutomationProperties.GetName(x) == "查看2026年9月5日全部4项待办");
                Point moreTop = more.TranslatePoint(new Point(), runtime.Window);
                var bars = Descendants(runtime.Window).OfType<Button>()
                    .Where(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 ") && Grid.GetRow(x) == 0)
                    .ToList();
                Check(bars.Count == 3, "Expected three stacked deadline bars on the busy day");
                Check(bars.All(bar => {
                    Point barTop = bar.TranslatePoint(new Point(), runtime.Window);
                    return moreTop.Y + more.ActualHeight <= barTop.Y || barTop.Y + bar.ActualHeight <= moreTop.Y;
                }), "Busy-day overflow action covers a deadline bar");
            }
        });
        Test("visual layout type exists for lane and overflow behavior", delegate {
            Type visuals = Type.GetType("LittleCalendar.CalendarVisuals, LittleCalendar");
            Check(visuals != null, "Calendar visual layout is missing");
            var deadlines = new List<Todo> {
                DeadlineTask("2026-09-03T15:00:00+08:00", 144),
                DeadlineTask("2026-09-03T16:00:00+08:00", 48),
                DeadlineTask("2026-09-03T17:00:00+08:00", 48),
                DeadlineTask("2026-09-03T18:00:00+08:00", 48)
            };
            object result = visuals.GetMethod("Build").Invoke(null, new object[] { deadlines, new DateTime(2026, 8, 31), new DateTime(2026, 9, 4), 3 });
            var segments = (System.Collections.IList)result.GetType().GetProperty("Segments").GetValue(result, null);
            var overflow = (IDictionary<string, int>)result.GetType().GetProperty("Overflow").GetValue(result, null);
            Check(segments.Count == 4, "Cross-week split or three-lane limit is wrong");
            Check(overflow["2026-09-03"] == 1 && overflow["2026-09-04"] == 1 && overflow["2026-09-05"] == 1, "Hidden overlap count is wrong");
            object first = segments.Cast<object>().First(x => (int)x.GetType().GetProperty("Week").GetValue(x, null) == 0 && (int)x.GetType().GetProperty("StartColumn").GetValue(x, null) == 3);
            Check((int)first.GetType().GetProperty("EndColumn").GetValue(first, null) == 6, "First week bar is not continuous to Sunday");
            object continuation = segments.Cast<object>().First(x => (int)x.GetType().GetProperty("Week").GetValue(x, null) == 1);
            Check((int)continuation.GetType().GetProperty("StartColumn").GetValue(continuation, null) == 0 && (int)continuation.GetType().GetProperty("EndColumn").GetValue(continuation, null) == 2, "Second week continuation has wrong columns");
        });
        Test("hidden deadline returns when a lane becomes available", delegate {
            var deadlines = new List<Todo>();
            for (int i = 0; i < 3; i++) {
                Todo shortRange = DeadlineTask("2026-09-01T10:00:00+08:00", 24); shortRange.Id = "short-" + i; deadlines.Add(shortRange);
            }
            Todo waiting = DeadlineTask("2026-09-02T10:00:00+08:00", 72); waiting.Id = "waiting"; waiting.Title = "稍后重新出现"; deadlines.Add(waiting);
            CalendarVisualLayout layout = CalendarVisuals.Build(deadlines, new DateTime(2026, 8, 31), new DateTime(2026, 9, 4), 3);
            Check(layout.Overflow.ContainsKey("2026-09-02") && layout.Overflow["2026-09-02"] == 1, "Crowded start day should collapse one deadline");
            Check(!layout.Overflow.ContainsKey("2026-09-03"), "Deadline remains collapsed after a lane is free");
            CalendarBarSegment resumed = layout.Segments.FirstOrDefault(x => x.Item.Id == "waiting");
            Check(resumed != null && resumed.StartColumn == 3 && resumed.EndColumn == 5 && resumed.ContinuesBefore, "Hidden deadline did not resume from September 3");
        });
        Test("deadline remains connected while crossing a month boundary", delegate {
            Todo item = DeadlineTask("2026-09-29T10:00:00+08:00", 144); item.Id = "cross-month";
            CalendarVisualLayout layout = CalendarVisuals.Build(new[] { item }, new DateTime(2026, 8, 31), new DateTime(2026, 9, 4), 3);
            CalendarBarSegment september = layout.Segments.First(x => x.Week == 4);
            CalendarBarSegment october = layout.Segments.First(x => x.Week == 5);
            Check(september.StartColumn == 1 && september.EndColumn == 6 && september.ContinuesAfter, "September portion is not continuous to the week edge");
            Check(october.StartColumn == 0 && october.EndColumn == 0 && october.ContinuesBefore, "October continuation is missing or misplaced");
        });
        Test("month view marks uncertain ranges and collapses a fourth overlap", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-overlap"));
            var a = DeadlineTask("2026-09-01T10:00:00+08:00", 72); a.Id = "a";
            var b = DeadlineTask("2026-09-01T10:00:00+08:00", 3, "days", false); b.Id = "b";
            var c = DeadlineTask("2026-09-01T10:00:00+08:00", 72); c.Id = "c";
            var d = DeadlineTask("2026-09-01T10:00:00+08:00", 48); d.Id = "d"; d.Completed = true;
            foreach (Todo item in new[] { a, b, c, d }) controller.SaveTodo(item);
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Check(Descendants(runtime.Window).OfType<Rectangle>().Any(x => x.StrokeDashArray != null && x.StrokeDashArray.Count > 0), "Unconfirmed deadline has no dashed outline");
                Check(Descendants(runtime.Window).OfType<TextBlock>().Any(x => x.Text == "+1 期限"), "Fourth overlapping deadline is not collapsed");
                TextBlock overflow = Descendants(runtime.Window).OfType<TextBlock>().First(x => x.Text == "+1 期限");
                Check(!overflow.IsHitTestVisible, "Overflow label intercepts the date-cell click");
                Capture(runtime.Window, "visual-overlap.png");
            }
        });
        Test("todo marker avoids a retained second deadline lane", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-lane-hole"));
            Todo early = DeadlineTask("2026-08-31T10:00:00+08:00", 48); early.Id = "early";
            Todo retained = DeadlineTask("2026-09-01T11:00:00+08:00", 96); retained.Id = "retained";
            controller.SaveTodo(early); controller.SaveTodo(retained);
            controller.SaveTodo(new Todo { Title = "当天普通事项", Date = "2026-09-04" });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Border bubble = Descendants(runtime.Window).OfType<Border>().First(x => (AutomationProperties.GetName(x) ?? "") == "待办气泡 当天普通事项");
                Check(bubble.Width == 10 && bubble.Height == 10, "Todo did not compact when a higher deadline lane is occupied");
            }
        });
        Test("three deadline lanes stay inside their date row at minimum window height", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var controller = new CalendarController(Store("visual-min-height"));
            for (int i = 0; i < 3; i++) { Todo item = DeadlineTask("2026-09-01T1" + i + ":00:00+08:00", 96); item.Id = "height-" + i; controller.SaveTodo(item); }
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.Height = runtime.Window.MinHeight; runtime.Window.SelectDate(now.Date); Pump();
                Button day = Find<Button>(runtime.Window, x => AutomationProperties.GetName(x) == "2026年9月4日");
                Point dayTop = day.TranslatePoint(new Point(), runtime.Window); double dayBottom = dayTop.Y + day.ActualHeight;
                var bars = Descendants(runtime.Window).OfType<Button>().Where(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 ") && Grid.GetRow(x) == 0).ToList();
                Check(bars.Count == 3, "Expected three visible deadline lanes");
                Check(bars.All(x => x.ActualHeight >= 18), "Deadline bars are too small to click reliably");
                double lowestBottom = bars.Max(x => x.TranslatePoint(new Point(), runtime.Window).Y + x.ActualHeight);
                Check(lowestBottom <= dayBottom + 0.5, "Deadline lane bleeds into the next week (day height " + day.ActualHeight.ToString("F1") + ", overflow " + (lowestBottom - dayBottom).ToString("F1") + ")");
            }
        });
        Test("deadline bar changes from normal to soon color without rebuilding the calendar", delegate {
            DateTime now = new DateTime(2026, 9, 3, 9, 0, 0);
            var controller = new CalendarController(Store("visual-live-color"));
            var task = DeadlineTask("2026-09-01T10:00:00+08:00", 72); task.Remind = false; controller.SaveTodo(task);
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Button bar = Descendants(runtime.Window).OfType<Button>().First(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 "));
                Border surface = Descendants(bar).OfType<Border>().First(x => x.CornerRadius.TopLeft == 5);
                Check(((SolidColorBrush)surface.Background).Color.ToString() != "#FFF5DEB7", "Normal range is incorrectly shown as soon-due");
                now = new DateTime(2026, 9, 3, 11, 0, 0); runtime.Tick(); Pump();
                Check(((SolidColorBrush)surface.Background).Color.ToString() == "#FFF5DEB7", "Soon range color did not refresh");
            }
        });
        Test("overdue range keeps its true span and appears today as a red bubble", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            var overdue = DeadlineTask("2026-08-01T10:00:00+08:00", 24); overdue.Title = "补做逾期测评";
            Type visuals = Type.GetType("LittleCalendar.CalendarVisuals, LittleCalendar");
            object result = visuals.GetMethod("Build").Invoke(null, new object[] { new List<Todo> { overdue }, new DateTime(2026, 8, 31), now.Date, 3 });
            var segments = (System.Collections.IList)result.GetType().GetProperty("Segments").GetValue(result, null);
            Check(segments.Count == 0, "Expired range was falsely stretched through today");
            var controller = new CalendarController(Store("visual-overdue-bubble")); controller.SaveTodo(overdue);
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Border marker = Descendants(runtime.Window).OfType<Border>().FirstOrDefault(x => (AutomationProperties.GetName(x) ?? "") == "逾期待办气泡 补做逾期测评");
                Check(marker != null, "Today has no overdue marker");
                Check(((SolidColorBrush)marker.Background).Color.ToString() == "#FFF3D7D2", "Overdue marker is not red");
            }
        });
        Test("deadline due earlier today uses one red bar without a duplicate overdue bubble", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            Todo dueToday = DeadlineTask("2026-09-03T09:00:00+08:00", 24); dueToday.Title = "今天刚截止";
            var controller = new CalendarController(Store("visual-due-today")); controller.SaveTodo(dueToday);
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Check(Descendants(runtime.Window).OfType<Button>().Any(x => (AutomationProperties.GetName(x) ?? "").StartsWith("期限横条 今天刚截止")), "Due-today deadline has no range bar");
                Check(!Descendants(runtime.Window).OfType<Border>().Any(x => (AutomationProperties.GetName(x) ?? "") == "逾期待办气泡 今天刚截止"), "Due-today deadline is duplicated as an overdue bubble");
            }
        });
        Test("fixed todo and historical overdue marker do not overlap beside one deadline lane", delegate {
            DateTime now = new DateTime(2026, 9, 4, 10, 0, 0);
            Todo active = DeadlineTask("2026-09-03T10:00:00+08:00", 72); active.Id = "active";
            Todo overdue = DeadlineTask("2026-09-01T10:00:00+08:00", 24); overdue.Id = "overdue"; overdue.Title = "历史逾期";
            var controller = new CalendarController(Store("visual-mixed-markers")); controller.SaveTodo(active); controller.SaveTodo(overdue);
            controller.SaveTodo(new Todo { Title = "普通事项", Date = "2026-09-04" });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Border fixedBubble = Descendants(runtime.Window).OfType<Border>().First(x => (AutomationProperties.GetName(x) ?? "") == "待办气泡 普通事项");
                Border overdueBubble = Descendants(runtime.Window).OfType<Border>().First(x => (AutomationProperties.GetName(x) ?? "") == "逾期待办气泡 历史逾期");
                Check(overdueBubble.Width == 10 && overdueBubble.Height == 10, "Historical overdue marker should compact beside an occupied todo lane");
                Point fixedTop = fixedBubble.TranslatePoint(new Point(), runtime.Window), overdueTop = overdueBubble.TranslatePoint(new Point(), runtime.Window);
                bool separated = fixedTop.Y + fixedBubble.ActualHeight <= overdueTop.Y || overdueTop.Y + overdueBubble.ActualHeight <= fixedTop.Y ||
                    fixedTop.X + fixedBubble.ActualWidth <= overdueTop.X || overdueTop.X + overdueBubble.ActualWidth <= fixedTop.X;
                Check(separated, "Fixed and overdue markers overlap");
            }
        });
        Test("completed deadline only occupies the date where its detail remains available", delegate {
            Todo completed = DeadlineTask("2026-09-01T10:00:00+08:00", 48); completed.Id = "completed-history"; completed.Completed = true;
            CalendarVisualLayout layout = CalendarVisuals.Build(new[] { completed }, new DateTime(2026, 8, 31), new DateTime(2026, 9, 4), 3);
            Check(layout.Segments.Count == 1 && layout.Segments[0].StartColumn == 3 && layout.Segments[0].EndColumn == 3, "Completed deadline occupies dates where its detail is hidden");
            Check(!Deadlines.OnDay(completed, new DateTime(2026, 9, 2), new DateTime(2026, 9, 4)) && Deadlines.OnDay(completed, new DateTime(2026, 9, 3), new DateTime(2026, 9, 4)), "Completed detail availability disagrees with its marker");
        });
        Test("deadline editor creates a preview, saves uncertainty, and reopens its original fields", delegate {
            var controller = new CalendarController(Store("deadline-ui"));
            var editor = new TodoEditor(controller, null, DateTime.Today); string uiError = null;
            editor.Loaded += delegate {
                try {
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "待办内容").Text = "笔试期限界面测试";
                    Find<ComboBox>(editor, x => AutomationProperties.GetName(x) == "安排类型").SelectedIndex = 1;
                    Find<DatePicker>(editor, x => AutomationProperties.GetName(x) == "起算日期").SelectedDate = new DateTime(2026, 9, 3);
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "起算时间").Text = "15:00";
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "时区偏移").Text = "+08:00";
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "期限数值").Text = "2";
                    Find<ComboBox>(editor, x => AutomationProperties.GetName(x) == "期限单位").SelectedIndex = 1;
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "邮件原始要求（选填）").Text = "请在发送后两天内完成";
                    Check(Descendants(editor).OfType<TextBlock>().Any(x => x.Text.Contains("预计截止") && x.Text.Contains("待确认")), "No live estimated preview");
                    Capture(editor, "deadline-editor.png");
                    Find<Button>(editor, x => Equals(x.Content, "保存待办")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                } catch (Exception e) { uiError = e.ToString(); editor.Close(); }
            };
            Check(editor.ShowDialog() == true, "Deadline editor failed: " + uiError);
            Todo saved = controller.Store.Load().Items.Single();
            Check(saved.Deadline != null && !saved.Deadline.Confirmed && saved.Deadline.Amount == 2 && saved.Deadline.OriginalText.Contains("两天"), "UI lost deadline fields");
            var reopened = new TodoEditor(controller, saved, DateTime.Today); uiError = null;
            reopened.Loaded += delegate {
                try {
                    Check(Find<ComboBox>(reopened, x => AutomationProperties.GetName(x) == "安排类型").SelectedIndex == 1, "Reopened as fixed time");
                    Check(Find<TextBox>(reopened, x => AutomationProperties.GetName(x) == "起算时间").Text == "15:00", "Lost source time");
                    Check(Find<CheckBox>(reopened, x => AutomationProperties.GetName(x) == "已核实截止时间").IsChecked == false, "Uncertainty silently confirmed");
                    Find<Button>(reopened, x => Equals(x.Content, "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                } catch (Exception e) { uiError = e.ToString(); reopened.Close(); }
            };
            reopened.ShowDialog(); Check(uiError == null, "Reopen failed: " + uiError);
        });
        Test("calendar displays active deadline on intermediate days and reminder says cutoff", delegate {
            var controller = new CalendarController(Store("deadline-calendar"));
            var task = DeadlineTask(DateTimeOffset.Now.AddDays(-1).ToString("o"), 72); controller.SaveTodo(task);
            using (var runtime = new CalendarRuntime(controller)) {
                runtime.ShowMain(); runtime.Window.SelectDate(DateTime.Today); Pump();
                Check(Descendants(runtime.Window).OfType<TextBlock>().Any(x => x.Text.Contains("截止") && x.Text.Contains("剩余")), "Active deadline missing from day view");
                Capture(runtime.Window, "deadline-calendar.png");
                var popup = new ReminderWindow(controller.Data.Items, DateTime.Now, delegate {}, delegate {}, true);
                popup.Show(); Pump();
                Check(Descendants(popup).OfType<TextBlock>().Any(x => x.Text.Contains("截止") && x.Text.Contains("剩余")), "Popup treats deadline as start time");
                Capture(popup, "deadline-reminder.png"); popup.Close();
            }
        });
        Test("editing notes preserves fractional timestamp precision and reminder delivery", delegate {
            var controller = new CalendarController(Store("deadline-precision"));
            var task = DeadlineTask("2026-09-03T15:00:00.1234567+08:00"); controller.SaveTodo(task);
            controller.Commit(data => Reminders.MarkShown(data, data.Items));
            var saved = controller.Data.Items.Single(); var editor = new TodoEditor(controller, saved, DateTime.Today); string uiError = null;
            editor.Loaded += delegate {
                try {
                    Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "备注（选填）").Text = "只修改备注";
                    Find<Button>(editor, x => Equals(x.Content, "保存待办")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                } catch (Exception e) { uiError = e.ToString(); editor.Close(); }
            };
            Check(editor.ShowDialog() == true, "Precision editor failure: " + uiError);
            var reread = controller.Store.Load().Items.Single();
            Check(Deadlines.End(reread.Deadline) == Deadlines.End(saved.Deadline), "Unrelated edit moved cutoff");
            Check(reread.DeliveredKey == saved.DeliveredKey, "Unrelated edit reset delivery");
        });
        Test("timer refresh preserves focused controls and updates cutoff status with its clock", delegate {
            var controller = new CalendarController(Store("deadline-live")); var task = DeadlineTask(amount: 2); task.Remind = false; controller.SaveTodo(task);
            DateTime now = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                var edit = Find<Button>(runtime.Window, x => Equals(x.Content, "编辑")); edit.Focus(); Pump();
                runtime.Tick(); Pump();
                Check(edit.IsKeyboardFocused, "Timer rebuilt the focused control");
                now = now.AddHours(2); runtime.Tick(); Pump();
                Check(Descendants(runtime.Window).OfType<TextBlock>().Any(x => x.Text.Contains("已逾期")), "Countdown did not use current clock");
            }
        });
        Test("native editor saves through its real button and calendar renders", delegate {
            var controller = new CalendarController(Store("ui"));
            DateTime tomorrow = DateTime.Today.AddDays(1);
            controller.SaveTodo(new Todo { Title = "准备面试材料", Date = Dates.Key(tomorrow), Time = "09:30", Important = true, Notes = "整理项目介绍，检查会议链接。" });
            controller.SaveTodo(new Todo { Title = "完成今日阅读", Date = Dates.Key(DateTime.Today), Completed = true });
            controller.SaveTodo(new Todo { Title = "整理本周学习计划", Date = Dates.Key(tomorrow.AddDays(2)), Time = "20:00" });
            using (var runtime = new CalendarRuntime(controller)) {
                runtime.ShowMain(); runtime.Window.SelectDate(tomorrow); Pump();
                var editor = new TodoEditor(controller, null, tomorrow) { Owner = runtime.Window };
                string uiError = null;
                editor.Loaded += delegate {
                    try {
                        Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "待办内容").Text = "通过界面新增待办";
                        Find<TextBox>(editor, x => AutomationProperties.GetName(x).StartsWith("时间")).Text = "14:00";
                        Find<Button>(editor, x => Equals(x.Content, "保存待办")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    } catch (Exception e) { uiError = e.ToString(); editor.Close(); }
                };
                Check(editor.ShowDialog() == true, "Dialog save failed: " + uiError);
                Check(controller.Store.Load().Items.Any(x => x.Title == "通过界面新增待办" && x.Time == "14:00"), "UI save did not reach storage");
                var interactionTimes = new List<double>();
                for (int i = 0; i < 30; i++) {
                    var stopwatch = Stopwatch.StartNew(); runtime.Window.SelectDate(tomorrow.AddDays(i % 14)); Pump(); stopwatch.Stop(); interactionTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                interactionTimes.Sort(); double p95 = interactionTimes[28];
                Check(p95 < 500, "Day selection exceeds 500 ms at p95");
                results.Add("PERF day selection p95: " + p95.ToString("F1", CultureInfo.InvariantCulture) + " ms (30 native UI interactions)");
                runtime.Window.SelectDate(tomorrow); Pump(); Capture(runtime.Window, "calendar.png");
                Check(Descendants(runtime.Window).OfType<Button>().Count(x => (AutomationProperties.GetName(x) ?? "").StartsWith(DateTime.Today.Year.ToString())) >= 28, "Calendar day controls missing");
            }
        });
        Test("editor options use icon cards and pending task cards keep visual breathing room", delegate {
            DateTime now = new DateTime(2026, 9, 7, 10, 0, 0);
            var controller = new CalendarController(Store("option-card-style"));
            controller.SaveTodo(new Todo { Title = "样式待办", Date = Dates.Key(now.Date) });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.ShowMain(); runtime.Window.SelectDate(now.Date); Pump();
                Border task = Descendants(runtime.Window).OfType<Border>().First(x => (AutomationProperties.GetName(x) ?? "") == "待办卡片 样式待办");
                Check(task.Margin.Left >= 4 && task.Margin.Top >= 8, "Pending task card sits against its surrounding frame");
                Check(((SolidColorBrush)task.Background).Color.ToString() == "#FFF6FBF8", "Pending task card lacks its distinct new-task color");
                var editor = new TodoEditor(controller, null, now.Date) { Owner = runtime.Window }; string uiError = null;
                editor.Loaded += delegate {
                    try {
                        CheckBox important = Find<CheckBox>(editor, x => (AutomationProperties.GetName(x) ?? "") == "重要选项");
                        CheckBox remind = Find<CheckBox>(editor, x => (AutomationProperties.GetName(x) ?? "") == "提醒选项");
                        Check(important.Padding.Left >= 10 && remind.Padding.Left >= 10, "Editor options are not presented as padded choice cards");
                        Check(Descendants(important).OfType<TextBlock>().Any(x => x.Text == "★") && Descendants(remind).OfType<TextBlock>().Any(x => x.Text == "◷"), "Editor choices have no visual icons");
                        Check(Find<TextBox>(editor, x => (AutomationProperties.GetName(x) ?? "") == "待办内容").Padding.Left >= 12, "Editor text fields do not use the refreshed inset border");
                        Check(Find<ComboBox>(editor, x => (AutomationProperties.GetName(x) ?? "") == "安排类型").MinHeight >= 42, "Arrangement type picker is still the compact system-style border");
                        DatePicker arrangedDate = Find<DatePicker>(editor, x => (AutomationProperties.GetName(x) ?? "") == "安排在哪一天");
                        Check(arrangedDate.MinHeight >= 42, "Date field does not match the refreshed editor borders");
                        Border dateFrame = VisualTreeHelper.GetParent(arrangedDate) as Border;
                        Check(dateFrame != null && dateFrame.CornerRadius.TopLeft >= 8, "Date field still exposes the square system border");
                        Find<Button>(editor, x => Equals(x.Content, "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    } catch (Exception e) { uiError = e.ToString(); editor.Close(); }
                };
                editor.ShowDialog(); Check(uiError == null, "Option card styling failed: " + uiError);
            }
        });
        Test("real reminder window appears at bottom-right while calendar is hidden", delegate {
            DateTime now = DateTime.Today.AddHours(19);
            var controller = new CalendarController(Store("notification")); controller.Commit(data => data.Sound = false);
            controller.SaveTodo(new Todo { Title = "检查明天的计划", Date = Dates.Key(now.AddDays(1)), Time = "09:00" });
            using (var runtime = new CalendarRuntime(controller, () => now)) {
                runtime.SessionLockChanged(true); runtime.Tick();
                Check(runtime.ActiveReminder == null && controller.Data.Items[0].DeliveredKey == "", "Reminder consumed while locked");
                runtime.SessionLockChanged(false);
                runtime.Tick(); Pump();
                Check(!runtime.Window.IsVisible, "Main window should stay hidden"); Check(runtime.ActiveReminder != null && runtime.ActiveReminder.IsVisible, "No visible toast");
                var toast = runtime.ActiveReminder; Rect work = SystemParameters.WorkArea;
                Check(Math.Abs(work.Right - toast.Left - toast.ActualWidth - 16) < 3, "Not aligned right"); Check(Math.Abs(work.Bottom - toast.Top - toast.ActualHeight - 16) < 3, "Not aligned bottom");
                Check(!toast.ShowActivated && toast.Topmost, "Reminder steals activation or sits behind apps");
                Capture(toast, "reminder.png");
                Check(new CalendarController(controller.Store).Data.Items[0].DeliveredKey.Length > 0, "Delivery not persisted");
                toast.Close(); runtime.Tick(); Check(runtime.ActiveReminder == null, "Reminder repeats each tick");
                controller.Commit(data => data.Items[0].DeliveredKey = ""); runtime.Tick(); Pump();
                Find<Button>(runtime.ActiveReminder, x => Equals(x.Content, "稍后 10 分钟")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(runtime.ActiveReminder == null && controller.Store.Load().Items[0].SnoozedUntil.Length > 0, "Snooze button not persisted");
                now = now.AddMinutes(10); runtime.Tick(); Pump(); Check(runtime.ActiveReminder != null, "Snoozed popup did not return");
                Find<Button>(runtime.ActiveReminder, x => Equals(x.Content, "打开日历")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(runtime.Window.IsVisible && runtime.Window.SelectedDate == now.Date.AddDays(1), "Open reminder did not select date");
                runtime.Window.Close(); Check(!runtime.Window.IsVisible, "Close did not hide to tray");
                runtime.ShowMain(); Check(runtime.Window.IsVisible, "Cannot reopen from tray");
            }
        });
        app.Shutdown();
        results.Add(passed + " passed; " + failed + " failed");
        File.WriteAllLines(Path.Combine(root, "results.txt"), results);
        Console.WriteLine(results.Last()); Console.WriteLine("Artifacts: " + root);
        return failed == 0 ? 0 : 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) {
            DependencyObject child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject { return Descendants(root).OfType<T>().First(predicate); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { frame.Continue = false; })); Dispatcher.PushFrame(frame);
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); System.Threading.Thread.Sleep(10); }
        Check(condition(), "Timed out waiting for background work.");
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, name))) encoder.Save(file);
    }
}
