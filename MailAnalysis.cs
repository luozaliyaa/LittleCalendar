using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace LittleCalendar
{
    public static class MailContent
    {
        public static NormalizedMail Normalize(MailMessageSnapshot message)
        {
            if (message == null) throw new ArgumentNullException("message");
            string text = String.IsNullOrWhiteSpace(message.PlainText) ? HtmlToText(message.HtmlText) : message.PlainText;
            text = CleanText(text);
            string calendar = CleanText(message.CalendarText);
            return new NormalizedMail {
                Sender = Limit((message.Sender ?? "").Trim(), 300),
                Subject = Limit((message.Subject ?? "").Trim(), 500),
                SentAt = message.SentAt ?? "",
                Text = Limit(text, 12000),
                CalendarText = Limit(calendar, 4000),
                AttachmentNames = (message.AttachmentNames ?? new List<string>()).Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => Limit(x.Trim(), 200)).Take(30).ToList()
            };
        }

        private static string HtmlToText(string html)
        {
            string value = html ?? "";
            value = Regex.Replace(value, @"<(script|style|svg|iframe)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            value = Regex.Replace(value, @"<(img|link|meta)\b[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            value = Regex.Replace(value, @"<br\s*/?>|</p\s*>|</div\s*>|</li\s*>", "\n", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"<[^>]+>", " ");
            return HttpUtility.HtmlDecode(value);
        }

        private static string CleanText(string value)
        {
            value = (value ?? "").Replace("\0", " ");
            value = Regex.Replace(value, @"[ \t]+", " ");
            value = Regex.Replace(value, @" *(\r\n|\r|\n) *", "\n");
            value = Regex.Replace(value, @"\n{3,}", "\n\n");
            return value.Trim();
        }
        private static string Limit(string value, int length) { return value.Length <= length ? value : value.Substring(0, length); }
    }

    public static class MailPrefilter
    {
        private static readonly string[] ActionTerms = {
            "笔试", "测评", "面试", "确认参加", "确认时间", "回复是否", "提交材料", "补充材料", "offer", "签约", "预约"
        };
        private static readonly string[] NoiseTerms = {
            "验证码", "verification code", "一次性密码", "新设备登录", "登录提醒", "找回密码", "重置密码",
            "申请已收到", "简历已收到", "感谢投递", "订阅退订", "unsubscribe"
        };

        public static bool ShouldSkip(NormalizedMail mail)
        {
            if (mail == null) return true;
            string content = ((mail.Subject ?? "") + "\n" + (mail.Text ?? "")).ToLowerInvariant();
            if (ActionTerms.Any(term => content.Contains(term.ToLowerInvariant()))) return false;
            return NoiseTerms.Any(term => content.Contains(term.ToLowerInvariant()));
        }
    }

    public static class MailActionParser
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private static readonly string[] Required = {
            "disposition", "actionable", "category", "title", "notes", "important", "deadlineKind", "deadlineStart", "deadlineEnd",
            "deadlineAmount", "deadlineUnit", "deadlineOriginalText", "confidence", "reason"
        };
        private static readonly string[] Categories = { "assessment", "written-test", "interview", "confirmation", "material", "offer", "scheduling", "other" };
        private static readonly string[] Kinds = { "absolute", "relative", "none" };
        private static readonly string[] Dispositions = { "todo", "notice", "ignore" };

        public static MailAction Parse(string json)
        {
            try {
                Dictionary<string, object> shape = Json.Deserialize<Dictionary<string, object>>(json);
                if (shape == null) throw new InvalidDataException("根对象为空");
                string[] missing = Required.Where(key => !shape.ContainsKey(key)).ToArray();
                if (missing.Length > 0) throw new InvalidDataException("缺少字段 " + String.Join(",", missing));
                MailAction value = Json.Deserialize<MailAction>(json);
                Validate(value);
                return value;
            } catch (Exception error) {
                throw new InvalidDataException("DeepSeek 邮件 JSON 校验失败：" + SafeReason(error.Message), error);
            }
        }

        private static void Validate(MailAction value)
        {
            if (value == null) throw new InvalidDataException("对象为空");
            if (!Dispositions.Contains(value.Disposition)) throw new InvalidDataException("disposition 不在允许范围");
            if (!Categories.Contains(value.Category)) throw new InvalidDataException("category 不在允许范围");
            if (!Kinds.Contains(value.DeadlineKind)) throw new InvalidDataException("deadlineKind 不在允许范围");
            if (value.Confidence < 0 || value.Confidence > 1) throw new InvalidDataException("confidence 必须在 0 到 1 之间");
            value.Title = Limit((value.Title ?? "").Trim(), 200);
            value.Notes = Limit((value.Notes ?? "").Trim(), 4000);
            value.Reason = Limit((value.Reason ?? "").Trim(), 500);
            value.DeadlineOriginalText = Limit((value.DeadlineOriginalText ?? "").Trim(), 1000);
            value.DeadlineStart = (value.DeadlineStart ?? "").Trim(); value.DeadlineEnd = (value.DeadlineEnd ?? "").Trim();
            value.DeadlineUnit = (value.DeadlineUnit ?? "").Trim();
            if (value.Disposition == "todo" && !value.Actionable) throw new InvalidDataException("todo 必须同时设置 actionable=true");
            if (value.Disposition != "todo" && value.Actionable) throw new InvalidDataException("notice/ignore 必须设置 actionable=false");
            if (value.Disposition != "ignore" && String.IsNullOrWhiteSpace(value.Title)) throw new InvalidDataException("todo/notice 的 title 不能为空");
            if (value.DeadlineKind == "relative" && (value.DeadlineAmount < 1 || (value.DeadlineUnit != "hours" && value.DeadlineUnit != "days")))
                throw new InvalidDataException("relative 期限需要正整数 deadlineAmount 和 hours/days 单位");
            if (value.DeadlineKind == "absolute") {
                Deadlines.ParseInstant(value.DeadlineEnd);
                if (!String.IsNullOrEmpty(value.DeadlineStart)) Deadlines.ParseInstant(value.DeadlineStart);
            }
        }
        private static string Limit(string value, int length) { return value.Length <= length ? value : value.Substring(0, length); }
        private static string SafeReason(string value)
        {
            value = (value ?? "未知结构错误").Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 180 ? value : value.Substring(0, 180);
        }
    }

    public static class MailIdentity
    {
        public static string Key(MailMessageSnapshot source)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!String.IsNullOrWhiteSpace(source.MessageId)) return "message:" + source.MessageId.Trim().ToLowerInvariant();
            string raw = (source.Sender ?? "") + "\n" + (source.Subject ?? "") + "\n" + (source.SentAt ?? "");
            using (SHA256 hash = SHA256.Create()) {
                return "fingerprint:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    public static class MailDeadlineMapper
    {
        public static Todo ToTodo(MailAction action, MailMessageSnapshot source, DateTime syncNow)
        {
            if (action == null || source == null || !action.Actionable) throw new ArgumentException();
            DateTimeOffset sentAt;
            if (!DateTimeOffset.TryParse(source.SentAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out sentAt))
                sentAt = new DateTimeOffset(syncNow);
            bool inferred = action.DeadlineKind == "none";
            DeadlineSpec deadline;
            if (action.DeadlineKind == "relative") {
                deadline = new DeadlineSpec {
                    StartAt = sentAt.ToString("o"), Amount = action.DeadlineAmount, Unit = action.DeadlineUnit,
                    Confirmed = action.DeadlineUnit == "hours", ManualEndAt = "", OriginalText = action.DeadlineOriginalText
                };
            } else {
                DateTimeOffset start, end;
                if (action.DeadlineKind == "absolute") {
                    start = String.IsNullOrWhiteSpace(action.DeadlineStart) ? sentAt : Deadlines.ParseInstant(action.DeadlineStart);
                    end = Deadlines.ParseInstant(action.DeadlineEnd);
                } else {
                    start = new DateTimeOffset(syncNow.Date);
                    end = new DateTimeOffset(syncNow.Date.AddDays(7).AddHours(23).AddMinutes(59));
                }
                deadline = new DeadlineSpec {
                    StartAt = start.ToString("o"), Amount = 1, Unit = "absolute",
                    Confirmed = !inferred && action.Confidence >= 0.8, ManualEndAt = end.ToString("o"),
                    OriginalText = inferred ? "邮件未给出期限，系统按一周生成" : action.DeadlineOriginalText
                };
            }
            string notes = action.Notes ?? "";
            if (action.Confidence < 0.8) notes = (String.IsNullOrWhiteSpace(notes) ? "" : notes + "\n") + "AI 待确认：" + action.Reason;
            string key = MailIdentity.Key(source);
            var todo = new Todo {
                Title = action.Title, Notes = notes, Important = action.Important, Remind = true, Deadline = deadline,
                EmailSource = new EmailSource {
                    EmailKey = key, FolderId = source.FolderId, UidValidity = source.UidValidity, Uid = source.Uid,
                    MessageId = source.MessageId ?? "", Fingerprint = key.StartsWith("fingerprint:") ? key.Substring(12) : "",
                    Subject = source.Subject ?? "", Sender = source.Sender ?? "", SentAt = sentAt.ToString("o"),
                    DeadlineOriginalText = action.DeadlineOriginalText ?? "", ActionCategory = action.Category ?? "", DeadlineInferred = inferred, CompletionSyncState = "none"
                }
            };
            Deadlines.Normalize(todo);
            return todo;
        }
    }

    public static class MailTodoDeduplication
    {
        public static bool IsDuplicate(Todo existing, Todo candidate)
        {
            if (existing == null || candidate == null || existing.EmailSource == null || candidate.EmailSource == null) return false;
            if (!String.Equals(Normalize(existing.Title), Normalize(candidate.Title), StringComparison.Ordinal)) return false;
            if (existing.Deadline != null && candidate.Deadline != null)
                return Math.Abs((Deadlines.End(existing.Deadline) - Deadlines.End(candidate.Deadline)).TotalHours) <= 12;
            return existing.Deadline == null && candidate.Deadline == null && String.Equals(existing.Date, candidate.Date, StringComparison.Ordinal);
        }
        public static string Normalize(string value)
        {
            return Regex.Replace((value ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");
        }
    }

    public static class MailOpportunityMapper
    {
        public static OpportunityNotice ToNotice(MailAction action, MailMessageSnapshot source)
        {
            if (action == null || source == null || action.Disposition != "notice") throw new ArgumentException();
            DateTimeOffset sentAt;
            if (!DateTimeOffset.TryParse(source.SentAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out sentAt)) sentAt = DateTimeOffset.Now;
            string key = MailIdentity.Key(source);
            return new OpportunityNotice {
                Title = String.IsNullOrWhiteSpace(action.Title) ? source.Subject ?? "招聘机会通知" : action.Title,
                Summary = action.Notes ?? "", ReceivedAt = sentAt.ToString("o"),
                EmailSource = new EmailSource {
                    EmailKey = key, FolderId = source.FolderId, UidValidity = source.UidValidity, Uid = source.Uid,
                    MessageId = source.MessageId ?? "", Fingerprint = key.StartsWith("fingerprint:") ? key.Substring(12) : "",
                    Subject = source.Subject ?? "", Sender = source.Sender ?? "", SentAt = sentAt.ToString("o"),
                    ActionCategory = "opportunity", ImportSyncState = "synced", CompletionSyncState = "none"
                }
            };
        }
    }

    public interface IMailActionAnalyzer
    {
        MailAction Analyze(NormalizedMail mail, DateTime now, string apiKey, string model);
    }

    public sealed class DeepSeekMailActionAnalyzer : IMailActionAnalyzer
    {
        private readonly IStructuredMailAgent agent;
        private readonly MailDiagnosticLog diagnostics;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        public DeepSeekMailActionAnalyzer(IStructuredMailAgent agent, MailDiagnosticLog diagnostics = null)
        {
            this.agent = agent ?? new DeepSeekAgent(); this.diagnostics = diagnostics;
        }
        public MailAction Analyze(NormalizedMail mail, DateTime now, string apiKey, string model)
        {
            string system = "你是求职邮件分流与行动项提取器。必须输出 disposition=todo、notice 或 ignore。只有邮件能证明用户已经投递或已进入招聘流程，并明确要求完成测评、笔试、面试、材料提交、时间确认、Offer答复等任务时，才使用 todo 且 actionable=true。邀请投递、邀您投递、岗位推荐、校招启动、招聘简章、宣讲会和招聘宣传属于 notice，不能进入日历，actionable=false。验证码、登录安全、普通广告、投递回执和无操作要求的进度通知使用 ignore。重复提醒仍输出同一个规范任务标题，供本地去重。必须严格遵循 JSON Schema，所有字段均不得为 null。notice 保留简洁 title 和 notes，category=other、deadlineKind=none。ignore 使用空 title/notes/reason。相对期限只允许 hours 或 days；绝对时间必须含时区。待办示例：{\"disposition\":\"todo\",\"actionable\":true,\"category\":\"assessment\",\"title\":\"完成在线测评\",\"notes\":\"使用邮件中的链接\",\"important\":true,\"deadlineKind\":\"relative\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":48,\"deadlineUnit\":\"hours\",\"deadlineOriginalText\":\"邮件发送后48小时内\",\"confidence\":0.95,\"reason\":\"邮件明确要求完成测评\"}。机会通知示例：{\"disposition\":\"notice\",\"actionable\":false,\"category\":\"other\",\"title\":\"贝泰妮集团校园招聘岗位\",\"notes\":\"邮件邀请用户自行选择岗位投递\",\"important\":false,\"deadlineKind\":\"none\",\"deadlineStart\":\"\",\"deadlineEnd\":\"\",\"deadlineAmount\":0,\"deadlineUnit\":\"\",\"deadlineOriginalText\":\"\",\"confidence\":0.96,\"reason\":\"这是邀请投递，并非用户已进入招聘流程后的任务\"}。";
            string input = json.Serialize(new {
                currentTime = now.ToString("o"), sender = mail.Sender, subject = mail.Subject, sentAt = mail.SentAt,
                body = mail.Text, calendar = mail.CalendarText, attachmentNames = mail.AttachmentNames
            });
            string output = agent.CompleteMailAction(apiKey, model, system, input);
            try { return MailDispositionPolicy.Apply(MailActionParser.Parse(output), mail); }
            catch (InvalidDataException firstError) {
                Log("JSON_RETRY", firstError.Message);
                string retryInstructions = system + " 上一次结果未通过本地校验。请重新读取输入并完整输出符合 Schema 的对象，不要省略任何字段。";
                try {
                    MailAction corrected = MailActionParser.Parse(agent.CompleteMailAction(apiKey, model, retryInstructions, input));
                    Log("JSON_RETRY_OK", "structured response accepted");
                    return MailDispositionPolicy.Apply(corrected, mail);
                } catch (Exception retryError) {
                    Log("JSON_RETRY_FAILED", retryError.Message);
                    throw;
                }
            }
        }
        private void Log(string stage, string detail)
        {
            if (diagnostics != null) diagnostics.Ai(stage, detail);
        }
    }

    public static class MailDispositionPolicy
    {
        private static readonly string[] OpportunityTerms = { "智联推荐", "邀请投递", "邀您投递", "诚邀投递", "岗位推荐", "校招启动", "校园招聘启动", "招聘简章", "宣讲会", "一键投递" };
        private static readonly string[] ExistingProcessTerms = { "笔试", "测评", "面试", "确认参加", "确认时间", "补充材料", "提交材料", "offer", "签约" };
        public static MailAction Apply(MailAction action, NormalizedMail mail)
        {
            string subject = (mail == null ? "" : mail.Subject ?? "").ToLowerInvariant();
            string content = subject + "\n" + (mail == null ? "" : mail.Text ?? "").ToLowerInvariant();
            bool opportunity = OpportunityTerms.Any(x => content.Contains(x.ToLowerInvariant()));
            bool activeProcess = ExistingProcessTerms.Any(x => subject.Contains(x.ToLowerInvariant()));
            if (opportunity && !activeProcess) {
                action.Disposition = "notice"; action.Actionable = false; action.Category = "other";
                if (String.IsNullOrWhiteSpace(action.Title)) action.Title = mail.Subject;
                action.DeadlineKind = "none"; action.DeadlineStart = ""; action.DeadlineEnd = "";
                action.DeadlineAmount = 0; action.DeadlineUnit = ""; action.DeadlineOriginalText = "";
            }
            return action;
        }
    }
}
