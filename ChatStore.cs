using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LittleCalendar
{
    public sealed class ChatHistoryStore
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public string FilePath { get; private set; }
        public string LoadWarning { get; private set; }

        public ChatHistoryStore(string directory)
        {
            FilePath = Path.Combine(directory, "chat-history.json");
            LoadWarning = "";
        }

        public ChatHistory Load()
        {
            LoadWarning = "";
            if (!File.Exists(FilePath)) return new ChatHistory();
            try { return Decode(File.ReadAllText(FilePath, Encoding.UTF8)); }
            catch (Exception primaryError) {
                string backup = FilePath + ".bak";
                if (File.Exists(backup)) {
                    try {
                        ChatHistory recovered = Decode(File.ReadAllText(backup, Encoding.UTF8));
                        File.Copy(FilePath, FilePath + ".damaged-" + Guid.NewGuid().ToString("N"), false);
                        LoadWarning = "对话记录异常，已读取上次备份；原文件已保留。";
                        return recovered;
                    } catch (Exception backupError) {
                        throw new IOException("对话记录和备份均无法读取，原文件未改动。", new AggregateException(primaryError, backupError));
                    }
                }
                throw new IOException("无法读取对话记录，原文件未改动。", primaryError);
            }
        }

        public void Save(ChatHistory history)
        {
            ChatHistory normalized = Decode(serializer.Serialize(Project(history)));
            normalized.Messages = normalized.Messages.Skip(Math.Max(0, normalized.Messages.Count - 50)).ToList();
            string json = serializer.Serialize(normalized);
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            string temporary = FilePath + ".tmp";
            WriteFlushed(temporary, json);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true);
            else File.Move(temporary, FilePath);
        }

        public void Append(ChatMessage message)
        {
            ChatHistory history = Load();
            history.Messages.Add(message);
            Save(history);
        }

        public void Clear() { Save(new ChatHistory()); }

        private ChatHistory Decode(string json)
        {
            ChatHistory source = serializer.Deserialize<ChatHistory>(json);
            if (source == null || source.Version != 1) throw new InvalidDataException("不支持或损坏的对话记录格式。");
            ChatHistory history = Project(source);
            if (history.Messages == null) history.Messages = new List<ChatMessage>();
            foreach (ChatMessage message in history.Messages) Normalize(message);
            return history;
        }

        private static ChatHistory Project(ChatHistory history)
        {
            if (history == null || history.Version != 1) throw new InvalidDataException("不支持或损坏的对话记录格式。");
            var projected = new ChatHistory { Version = 1, Messages = new List<ChatMessage>() };
            foreach (ChatMessage message in history.Messages ?? new List<ChatMessage>()) projected.Messages.Add(Project(message));
            return projected;
        }

        private static ChatMessage Project(ChatMessage message)
        {
            if (message == null) throw new InvalidDataException("对话消息无效。");
            var projected = new ChatMessage {
                Id = ProjectId(message.Id), Role = message.Role, CreatedAt = message.CreatedAt,
                Intent = ProjectIntent(message.Intent), TodoIds = ProjectTodoIds(message.TodoIds), Sync = ProjectSync(message.Sync)
            };
            projected.Display = ChatDisplay.Project(message.Display, (projected.Role ?? "").Trim().ToLowerInvariant());
            projected.Text = projected.Display == null ? ChatMessage.DisplaySummary(projected.Role, projected.Intent) : projected.Display.Text;
            return projected;
        }

        private static string ProjectId(string value)
        {
            Guid id;
            return Guid.TryParseExact(value, "N", out id) ? id.ToString("N") : Guid.NewGuid().ToString("N");
        }

        private static string ProjectIntent(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return new[] { "", "sync_mail_incremental", "list_tasks", "chat" }.Contains(value) ? value : "";
        }

        private static List<string> ProjectTodoIds(List<string> todoIds)
        {
            return (todoIds ?? new List<string>()).Select(value => {
                Guid id;
                return Guid.TryParseExact(value, "N", out id) ? id.ToString("N") : "";
            }).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(50).ToList();
        }

        private static ChatSyncSummary ProjectSync(ChatSyncSummary sync)
        {
            if (sync == null) return null;
            var projected = new ChatSyncSummary {
                PreviousCompletedAt = sync.PreviousCompletedAt, StartedAt = sync.StartedAt, CompletedAt = sync.CompletedAt,
                ScannedCount = Math.Max(0, sync.ScannedCount), CreatedCount = Math.Max(0, sync.CreatedCount), NoticeCount = Math.Max(0, sync.NoticeCount),
                IgnoredCount = Math.Max(0, sync.IgnoredCount), ErrorCount = Math.Max(0, sync.ErrorCount), Folders = new List<ChatFolderCursorSummary>()
            };
            int folderNumber = 0;
            foreach (ChatFolderCursorSummary folder in (sync.Folders ?? new List<ChatFolderCursorSummary>()).Where(folder => folder != null).Take(50)) {
                folderNumber++;
                projected.Folders.Add(new ChatFolderCursorSummary {
                    DisplayName = "文件夹 " + folderNumber, PreviousScannedAt = folder.PreviousScannedAt, CompletedAt = folder.CompletedAt,
                    PreviousUid = folder.PreviousUid, RequestedMinimumUid = folder.RequestedMinimumUid, FinalUid = folder.FinalUid,
                    FetchedCount = Math.Max(0, folder.FetchedCount), Error = String.IsNullOrWhiteSpace(folder.Error) ? "" : "同步出错"
                });
            }
            return projected;
        }

        private static void Normalize(ChatMessage message)
        {
            if (message == null || String.IsNullOrWhiteSpace(message.Id)) throw new InvalidDataException("对话消息缺少标识。");
            message.Id = message.Id.Trim();
            message.Role = (message.Role ?? "").Trim().ToLowerInvariant();
            if (!new[] { "user", "assistant", "system" }.Contains(message.Role)) throw new InvalidDataException("对话消息角色无效。");
            message.Text = (message.Text ?? "").Trim();
            if (message.Text.Length > 4000) message.Text = message.Text.Substring(0, 4000);
            DateTimeOffset timestamp;
            if (!DateTimeOffset.TryParse(message.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp))
                throw new InvalidDataException("对话消息时间无效。");
            message.CreatedAt = timestamp.ToString("o", CultureInfo.InvariantCulture);
            message.Intent = (message.Intent ?? "").Trim();
            if (message.TodoIds == null) message.TodoIds = new List<string>();
            message.TodoIds = message.TodoIds.Where(id => !String.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
            NormalizeSync(message.Sync);
        }

        private static void NormalizeSync(ChatSyncSummary sync)
        {
            if (sync == null) return;
            sync.PreviousCompletedAt = NormalizeOptionalTimestamp(sync.PreviousCompletedAt, "上次同步完成时间");
            sync.StartedAt = NormalizeOptionalTimestamp(sync.StartedAt, "同步开始时间");
            sync.CompletedAt = NormalizeOptionalTimestamp(sync.CompletedAt, "同步完成时间");
            if (sync.Folders == null) sync.Folders = new List<ChatFolderCursorSummary>();
            foreach (ChatFolderCursorSummary folder in sync.Folders) {
                if (folder == null) throw new InvalidDataException("对话同步文件夹摘要无效。");
                folder.DisplayName = (folder.DisplayName ?? "").Trim();
                folder.PreviousScannedAt = NormalizeOptionalTimestamp(folder.PreviousScannedAt, "文件夹上次扫描时间");
                folder.CompletedAt = NormalizeOptionalTimestamp(folder.CompletedAt, "文件夹同步完成时间");
                folder.Error = (folder.Error ?? "").Trim();
            }
        }

        private static string NormalizeOptionalTimestamp(string value, string field)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return "";
            DateTimeOffset timestamp;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp))
                throw new InvalidDataException(field + "无效。");
            return timestamp.ToString("o", CultureInfo.InvariantCulture);
        }

        private static void WriteFlushed(string path, string json)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
