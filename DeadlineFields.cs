using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LittleCalendar
{
    // Manual entry today; future mail extraction can supply the same DeadlineSpec.
    public sealed class DeadlineFields : StackPanel
    {
        private readonly DatePicker startDate = new DatePicker();
        private readonly TextBox startTime = new TextBox { MaxLength = 8 };
        private readonly TextBox offset = new TextBox { MaxLength = 6 };
        private readonly TextBox amount = new TextBox { MaxLength = 4 };
        private readonly ComboBox unit = new ComboBox { ItemsSource = new[] { "小时", "天（按 24 小时估算）", "工作日（人工指定截止）" } };
        private readonly DatePicker endDate = new DatePicker();
        private readonly TextBox endTime = new TextBox { MaxLength = 8 };
        private readonly CheckBox confirmed = new CheckBox { Content = "已核实截止时间" };
        private readonly TextBox original = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock preview = UI.Text("", 12, "#197B68");
        private readonly StackPanel working;
        private readonly DeadlineSpec originalSpec;

        public DeadlineFields(DeadlineSpec existing, DateTime selected)
        {
            originalSpec = existing == null ? null : existing.Copy();
            DateTimeOffset start = existing == null ? new DateTimeOffset(selected.Date.Add(DateTime.Now.TimeOfDay)) : Deadlines.ParseInstant(existing.StartAt);
            startDate.SelectedDate = start.Date; startTime.Text = start.ToString(start.Second == 0 ? "HH:mm" : "HH:mm:ss"); offset.Text = start.ToString("zzz");
            amount.Text = existing == null ? "48" : existing.Amount.ToString(CultureInfo.InvariantCulture);
            unit.SelectedIndex = existing == null || existing.Unit == "hours" ? 0 : existing.Unit == "days" ? 1 : 2;
            confirmed.IsChecked = existing == null || existing.Confirmed;
            original.Text = existing == null ? "" : existing.OriginalText;
            if (existing != null && existing.Unit == "workingDays") {
                DateTimeOffset end = Deadlines.End(existing).ToOffset(start.Offset);
                endDate.SelectedDate = end.Date; endTime.Text = end.ToString(end.Second == 0 ? "HH:mm" : "HH:mm:ss");
            }
            Children.Add(UI.Text("从邮件发送／收到的时间起算，请按原文选择；不是从今天或扫描时间起算。", 12, "#6F877C"));
            Children.Add(Row(UI.Field("起算日期", startDate), UI.Field("起算时间", startTime), UI.Field("时区偏移", offset)));
            Children.Add(Row(UI.Field("期限数值", amount), UI.Field("期限单位", unit)));
            working = UI.Stack(UI.Text("工作日受节假日、调休及招聘方规则影响，不自动推算。请核实并填写截止时间（与起算时间使用同一时区）。", 12, "#A27536"), Row(UI.Field("人工核实的截止日期", endDate), UI.Field("人工核实的截止时间", endTime)));
            Children.Add(working);
            System.Windows.Automation.AutomationProperties.SetName(confirmed, "已核实截止时间");
            Children.Add(confirmed);
            Children.Add(UI.Text("仅写“两天内”可能有歧义：未核实时保留“待确认”；仅用于估算提醒。所有日历时间按本机时区显示。", 11, "#8B7862"));
            var card = UI.Card(preview, 12); card.Margin = new Thickness(0, 8, 0, 12); Children.Add(card);
            Children.Add(UI.Field("邮件原始要求（选填）", original));

            unit.SelectionChanged += delegate { confirmed.IsChecked = unit.SelectedIndex == 0; UpdatePreview(); };
            startDate.SelectedDateChanged += delegate { InvalidateConfirmation(); };
            endDate.SelectedDateChanged += delegate { InvalidateConfirmation(); };
            foreach (TextBox box in new[] { startTime, offset, amount, endTime }) box.TextChanged += delegate { InvalidateConfirmation(); };
            confirmed.Checked += delegate { UpdatePreview(); }; confirmed.Unchecked += delegate { UpdatePreview(); };
            UpdatePreview();
        }

        private void InvalidateConfirmation()
        {
            if (unit.SelectedIndex != 0) confirmed.IsChecked = false;
            UpdatePreview();
        }

        private static Grid Row(params UIElement[] fields)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < fields.Length; i++) {
                row.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetColumn(fields[i], i);
                ((FrameworkElement)fields[i]).Margin = new Thickness(0, 0, i == fields.Length - 1 ? 0 : 10, 10); row.Children.Add(fields[i]);
            }
            return row;
        }

        private string Instant(DatePicker date, TextBox time, string originalInstant)
        {
            DateTime parsed;
            if (!date.SelectedDate.HasValue || !DateTime.TryParseExact(time.Text.Trim(), new[] { "HH:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                throw new ArgumentException("请选择日期，并填写 HH:mm 或 HH:mm:ss 格式的时间。");
            DateTimeOffset value = Deadlines.ParseInstant(date.SelectedDate.Value.ToString("yyyy-MM-dd") + "T" + parsed.ToString("HH:mm:ss") + offset.Text.Trim());
            if (!String.IsNullOrEmpty(originalInstant)) {
                DateTimeOffset previous = Deadlines.ParseInstant(originalInstant);
                if (previous.UtcTicks / TimeSpan.TicksPerSecond == value.UtcTicks / TimeSpan.TicksPerSecond)
                    value = value.AddTicks(previous.Ticks % TimeSpan.TicksPerSecond);
            }
            return value.ToString("o");
        }

        public DeadlineSpec Read()
        {
            int value;
            if (!Int32.TryParse(amount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("期限请填写正整数。");
            var spec = new DeadlineSpec {
                StartAt = Instant(startDate, startTime, originalSpec == null ? null : originalSpec.StartAt), Amount = value,
                Unit = unit.SelectedIndex == 0 ? "hours" : unit.SelectedIndex == 1 ? "days" : "workingDays",
                Confirmed = confirmed.IsChecked == true, OriginalText = original.Text,
                ManualEndAt = unit.SelectedIndex == 2 ? Instant(endDate, endTime, originalSpec == null ? null : originalSpec.ManualEndAt) : ""
            };
            Deadlines.End(spec); return spec;
        }

        private void UpdatePreview()
        {
            working.Visibility = unit.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            try { preview.Text = Deadlines.Description(new Todo { Deadline = Read() }, DateTime.Now); preview.Foreground = UI.Brush("#197B68"); }
            catch (Exception error) { preview.Text = error.Message; preview.Foreground = UI.Brush("#A27536"); }
        }
    }
}
