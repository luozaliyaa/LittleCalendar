using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LittleCalendar
{
    // Only these display projections may carry text into history. Legacy Text is never trusted.
    public sealed class ChatDisplay
    {
        public string Kind { get; set; }
        public string Text { get; set; }
        public static ChatDisplay UserInput(string value) { return Create("user_input", value); }
        internal static ChatDisplay LocalSummary(string value) { return Create("local_summary", value); }
        internal static ChatDisplay AssistantAnswer(string value) { return Create("assistant_answer", value); }
        private static ChatDisplay Create(string kind, string value) { return new ChatDisplay { Kind = kind, Text = Clean(value, 4000) }; }
        internal static ChatDisplay Project(ChatDisplay value, string role)
        {
            if (value == null) return null;
            if (!((role == "user" && value.Kind == "user_input") ||
                (role == "assistant" && (value.Kind == "local_summary" || value.Kind == "assistant_answer")))) return null;
            return Create(value.Kind, value.Text);
        }
        public static string Clean(string value, int limit)
        {
            value = (value ?? "").Trim();
            // Payload-shaped text is never a display projection, even if loaded with a forged kind.
            if (value.Contains(ChatLanguagePrompts.SystemPrompt) || Regex.IsMatch(value, @"(?im)(^\s*[\[{]|^\s*(system|developer|from|subject|message-id|发件人|主题)\s*:|\""(choices|messages|output|instructions|todoIds)\""\s*:)", RegexOptions.CultureInvariant))
                return "[已省略原始载荷]";
            value = Regex.Replace(value, @"(?i)\bsk-[A-Za-z0-9_-]+", "[REDACTED]");
            value = Regex.Replace(value, @"(?i)\bBearer\s+[^\s,;\""']+", "Bearer [REDACTED]");
            value = Regex.Replace(value, @"(?i)(authorization|password|api[-_ ]?key|secret|mail[-_ ]?(auth|authorization)([-_ ]?code)?|授权码|密码|密钥)\s*[:=：]\s*[^\s,;，；]+", "$1=[REDACTED]");
            value = Regex.Replace(value, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[已隐藏邮箱]");
            value = Regex.Replace(value, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
            return value.Length <= limit ? value : value.Substring(0, limit);
        }
    }
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
        public ChatDisplay Display { get; set; }
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
        public static ChatMessage FromDisplay(string role, ChatDisplay display, string createdAt, string intent = "", List<string> todoIds = null, ChatSyncSummary sync = null)
        {
            ChatDisplay projected = ChatDisplay.Project(display, role);
            return new ChatMessage {
                Id = Guid.NewGuid().ToString("N"), Role = role, Display = projected,
                Text = projected == null ? DisplaySummary(role, intent) : projected.Text, CreatedAt = createdAt,
                Intent = intent, TodoIds = todoIds ?? new List<string>(), Sync = sync
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
