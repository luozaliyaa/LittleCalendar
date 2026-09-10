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
