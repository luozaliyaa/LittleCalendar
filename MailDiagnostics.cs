using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LittleCalendar
{
    public sealed class MailDiagnosticLog
    {
        private readonly object gate = new object();
        private readonly long maxBytes;
        public string DirectoryPath { get; private set; }
        public string ConnectionPath { get; private set; }
        public string ReadPath { get; private set; }
        public string AiPath { get; private set; }

        public MailDiagnosticLog(string dataDirectory, long maxBytes = 524288)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("日志资料目录不能为空。");
            DirectoryPath = Path.Combine(dataDirectory, "logs");
            ConnectionPath = Path.Combine(DirectoryPath, "mail-connection.log");
            ReadPath = Path.Combine(DirectoryPath, "mail-read.log");
            AiPath = Path.Combine(DirectoryPath, "mail-ai.log");
            this.maxBytes = Math.Max(4096, maxBytes);
        }

        public void Connection(string stage, string detail = "") { Write(ConnectionPath, stage, detail); }
        public void Read(string stage, string detail = "") { Write(ReadPath, stage, detail); }
        public void Ai(string stage, string detail = "") { Write(AiPath, stage, detail); }
        public void ReadMessage(MailMessageSnapshot message)
        {
            if (message == null) return;
            Read("MESSAGE", "folder=" + Clean(message.FolderId) + " uid=" + message.Uid
                + " sent=" + Clean(message.SentAt) + " sender=" + Clean(message.Sender)
                + " subject=" + Clean(message.Subject));
        }

        private void Write(string path, string stage, string detail)
        {
            try {
                string line = DateTimeOffset.Now.ToString("o") + " " + Clean(stage);
                if (!String.IsNullOrWhiteSpace(detail)) line += " " + Redact(Clean(detail));
                lock (gate) {
                    Directory.CreateDirectory(DirectoryPath);
                    Rotate(path);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            } catch { }
        }

        private void Rotate(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < maxBytes) return;
            string previous = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".previous.log");
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string Redact(string value)
        {
            return Regex.Replace(value ?? "", @"(?i)\b(authorization|password|api[-_]?key|secret)\s*=\s*[^\s;]+", "$1=[REDACTED]");
        }
    }
}
