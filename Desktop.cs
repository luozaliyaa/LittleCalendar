using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LittleCalendar
{
    public static class MailSourceNavigation
    {
        public static string SenderAddress(string sender)
        {
            Match match = Regex.Match(sender ?? "", @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
            return match.Success ? match.Value : (sender ?? "").Trim();
        }
        public static string SearchText(EmailSource source)
        {
            if (source == null) return "";
            return !String.IsNullOrWhiteSpace(source.Subject) ? source.Subject.Trim() : (source.MessageId ?? "").Trim();
        }
        public static string WebmailUrl() { return "https://mail.163.com/"; }
        public static void Open(Window owner, EmailSource source)
        {
            string search = SearchText(source);
            if (!String.IsNullOrWhiteSpace(search)) Clipboard.SetText(search);
            Process.Start(new ProcessStartInfo(WebmailUrl()) { UseShellExecute = true });
            if (owner != null) MessageBox.Show(owner, "已打开网易邮箱，并复制了邮件主题。请在邮箱搜索框中粘贴，即可定位原邮件。", "打开原邮件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public static class UI
    {
        public static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        public static TextBlock Text(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }
        public static Button Button(string text, Action action, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0) };
            if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
            button.Click += delegate { action(); };
            return button;
        }
        public static Border Card(UIElement child, double padding = 18)
        {
            return new Border { Background = Brushes.White, BorderBrush = Brush("#E0EAE7"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(padding), Child = child };
        }
        public static StackPanel Stack(params UIElement[] children)
        {
            var panel = new StackPanel(); foreach (UIElement child in children) panel.Children.Add(child); return panel;
        }
        public static StackPanel Field(string label, UIElement input)
        {
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            var caption = Text(label, 12, "#607873"); caption.Margin = new Thickness(0, 0, 0, 6);
            UIElement control = input;
            if (input is DatePicker) control = new Border { Background = Brushes.White, BorderBrush = Brush("#CFE0DB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(1), Child = input };
            var field = Stack(caption, control); field.Margin = new Thickness(0, 0, 0, 15); return field;
        }
        public static StackPanel OptionContent(string icon, string text, string color)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var mark = Text(icon, 15, color); mark.Width = 19; mark.Margin = new Thickness(0, 0, 5, 0); content.Children.Add(mark);
            content.Children.Add(Text(text, 13, "#355855"));
            return content;
        }
        public static CheckBox Option(string icon, string text, string name, bool important = false)
        {
            var option = new CheckBox { Content = OptionContent(icon, text, important ? "#B7791F" : "#197B68") };
            option.SetResourceReference(FrameworkElement.StyleProperty, important ? "ImportantOptionCheckBox" : "OptionCheckBox");
            System.Windows.Automation.AutomationProperties.SetName(option, name);
            return option;
        }
        public static CheckBox Toggle(string text, string name)
        {
            var toggle = new CheckBox { Content = text };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
            System.Windows.Automation.AutomationProperties.SetName(toggle, name);
            return toggle;
        }
    }

    public sealed class ChatWindow : Window
    {
        private readonly ListBox messages = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 150, MaxLength = 2000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock progress = UI.Text("", 12, "#607873");
        private readonly TextBlock empty = UI.Text("可以查询今天、明天、未来七天的待办，或读取上次同步后的新邮件。没有配置智能服务时，也能查询本地待办。", 14, "#607873");
        private readonly StackPanel confirmation = new StackPanel { Visibility = Visibility.Collapsed };
        private readonly List<Button> shortcuts = new List<Button>();
        private readonly Button send;
        private readonly Button clear;
        private readonly Action<string> sendMessage;
        public bool IsBusy { get; private set; }

        private sealed class TodoLink
        {
            public string Id { get; set; }
            public string Label { get; set; }
        }
        private sealed class MessageView
        {
            public string Role { get; set; }
            public string Speaker { get; set; }
            public string Text { get; set; }
            public string SyncText { get; set; }
            public List<TodoLink> Links { get; set; }
        }

        public ChatWindow(ChatHistory history, Action<string> sendMessage, Action clearHistory, Action<string> navigateTodo)
        {
            this.sendMessage = sendMessage;
            SetResourceReference(StyleProperty, typeof(Window));
            Title = "对话助手 · 小日历"; Width = 720; Height = 780; MinWidth = 540; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            clear = UI.Button("清空对话", delegate { if (!IsBusy) confirmation.Visibility = Visibility.Visible; });
            AutomationProperties.SetName(clear, "清空对话"); DockPanel.SetDock(clear, Dock.Right); header.Children.Add(clear);
            header.Children.Add(UI.Stack(UI.Text("对话助手", 24, "#197B68"), UI.Text("待办查询 · 新邮件增量同步", 12, "#78918A"))); root.Children.Add(header);
            var conversation = new Grid(); Grid.SetRow(conversation, 1); root.Children.Add(conversation);
            AutomationProperties.SetName(messages, "对话消息列表");
            VirtualizingStackPanel.SetIsVirtualizing(messages, true); VirtualizingStackPanel.SetVirtualizationMode(messages, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(messages, ScrollUnit.Pixel);
            ScrollViewer.SetCanContentScroll(messages, true); ScrollViewer.SetHorizontalScrollBarVisibility(messages, ScrollBarVisibility.Disabled);
            messages.SetResourceReference(ItemsControl.ItemTemplateProperty, "ChatMessageTemplate");
            messages.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ChatMessageContainer");
            messages.AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs args) {
                Button link = args.OriginalSource as Button;
                if (link != null && link.Tag is string && navigateTodo != null) { navigateTodo((string)link.Tag); args.Handled = true; }
            }));
            conversation.Children.Add(messages); empty.Margin = new Thickness(30); empty.VerticalAlignment = VerticalAlignment.Center; conversation.Children.Add(empty);
            var composer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(composer, 2); root.Children.Add(composer);
            confirmation.Children.Add(UI.Text("清空本机对话记录？日历待办不会受影响。", 13, "#8C583C"));
            var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 10) };
            confirmButtons.Children.Add(UI.Button("取消清空", delegate { confirmation.Visibility = Visibility.Collapsed; }));
            confirmButtons.Children.Add(UI.Button("确认清空", delegate {
                if (IsBusy) return;
                confirmation.Visibility = Visibility.Collapsed; SetBusy(true, "正在清空对话…");
                if (clearHistory != null) clearHistory();
            })); confirmation.Children.Add(confirmButtons); composer.Children.Add(confirmation);
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (string label in new[] { "读取新邮件", "今天要做什么", "明天截止", "未来七天" }) {
                string command = label;
                var chip = UI.Button(label, delegate { if (!IsBusy) { input.Text = command; Submit(); } });
                chip.Padding = new Thickness(10, 7, 10, 7); chip.Margin = new Thickness(0, 0, 6, 6);
                shortcuts.Add(chip); chips.Children.Add(chip);
            }
            composer.Children.Add(chips); AutomationProperties.SetName(input, "消息输入框"); composer.Children.Add(input);
            input.PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { args.Handled = true; Submit(); }
            };
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            send = UI.Button("发送消息", Submit, true); AutomationProperties.SetName(send, "发送消息"); DockPanel.SetDock(send, Dock.Right); footer.Children.Add(send);
            footer.Children.Add(UI.Stack(progress, UI.Text("Enter 发送 · Shift+Enter 换行", 11, "#78918A"))); composer.Children.Add(footer);
            Content = root; DisplayHistory(history);
            Loaded += delegate { input.Focus(); };
        }

        private void Submit()
        {
            if (IsBusy || String.IsNullOrWhiteSpace(input.Text) || sendMessage == null) return;
            SetBusy(true, "正在处理消息…"); sendMessage(input.Text.Trim());
        }

        public void SetBusy(bool busy, string status)
        {
            IsBusy = busy; input.IsEnabled = send.IsEnabled = clear.IsEnabled = !busy;
            foreach (Button chip in shortcuts) chip.IsEnabled = !busy;
            if (busy) confirmation.Visibility = Visibility.Collapsed;
            progress.Text = status ?? "";
        }

        public void CompleteSend(ChatHistory history, string status, bool clearInput)
        {
            if (history != null) DisplayHistory(history);
            if (clearInput) input.Clear();
            SetBusy(false, status); input.Focus();
        }

        private void DisplayHistory(ChatHistory history)
        {
            // Render the store's safe display projection, never raw input or model output.
            var views = (history ?? new ChatHistory()).Messages.Select(message => new MessageView {
                Role = message.Role, Speaker = message.Role == "user" ? "你" : "助手", Text = message.Sync == null ? message.Text : (message.Text ?? "").Split('\n')[0],
                SyncText = SyncText(message.Sync), Links = message.TodoIds.Select((id, index) => new TodoLink { Id = id, Label = "在日历中查看待办 " + (index + 1) }).ToList()
            }).ToList();
            messages.ItemsSource = views; empty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (views.Count > 0) messages.ScrollIntoView(views.Last());
        }

        private static string SyncText(ChatSyncSummary sync)
        {
            if (sync == null) return null;
            Func<string, string> time = value => {
                DateTimeOffset timestamp;
                return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp) ? timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "无记录";
            };
            var lines = new List<string> {
                "上次完成：" + time(sync.PreviousCompletedAt), "开始：" + time(sync.StartedAt) + "\n完成：" + time(sync.CompletedAt),
                "扫描 " + sync.ScannedCount + " 封 · 新增待办 " + sync.CreatedCount + " 项 · 通知 " + sync.NoticeCount + " 条 · 错误 " + sync.ErrorCount + " 项"
            };
            foreach (ChatFolderCursorSummary folder in sync.Folders) lines.Add(folder.DisplayName + " · UID " + folder.PreviousUid + " → 请求 " + folder.RequestedMinimumUid + " → " + folder.FinalUid +
                " · 读取 " + folder.FetchedCount + " 封\n上次扫描：" + time(folder.PreviousScannedAt) + "\n完成：" + time(folder.CompletedAt) + (String.IsNullOrEmpty(folder.Error) ? "" : " · 同步出错，请重试"));
            return String.Join("\n", lines);
        }
    }

    public sealed class CalendarWindow : Window
    {
        private readonly CalendarController controller;
        private readonly Func<DateTime> clock;
        private readonly Dictionary<TextBlock, Todo> deadlineLabels = new Dictionary<TextBlock, Todo>();
        private sealed class DeadlineBarView { public Todo Item; public Border Surface; public TextBlock Label; public Button Button; public int Lane; }
        private readonly List<DeadlineBarView> deadlineBarViews = new List<DeadlineBarView>();
        private readonly Grid days = new Grid();
        private readonly TextBlock monthTitle = UI.Text("", 23, "#243E42");
        private readonly TextBlock monthSummary = UI.Text("", 12, "#7B908B");
        private readonly TextBlock dayTitle = UI.Text("", 22, "#243E42");
        private readonly TextBlock daySummary = UI.Text("", 12, "#7B908B");
        private readonly TextBlock status = UI.Text("", 12, "#6D8880");
        private readonly StackPanel taskList = new StackPanel();
        private readonly TextBlock agentOverview = UI.Text("", 13, "#355449");
        private readonly TextBlock agentDetails = UI.Text("", 12, "#6D8880");
        private readonly TextBlock agentStatus = UI.Text("", 11, "#82958C");
        private readonly Button agentRefresh = UI.Button("立即总结", delegate { });
        private readonly Button agentMore = UI.Button("查看完整结果", delegate { });
        private readonly Button opportunityButton = UI.Button("机会通知", delegate { });
        private readonly TextBox search = new TextBox { Width = 184, Height = 38, MinHeight = 0, ToolTip = "搜索所有日期的标题和备注", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(7, 5, 4, 5) };
        private readonly Button[] modes = new Button[3];
        private int mode;
        public DateTime SelectedDate { get; private set; }
        public DateTime VisibleMonth { get; private set; }
        public Action SettingsRequested;
        public Action ChatRequested;
        public Action AgentSummaryRequested;
        public CalendarWindow(CalendarController controller, Func<DateTime> clock = null)
        {
            SetResourceReference(StyleProperty, typeof(Window));
            this.controller = controller;
            this.clock = clock ?? (() => DateTime.Now);
            Title = "小日历 · 待办与提醒"; Width = 1160; Height = 920; MinWidth = 940; MinHeight = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            for (int i = 0; i < 7; i++) days.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 6; i++) days.RowDefinitions.Add(new RowDefinition());
            days.SizeChanged += delegate { UpdateDeadlineBarPositions(); };
            SelectedDate = this.clock().Date; VisibleMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);
            var root = new Grid { Margin = new Thickness(16, 18, 16, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var branding = UI.Stack(UI.Text("小日历", 29, "#197B68"), UI.Text("给今天一点条理，为明天留个提醒。", 12, "#78918A"));
            header.Children.Add(branding);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var searchContent = new DockPanel(); var searchMark = UI.Text("⌕", 17, "#78918A"); searchMark.Width = 22; searchMark.HorizontalAlignment = HorizontalAlignment.Center; DockPanel.SetDock(searchMark, Dock.Left); searchContent.Children.Add(searchMark); searchContent.Children.Add(search);
            var searchShell = new Border { Width = 220, Height = 42, Background = Brushes.White, BorderBrush = UI.Brush("#CFE0DB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 1, 4, 1), Margin = new Thickness(0, 0, 8, 0), Child = searchContent };
            AutomationProperties.SetName(search, "搜索所有待办"); AutomationProperties.SetName(searchShell, "搜索待办框"); actions.Children.Add(searchShell);
            opportunityButton.Click += delegate { new OpportunityWindow(controller) { Owner = this }.ShowDialog(); };
            actions.Children.Add(opportunityButton);
            var chatButton = UI.Button("对话助手", delegate { if (ChatRequested != null) ChatRequested(); });
            AutomationProperties.SetName(chatButton, "对话助手"); actions.Children.Add(chatButton);
            actions.Children.Add(UI.Button("提醒设置", delegate { if (SettingsRequested != null) SettingsRequested(); }));
            actions.Children.Add(UI.Button("＋ 新建待办", delegate { Edit(null); }, true));
            Grid.SetColumn(actions, 1); header.Children.Add(actions); root.Children.Add(header);

            var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
            Grid.SetRow(columns, 1); root.Children.Add(columns);
            var calendar = new Grid(); calendar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); calendar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); calendar.RowDefinitions.Add(new RowDefinition());
            var monthHeader = new Grid { Margin = new Thickness(0, 0, 0, 18) }; monthHeader.ColumnDefinitions.Add(new ColumnDefinition()); monthHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            monthHeader.Children.Add(UI.Stack(monthTitle, monthSummary));
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            navigation.Children.Add(UI.Button("‹", delegate { MoveMonth(-1); })); navigation.Children.Add(UI.Button("今天", delegate { SelectDate(this.clock().Date); })); navigation.Children.Add(UI.Button("›", delegate { MoveMonth(1); }));
            Grid.SetColumn(navigation, 1); monthHeader.Children.Add(navigation); calendar.Children.Add(monthHeader);
            var weekdays = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 8) };
            foreach (string name in new[] { "一", "二", "三", "四", "五", "六", "日" }) { TextBlock label = UI.Text(name, 12, "#879990"); label.HorizontalAlignment = HorizontalAlignment.Center; weekdays.Children.Add(label); }
            Grid.SetRow(weekdays, 1); calendar.Children.Add(weekdays);
            Grid.SetRow(days, 2); calendar.Children.Add(days);
            Border calendarCard = UI.Card(calendar, 14); calendarCard.Margin = new Thickness(0, 0, 14, 0); columns.Children.Add(calendarCard);

            var side = new Grid(); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); side.RowDefinitions.Add(new RowDefinition()); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.Children.Add(UI.Stack(dayTitle, daySummary));
            Border agentCard = BuildAgentCard(); Grid.SetRow(agentCard, 1); side.Children.Add(agentCard);
            var tabs = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 14, 0, 16) };
            for (int i = 0; i < 3; i++) { int captured = i; modes[i] = UI.Button(new[] { "当日", "全部", "回收站" }[i], delegate { mode = captured; RefreshTasks(); }); modes[i].Padding = new Thickness(8); tabs.Children.Add(modes[i]); }
            Grid.SetRow(tabs, 2); side.Children.Add(tabs);
            var scroll = new ScrollViewer { Content = taskList }; Grid.SetRow(scroll, 3); side.Children.Add(scroll);
            var add = UI.Button("＋ 为这一天添加待办", delegate { Edit(null); }, true); add.Margin = new Thickness(0, 14, 0, 0); Grid.SetRow(add, 4); side.Children.Add(add);
            Border sideCard = UI.Card(side, 16); Grid.SetColumn(sideCard, 1); columns.Children.Add(sideCard);

            status.Margin = new Thickness(2, 10, 0, 0); Grid.SetRow(status, 2); root.Children.Add(status);
            Content = root;
            search.TextChanged += delegate { RefreshTasks(); };
            controller.Changed += delegate {
                if (Dispatcher.CheckAccess()) Refresh();
                else Dispatcher.BeginInvoke(new Action(Refresh));
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.N) { Edit(null); args.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.F) { search.Focus(); search.SelectAll(); args.Handled = true; }
            };
            Refresh();
        }
        private Border BuildAgentCard()
        {
            var content = new StackPanel();
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(UI.Text("✦ 智能整理", 15, "#197B68"));
            agentRefresh.Padding = new Thickness(10, 5, 10, 5); agentRefresh.Click += delegate { if (AgentSummaryRequested != null) AgentSummaryRequested(); };
            Grid.SetColumn(agentRefresh, 1); heading.Children.Add(agentRefresh); content.Children.Add(heading);
            agentOverview.TextWrapping = TextWrapping.Wrap; agentOverview.Margin = new Thickness(0, 10, 0, 5); content.Children.Add(agentOverview);
            agentDetails.TextWrapping = TextWrapping.Wrap; content.Children.Add(agentDetails);
            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(agentStatus); agentMore.Padding = new Thickness(8, 4, 8, 4); agentMore.Click += delegate {
                if (controller.Data.Agent.LastSummary != null) new AgentSummaryWindow(controller.Data.Agent.LastSummary, controller.Data.Agent.LastSummaryAt, controller.Data.Agent.LastSummaryError) { Owner = this }.ShowDialog();
            }; Grid.SetColumn(agentMore, 1); footer.Children.Add(agentMore); content.Children.Add(footer);
            var card = new Border { Background = UI.Brush("#F1F8F5"), BorderBrush = UI.Brush("#D2E7DF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 16, 0, 0), Child = content };
            AutomationProperties.SetName(card, "智能整理卡片"); return card;
        }
        private void RefreshAgent()
        {
            AgentSummary summary = controller.Data.Agent.LastSummary;
            string lastError = controller.Data.Agent.LastSummaryError;
            if (summary == null) {
                agentOverview.Text = "让 DeepSeek 帮你整理今天和未来两周的安排。";
                agentDetails.Text = "点击立即总结；首次使用请先在提醒设置中配置 API Key。";
                agentStatus.Text = String.IsNullOrWhiteSpace(lastError) ? "尚未生成总结" : "上次整理失败：" + lastError;
                agentMore.Visibility = Visibility.Collapsed;
            } else {
                AgentCardProjection view = AgentCardProjection.From(summary);
                agentOverview.Text = view.Headline;
                var lines = view.Priorities.Select((x, i) => (i + 1) + "  " + x).ToList();
                string priorities = lines.Count == 0 ? "暂时没有需要特别关注的事项。" : "优先处理\n" + String.Join("\n", lines);
                agentDetails.Text = String.IsNullOrWhiteSpace(view.Risk) ? priorities : priorities + "\n\n⚠ 风险提醒\n" + view.Risk;
                DateTimeOffset when; string cachedAt = DateTimeOffset.TryParse(controller.Data.Agent.LastSummaryAt, out when) ? when.LocalDateTime.ToString("M月d日 HH:mm") : "未知时间";
                agentStatus.Text = String.IsNullOrWhiteSpace(lastError) ? "更新于 " + cachedAt : "上次整理失败：" + lastError + "；仍显示旧总结（" + cachedAt + "）";
                agentMore.Visibility = Visibility.Visible;
            }
        }
        public void SetAgentBusy(bool busy, string message)
        {
            agentRefresh.IsEnabled = !busy; agentRefresh.Content = busy ? "整理中…" : "立即总结";
            if (!String.IsNullOrWhiteSpace(message)) agentStatus.Text = message;
        }
        public void SelectDate(DateTime date)
        {
            SelectedDate = date.Date; VisibleMonth = new DateTime(date.Year, date.Month, 1); mode = 0;
            search.Text = ""; Refresh();
        }
        private void MoveMonth(int step)
        {
            if ((step < 0 && VisibleMonth.Year <= 1900) || (step > 0 && VisibleMonth.Year >= 9998)) return;
            VisibleMonth = VisibleMonth.AddMonths(step); SelectedDate = VisibleMonth; Refresh();
        }
        public void Refresh()
        {
            RefreshAgent();
            int unreadNotices = controller.Data.Notices.Count(x => !x.Read);
            opportunityButton.Content = unreadNotices > 0 ? "机会通知 " + unreadNotices : "机会通知";
            monthTitle.Text = VisibleMonth.ToString("yyyy 年 M 月");
            int pending = controller.Data.Items.Count(x => !x.Deleted && !x.Completed && Enumerable.Range(0, DateTime.DaysInMonth(VisibleMonth.Year, VisibleMonth.Month)).Any(day => Deadlines.OnDay(x, VisibleMonth.AddDays(day), clock().Date)));
            monthSummary.Text = pending == 0 ? "这个月，可以从一件小事开始" : "本月还有 " + pending + " 项待办";
            status.Text = "本机保存 · 普通待办前一天 " + controller.Data.ReminderTime + " 提醒 · 期限提前 24 小时提醒 · 关闭窗口后在托盘运行";
            days.Children.Clear(); DateTime start = Dates.GridStart(VisibleMonth);
            deadlineBarViews.Clear();
            CalendarVisualLayout visualLayout = CalendarVisuals.Build(controller.Data.Items, start, clock().Date, 3);
            for (int i = 0; i < 42; i++) {
                DateTime date = start.AddDays(i); bool selected = date == SelectedDate; bool today = date == clock().Date;
                var cell = new Grid(); cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); cell.RowDefinitions.Add(new RowDefinition());
                var day = UI.Text(date.Day.ToString(), 15, date.Month != VisibleMonth.Month ? "#B4C0BA" : today ? "#197B68" : "#3B5752");
                day.FontWeight = today || selected ? FontWeights.Bold : FontWeights.Normal;
                var dateLine = new DockPanel(); if (today) { var mark = UI.Text("今", 10, "#197B68"); mark.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(mark, Dock.Right); dateLine.Children.Add(mark); } dateLine.Children.Add(day); cell.Children.Add(dateLine);
                List<Todo> items = controller.Data.Items.Where(x => !x.Deleted && Deadlines.OnDay(x, date, clock().Date)).ToList();
                int week = i / 7, column = i % 7;
                int highestLane = visualLayout.Segments.Where(x => x.Week == week && x.StartColumn <= column && x.EndColumn >= column).Select(x => x.Lane).DefaultIfEmpty(-1).Max();
                var fixedItems = items.Where(x => x.Deadline == null).OrderBy(x => x.Completed).ThenByDescending(x => x.Important).ToList();
                if (fixedItems.Count > 0) {
                    Todo todo = fixedItems[0];
                    string bubbleText = (todo.Completed ? "✓ " : "• ") + todo.Title + (fixedItems.Count > 1 ? "  +" + (fixedItems.Count - 1) : "");
                    var preview = UI.Text(bubbleText, 10, todo.Completed ? "#7F9089" : todo.Important ? "#855C25" : "#286E5D");
                    preview.TextWrapping = TextWrapping.NoWrap; preview.TextTrimming = TextTrimming.CharacterEllipsis;
                    var bubble = new Border { Child = preview, CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1),
                        Margin = new Thickness(0, highestLane < 0 ? 15 : 64, 0, 0), Height = 17, VerticalAlignment = VerticalAlignment.Top,
                        Background = UI.Brush(todo.Completed ? "#E5EAE8" : todo.Important ? "#F5E7CC" : "#DCEFE8"), ToolTip = String.Join("\n", fixedItems.Select(x => x.Title)) };
                    System.Windows.Automation.AutomationProperties.SetName(bubble, "待办气泡 " + todo.Title);
                    Grid.SetRowSpan(bubble, 2);
                    if (highestLane >= 1) { bubble.Width = 10; bubble.Height = 10; bubble.Padding = new Thickness(0); bubble.Margin = new Thickness(0, 1, 18, 0); bubble.HorizontalAlignment = HorizontalAlignment.Right; bubble.Child = null; }
                    cell.Children.Add(bubble);
                }
                var overdueItems = items.Where(x => x.Deadline != null && !x.Completed && new DateTimeOffset(clock()) >= Deadlines.End(x.Deadline) && Deadlines.End(x.Deadline).LocalDateTime.Date < date.Date).ToList();
                if (overdueItems.Count > 0) {
                    Todo overdue = overdueItems[0];
                    var overdueText = UI.Text("! " + overdue.Title + (overdueItems.Count > 1 ? "  +" + (overdueItems.Count - 1) : ""), 10, "#9E4338"); overdueText.TextWrapping = TextWrapping.NoWrap; overdueText.TextTrimming = TextTrimming.CharacterEllipsis;
                    var overdueBubble = new Border { Child = overdueText, CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1), Height = 17, VerticalAlignment = VerticalAlignment.Top, Background = UI.Brush("#F3D7D2"), ToolTip = "已逾期\n" + String.Join("\n", overdueItems.Select(x => x.Title)) };
                    overdueBubble.Margin = new Thickness(0, highestLane < 0 ? (fixedItems.Count == 0 ? 15 : 34) : 64, 0, 0);
                    if (highestLane >= 1 || (highestLane >= 0 && fixedItems.Count > 0)) { overdueBubble.Width = 10; overdueBubble.Height = 10; overdueBubble.Padding = new Thickness(0); overdueBubble.Margin = new Thickness(0, 1, fixedItems.Count > 0 ? 31 : 18, 0); overdueBubble.HorizontalAlignment = HorizontalAlignment.Right; overdueBubble.Child = null; }
                    System.Windows.Automation.AutomationProperties.SetName(overdueBubble, "逾期待办气泡 " + overdue.Title); Grid.SetRowSpan(overdueBubble, 2); cell.Children.Add(overdueBubble);
                }
                var button = UI.Button("", delegate { SelectDate(date); }); button.Content = cell; button.Margin = new Thickness(2); button.Padding = new Thickness(7, 2, 7, 7);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
                button.Background = UI.Brush(selected ? "#E2F2EC" : date.Month == VisibleMonth.Month ? "#F8FAF9" : "#FCFDFD");
                button.BorderBrush = UI.Brush(today ? "#75BCA6" : selected ? "#B0DAC9" : "#F0F4F2"); button.BorderThickness = new Thickness(1);
                button.ToolTip = date.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
                System.Windows.Automation.AutomationProperties.SetName(button, date.ToString("yyyy年M月d日"));
                Grid.SetRow(button, i / 7); Grid.SetColumn(button, i % 7); days.Children.Add(button);
                if (items.Count > 3) {
                    DateTime overflowDate = date;
                    DateTimeOffset current = new DateTimeOffset(clock());
                    var orderedItems = items.OrderBy(x => x.Completed ? 3 :
                            x.Deadline != null && Deadlines.End(x.Deadline) <= current.AddHours(24) ? 0 :
                            x.Important ? 1 : 2)
                        .ThenBy(x => x.Deadline == null ? x.Date + " " + x.Time : Deadlines.End(x.Deadline).ToString("o"))
                        .ToList();
                    var more = UI.Button("+" + (items.Count - 3) + " 条", delegate { SelectDate(overflowDate); });
                    more.FontSize = 9; more.Height = 16; more.Padding = new Thickness(4, 0, 4, 0);
                    more.Margin = new Thickness(0, 2, 7, 0); more.HorizontalAlignment = HorizontalAlignment.Right; more.VerticalAlignment = VerticalAlignment.Top;
                    more.Background = UI.Brush("#FFF1CF"); more.Foreground = UI.Brush("#8A5B16");
                    more.ToolTip = "当天全部 " + items.Count + " 项：\n" + String.Join("\n", orderedItems.Select(x => "• " + x.Title));
                    AutomationProperties.SetName(more, "查看" + date.ToString("yyyy年M月d日") + "全部" + items.Count + "项待办");
                    Grid.SetRow(more, week); Grid.SetColumn(more, column); Panel.SetZIndex(more, 20); days.Children.Add(more);
                }
            }
            RenderDeadlineBars(start, visualLayout);
            RefreshTasks();
        }
        private void RenderDeadlineBars(DateTime gridStart, CalendarVisualLayout layout)
        {
            foreach (CalendarBarSegment segment in layout.Segments) {
                Todo item = segment.Item; DateTimeOffset end = Deadlines.End(item.Deadline); DateTime now = clock();
                var visual = new Grid();
                var surface = new Border { CornerRadius = new CornerRadius(5) }; visual.Children.Add(surface);
                if (!item.Deadline.Confirmed) visual.Children.Add(new Rectangle { Stroke = UI.Brush("#B98231"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, RadiusX = 5, RadiusY = 5, Margin = new Thickness(1) });
                string label = (segment.ContinuesBefore ? "← " : "") + item.Title + (segment.ContinuesAfter ? " →" : "");
                var text = UI.Text(label, 9, "#216E5B"); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(6, 0, 6, 0); visual.Children.Add(text);
                var bar = UI.Button("", delegate { Edit(item); }); bar.Content = visual; bar.Height = 18; bar.Padding = new Thickness(0); bar.Margin = new Thickness(8, 38 + segment.Lane * 20, 8, 0);
                bar.VerticalAlignment = VerticalAlignment.Top; bar.HorizontalContentAlignment = HorizontalAlignment.Stretch; bar.Background = Brushes.Transparent;
                bar.ToolTip = item.Title + "\n" + Deadlines.Description(item, now);
                System.Windows.Automation.AutomationProperties.SetName(bar, "期限横条 " + item.Title + " " + segment.Week + " " + segment.StartColumn + "-" + segment.EndColumn);
                Grid.SetRow(bar, segment.Week); Grid.SetColumn(bar, segment.StartColumn); Grid.SetColumnSpan(bar, segment.EndColumn - segment.StartColumn + 1); Panel.SetZIndex(bar, 10); days.Children.Add(bar);
                var view = new DeadlineBarView { Item = item, Surface = surface, Label = text, Button = bar, Lane = segment.Lane }; deadlineBarViews.Add(view); RefreshDeadlineBar(view, now);
            }
            foreach (var overflow in layout.Overflow) {
                DateTime date = Dates.Parse(overflow.Key); int index = (date - gridStart).Days;
                if (index < 0 || index >= 42) continue;
                var more = UI.Text("+" + overflow.Value + " 期限", 9, "#8B6A36"); more.HorizontalAlignment = HorizontalAlignment.Right; more.VerticalAlignment = VerticalAlignment.Top; more.Margin = new Thickness(0, 24, 4, 0);
                more.IsHitTestVisible = false;
                System.Windows.Automation.AutomationProperties.SetName(more, overflow.Value + " 个未显示期限");
                Grid.SetRow(more, index / 7); Grid.SetColumn(more, index % 7); Panel.SetZIndex(more, 11); days.Children.Add(more);
            }
            UpdateDeadlineBarPositions();
        }
        private void UpdateDeadlineBarPositions()
        {
            if (days.ActualHeight <= 0) return;
            double rowHeight = days.ActualHeight / 6.0;
            double firstLane = Math.Min(38, Math.Max(20, rowHeight - 62));
            foreach (DeadlineBarView view in deadlineBarViews) view.Button.Margin = new Thickness(8, firstLane + view.Lane * 20, 8, 0);
        }
        private void RefreshTasks()
        {
            if (modes[0] == null) return;
            bool searching = !String.IsNullOrWhiteSpace(search.Text);
            dayTitle.Text = mode == 2 ? "回收站" : searching ? "搜索结果" : mode == 1 ? "全部待办" : SelectedDate.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
            for (int i = 0; i < modes.Length; i++) { modes[i].Background = UI.Brush(i == mode ? "#E2F2EC" : "#F4F7F5"); modes[i].Foreground = UI.Brush(i == mode ? "#197B68" : "#789088"); }
            IEnumerable<Todo> selected = controller.Data.Items.Where(x => x.Deleted == (mode == 2));
            if (searching) selected = selected.Where(x => (x.Title + " " + x.Notes + " " + (x.Deadline == null ? "" : x.Deadline.OriginalText)).IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            else if (mode == 0) selected = selected.Where(x => Deadlines.OnDay(x, SelectedDate, clock().Date));
            var items = selected.OrderBy(x => x.Completed).ThenBy(x => x.Date).ThenBy(x => String.IsNullOrEmpty(x.Time) ? "99:99" : x.Time).ThenByDescending(x => x.Important).ToList();
            daySummary.Text = mode == 2 ? "删除的记录可以在这里恢复" : items.Count + " 项安排 · " + items.Count(x => x.Completed) + " 项已完成";
            taskList.Children.Clear();
            deadlineLabels.Clear();
            if (items.Count == 0) {
                var empty = UI.Stack(UI.Text(mode == 2 ? "回收站是空的" : searching ? "没有找到相关待办" : "这一天，留给你安排", 17, "#779087"), UI.Text(searching ? "试试其他关键词。" : "记录一件小事，让下一步更清楚。", 12, "#98AAA3"));
                empty.Margin = new Thickness(12, 52, 12, 0); taskList.Children.Add(empty);
            }
            foreach (Todo item in items) taskList.Children.Add(TaskCard(item, mode != 0 || searching));
        }
        private Border TaskCard(Todo item, bool showDate)
        {
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            var complete = new CheckBox { IsChecked = item.Completed, Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = VerticalAlignment.Top, IsEnabled = !item.Deleted, ToolTip = "标记完成 / 未完成" };
            complete.SetResourceReference(FrameworkElement.StyleProperty, "TaskCheckBox");
            System.Windows.Automation.AutomationProperties.SetName(complete, "完成 " + item.Title);
            complete.Click += delegate { Guard(delegate { controller.Complete(item.Id, complete.IsChecked == true); }); }; layout.Children.Add(complete);
            var content = new StackPanel(); Grid.SetColumn(content, 1); layout.Children.Add(content);
            var title = UI.Text(item.Title, 14, item.Completed ? "#8D9C95" : "#2C4841"); if (item.Completed) title.TextDecorations = TextDecorations.Strikethrough; content.Children.Add(title);
            string timing = item.Deadline == null ? (showDate ? Dates.Parse(item.Date).ToString("M月d日") + " · " : "") + (String.IsNullOrEmpty(item.Time) ? "全天" : item.Time) : Deadlines.Description(item, clock());
            bool overdue = item.Deadline != null && !item.Completed && new DateTimeOffset(clock()) >= Deadlines.End(item.Deadline);
            var info = UI.Text(timing + (item.Important ? " · 重要" : "") + (item.Remind ? item.Deadline == null ? " · 前一天提醒" : " · 提前 24 小时提醒" : ""), 11, overdue ? "#B2634F" : item.Important ? "#A27536" : "#6F877C"); info.Margin = new Thickness(0, 6, 0, 0); content.Children.Add(info);
            if (item.Deadline != null) {
                deadlineLabels.Add(info, item);
                var source = UI.Text(Deadlines.Source(item.Deadline), 11, "#8B9F97"); source.Margin = new Thickness(0, 6, 0, 0); content.Children.Add(source);
                if (!String.IsNullOrWhiteSpace(item.Deadline.OriginalText)) { var quote = UI.Text("原始要求：" + item.Deadline.OriginalText, 11, "#8B7862"); quote.MaxHeight = 48; quote.ToolTip = item.Deadline.OriginalText; quote.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(quote); }
            }
            if (item.EmailSource != null) {
                string sender = String.IsNullOrWhiteSpace(item.EmailSource.Sender) ? "未知发件人" : item.EmailSource.Sender;
                string sourceText = "来自邮件 · " + sender + (String.IsNullOrWhiteSpace(item.EmailSource.Subject) ? "" : " · " + item.EmailSource.Subject);
                if (item.EmailSource.DeadlineInferred) sourceText += "\n期限由系统推断，请核对邮件原文";
                var mailSource = UI.Text(sourceText, 11, item.EmailSource.DeadlineInferred ? "#9A6D2F" : "#5F7F74");
                mailSource.Margin = new Thickness(0, 6, 0, 0); mailSource.MaxHeight = 42; mailSource.TextTrimming = TextTrimming.CharacterEllipsis;
                System.Windows.Automation.AutomationProperties.SetName(mailSource, "邮件来源 " + item.Title); content.Children.Add(mailSource);
                Button openMail = UI.Button("打开原邮件", delegate { Guard(delegate { MailSourceNavigation.Open(this, item.EmailSource); }); });
                openMail.HorizontalAlignment = HorizontalAlignment.Left; openMail.Margin = new Thickness(0, 7, 0, 0); openMail.Padding = new Thickness(9, 4, 9, 4); content.Children.Add(openMail);
            }
            if (!String.IsNullOrWhiteSpace(item.Notes)) { var notes = UI.Text(item.Notes, 12, "#6F877C"); notes.MaxHeight = 44; notes.Margin = new Thickness(0, 8, 0, 0); notes.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(notes); }
            var edit = UI.Button(item.Deleted ? "恢复" : "编辑", delegate { if (item.Deleted) Guard(delegate { controller.Trash(item.Id, false); }); else Edit(item); }); edit.HorizontalAlignment = HorizontalAlignment.Right; edit.Padding = new Thickness(10, 4, 10, 4); edit.FontSize = 11; edit.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(edit);
            bool pending = !item.Completed && !item.Deleted;
            var card = UI.Card(layout, 13); card.Background = UI.Brush(pending ? "#F6FBF8" : "#FAFBFA"); card.BorderBrush = UI.Brush(pending ? "#C9E3D8" : "#E0EAE7");
            card.Margin = new Thickness(4, 8, 4, 12); System.Windows.Automation.AutomationProperties.SetName(card, "待办卡片 " + item.Title); return card;
        }
        public void RefreshCountdowns()
        {
            DateTime now = clock();
            foreach (var pair in deadlineLabels) {
                Todo item = pair.Value;
                pair.Key.Text = Deadlines.Description(item, now) + (item.Important ? " · 重要" : "") + (item.Remind ? " · 提前 24 小时提醒" : "");
                pair.Key.Foreground = UI.Brush(!item.Completed && new DateTimeOffset(now) >= Deadlines.End(item.Deadline) ? "#B2634F" : item.Important ? "#A27536" : "#6F877C");
            }
            foreach (DeadlineBarView view in deadlineBarViews) RefreshDeadlineBar(view, now);
        }
        private static void RefreshDeadlineBar(DeadlineBarView view, DateTime now)
        {
            view.Surface.Background = UI.Brush(CalendarPalette.DeadlineBackground(view.Item, now));
            view.Label.Foreground = UI.Brush(CalendarPalette.DeadlineForeground(view.Item, now));
            view.Button.ToolTip = view.Item.Title + "\n" + Deadlines.Description(view.Item, now);
        }
        public void Edit(Todo item)
        {
            var editor = new TodoEditor(controller, item, SelectedDate) { Owner = this };
            if (editor.ShowDialog() == true) { SelectDate(editor.SavedDate); status.Text = "已保存 · " + editor.SavedDate.ToString("M月d日") + " 的安排更新了"; }
        }
        public void Guard(Action action)
        {
            try { action(); } catch (Exception error) { MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); Refresh(); }
        }
    }

    public sealed class OpportunityWindow : Window
    {
        private readonly CalendarController controller;
        private readonly StackPanel list = new StackPanel();
        public OpportunityWindow(CalendarController controller)
        {
            this.controller = controller; SetResourceReference(StyleProperty, typeof(Window));
            Title = "机会通知"; Width = 650; Height = 720; MinWidth = 520; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var root = new Grid { Margin = new Thickness(24) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            var heading = UI.Stack(UI.Text("机会通知", 23, "#27493F"), UI.Text("岗位推荐和招聘宣传集中放在这里，不占用日历。", 12, "#78918A")); heading.Margin = new Thickness(0, 0, 0, 18); root.Children.Add(heading);
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); root.Children.Add(scroll); Content = root;
            Render();
        }
        private void Render()
        {
            list.Children.Clear(); List<OpportunityNotice> notices = controller.Data.Notices.OrderBy(x => x.Read).ThenByDescending(x => x.ReceivedAt).ToList();
            if (notices.Count == 0) { var empty = UI.Text("暂无机会通知", 16, "#82958C"); empty.Margin = new Thickness(8, 48, 8, 0); list.Children.Add(empty); return; }
            foreach (OpportunityNotice notice in notices) {
                var content = new StackPanel();
                var title = UI.Text((notice.Read ? "" : "● ") + notice.Title, 15, notice.Read ? "#73867F" : "#284C42"); content.Children.Add(title);
                if (!String.IsNullOrWhiteSpace(notice.Summary)) { var summary = UI.Text(notice.Summary, 12, "#657E75"); summary.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(summary); }
                string sender = notice.EmailSource == null ? "未知发件人" : MailSourceNavigation.SenderAddress(notice.EmailSource.Sender);
                DateTimeOffset received; string when = DateTimeOffset.TryParse(notice.ReceivedAt, out received) ? received.LocalDateTime.ToString("M月d日 HH:mm") : "";
                var meta = UI.Text("发件人：" + sender + (String.IsNullOrWhiteSpace(when) ? "" : " · " + when), 11, "#84968F"); meta.Margin = new Thickness(0, 7, 0, 10); content.Children.Add(meta);
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                if (notice.EmailSource != null) actions.Children.Add(UI.Button("打开原邮件", delegate { MailSourceNavigation.Open(this, notice.EmailSource); }));
                actions.Children.Add(UI.Button(notice.Read ? "标为未读" : "标为已读", delegate { controller.ReadOpportunity(notice.Id, !notice.Read); Render(); }));
                actions.Children.Add(UI.Button("删除", delegate { controller.DeleteOpportunity(notice.Id); Render(); })); content.Children.Add(actions);
                Border card = UI.Card(content, 15); card.Margin = new Thickness(0, 0, 0, 12); card.Background = UI.Brush(notice.Read ? "#FAFBFA" : "#F1F8F5"); list.Children.Add(card);
            }
        }
    }

    public sealed class AgentSummaryWindow : Window
    {
        public AgentSummaryWindow(AgentSummary summary, string updatedAt, string lastError = "")
        {
            SetResourceReference(StyleProperty, typeof(Window)); Title = "近期工作总结"; Width = 600; Height = 650; MinWidth = 500; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var panel = new StackPanel { Margin = new Thickness(28) };
            panel.Children.Add(UI.Text("✦ 近期工作总结", 23, "#197B68"));
            DateTimeOffset when; string time = DateTimeOffset.TryParse(updatedAt, out when) ? "更新于 " + when.LocalDateTime.ToString("yyyy年M月d日 HH:mm") : "本机缓存";
            var meta = UI.Text(time, 12, "#82958C"); meta.Margin = new Thickness(0, 4, 0, 20); panel.Children.Add(meta);
            if (!String.IsNullOrWhiteSpace(lastError)) {
                var stale = UI.Text("上次整理失败：" + lastError + " 当前显示的是旧总结。", 12, "#A2663F");
                stale.TextWrapping = TextWrapping.Wrap; stale.Margin = new Thickness(0, -10, 0, 18); panel.Children.Add(stale);
            }
            var overview = UI.Text(summary.Overview, 15, "#355449"); overview.TextWrapping = TextWrapping.Wrap; overview.Margin = new Thickness(0, 0, 0, 18); panel.Children.Add(overview);
            AddSection(panel, "今天优先", summary.Today); AddSection(panel, "近期安排", summary.Upcoming); AddSection(panel, "风险提示", summary.Risks);
            var close = UI.Button("关闭", delegate { Close(); }, true); close.HorizontalAlignment = HorizontalAlignment.Right; panel.Children.Add(close);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        private static void AddSection(Panel panel, string title, IEnumerable<string> items)
        {
            var heading = UI.Text(title, 15, "#25483F"); heading.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(heading);
            string text = items == null || !items.Any() ? "暂无" : String.Join("\n", items.Select(x => "• " + x));
            var body = UI.Text(text, 13, "#607873"); body.TextWrapping = TextWrapping.Wrap; body.Margin = new Thickness(0, 0, 0, 18); panel.Children.Add(body);
        }
    }

    public sealed class TodoEditor : Window
    {
        private readonly TextBox title = new TextBox { MaxLength = 200 };
        private readonly DatePicker date = new DatePicker();
        private readonly TextBox time = new TextBox { MaxLength = 5 };
        private readonly TextBox notes = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 122, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly CheckBox important;
        private readonly CheckBox remind;
        private readonly TextBlock error = UI.Text("", 12, "#B2634F");
        public DateTime SavedDate { get; private set; }
        public TodoEditor(CalendarController controller, Todo existing, DateTime selected)
        {
            SetResourceReference(StyleProperty, typeof(Window));
            Todo draft = existing == null ? new Todo { Date = Dates.Key(selected) } : existing.Copy();
            Title = existing == null ? "新建待办" : "编辑待办"; Width = 620; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            important = UI.Option("★", "标为重要", "重要选项", true);
            remind = UI.Option("◷", "前一天提醒我", "提醒选项");
            title.Text = draft.Title; date.SelectedDate = Dates.Parse(draft.Date); time.Text = draft.Time; notes.Text = draft.Notes; important.IsChecked = draft.Important; remind.IsChecked = draft.Remind;
            var panel = new StackPanel { Margin = new Thickness(26) };
            var heading = UI.Text(Title, 23, "#27493F"); heading.Margin = new Thickness(0, 0, 0, 20); panel.Children.Add(heading);
            panel.Children.Add(UI.Field("待办内容", title));
            var kind = new ComboBox { ItemsSource = new[] { "固定时间", "截止期限" }, SelectedIndex = draft.Deadline == null ? 0 : 1 };
            panel.Children.Add(UI.Field("安排类型", kind));
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
            var dateField = UI.Field("安排在哪一天", date); dateField.Margin = new Thickness(0, 0, 12, 15); row.Children.Add(dateField);
            var timeField = UI.Field("时间（选填，例如 09:30）", time); Grid.SetColumn(timeField, 1); row.Children.Add(timeField); panel.Children.Add(row);
            var deadlineFields = new DeadlineFields(draft.Deadline, selected); panel.Children.Add(deadlineFields);
            panel.Children.Add(UI.Field("备注（选填）", notes));
            if (draft.EmailSource != null) {
                var sourcePanel = new StackPanel();
                sourcePanel.Children.Add(UI.Text("来源邮件", 12, "#607873"));
                string address = MailSourceNavigation.SenderAddress(draft.EmailSource.Sender);
                var sourceMeta = UI.Text("发件人：" + (String.IsNullOrWhiteSpace(address) ? "未知" : address) + "\n主题：" + draft.EmailSource.Subject, 12, "#46675E"); sourceMeta.Margin = new Thickness(0, 7, 0, 9); sourcePanel.Children.Add(sourceMeta);
                Button openSource = UI.Button("打开原邮件", delegate { try { MailSourceNavigation.Open(this, draft.EmailSource); } catch (Exception exception) { error.Text = exception.Message; } }); openSource.HorizontalAlignment = HorizontalAlignment.Left; sourcePanel.Children.Add(openSource);
                Border sourceCard = UI.Card(sourcePanel, 13); sourceCard.Background = UI.Brush("#F2F8F5"); sourceCard.Margin = new Thickness(0, 0, 0, 15); panel.Children.Add(sourceCard);
            }
            var checks = new StackPanel { Orientation = Orientation.Horizontal }; important.Margin = new Thickness(0, 6, 12, 6); checks.Children.Add(important); checks.Children.Add(remind); panel.Children.Add(checks);
            var hint = UI.Text("将在前一天 " + controller.Data.ReminderTime + " 提醒；若已错过，会在程序运行时补发。", 11, "#8DA198"); hint.Margin = new Thickness(0, 4, 0, 8); panel.Children.Add(hint);
            Action updateKind = delegate {
                bool deadline = kind.SelectedIndex == 1;
                row.Visibility = deadline ? Visibility.Collapsed : Visibility.Visible;
                deadlineFields.Visibility = deadline ? Visibility.Visible : Visibility.Collapsed;
                remind.Content = UI.OptionContent("◷", deadline ? "截止前 24 小时提醒我" : "前一天提醒我", "#197B68");
                hint.Text = deadline ? "不足 24 小时时会在运行期间补发，截止后不再弹窗。完成后停止提醒。" : "将在前一天 " + controller.Data.ReminderTime + " 提醒；若已错过，会在程序运行时补发。";
            };
            kind.SelectionChanged += delegate { updateKind(); }; updateKind();
            error.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(error);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (existing != null) {
                Button trash = UI.Button("移到回收站", delegate {
                    try { controller.Trash(existing.Id, true); SavedDate = Dates.Parse(existing.Date); DialogResult = true; }
                    catch (Exception exception) { error.Text = exception.Message; }
                }); trash.HorizontalAlignment = HorizontalAlignment.Left; trash.Foreground = UI.Brush("#A66055"); actions.Children.Add(trash);
            }
            var right = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(right, 1); actions.Children.Add(right);
            Button cancel = UI.Button("取消", delegate { DialogResult = false; }); cancel.IsCancel = true; right.Children.Add(cancel);
            Button save = UI.Button("保存待办", delegate {
                try {
                    if (kind.SelectedIndex == 0 && !date.SelectedDate.HasValue) throw new ArgumentException("请选择一个日期。");
                    draft.Title = title.Text; draft.Date = Dates.Key(date.SelectedDate ?? selected); draft.Time = time.Text.Trim(); draft.Notes = notes.Text; draft.Important = important.IsChecked == true; draft.Remind = remind.IsChecked == true;
                    draft.Deadline = kind.SelectedIndex == 1 ? deadlineFields.Read() : null;
                    Deadlines.Normalize(draft); controller.SaveTodo(draft); SavedDate = Dates.Parse(draft.Date); DialogResult = true;
                }
                catch (Exception exception) { error.Text = exception.Message; }
            }, true); save.IsDefault = true; right.Children.Add(save);
            var body = new DockPanel(); actions.Margin = new Thickness(26, 12, 26, 20); DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);
            body.Children.Add(new ScrollViewer { Content = panel }); Content = body;
            Loaded += delegate { title.Focus(); };
        }
    }

    public sealed class ReminderWindow : Window
    {
        private readonly DispatcherTimer autoClose = new DispatcherTimer();
        public ReminderWindow(IList<Todo> items, DateTime now, Action open, Action snooze, bool test)
        {
            SetResourceReference(StyleProperty, typeof(Window));
            Title = "小日历提醒"; Width = 390; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
            var panel = new StackPanel();
            var header = new DockPanel(); var close = UI.Button("×", Close); close.Padding = new Thickness(6, 0, 6, 2); close.FontSize = 20; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            header.Children.Add(UI.Text(test ? "提醒预览" : items.Any(x => x.Deadline != null) ? "留意待办截止期限" : "给明天留一点准备", 17, "#197B68")); panel.Children.Add(header);
            var count = UI.Text(test ? "这就是待办提醒的样子" : "你有 " + items.Count + " 项待办需要留意", 12, "#789087"); count.Margin = new Thickness(0, 6, 0, 14); panel.Children.Add(count);
            foreach (Todo item in items.Take(3)) {
                var title = UI.Text(item.Title, 14, "#304D43"); title.MaxHeight = 45; title.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(title);
                var detail = UI.Text(item.Deadline == null ? Dates.Relative(Dates.Parse(item.Date), now) + (String.IsNullOrEmpty(item.Time) ? " · 全天" : " · " + item.Time) : Deadlines.Description(item, now), 11, "#8A9E95"); detail.Margin = new Thickness(0, 3, 0, 12); panel.Children.Add(detail);
            }
            if (items.Count > 3) panel.Children.Add(UI.Text("还有 " + (items.Count - 3) + " 项，打开日历查看", 12, "#7D9388"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(UI.Button("稍后 10 分钟", delegate { snooze(); Close(); }));
            actions.Children.Add(UI.Button("打开日历", delegate { open(); Close(); }, true)); panel.Children.Add(actions);
            var border = UI.Card(panel, 20); border.BorderBrush = UI.Brush("#B1D2C5"); border.BorderThickness = new Thickness(1); Content = border;
            Loaded += delegate { Rect area = SystemParameters.WorkArea; Left = area.Right - ActualWidth - 16; Top = area.Bottom - ActualHeight - 16; };
            autoClose.Interval = TimeSpan.FromSeconds(30); autoClose.Tick += delegate { Close(); };
            MouseEnter += delegate { autoClose.Stop(); }; MouseLeave += delegate { autoClose.Start(); };
            Closed += delegate { autoClose.Stop(); };
            autoClose.Start();
        }
    }
}
