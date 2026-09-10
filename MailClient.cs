using System;
using System.Collections.Generic;
using System.Linq;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace LittleCalendar
{
    public static class NetEaseImapIdentity
    {
        private static readonly string[] Hosts = { "imap.163.com", "imap.126.com", "imap.yeah.net", "imap.188.com" };
        public static bool ShouldIdentify(string host)
        {
            return !String.IsNullOrWhiteSpace(host) && Hosts.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        public static ImapImplementation Create()
        {
            return new ImapImplementation { Name = "LittleCalendar", Version = "3", Vendor = "Local desktop application" };
        }
    }

    public static class MailFolderPolicy
    {
        private static readonly string[] ExcludedNames = {
            "sent", "sent messages", "已发送", "已发送邮件", "draft", "drafts", "草稿", "草稿箱",
            "trash", "deleted", "deleted messages", "已删除", "废纸篓", "回收站"
        };
        public static bool ShouldInclude(MailboxFolder folder)
        {
            if (folder == null || String.IsNullOrWhiteSpace(folder.FullName)) return false;
            MailFolderAttributes excluded = MailFolderAttributes.Sent | MailFolderAttributes.Drafts | MailFolderAttributes.Trash | MailFolderAttributes.NoSelect;
            if ((folder.Attributes & excluded) != 0) return false;
            string leaf = folder.FullName.Replace('\\', '/').Split('/').Last().Trim();
            return !ExcludedNames.Any(name => String.Equals(name, leaf, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class MailStatusPolicy
    {
        public static MailFlagChange Imported() { return new MailFlagChange { AddFlagged = true }; }
        public static MailFlagChange Completed() { return new MailFlagChange { AddSeen = true, RemoveFlagged = true }; }
    }

    public interface IMailboxClient : IDisposable
    {
        MailboxCapabilities Capabilities { get; }
        void Connect(MailConnectionOptions options, string authorizationCode);
        IList<MailboxFolder> ListFolders();
        IList<MailMessageSnapshot> Fetch(MailboxFolder folder, MailFetchRequest request);
        MailMessageSnapshot FindByMessageId(IEnumerable<MailboxFolder> folders, string messageId);
        void KeepAlive();
        void MarkImported(MailMessageLocator message);
        void MarkCompleted(MailMessageLocator message);
    }

    public interface IMailboxClientFactory { IMailboxClient Create(); }
    public sealed class MailKitClientFactory : IMailboxClientFactory
    {
        private readonly MailDiagnosticLog diagnostics;
        public MailKitClientFactory(string dataDirectory = null)
        {
            if (!String.IsNullOrWhiteSpace(dataDirectory)) diagnostics = new MailDiagnosticLog(dataDirectory);
        }
        public IMailboxClient Create() { return new MailKitMailboxClient(diagnostics); }
    }

    public sealed class MailKitMailboxClient : IMailboxClient
    {
        private readonly ImapClient client = new ImapClient();
        private readonly MailDiagnosticLog diagnostics;
        public MailboxCapabilities Capabilities { get; private set; }
        public MailKitMailboxClient(MailDiagnosticLog diagnostics = null) { Capabilities = new MailboxCapabilities(); this.diagnostics = diagnostics; }

        public void Connect(MailConnectionOptions options, string authorizationCode)
        {
            if (options == null || String.IsNullOrWhiteSpace(options.Address) || String.IsNullOrWhiteSpace(options.Host))
                throw new ArgumentException("邮箱连接设置不完整。");
            if (String.IsNullOrWhiteSpace(authorizationCode)) throw new ArgumentException("网易邮箱授权码不能为空。");
            string stage = "CONNECT_START";
            LogConnection(stage, "host=" + options.Host.Trim() + " port=" + options.Port + " ssl=" + options.UseSsl);
            try {
                client.Timeout = 45000;
                client.Connect(options.Host.Trim(), options.Port, options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
                stage = "TLS_CONNECTED"; LogConnection(stage, "encrypted=" + client.IsSecure);
                client.Authenticate(options.Address.Trim(), authorizationCode.Trim());
                stage = "AUTHENTICATED"; LogConnection(stage, "account=" + MaskAccount(options.Address));
                if (NetEaseImapIdentity.ShouldIdentify(options.Host) && (client.Capabilities & ImapCapabilities.Id) != 0) {
                    client.Identify(NetEaseImapIdentity.Create());
                    stage = "IMAP_ID_SENT"; LogConnection(stage, "name=LittleCalendar version=3");
                } else LogConnection("IMAP_ID_SKIPPED", "serverSupportsId=" + ((client.Capabilities & ImapCapabilities.Id) != 0));
                LogConnection("CONNECT_READY", "authenticated=true");
            } catch (Exception error) {
                LogConnection("CONNECT_FAILED", "stage=" + stage + " type=" + error.GetType().Name + " message=" + error.Message);
                throw;
            }
        }

        public IList<MailboxFolder> ListFolders()
        {
            EnsureConnected();
            try {
                var results = new List<MailboxFolder>();
                LogConnection("FOLDER_LIST_START", "");
                foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
                    foreach (IMailFolder folder in client.GetFolders(folderNamespace, StatusItems.None, false))
                        AddFolder(results, folder);
                IList<MailboxFolder> folders = results.GroupBy(x => x.FullName, StringComparer.Ordinal).Select(x => x.First()).ToList();
                LogConnection("FOLDER_LIST_OK", "count=" + folders.Count + " folders=" + String.Join(" | ", folders.Select(x => x.FullName)));
                return folders;
            } catch (Exception error) {
                LogConnection("FOLDER_LIST_FAILED", "type=" + error.GetType().Name + " message=" + error.Message);
                throw;
            }
        }

        public IList<MailMessageSnapshot> Fetch(MailboxFolder descriptor, MailFetchRequest request)
        {
            EnsureConnected();
            if (descriptor == null || request == null) throw new ArgumentNullException();
            LogRead("SEARCH_START", "folder=" + descriptor.FullName + " since=" + request.Since.ToString("o") + " minimumUid=" + request.MinimumUid);
            try {
                IMailFolder folder = OpenFolder(descriptor.FullName, FolderAccess.ReadOnly);
            SearchQuery query = SearchQuery.DeliveredAfter(request.Since.Date.AddDays(-1));
            if (request.MinimumUid > 0)
                    query = query.And(SearchQuery.Uids(new UniqueIdRange(new UniqueId(request.MinimumUid), UniqueId.MaxValue)));
                IList<UniqueId> ids = folder.Search(query);
                LogRead("SEARCH_RESULT", "folder=" + descriptor.FullName + " count=" + ids.Count);
                if (ids.Count == 0) return new List<MailMessageSnapshot>();
                IList<IMessageSummary> summaries = folder.Fetch(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);
                var messages = summaries.OrderBy(x => x.UniqueId.Id).Select(x => ReadSnapshot(folder, x)).ToList();
                if (diagnostics != null) foreach (MailMessageSnapshot message in messages) diagnostics.ReadMessage(message);
                return messages;
            } catch (Exception error) {
                LogRead("READ_FAILED", "folder=" + descriptor.FullName + " type=" + error.GetType().Name + " message=" + error.Message);
                throw;
            }
        }

        public MailMessageSnapshot FindByMessageId(IEnumerable<MailboxFolder> folders, string messageId)
        {
            EnsureConnected();
            if (String.IsNullOrWhiteSpace(messageId)) return null;
            foreach (MailboxFolder descriptor in folders.Where(MailFolderPolicy.ShouldInclude)) {
                IMailFolder folder = OpenFolder(descriptor.FullName, FolderAccess.ReadOnly);
                IList<UniqueId> ids = folder.Search(SearchQuery.HeaderContains("Message-Id", messageId));
                if (ids.Count == 0) continue;
                IList<IMessageSummary> summaries = folder.Fetch(new[] { ids[0] }, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);
                if (summaries.Count > 0) return ReadSnapshot(folder, summaries[0]);
            }
            return null;
        }

        public void MarkImported(MailMessageLocator message) { Apply(message, MailStatusPolicy.Imported()); }
        public void MarkCompleted(MailMessageLocator message) { Apply(message, MailStatusPolicy.Completed()); }
        public void KeepAlive()
        {
            EnsureConnected(); client.NoOp(); LogConnection("KEEPALIVE_OK", "");
        }
        private void Apply(MailMessageLocator message, MailFlagChange change)
        {
            EnsureConnected();
            if (message == null || message.Uid == 0) throw new ArgumentException("邮件定位信息无效。");
            IMailFolder folder = OpenFolder(message.FolderId, FolderAccess.ReadWrite);
            var uid = new UniqueId(message.Uid);
            if (change.AddSeen) folder.AddFlags(uid, MessageFlags.Seen, true);
            if (change.AddFlagged) folder.AddFlags(uid, MessageFlags.Flagged, true);
            if (change.RemoveFlagged) folder.RemoveFlags(uid, MessageFlags.Flagged, true);
        }

        private IMailFolder OpenFolder(string fullName, FolderAccess access)
        {
            IMailFolder folder = client.GetFolder(fullName);
            if (!folder.IsOpen || folder.Access != access) {
                if (folder.IsOpen) folder.Close();
                LogRead("FOLDER_OPEN_START", "folder=" + fullName + " access=" + access);
                folder.Open(access);
                LogRead("FOLDER_OPEN_OK", "folder=" + fullName + " access=" + folder.Access + " uidValidity=" + folder.UidValidity);
            }
            Capabilities.AllowsCustomKeywords = (folder.PermanentFlags & MessageFlags.UserDefined) != 0;
            return folder;
        }

        private static string MaskAccount(string address)
        {
            if (String.IsNullOrWhiteSpace(address)) return "";
            int at = address.LastIndexOf('@');
            return at < 0 ? "***" : "***" + address.Substring(at);
        }
        private void LogConnection(string stage, string detail)
        {
            if (diagnostics != null) diagnostics.Connection(stage, detail);
        }
        private void LogRead(string stage, string detail)
        {
            if (diagnostics != null) diagnostics.Read(stage, detail);
        }

        private static void AddFolder(List<MailboxFolder> results, IMailFolder folder)
        {
            try { folder.Status(StatusItems.UidValidity); } catch { }
            results.Add(new MailboxFolder { FullName = folder.FullName, DisplayName = folder.Name, Attributes = Convert(folder.Attributes), UidValidity = folder.UidValidity });
            foreach (IMailFolder child in folder.GetSubfolders(false)) AddFolder(results, child);
        }

        private static MailFolderAttributes Convert(FolderAttributes attributes)
        {
            MailFolderAttributes value = MailFolderAttributes.None;
            if ((attributes & FolderAttributes.Inbox) != 0) value |= MailFolderAttributes.Inbox;
            if ((attributes & FolderAttributes.Junk) != 0) value |= MailFolderAttributes.Junk;
            if ((attributes & FolderAttributes.Sent) != 0) value |= MailFolderAttributes.Sent;
            if ((attributes & FolderAttributes.Drafts) != 0) value |= MailFolderAttributes.Drafts;
            if ((attributes & FolderAttributes.Trash) != 0) value |= MailFolderAttributes.Trash;
            if ((attributes & FolderAttributes.NoSelect) != 0) value |= MailFolderAttributes.NoSelect;
            return value;
        }

        private static MailMessageSnapshot ReadSnapshot(IMailFolder folder, IMessageSummary summary)
        {
            Envelope envelope = summary.Envelope;
            MailboxAddress sender = envelope == null ? null : envelope.From.Mailboxes.FirstOrDefault();
            var value = new MailMessageSnapshot {
                FolderId = folder.FullName, UidValidity = folder.UidValidity, Uid = summary.UniqueId.Id,
                MessageId = envelope == null ? "" : (envelope.MessageId ?? ""),
                Subject = envelope == null ? "" : (envelope.Subject ?? ""),
                Sender = sender == null ? "" : (String.IsNullOrWhiteSpace(sender.Name) ? sender.Address : sender.Name + " <" + sender.Address + ">"),
                SentAt = ((envelope != null && envelope.Date.HasValue) ? envelope.Date.Value : summary.InternalDate.GetValueOrDefault()).ToString("o")
            };
            if (summary.TextBody != null) {
                TextPart text = folder.GetBodyPart(summary.UniqueId, summary.TextBody) as TextPart;
                if (text != null) value.PlainText = text.Text ?? "";
            }
            if (summary.HtmlBody != null) {
                TextPart html = folder.GetBodyPart(summary.UniqueId, summary.HtmlBody) as TextPart;
                if (html != null) value.HtmlText = html.Text ?? "";
            }
            foreach (BodyPartBasic part in summary.BodyParts.OfType<BodyPartBasic>()) {
                string name = part.FileName ?? "";
                if (part.IsAttachment && !String.IsNullOrWhiteSpace(name)) value.AttachmentNames.Add(name);
                if (String.Equals(part.ContentType.MimeType, "text/calendar", StringComparison.OrdinalIgnoreCase)) {
                    TextPart calendar = folder.GetBodyPart(summary.UniqueId, part) as TextPart;
                    if (calendar != null) value.CalendarText = calendar.Text ?? "";
                }
            }
            return value;
        }

        private void EnsureConnected()
        {
            if (!client.IsConnected || !client.IsAuthenticated) throw new InvalidOperationException("邮箱尚未连接。");
        }
        public void Dispose()
        {
            if (client.IsConnected) {
                try { client.Disconnect(true); } catch { client.Disconnect(false); }
            }
            client.Dispose();
        }
    }
}
