using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LittleCalendar
{
    public interface IMailSyncService
    {
        bool IsDue(DateTime now);
        MailSyncResult Run(bool automatic, int days = 0);
    }

    public sealed class MailSyncCoordinator : IMailSyncService
    {
        private readonly CalendarController controller;
        private readonly MailStateStore stateStore;
        private readonly MailSecretStore mailSecret;
        private readonly SecretStore deepSeekSecret;
        private readonly IMailboxClientFactory clientFactory;
        private readonly IMailActionAnalyzer analyzer;
        private readonly Func<DateTime> clock;
        private readonly MailDiagnosticLog diagnostics;
        private bool running;

        public MailSyncCoordinator(CalendarController controller, MailStateStore stateStore, MailSecretStore mailSecret,
            SecretStore deepSeekSecret, IMailboxClientFactory clientFactory, IMailActionAnalyzer analyzer, Func<DateTime> clock,
            MailDiagnosticLog diagnostics = null)
        {
            this.controller = controller; this.stateStore = stateStore; this.mailSecret = mailSecret;
            this.deepSeekSecret = deepSeekSecret; this.clientFactory = clientFactory; this.analyzer = analyzer;
            this.clock = clock ?? (() => DateTime.Now);
            this.diagnostics = diagnostics;
        }

        public MailSyncResult Run(bool automatic, int days = 0)
        {
            lock (this) {
                if (running) throw new InvalidOperationException("邮箱同步正在进行，请稍后再试。");
                running = true;
            }
            try { return RunCore(automatic, days); }
            finally { lock (this) running = false; }
        }

        public bool IsDue(DateTime now)
        {
            MailSyncState state;
            try { state = stateStore.Load(); } catch { return false; }
            if (state.Account == null || !state.Account.Enabled || !Dates.IsTime(controller.Data.Agent.DailyTime)) return false;
            if (!mailSecret.HasKey || !deepSeekSecret.HasKey || state.LastAutomaticDate == Dates.Key(now.Date)) return false;
            DateTimeOffset retryAt;
            if (!String.IsNullOrEmpty(state.NextRetryAt) && DateTimeOffset.TryParse(state.NextRetryAt, out retryAt) && new DateTimeOffset(now) < retryAt) return false;
            return now.TimeOfDay >= Dates.Time(controller.Data.Agent.DailyTime);
        }

        private MailSyncResult RunCore(bool automatic, int days)
        {
            DateTime now = clock();
            MailSyncState state = stateStore.Load();
            if (state.Account == null || String.IsNullOrWhiteSpace(state.Account.Address))
                throw new InvalidOperationException("请先在提醒设置中填写网易邮箱地址。");
            if (automatic && !state.Account.Enabled) return new MailSyncResult();
            string authorizationCode = mailSecret.Load();
            string apiKey = deepSeekSecret.Load();
            if (String.IsNullOrWhiteSpace(authorizationCode)) throw new InvalidOperationException("请先填写网易邮箱授权码。");
            if (String.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("请先填写 DeepSeek API Key。");
            int windowDays = automatic ? 2 : NormalizeDays(days == 0 ? state.Account.ManualSyncDays : days);
            if (!automatic) state.Account.ManualSyncDays = windowDays;
            Log("SYNC_START", "mode=" + (automatic ? "automatic" : "manual") + " days=" + windowDays + " since=" + now.AddDays(-windowDays).ToString("o"));
            state.LastStartedAt = new DateTimeOffset(now).ToString("o");
            state.LastError = "";
            stateStore.Save(state);
            var result = new MailSyncResult();
            try {
                using (IMailboxClient mailbox = clientFactory.Create()) {
                    mailbox.Connect(new MailConnectionOptions {
                        Address = state.Account.Address, Host = state.Account.Host, Port = state.Account.Port, UseSsl = state.Account.UseSsl
                    }, authorizationCode);
                    IList<MailboxFolder> discovered = mailbox.ListFolders().Where(MailFolderPolicy.ShouldInclude).ToList();
                    MergeFolders(state, discovered);
                    ReconcileImported(mailbox, result);
                    ReconcileCompleted(mailbox, discovered, result, now);
                    foreach (MailboxFolder folder in discovered) {
                        MailFolderState cursor = state.Folders.First(x => x.FolderId == folder.FullName);
                        if (!cursor.Enabled) continue;
                        try {
                            if (cursor.UidValidity != 0 && folder.UidValidity != 0 && cursor.UidValidity != folder.UidValidity) cursor.LastUid = 0;
                            if (folder.UidValidity != 0) cursor.UidValidity = folder.UidValidity;
                            var request = new MailFetchRequest {
                                Since = now.AddDays(-windowDays),
                                MinimumUid = !automatic || cursor.LastUid == 0 || cursor.LastUid == UInt32.MaxValue ? 0 : cursor.LastUid + 1
                            };
                            IList<MailMessageSnapshot> messages = mailbox.Fetch(folder, request).OrderBy(x => x.Uid).ToList();
                            Log("FOLDER_FETCHED", "folder=" + SafeLog(folder.FullName) + " count=" + messages.Count);
                            bool folderFailed = false;
                            uint highest = cursor.LastUid;
                            foreach (MailMessageSnapshot message in messages) {
                                result.ScannedCount++;
                                if (!ProcessMessage(mailbox, message, state, result, now, apiKey, controller.Data.Agent.Model, automatic)) folderFailed = true;
                                if (!folderFailed && message.Uid > highest) highest = message.Uid;
                            }
                            if (!folderFailed) cursor.LastUid = highest;
                            cursor.LastScannedAt = new DateTimeOffset(now).ToString("o");
                            stateStore.Save(state);
                        } catch (Exception folderError) {
                            string safe = SafeError(folderError.Message);
                            result.Errors.Add((folder.DisplayName ?? folder.FullName) + "读取失败：" + safe);
                            Log("FOLDER_FAILED", "folder=" + SafeLog(folder.FullName) + " error=" + safe);
                        }
                    }
                    state.AllowsCustomKeywords = mailbox.Capabilities.AllowsCustomKeywords;
                    state.WritableKeywords = mailbox.Capabilities.WritableKeywords.ToList();
                }
                state.LastCompletedAt = new DateTimeOffset(now).ToString("o");
                state.LastCompletionReconciledAt = state.LastCompletedAt;
                if (automatic) state.LastAutomaticDate = Dates.Key(now.Date);
                state.LastScannedCount = result.ScannedCount;
                state.LastCreatedCount = result.CreatedCount;
                state.LastReconciledCount = result.ReconciledCount;
                state.NextRetryAt = "";
                state.LastError = result.Errors.Count == 0 ? "" : "部分邮件处理失败：" + SafeError(String.Join("；", result.Errors.Take(3)));
                TrimProcessed(state.ProcessedKeys);
                stateStore.Save(state);
                Log(result.Errors.Count == 0 ? "SYNC_COMPLETE" : "SYNC_PARTIAL", "scanned=" + result.ScannedCount + " created=" + result.CreatedCount + " errors=" + result.Errors.Count);
                return result;
            } catch (Exception error) {
                state.LastError = SafeError(error.Message);
                state.NextRetryAt = new DateTimeOffset(now.AddHours(1)).ToString("o");
                stateStore.Save(state);
                Log("SYNC_FAILED", "error=" + SafeError(error.Message));
                throw;
            }
        }

        private bool ProcessMessage(IMailboxClient mailbox, MailMessageSnapshot message, MailSyncState state, MailSyncResult result,
            DateTime now, string apiKey, string model, bool automatic)
        {
            string key = MailIdentity.Key(message);
            string identity = "folder=" + SafeLog(message.FolderId) + " uid=" + message.Uid + " subject=" + SafeLog(message.Subject);
            Log("MAIL_START", identity);
            Todo existing = controller.FindByEmailKey(key);
            if (existing != null) {
                if (existing.EmailSource.ImportSyncState != "synced") TryMarkImported(mailbox, existing, result);
                if (!state.ProcessedKeys.Contains(key)) state.ProcessedKeys.Add(key);
                Log("TODO_EXISTS", identity);
                return true;
            }
            if (automatic && state.ProcessedKeys.Contains(key)) { Log("PROCESSED_SKIP", identity); return true; }
            try {
                NormalizedMail normalized = MailContent.Normalize(message);
                if (MailPrefilter.ShouldSkip(normalized)) { if (!state.ProcessedKeys.Contains(key)) state.ProcessedKeys.Add(key); Log("PREFILTER_SKIP", identity); return true; }
                var timer = Stopwatch.StartNew();
                Log("LLM_START", identity + " bodyChars=" + (normalized.Text ?? "").Length);
                MailAction action = analyzer.Analyze(normalized, now, apiKey, model);
                timer.Stop();
                Log("LLM_RESULT", identity + " elapsedMs=" + timer.ElapsedMilliseconds + " actionable=" + action.Actionable
                    + " disposition=" + SafeLog(action.Disposition) + " category=" + SafeLog(action.Category) + " confidence=" + action.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                    + " deadlineKind=" + SafeLog(action.DeadlineKind) + " title=" + SafeLog(action.Title));
                mailbox.KeepAlive();
                if (action.Disposition == "notice") {
                    OpportunityNotice notice = MailOpportunityMapper.ToNotice(action, message);
                    notice.EmailSource.AccountId = Hash(state.Account.Address.Trim().ToLowerInvariant());
                    if (controller.AddOpportunityNotice(notice)) {
                        result.NoticeCount++; result.NewNotices.Add(notice.Copy()); Log("NOTICE_CREATED", identity + " notice=" + SafeLog(notice.Title));
                    } else Log("NOTICE_DUPLICATE", identity + " notice=" + SafeLog(notice.Title));
                    if (!state.ProcessedKeys.Contains(key)) state.ProcessedKeys.Add(key);
                    return true;
                }
                if (action.Disposition != "todo" || !action.Actionable) { if (!state.ProcessedKeys.Contains(key)) state.ProcessedKeys.Add(key); return true; }
                Todo todo = MailDeadlineMapper.ToTodo(action, message, now);
                todo.EmailSource.AccountId = Hash(state.Account.Address.Trim().ToLowerInvariant());
                if (controller.AddMailTodo(todo)) {
                    result.CreatedCount++; result.NewTodos.Add(todo.Copy()); Log("TODO_CREATED", identity + " todo=" + SafeLog(todo.Title) + " date=" + todo.Date);
                }
                Todo stored = controller.FindByEmailKey(key);
                if (stored != null) TryMarkImported(mailbox, stored, result);
                else Log("TODO_DUPLICATE", identity + " todo=" + SafeLog(todo.Title));
                if (!state.ProcessedKeys.Contains(key)) state.ProcessedKeys.Add(key);
                return true;
            } catch (Exception error) {
                result.Errors.Add((message.Subject ?? "无主题") + "：" + SafeError(error.Message));
                Log("MAIL_ERROR", identity + " error=" + SafeError(error.Message));
                return false;
            }
        }

        private void TryMarkImported(IMailboxClient mailbox, Todo item, MailSyncResult result)
        {
            try {
                mailbox.MarkImported(Locator(item.EmailSource));
                UpdateSource(item.Id, source => { source.ImportSyncState = "synced"; source.ImportSyncError = ""; });
            } catch (Exception error) {
                string safe = SafeError(error.Message);
                UpdateSource(item.Id, source => { source.ImportSyncState = "failed"; source.ImportSyncError = safe; });
                result.Errors.Add("邮件加星失败：" + safe);
            }
        }

        private void ReconcileImported(IMailboxClient mailbox, MailSyncResult result)
        {
            List<Todo> pending = controller.Data.Items.Where(x => !x.Deleted && x.EmailSource != null &&
                (x.EmailSource.ImportSyncState == "pending" || x.EmailSource.ImportSyncState == "failed")).Select(x => x.Copy()).ToList();
            foreach (Todo item in pending) TryMarkImported(mailbox, item, result);
        }

        private void ReconcileCompleted(IMailboxClient mailbox, IList<MailboxFolder> folders, MailSyncResult result, DateTime now)
        {
            List<Todo> pending = controller.Data.Items.Where(x => x.Completed && !x.Deleted && x.EmailSource != null &&
                (x.EmailSource.CompletionSyncState == "pending" || x.EmailSource.CompletionSyncState == "failed")).Select(x => x.Copy()).ToList();
            foreach (Todo item in pending) {
                result.NewlyCompletedItems.Add(item.Copy());
                try {
                    try { mailbox.MarkCompleted(Locator(item.EmailSource)); }
                    catch {
                        MailMessageSnapshot moved = mailbox.FindByMessageId(folders, item.EmailSource.MessageId);
                        if (moved == null) {
                            UpdateSource(item.Id, source => { source.CompletionSyncState = "source-not-found"; source.CompletionSyncError = "未找到源邮件。"; });
                            continue;
                        }
                        mailbox.MarkCompleted(new MailMessageLocator { FolderId = moved.FolderId, UidValidity = moved.UidValidity, Uid = moved.Uid, MessageId = moved.MessageId });
                    }
                    string at = new DateTimeOffset(now).ToString("o");
                    UpdateSource(item.Id, source => { source.CompletionSyncState = "synced"; source.CompletionSyncedAt = at; source.CompletionSyncError = ""; });
                    result.ReconciledCount++;
                } catch (Exception error) {
                    string safe = SafeError(error.Message);
                    UpdateSource(item.Id, source => { source.CompletionSyncState = "failed"; source.CompletionSyncError = safe; });
                    result.Errors.Add("完成状态回写失败：" + safe);
                }
            }
        }

        private void UpdateSource(string id, Action<EmailSource> update)
        {
            controller.Commit(data => {
                Todo current = data.Items.First(x => x.Id == id);
                if (current.EmailSource != null) update(current.EmailSource);
            });
        }

        private static MailMessageLocator Locator(EmailSource source)
        {
            return new MailMessageLocator { FolderId = source.FolderId, UidValidity = source.UidValidity, Uid = source.Uid, MessageId = source.MessageId };
        }

        private static void MergeFolders(MailSyncState state, IEnumerable<MailboxFolder> folders)
        {
            foreach (MailboxFolder folder in folders) {
                MailFolderState existing = state.Folders.FirstOrDefault(x => x.FolderId == folder.FullName);
                if (existing == null) state.Folders.Add(new MailFolderState {
                    FolderId = folder.FullName, DisplayName = folder.DisplayName, Enabled = true, UidValidity = folder.UidValidity
                });
                else {
                    existing.DisplayName = folder.DisplayName;
                    if (existing.UidValidity == 0) existing.UidValidity = folder.UidValidity;
                }
            }
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        private static void TrimProcessed(List<string> keys)
        {
            if (keys.Count > 5000) keys.RemoveRange(0, keys.Count - 5000);
        }
        private static string SafeError(string value)
        {
            value = (value ?? "未知错误").Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300);
        }
        private static int NormalizeDays(int days)
        {
            return new[] { 1, 3, 7, 14, 30 }.Contains(days) ? days : 7;
        }
        private static string SafeLog(string value)
        {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 120 ? value : value.Substring(0, 120);
        }
        private void Log(string stage, string detail)
        {
            if (diagnostics != null) diagnostics.Ai(stage, detail);
        }
    }
}
