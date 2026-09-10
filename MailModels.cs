using System;
using System.Collections.Generic;

namespace LittleCalendar
{
    [Flags]
    public enum MailFolderAttributes
    {
        None = 0, Inbox = 1, Junk = 2, Sent = 4, Drafts = 8, Trash = 16, NoSelect = 32
    }

    public sealed class MailboxFolder
    {
        public string FullName { get; set; }
        public string DisplayName { get; set; }
        public MailFolderAttributes Attributes { get; set; }
        public uint UidValidity { get; set; }
        public MailboxFolder() { FullName = ""; DisplayName = ""; }
    }

    public sealed class MailConnectionOptions
    {
        public string Address { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
    }

    public sealed class MailFetchRequest
    {
        public DateTime Since { get; set; }
        public uint MinimumUid { get; set; }
    }

    public sealed class MailMessageLocator
    {
        public string FolderId { get; set; }
        public uint UidValidity { get; set; }
        public uint Uid { get; set; }
        public string MessageId { get; set; }
    }

    public sealed class MailboxCapabilities
    {
        public bool AllowsCustomKeywords { get; set; }
        public List<string> WritableKeywords { get; set; }
        public MailboxCapabilities() { WritableKeywords = new List<string>(); }
    }

    public sealed class MailFlagChange
    {
        public bool AddSeen { get; set; }
        public bool AddFlagged { get; set; }
        public bool RemoveFlagged { get; set; }
        public string CustomKeyword { get; set; }
        public MailFlagChange() { CustomKeyword = ""; }
    }

    public sealed class EmailSource
    {
        public string EmailKey { get; set; }
        public string AccountId { get; set; }
        public string FolderId { get; set; }
        public uint UidValidity { get; set; }
        public uint Uid { get; set; }
        public string MessageId { get; set; }
        public string Fingerprint { get; set; }
        public string Subject { get; set; }
        public string Sender { get; set; }
        public string SentAt { get; set; }
        public string DeadlineOriginalText { get; set; }
        public string ActionCategory { get; set; }
        public bool DeadlineInferred { get; set; }
        public string ImportSyncState { get; set; }
        public string ImportSyncError { get; set; }
        public string CompletionSyncState { get; set; }
        public string CompletionSyncedAt { get; set; }
        public string CompletionSyncError { get; set; }
        public EmailSource()
        {
            EmailKey = ""; AccountId = ""; FolderId = ""; MessageId = ""; Fingerprint = "";
            Subject = ""; Sender = ""; SentAt = ""; DeadlineOriginalText = ""; ActionCategory = "";
            ImportSyncState = "pending"; ImportSyncError = "";
            CompletionSyncState = "none"; CompletionSyncedAt = ""; CompletionSyncError = "";
        }
        public EmailSource Copy() { return (EmailSource)MemberwiseClone(); }
    }

    public sealed class MailAccountSettings
    {
        public bool Enabled { get; set; }
        public string Address { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
        public int ManualSyncDays { get; set; }
        public MailAccountSettings() { Address = ""; Host = "imap.163.com"; Port = 993; UseSsl = true; ManualSyncDays = 7; }
    }

    public sealed class MailFolderState
    {
        public string FolderId { get; set; }
        public string DisplayName { get; set; }
        public bool Enabled { get; set; }
        public uint UidValidity { get; set; }
        public uint LastUid { get; set; }
        public string LastScannedAt { get; set; }
        public MailFolderState() { FolderId = ""; DisplayName = ""; Enabled = true; LastScannedAt = ""; }
    }

    public sealed class MailSyncState
    {
        public int Version { get; set; }
        public MailAccountSettings Account { get; set; }
        public List<MailFolderState> Folders { get; set; }
        public List<string> ProcessedKeys { get; set; }
        public string LastAutomaticDate { get; set; }
        public string LastStartedAt { get; set; }
        public string LastCompletedAt { get; set; }
        public string LastCompletionReconciledAt { get; set; }
        public string LastError { get; set; }
        public int LastScannedCount { get; set; }
        public int LastCreatedCount { get; set; }
        public int LastReconciledCount { get; set; }
        public string NextRetryAt { get; set; }
        public bool AllowsCustomKeywords { get; set; }
        public List<string> WritableKeywords { get; set; }
        public MailSyncState()
        {
            Version = 1; Account = new MailAccountSettings(); Folders = new List<MailFolderState>();
            ProcessedKeys = new List<string>(); WritableKeywords = new List<string>();
            LastAutomaticDate = ""; LastStartedAt = ""; LastCompletedAt = "";
            LastCompletionReconciledAt = ""; LastError = ""; NextRetryAt = "";
        }
    }

    public sealed class MailMessageSnapshot
    {
        public string FolderId { get; set; }
        public uint UidValidity { get; set; }
        public uint Uid { get; set; }
        public string MessageId { get; set; }
        public string Subject { get; set; }
        public string Sender { get; set; }
        public string SentAt { get; set; }
        public string PlainText { get; set; }
        public string HtmlText { get; set; }
        public string CalendarText { get; set; }
        public List<string> AttachmentNames { get; set; }
        public MailMessageSnapshot()
        {
            FolderId = ""; MessageId = ""; Subject = ""; Sender = ""; SentAt = "";
            PlainText = ""; HtmlText = ""; CalendarText = ""; AttachmentNames = new List<string>();
        }
    }

    public sealed class MailAction
    {
        public string Disposition { get; set; }
        public bool Actionable { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public bool Important { get; set; }
        public string DeadlineKind { get; set; }
        public string DeadlineStart { get; set; }
        public string DeadlineEnd { get; set; }
        public int DeadlineAmount { get; set; }
        public string DeadlineUnit { get; set; }
        public string DeadlineOriginalText { get; set; }
        public double Confidence { get; set; }
        public string Reason { get; set; }
        public MailAction()
        {
            Disposition = ""; Category = ""; Title = ""; Notes = ""; DeadlineKind = ""; DeadlineStart = ""; DeadlineEnd = "";
            DeadlineUnit = ""; DeadlineOriginalText = ""; Reason = "";
        }
    }

    public sealed class OpportunityNotice
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public bool Read { get; set; }
        public string ReceivedAt { get; set; }
        public EmailSource EmailSource { get; set; }
        public OpportunityNotice() { Id = Guid.NewGuid().ToString("N"); Title = ""; Summary = ""; ReceivedAt = ""; }
        public OpportunityNotice Copy()
        {
            var copy = (OpportunityNotice)MemberwiseClone();
            copy.EmailSource = EmailSource == null ? null : EmailSource.Copy();
            return copy;
        }
    }

    public sealed class NormalizedMail
    {
        public string Sender { get; set; }
        public string Subject { get; set; }
        public string SentAt { get; set; }
        public string Text { get; set; }
        public string CalendarText { get; set; }
        public List<string> AttachmentNames { get; set; }
        public NormalizedMail()
        {
            Sender = ""; Subject = ""; SentAt = ""; Text = ""; CalendarText = ""; AttachmentNames = new List<string>();
        }
    }

    public sealed class MailSyncResult
    {
        public int ScannedCount { get; set; }
        public int CreatedCount { get; set; }
        public int NoticeCount { get; set; }
        public int ReconciledCount { get; set; }
        public List<Todo> NewTodos { get; set; }
        public List<OpportunityNotice> NewNotices { get; set; }
        public List<Todo> NewlyCompletedItems { get; set; }
        public List<string> Errors { get; set; }
        public MailSyncResult()
        {
            NewTodos = new List<Todo>(); NewNotices = new List<OpportunityNotice>(); NewlyCompletedItems = new List<Todo>(); Errors = new List<string>();
        }
    }
}
