using System.Collections.Generic;

namespace LittleCalendar
{
    public sealed class ChatFolderCursorSummary
    {
        public string DisplayName { get; set; }
        public string PreviousScannedAt { get; set; }
        public string CompletedAt { get; set; }
        public string Error { get; set; }
        public uint PreviousUid { get; set; }
        public uint RequestedMinimumUid { get; set; }
        public uint FinalUid { get; set; }
        public int FetchedCount { get; set; }
        public ChatFolderCursorSummary()
        {
            DisplayName = ""; PreviousScannedAt = ""; CompletedAt = ""; Error = "";
        }
    }

    public sealed class ChatSyncSummary
    {
        public string PreviousCompletedAt { get; set; }
        public string StartedAt { get; set; }
        public string CompletedAt { get; set; }
        public int ScannedCount { get; set; }
        public int CreatedCount { get; set; }
        public int NoticeCount { get; set; }
        public int IgnoredCount { get; set; }
        public int ErrorCount { get; set; }
        public List<ChatFolderCursorSummary> Folders { get; set; }
        public ChatSyncSummary()
        {
            PreviousCompletedAt = ""; StartedAt = ""; CompletedAt = "";
            Folders = new List<ChatFolderCursorSummary>();
        }
    }

    public sealed class ChatMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public string CreatedAt { get; set; }
        public string Intent { get; set; }
        public List<string> TodoIds { get; set; }
        public ChatSyncSummary Sync { get; set; }
        public ChatMessage()
        {
            Id = ""; Role = ""; Text = ""; CreatedAt = ""; Intent = "";
            TodoIds = new List<string>();
        }
        public static ChatMessage CreateSafeDisplay(string id, string role, string displayText, string createdAt, string intent = "", List<string> todoIds = null, ChatSyncSummary sync = null)
        {
            return new ChatMessage {
                Id = id, Role = role, Text = DisplaySummary(role, intent), CreatedAt = createdAt, Intent = intent,
                TodoIds = todoIds ?? new List<string>(), Sync = sync
            };
        }
        internal static string DisplaySummary(string role, string intent)
        {
            role = (role ?? "").Trim().ToLowerInvariant(); intent = (intent ?? "").Trim().ToLowerInvariant();
            if (role == "user") return "已保存用户消息";
            if (role == "system") return "已保存系统消息";
            if (intent == "sync_mail_incremental") return "已完成邮箱增量同步";
            if (intent == "list_tasks") return "已完成任务查询";
            return "已保存助手消息";
        }
    }

    public sealed class ChatHistory
    {
        public int Version { get; set; }
        public List<ChatMessage> Messages { get; set; }
        public ChatHistory()
        {
            Version = 1; Messages = new List<ChatMessage>();
        }
    }
}
