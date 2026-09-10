using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LittleCalendar
{
    public sealed class MailStateStore
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public string FilePath { get; private set; }
        public string LoadWarning { get; private set; }

        public MailStateStore(string directory)
        {
            FilePath = Path.Combine(directory, "mail-sync.json");
            LoadWarning = "";
        }

        public MailSyncState Load()
        {
            if (!File.Exists(FilePath)) return new MailSyncState();
            try { return Decode(File.ReadAllText(FilePath, Encoding.UTF8)); }
            catch (Exception primaryError) {
                string backup = FilePath + ".bak";
                if (File.Exists(backup)) {
                    try {
                        MailSyncState recovered = Decode(File.ReadAllText(backup, Encoding.UTF8));
                        File.Copy(FilePath, FilePath + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmss"), false);
                        LoadWarning = "邮箱同步状态异常，已读取上次备份；原文件已保留。";
                        return recovered;
                    } catch (Exception backupError) {
                        throw new IOException("邮箱同步状态和备份均无法读取，原文件未改动。", new AggregateException(primaryError, backupError));
                    }
                }
                throw new IOException("无法读取邮箱同步状态，原文件未改动。", primaryError);
            }
        }

        public MailSyncState Decode(string json)
        {
            MailSyncState state = serializer.Deserialize<MailSyncState>(json);
            if (state == null || state.Version != 1) throw new InvalidDataException("不支持或损坏的邮箱同步状态。");
            if (state.Account == null) state.Account = new MailAccountSettings();
            if (state.Folders == null) state.Folders = new List<MailFolderState>();
            if (state.ProcessedKeys == null) state.ProcessedKeys = new List<string>();
            if (state.WritableKeywords == null) state.WritableKeywords = new List<string>();
            state.Account.Address = (state.Account.Address ?? "").Trim();
            state.Account.Host = String.IsNullOrWhiteSpace(state.Account.Host) ? "imap.163.com" : state.Account.Host.Trim();
            if (state.Account.Port < 1 || state.Account.Port > 65535) throw new InvalidDataException("邮箱端口无效。");
            if (!new[] { 1, 3, 7, 14, 30 }.Contains(state.Account.ManualSyncDays)) state.Account.ManualSyncDays = 7;
            var folderIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (MailFolderState folder in state.Folders) {
                if (folder == null || String.IsNullOrWhiteSpace(folder.FolderId) || !folderIds.Add(folder.FolderId))
                    throw new InvalidDataException("邮箱文件夹状态无效。");
                folder.DisplayName = folder.DisplayName ?? ""; folder.LastScannedAt = folder.LastScannedAt ?? "";
            }
            state.ProcessedKeys = state.ProcessedKeys.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(5000).ToList();
            state.LastAutomaticDate = state.LastAutomaticDate ?? ""; state.LastStartedAt = state.LastStartedAt ?? "";
            state.LastCompletedAt = state.LastCompletedAt ?? ""; state.LastCompletionReconciledAt = state.LastCompletionReconciledAt ?? "";
            state.LastError = state.LastError ?? ""; state.NextRetryAt = state.NextRetryAt ?? "";
            return state;
        }

        public void Save(MailSyncState state)
        {
            string json = serializer.Serialize(Decode(serializer.Serialize(state)));
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            string temporary = FilePath + ".tmp";
            WriteFlushed(temporary, json);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true);
            else File.Move(temporary, FilePath);
        }

        public MailSyncState Copy(MailSyncState state) { return Decode(serializer.Serialize(state)); }

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
