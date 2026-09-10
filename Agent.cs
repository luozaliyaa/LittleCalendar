using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LittleCalendar
{
    public sealed class AgentSettings
    {
        public bool Enabled { get; set; }
        public string DailyTime { get; set; }
        public string Model { get; set; }
        public string LastAutomaticDate { get; set; }
        public string LastSummaryAt { get; set; }
        public AgentSummary LastSummary { get; set; }
        public AgentSettings()
        {
            Enabled = false; DailyTime = "09:00"; Model = "deepseek-v4-flash";
            LastAutomaticDate = ""; LastSummaryAt = "";
        }
    }

    public sealed class AgentSummary
    {
        public string Overview { get; set; }
        public List<string> Today { get; set; }
        public List<string> Upcoming { get; set; }
        public List<string> Risks { get; set; }
        public AgentSummary()
        {
            Overview = ""; Today = new List<string>(); Upcoming = new List<string>(); Risks = new List<string>();
        }
    }

    public static class AgentSchedule
    {
        public static bool IsDue(AgentSettings settings, DateTime now)
        {
            if (settings == null || !settings.Enabled || !Dates.IsTime(settings.DailyTime)) return false;
            if (settings.LastAutomaticDate == Dates.Key(now.Date)) return false;
            return now.TimeOfDay >= Dates.Time(settings.DailyTime);
        }
    }

    public static class AgentPrompts
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        public const string SystemPrompt = "你是一个只读的求职待办整理助手。根据用户提供的当前时间和未完成事项，给出简洁、具体、无臆测的中文安排。不得声称已经修改、完成或创建任何待办。输出严格 JSON，字段只能是 overview、today、upcoming、risks；overview 是字符串，其余字段都是字符串数组。";

        public static string Build(CalendarData data, DateTime now)
        {
            DateTime horizon = now.Date.AddDays(14);
            DateTimeOffset completedSince;
            if (!DateTimeOffset.TryParse(data.Agent.LastSummaryAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out completedSince))
                completedSince = new DateTimeOffset(now.Date.AddDays(-1));
            var items = data.Items.Where(item => item != null && !item.Deleted && !item.Completed && Dates.IsDate(item.Date))
                .Where(item => {
                    DateTime due = item.Deadline == null ? Dates.Parse(item.Date) : Deadlines.End(item.Deadline).LocalDateTime;
                    return due < now || due <= horizon.AddDays(1);
                })
                .OrderBy(item => item.Deadline == null ? Dates.Parse(item.Date) : Deadlines.End(item.Deadline).LocalDateTime)
                .ThenBy(item => item.Time).Take(50)
                .Select(item => new {
                    title = item.Title,
                    notes = item.Notes ?? "",
                    important = item.Important,
                    scheduleType = item.Deadline == null ? "fixed" : "deadline",
                    scheduledDate = item.Date,
                    scheduledTime = item.Time ?? "",
                    deadlineStart = item.Deadline == null ? "" : Deadlines.ParseInstant(item.Deadline.StartAt).LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    deadlineEnd = item.Deadline == null ? "" : Deadlines.End(item.Deadline).LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    deadlineConfirmed = item.Deadline == null || item.Deadline.Confirmed,
                    originalRequirement = item.Deadline == null ? "" : (item.Deadline.OriginalText ?? "")
                }).ToArray();
            var recentlyCompleted = data.Items.Where(item => item != null && item.Completed && !item.Deleted && !String.IsNullOrWhiteSpace(item.CompletedAt))
                .Select(item => {
                    DateTimeOffset completedAt;
                    return new { item = item, valid = DateTimeOffset.TryParse(item.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out completedAt), completedAt = completedAt };
                })
                .Where(x => x.valid && x.completedAt > completedSince && x.completedAt <= new DateTimeOffset(now))
                .OrderBy(x => x.completedAt).Take(50)
                .Select(x => new { title = x.item.Title, completedAt = x.completedAt.ToString("o"), notes = (x.item.Notes ?? "").Length <= 500 ? (x.item.Notes ?? "") : x.item.Notes.Substring(0, 500) })
                .ToArray();
            return Json.Serialize(new {
                currentTime = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                timezone = TimeZoneInfo.Local.DisplayName,
                rangeDays = 14,
                instruction = "总结今天和未来两周需要完成的工作；逾期、临近截止、重要事项优先。若没有事项，请明确说明暂无近期待办。",
                items = items,
                recentlyCompleted = recentlyCompleted
            });
        }
    }

    public static class AgentSummaries
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        public static AgentSummary Parse(string json)
        {
            AgentSummary value;
            try {
                var shape = Json.Deserialize<Dictionary<string, object>>(json);
                if (shape == null || !shape.ContainsKey("overview") || !shape.ContainsKey("today") || !shape.ContainsKey("upcoming") || !shape.ContainsKey("risks"))
                    throw new InvalidDataException("DeepSeek 返回的总结字段不完整。");
                value = Json.Deserialize<AgentSummary>(json);
            }
            catch (Exception error) { throw new InvalidDataException("DeepSeek 返回的总结不是有效 JSON。", error); }
            if (value == null || String.IsNullOrWhiteSpace(value.Overview) || value.Today == null || value.Upcoming == null || value.Risks == null)
                throw new InvalidDataException("DeepSeek 返回的总结字段不完整。");
            value.Overview = Limit(value.Overview.Trim(), 600);
            value.Today = Clean(value.Today); value.Upcoming = Clean(value.Upcoming); value.Risks = Clean(value.Risks);
            return value;
        }
        private static List<string> Clean(IEnumerable<string> values)
        {
            return values.Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => Limit(x.Trim(), 300)).Take(12).ToList();
        }
        private static string Limit(string value, int length) { return value.Length <= length ? value : value.Substring(0, length); }
    }

    public class ProtectedSecretStore
    {
        private readonly byte[] entropy;
        private readonly string displayName;
        public string FilePath { get; private set; }
        public ProtectedSecretStore(string directory, string fileName, byte[] entropy, string displayName)
        {
            if (String.IsNullOrWhiteSpace(directory) || String.IsNullOrWhiteSpace(fileName)) throw new ArgumentException();
            FilePath = Path.Combine(directory, fileName);
            this.entropy = entropy == null ? new byte[0] : (byte[])entropy.Clone();
            this.displayName = String.IsNullOrWhiteSpace(displayName) ? "秘密信息" : displayName;
        }
        public void Save(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException(displayName + "不能为空。");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), entropy, DataProtectionScope.CurrentUser);
            string temporary = FilePath + ".tmp"; File.WriteAllText(temporary, Convert.ToBase64String(protectedBytes), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null, true); else File.Move(temporary, FilePath);
        }
        public string Load()
        {
            if (!File.Exists(FilePath)) return "";
            try {
                byte[] protectedBytes = Convert.FromBase64String(File.ReadAllText(FilePath, Encoding.UTF8));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser));
            } catch (Exception error) { throw new InvalidDataException("已保存的" + displayName + "无法解密，请清除后重新填写。", error); }
        }
        public bool HasKey { get { try { return !String.IsNullOrWhiteSpace(Load()); } catch { return false; } } }
        public void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); }
    }

    public sealed class SecretStore : ProtectedSecretStore
    {
        private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("LittleCalendar.DeepSeek.ApiKey.v1");
        public SecretStore(string directory) : base(directory, "deepseek-key.dat", SecretEntropy, "DeepSeek API Key") { }
    }

    public sealed class MailSecretStore : ProtectedSecretStore
    {
        private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("LittleCalendar.NetEase.Mail.Authorization.v1");
        public MailSecretStore(string directory) : base(directory, "netease-mail-key.dat", SecretEntropy, "网易邮箱授权码") { }
    }

    public interface IWorkAgent
    {
        AgentSummary Summarize(CalendarData data, DateTime now, string apiKey, string model);
        void Test(string apiKey, string model);
    }

    public sealed class AgentJob
    {
        public CalendarData Data { get; set; }
        public DateTime Now { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public bool Automatic { get; set; }
    }

    public sealed class AgentCoordinator
    {
        private readonly CalendarController controller;
        private readonly SecretStore secrets;
        private readonly Func<DateTime> clock;
        public AgentCoordinator(CalendarController controller, SecretStore secrets, Func<DateTime> clock)
        {
            this.controller = controller; this.secrets = secrets; this.clock = clock ?? (() => DateTime.Now);
        }
        public AgentJob Prepare(bool automatic)
        {
            DateTime now = clock();
            if (automatic && (!AgentSchedule.IsDue(controller.Data.Agent, now) || !secrets.HasKey)) return null;
            string key = secrets.Load();
            if (String.IsNullOrWhiteSpace(key)) {
                if (automatic) return null;
                throw new InvalidOperationException("请先在提醒设置中填写 DeepSeek API Key。");
            }
            return new AgentJob { Data = controller.Store.Copy(controller.Data), Now = now, ApiKey = key, Model = controller.Data.Agent.Model, Automatic = automatic };
        }
        public void Complete(AgentJob job, AgentSummary summary)
        {
            if (job == null || summary == null) throw new ArgumentNullException();
            controller.Commit(data => {
                data.Agent.LastSummary = summary;
                data.Agent.LastSummaryAt = job.Now.ToString("o", CultureInfo.InvariantCulture);
                if (job.Automatic) data.Agent.LastAutomaticDate = Dates.Key(job.Now.Date);
            });
        }
        public void Fail(AgentJob job)
        {
            if (job != null && job.Automatic) controller.Commit(data => data.Agent.LastAutomaticDate = Dates.Key(job.Now.Date));
        }
    }

    public static class DeepSeekRequestPayload
    {
        public static string Build(string model, string system, string user, int maxTokens, bool disableThinking)
        {
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Serialize(new {
                model = model.Trim(), stream = false, max_tokens = maxTokens,
                thinking = new { type = disableThinking ? "disabled" : "enabled" },
                response_format = new { type = "json_object" },
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } }
            });
        }
    }

    public interface IStructuredMailAgent
    {
        string CompleteMailAction(string apiKey, string model, string instructions, string input);
    }

    public static class DeepSeekStructuredPayload
    {
        public static string BuildMailAction(string model, string instructions, string input, int maxTokens)
        {
            var properties = new Dictionary<string, object> {
                { "disposition", EnumField("string", new[] { "todo", "notice", "ignore" }) },
                { "actionable", Field("boolean") },
                { "category", EnumField("string", new[] { "assessment", "written-test", "interview", "confirmation", "material", "offer", "scheduling", "other" }) },
                { "title", Field("string") }, { "notes", Field("string") }, { "important", Field("boolean") },
                { "deadlineKind", EnumField("string", new[] { "absolute", "relative", "none" }) },
                { "deadlineStart", Field("string") }, { "deadlineEnd", Field("string") },
                { "deadlineAmount", NumberField("integer", 0, null) },
                { "deadlineUnit", EnumField("string", new[] { "hours", "days", "" }) },
                { "deadlineOriginalText", Field("string") },
                { "confidence", NumberField("number", 0, 1) }, { "reason", Field("string") }
            };
            var schema = new Dictionary<string, object> {
                { "type", "object" }, { "properties", properties }, { "required", properties.Keys.ToArray() }, { "additionalProperties", false }
            };
            var format = new Dictionary<string, object> { { "type", "json_schema" }, { "name", "mail_action" }, { "schema", schema } };
            var root = new Dictionary<string, object> {
                { "model", model.Trim() }, { "instructions", instructions }, { "input", input },
                { "reasoning", new Dictionary<string, object> { { "effort", "none" } } },
                { "max_output_tokens", maxTokens }, { "stream", false }, { "store", false },
                { "text", new Dictionary<string, object> { { "format", format } } }
            };
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Serialize(root);
        }
        private static Dictionary<string, object> Field(string type)
        {
            return new Dictionary<string, object> { { "type", type } };
        }
        private static Dictionary<string, object> EnumField(string type, string[] values)
        {
            return new Dictionary<string, object> { { "type", type }, { "enum", values } };
        }
        private static Dictionary<string, object> NumberField(string type, int minimum, int? maximum)
        {
            var field = new Dictionary<string, object> { { "type", type }, { "minimum", minimum } };
            if (maximum.HasValue) field["maximum"] = maximum.Value;
            return field;
        }
    }

    public static class DeepSeekResponses
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        public static string ExtractOutputText(string responseJson)
        {
            ResponseEnvelope envelope;
            try { envelope = Json.Deserialize<ResponseEnvelope>(responseJson); }
            catch (Exception error) { throw new InvalidDataException("DeepSeek Responses API 返回无效响应。", error); }
            if (envelope == null || envelope.status != "completed")
                throw new InvalidDataException("DeepSeek Responses API 未完成：" + (envelope == null ? "empty" : envelope.status ?? "unknown"));
            if (envelope.output != null) foreach (ResponseItem item in envelope.output)
                if (item != null && item.type == "message" && item.content != null) foreach (ResponseContent content in item.content)
                    if (content != null && content.type == "output_text" && !String.IsNullOrWhiteSpace(content.text)) return content.text;
            throw new InvalidDataException("DeepSeek Responses API 没有返回结构化文本。");
        }
        private sealed class ResponseEnvelope { public string status { get; set; } public List<ResponseItem> output { get; set; } }
        private sealed class ResponseItem { public string type { get; set; } public List<ResponseContent> content { get; set; } }
        private sealed class ResponseContent { public string type { get; set; } public string text { get; set; } }
    }

    public sealed class DeepSeekAgent : IWorkAgent, IStructuredMailAgent
    {
        private const string Endpoint = "https://api.deepseek.com/chat/completions";
        private const string ResponsesEndpoint = "https://api.deepseek.com/responses";
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        public AgentSummary Summarize(CalendarData data, DateTime now, string apiKey, string model)
        {
            string content = CompleteJson(apiKey, model, AgentPrompts.SystemPrompt, AgentPrompts.Build(data, now), 1000);
            return AgentSummaries.Parse(content);
        }
        public void Test(string apiKey, string model)
        {
            CompleteJson(apiKey, model, "只回复严格 JSON：{\"overview\":\"连接成功\",\"today\":[],\"upcoming\":[],\"risks\":[]}", "测试连接", 100);
        }
        public string CompleteJson(string apiKey, string model, string system, string user, int maxTokens)
        {
            return CompleteJson(apiKey, model, system, user, maxTokens, false);
        }
        public string CompleteJson(string apiKey, string model, string system, string user, int maxTokens, bool disableThinking)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请先填写 DeepSeek API Key。");
            if (String.IsNullOrWhiteSpace(model)) throw new ArgumentException("模型名不能为空。");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "POST"; request.ContentType = "application/json"; request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey.Trim(); request.Timeout = 45000; request.ReadWriteTimeout = 45000;
            string body = DeepSeekRequestPayload.Build(model, system, user, maxTokens, disableThinking);
            byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
            try {
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                    var envelope = json.Deserialize<CompletionEnvelope>(reader.ReadToEnd());
                    if (envelope == null || envelope.choices == null || envelope.choices.Count == 0 || envelope.choices[0].message == null || String.IsNullOrWhiteSpace(envelope.choices[0].message.content))
                        throw new InvalidDataException("DeepSeek 没有返回可用内容。");
                    return envelope.choices[0].message.content;
                }
            } catch (WebException error) {
                string detail = "";
                if (error.Response != null) try { using (var reader = new StreamReader(error.Response.GetResponseStream(), Encoding.UTF8)) detail = reader.ReadToEnd(); } catch { }
                throw new InvalidOperationException("DeepSeek 请求失败" + (String.IsNullOrWhiteSpace(detail) ? "，请检查网络、Key 和模型名。" : "：" + LimitError(detail)), error);
            }
        }
        public string CompleteMailAction(string apiKey, string model, string instructions, string input)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请先填写 DeepSeek API Key。");
            if (String.IsNullOrWhiteSpace(model)) throw new ArgumentException("模型名不能为空。");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(ResponsesEndpoint);
            request.Method = "POST"; request.ContentType = "application/json"; request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey.Trim(); request.Timeout = 45000; request.ReadWriteTimeout = 45000;
            byte[] bytes = Encoding.UTF8.GetBytes(DeepSeekStructuredPayload.BuildMailAction(model, instructions, input, 1200));
            request.ContentLength = bytes.Length;
            try {
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return DeepSeekResponses.ExtractOutputText(reader.ReadToEnd());
            } catch (WebException error) {
                string detail = "";
                if (error.Response != null) try { using (var reader = new StreamReader(error.Response.GetResponseStream(), Encoding.UTF8)) detail = reader.ReadToEnd(); } catch { }
                throw new InvalidOperationException("DeepSeek 结构化请求失败" + (String.IsNullOrWhiteSpace(detail) ? "，请检查网络、Key 和模型名。" : "：" + LimitError(detail)), error);
            }
        }
        private static string LimitError(string value) { value = value.Replace("\r", " ").Replace("\n", " "); return value.Length <= 300 ? value : value.Substring(0, 300); }
        private sealed class CompletionEnvelope { public List<Choice> choices { get; set; } }
        private sealed class Choice { public Message message { get; set; } }
        private sealed class Message { public string content { get; set; } }
    }
}
