using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LittleCalendar
{
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string Command { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --background"; } }
        public static bool Enabled
        {
            get { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && String.Equals(key.GetValue("LittleCalendar") as string, Command, StringComparison.OrdinalIgnoreCase); }
        }
        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (enabled) key.SetValue("LittleCalendar", Command, RegistryValueKind.String);
                else key.DeleteValue("LittleCalendar", false);
            }
        }
    }

    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(CalendarController controller, Action testReminder, SecretStore secrets, IWorkAgent agent,
            MailStateStore mailStateStore = null, MailSecretStore mailSecrets = null, IMailboxClientFactory mailFactory = null, Action<int> syncMail = null)
        {
            string dataDirectory = Path.GetDirectoryName(controller.Store.FilePath);
            mailStateStore = mailStateStore ?? new MailStateStore(dataDirectory);
            mailSecrets = mailSecrets ?? new MailSecretStore(dataDirectory);
            mailFactory = mailFactory ?? new MailKitClientFactory(dataDirectory);
            MailSyncState mailState;
            try { mailState = mailStateStore.Load(); } catch { mailState = new MailSyncState(); }
            SetResourceReference(StyleProperty, typeof(Window));
            AppearancePalette.Normalize(controller.Data.Appearance); AppearancePalette.CurrentSettings = controller.Data.Appearance.Copy();
            AppearanceProfile settingsPalette = AppearancePalette.Current;
            Title = "设置"; Width = 900; Height = 700; MinWidth = 760; MinHeight = 600; ResizeMode = ResizeMode.CanResizeWithGrip; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = UI.Brush(settingsPalette.Canvas);
            var reminderPanel = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            var appearancePanel = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            var agentPanel = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            var mailPanel = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            var dataPanel = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            StackPanel panel = reminderPanel;
            var message = UI.Text("", 12, "#7E968A");
            var heading = UI.Text("提前一天，心里有数", 23, "#25483F"); heading.Margin = new Thickness(0, 0, 0, 20); panel.Children.Add(heading);
            var time = new TextBox { Text = controller.Data.ReminderTime, MaxLength = 5 }; panel.Children.Add(UI.Field("普通待办：前一天几点提醒（24 小时制）", time));
            panel.Children.Add(UI.Text("截止期限：固定提前 24 小时提醒，不受上面的时间设置影响。", 12, "#82958C"));
            var sound = UI.Option("◷", "提醒时播放提示音", "声音选项"); sound.IsChecked = controller.Data.Sound; panel.Children.Add(sound);
            bool originalStartup = Startup.Enabled;
            var startup = UI.Option("↗", "随 Windows 登录启动，在托盘等待提醒", "开机启动选项"); startup.IsChecked = originalStartup; panel.Children.Add(startup);
            var help = UI.Text("关闭窗口后仍会提醒。通过托盘菜单退出或关机后，提醒会暂停；再次运行会补发当天及次日未过期的提醒。", 12, "#82958C"); help.Margin = new Thickness(0, 8, 0, 18); panel.Children.Add(help);
            var preview = UI.Button("看看提醒长什么样", testReminder); preview.HorizontalAlignment = HorizontalAlignment.Left; preview.Margin = new Thickness(0, 0, 0, 18); panel.Children.Add(preview);
            panel = appearancePanel;
            panel.Children.Add(UI.Text("外观", 16, "#355449"));
            AppearanceSettings savedAppearance = (controller.Data.Appearance ?? new AppearanceSettings()).Copy();
            AppearancePalette.Normalize(savedAppearance);
            var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
            theme.Items.Add(new ComboBoxItem { Content = "翡翠绿", Tag = "emerald" });
            theme.Items.Add(new ComboBoxItem { Content = "静谧蓝", Tag = "blue" });
            theme.Items.Add(new ComboBoxItem { Content = "暖紫色", Tag = "purple" });
            theme.Items.Add(new ComboBoxItem { Content = "樱花粉", Tag = "sakura" });
            theme.SelectedIndex = new[] { "emerald", "blue", "purple", "sakura" }.ToList().IndexOf(savedAppearance.Theme);
            panel.Children.Add(UI.Field("页面配色", theme));
            var opacity = new Slider { Minimum = 70, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = savedAppearance.BackgroundOpacity, Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
            var opacityValue = UI.Text(((int)opacity.Value) + "%", 12, "#607873");
            opacity.ValueChanged += delegate { opacityValue.Text = ((int)opacity.Value) + "%"; };
            var opacityRow = new StackPanel { Orientation = Orientation.Horizontal }; opacityRow.Children.Add(opacity); opacityValue.Margin = new Thickness(12, 0, 0, 0); opacityRow.Children.Add(opacityValue);
            panel.Children.Add(UI.Field("页面背景透明度", opacityRow));
            var backgroundMode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
            backgroundMode.Items.Add(new ComboBoxItem { Content = "铺满裁切（不拉伸）", Tag = "cover" });
            backgroundMode.Items.Add(new ComboBoxItem { Content = "完整显示（不拉伸）", Tag = "contain" });
            backgroundMode.SelectedIndex = savedAppearance.BackgroundMode == "contain" ? 1 : 0;
            panel.Children.Add(UI.Field("背景图片适配", backgroundMode));
            string pendingBackgroundPath = ""; bool removeBackground = false;
            var backgroundHint = UI.Text(String.IsNullOrWhiteSpace(savedAppearance.BackgroundFile) ? "未设置背景图片。支持 JPG、PNG、BMP，最大 10 MB。" : "当前已设置本地背景图片；保存后仍由小日历管理，不依赖原文件位置。", 12, "#82958C");
            backgroundHint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(backgroundHint);
            var backgroundActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            backgroundActions.Children.Add(UI.Button("选择背景图片", delegate {
                var dialog = new OpenFileDialog { Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" };
                if (dialog.ShowDialog(this) != true) return;
                if (new FileInfo(dialog.FileName).Length > 10 * 1024 * 1024) { message.Text = "背景图片请控制在 10 MB 以内。"; return; }
                pendingBackgroundPath = dialog.FileName; removeBackground = false; backgroundHint.Text = "已选择：" + Path.GetFileName(dialog.FileName) + "。保存设置后生效。";
            }));
            backgroundActions.Children.Add(UI.Button("移除背景图", delegate { pendingBackgroundPath = ""; removeBackground = true; backgroundHint.Text = "保存设置后将移除背景图片。"; }));
            backgroundActions.Children.Add(UI.Button("恢复默认外观", delegate {
                theme.SelectedIndex = 0; opacity.Value = 100; backgroundMode.SelectedIndex = 0; pendingBackgroundPath = ""; removeBackground = true; backgroundHint.Text = "保存设置后恢复默认外观。";
            }));
            panel.Children.Add(backgroundActions);
            panel = agentPanel;
            panel.Children.Add(UI.Text("智能整理 · DeepSeek", 16, "#355449"));
            var agentEnabled = UI.Option("✦", "每天自动整理一次近期待办", "启用每日智能整理"); agentEnabled.IsChecked = controller.Data.Agent.Enabled; panel.Children.Add(agentEnabled);
            var agentTime = new TextBox { Text = controller.Data.Agent.DailyTime, MaxLength = 5 }; panel.Children.Add(UI.Field("每日总结时间", agentTime));
            var model = new TextBox { Text = controller.Data.Agent.Model, MaxLength = 80 }; panel.Children.Add(UI.Field("DeepSeek 模型", model));
            var key = new PasswordBox { MaxLength = 300 }; AutomationProperties.SetName(key, "DeepSeek API Key"); panel.Children.Add(UI.Field("DeepSeek API Key", key));
            var keyHint = UI.Text(secrets.HasKey ? "已使用 Windows 当前用户加密保存。留空不会覆盖现有 Key。" : "尚未保存 Key。Key 不会写入日历数据、备份或日志。", 12, "#82958C"); keyHint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(keyHint);
            var privacy = UI.Text("生成总结时，会把未完成待办的标题、时间、期限、重要性和备注发送给 DeepSeek；不会发送已完成项、回收站或两周后的普通安排。", 12, "#82958C"); privacy.TextWrapping = TextWrapping.Wrap; privacy.Margin = new Thickness(0, 8, 0, 10); panel.Children.Add(privacy);
            var keyActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
            Button testConnection = null;
            testConnection = UI.Button("测试连接", delegate {
                string candidate;
                try { candidate = String.IsNullOrWhiteSpace(key.Password) ? secrets.Load() : key.Password.Trim(); }
                catch (Exception e) { message.Text = e.Message; return; }
                if (String.IsNullOrWhiteSpace(candidate)) { message.Text = "请先填写 DeepSeek API Key。"; return; }
                if (String.IsNullOrWhiteSpace(model.Text)) { message.Text = "模型名不能为空。"; return; }
                testConnection.IsEnabled = false; message.Text = "正在连接 DeepSeek…";
                ThreadPool.QueueUserWorkItem(delegate {
                    string result; try { agent.Test(candidate, model.Text.Trim()); result = "连接成功，当前 Key 和模型可用。"; }
                    catch (Exception e) { result = e.Message; }
                    Dispatcher.BeginInvoke(new Action(delegate { testConnection.IsEnabled = true; message.Text = result; }));
                });
            }); keyActions.Children.Add(testConnection);
            var clearKey = UI.Button("清除 Key", delegate {
                try { secrets.Clear(); key.Password = ""; keyHint.Text = "Key 已清除。保存设置后，自动整理仍会保持关闭，直到重新配置。"; message.Text = "已清除本机保存的 DeepSeek API Key。"; }
                catch (Exception e) { message.Text = "清除失败：" + e.Message; }
            }); keyActions.Children.Add(clearKey); panel.Children.Add(keyActions);
            panel = mailPanel;
            panel.Children.Add(UI.Text("网易邮箱同步", 16, "#355449"));
            var mailEnabled = UI.Option("✉", "每天读取招聘邮件并自动生成待办", "启用网易邮箱每日同步");
            mailEnabled.IsChecked = mailState.Account.Enabled; panel.Children.Add(mailEnabled);
            var mailAddress = new TextBox { Text = mailState.Account.Address, MaxLength = 160 };
            AutomationProperties.SetName(mailAddress, "网易邮箱地址"); panel.Children.Add(UI.Field("网易邮箱地址", mailAddress));
            var mailHost = new TextBox { Text = String.IsNullOrWhiteSpace(mailState.Account.Host) ? "imap.163.com" : mailState.Account.Host, MaxLength = 160 };
            AutomationProperties.SetName(mailHost, "IMAP服务器");
            var mailPort = new TextBox { Text = (mailState.Account.Port <= 0 ? 993 : mailState.Account.Port).ToString(), MaxLength = 5 };
            AutomationProperties.SetName(mailPort, "IMAP端口");
            var serverRow = new Grid(); serverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) }); serverRow.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement hostField = UI.Field("IMAP服务器", mailHost); FrameworkElement portField = UI.Field("SSL端口", mailPort);
            hostField.Margin = new Thickness(0, 0, 10, 0); Grid.SetColumn(portField, 1); serverRow.Children.Add(hostField); serverRow.Children.Add(portField); panel.Children.Add(serverRow);
            var mailAuthorization = new PasswordBox { MaxLength = 300 };
            panel.Children.Add(UI.Field("客户端授权码（不是网页登录密码）", mailAuthorization));
            AutomationProperties.SetName(mailAuthorization, "网易邮箱授权码");
            var mailHint = UI.Text(mailSecrets.HasKey ? "授权码已使用 Windows 当前用户加密保存，留空不会覆盖。" : "请在网易邮箱开启 IMAP 后生成客户端授权码。", 12, "#82958C");
            mailHint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(mailHint);
            string mailStatusText;
            DateTimeOffset lastMailSync;
            if (DateTimeOffset.TryParse(mailState.LastCompletedAt, out lastMailSync)) {
                mailStatusText = "上次同步 " + lastMailSync.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    + " · 扫描 " + mailState.LastScannedCount
                    + " · 新增 " + mailState.LastCreatedCount
                    + " · 回写 " + mailState.LastReconciledCount;
            } else mailStatusText = "尚未完成过邮箱同步。首次同步会读取最近 7 天。";
            if (!String.IsNullOrWhiteSpace(mailState.LastError)) mailStatusText += "\n最近错误：" + mailState.LastError;
            var mailStatus = UI.Text(mailStatusText, 12, String.IsNullOrWhiteSpace(mailState.LastError) ? "#526E64" : "#A45D43");
            mailStatus.TextWrapping = TextWrapping.Wrap; mailStatus.Margin = new Thickness(0, 6, 0, 8);
            AutomationProperties.SetName(mailStatus, "邮箱同步状态"); panel.Children.Add(mailStatus);
            var syncDays = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(syncDays, "手动同步邮件范围");
            foreach (int value in new[] { 1, 3, 7, 14, 30 }) syncDays.Items.Add(new ComboBoxItem { Content = "最近 " + value + " 天", Tag = value });
            int savedDays = new[] { 1, 3, 7, 14, 30 }.Contains(mailState.Account.ManualSyncDays) ? mailState.Account.ManualSyncDays : 7;
            syncDays.SelectedIndex = Array.IndexOf(new[] { 1, 3, 7, 14, 30 }, savedDays);
            panel.Children.Add(UI.Field("立即同步读取范围", syncDays));
            AutomationProperties.SetName(syncDays, "手动同步邮件范围");
            Func<int> selectedSyncDays = () => Convert.ToInt32(((ComboBoxItem)syncDays.SelectedItem).Tag);
            var folderPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(UI.Text("同步文件夹", 12, "#526E64")); panel.Children.Add(folderPanel);
            var folderChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
            Action<IList<MailboxFolder>> renderFolders = folders => {
                folderPanel.Children.Clear(); folderChecks.Clear();
                foreach (MailboxFolder folder in folders.Where(MailFolderPolicy.ShouldInclude)) {
                    MailFolderState saved = mailState.Folders.FirstOrDefault(x => x.FolderId == folder.FullName);
                    var choice = UI.Option("•", String.IsNullOrWhiteSpace(folder.DisplayName) ? folder.FullName : folder.DisplayName, "同步文件夹 " + folder.FullName);
                    choice.IsChecked = saved == null || saved.Enabled; folderChecks[folder.FullName] = choice; folderPanel.Children.Add(choice);
                }
                if (folderChecks.Count == 0) folderPanel.Children.Add(UI.Text("测试连接后会显示可同步的文件夹。", 11, "#8B9F97"));
            };
            renderFolders(mailState.Folders.Select(x => new MailboxFolder { FullName = x.FolderId, DisplayName = x.DisplayName }).ToList());
            Func<string> candidateAuthorization = () => String.IsNullOrWhiteSpace(mailAuthorization.Password) ? mailSecrets.Load() : mailAuthorization.Password.Trim();
            Func<MailConnectionOptions> connectionOptions = () => {
                int portValue;
                if (!Int32.TryParse(mailPort.Text.Trim(), out portValue) || portValue < 1 || portValue > 65535) throw new ArgumentException("IMAP端口无效。");
                if (String.IsNullOrWhiteSpace(mailAddress.Text) || !mailAddress.Text.Trim().EndsWith("@163.com", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请填写有效的 @163.com 邮箱地址。");
                if (String.IsNullOrWhiteSpace(mailHost.Text)) throw new ArgumentException("IMAP服务器不能为空。");
                return new MailConnectionOptions { Address = mailAddress.Text.Trim(), Host = mailHost.Text.Trim(), Port = portValue, UseSsl = true };
            };
            var mailActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 18) };
            Button testMail = null;
            testMail = UI.Button("测试邮箱连接", delegate {
                string authorization; MailConnectionOptions options;
                try { authorization = candidateAuthorization(); options = connectionOptions(); }
                catch (Exception e) { message.Text = e.Message; return; }
                if (String.IsNullOrWhiteSpace(authorization)) { message.Text = "请先填写网易邮箱授权码。"; return; }
                testMail.IsEnabled = false; message.Text = "正在连接网易邮箱…";
                ThreadPool.QueueUserWorkItem(delegate {
                    IList<MailboxFolder> folders = null; Exception error = null;
                    try { using (IMailboxClient mailbox = mailFactory.Create()) { mailbox.Connect(options, authorization); folders = mailbox.ListFolders(); } }
                    catch (Exception e) { error = e; }
                    Dispatcher.BeginInvoke(new Action(delegate {
                        testMail.IsEnabled = true;
                        if (error == null) { renderFolders(folders); message.Text = "连接成功，发现 " + folders.Count(MailFolderPolicy.ShouldInclude) + " 个可同步文件夹。"; }
                        else message.Text = "邮箱连接失败：" + error.Message;
                    }));
                });
            }); mailActions.Children.Add(testMail);
            mailActions.Children.Add(UI.Button("立即同步邮件", delegate {
                if (syncMail == null) { message.Text = "保存设置后，可从主程序立即同步。"; return; }
                try {
                    MailConnectionOptions options = connectionOptions();
                    string authorization = candidateAuthorization();
                    if (String.IsNullOrWhiteSpace(authorization)) throw new ArgumentException("请先填写网易邮箱授权码。");
                    if (!String.IsNullOrWhiteSpace(mailAuthorization.Password)) mailSecrets.Save(mailAuthorization.Password);
                    mailState.Account.Enabled = true; mailState.Account.Address = options.Address; mailState.Account.Host = options.Host; mailState.Account.Port = options.Port; mailState.Account.UseSsl = true;
                    mailState.Account.ManualSyncDays = selectedSyncDays();
                    mailStateStore.Save(mailState); syncMail(mailState.Account.ManualSyncDays); message.Text = "已开始同步最近 " + mailState.Account.ManualSyncDays + " 天邮件，可回到日历查看进度。";
                } catch (Exception e) { message.Text = e.Message; }
            }));
            mailActions.Children.Add(UI.Button("清除邮箱授权", delegate {
                try { mailSecrets.Clear(); mailAuthorization.Password = ""; mailHint.Text = "授权码已清除。"; message.Text = "已清除本机保存的网易邮箱授权码。"; }
                catch (Exception e) { message.Text = "清除失败：" + e.Message; }
            }));
            mailActions.Children.Add(UI.Button("打开邮箱日志目录", delegate {
                try {
                    string logDirectory = new MailDiagnosticLog(dataDirectory).DirectoryPath;
                    Directory.CreateDirectory(logDirectory);
                    Process.Start("explorer.exe", "\"" + logDirectory + "\"");
                    message.Text = "已打开邮箱日志目录。";
                } catch (Exception e) { message.Text = "打开日志目录失败：" + e.Message; }
            }));
            panel.Children.Add(mailActions);
            panel = dataPanel;
            panel.Children.Add(UI.Text("数据与备份", 16, "#355449"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
            actions.Children.Add(UI.Button("导出备份", delegate {
                var dialog = new SaveFileDialog { Filter = "日历备份 (*.json)|*.json", FileName = "小日历备份-" + DateTime.Now.ToString("yyyyMMdd") + ".json", AddExtension = true };
                if (dialog.ShowDialog(this) == true) { try { controller.Store.Export(controller.Data, dialog.FileName); message.Text = "备份已导出。"; } catch (Exception e) { message.Text = e.Message; } }
            }));
            actions.Children.Add(UI.Button("恢复备份", delegate {
                var dialog = new OpenFileDialog { Filter = "日历备份 (*.json)|*.json" };
                if (dialog.ShowDialog(this) != true) return;
                try {
                    CalendarData imported = controller.Store.Decode(File.ReadAllText(dialog.FileName));
                    if (MessageBox.Show(this, "备份中有 " + imported.Items.Count(x => !x.Deleted) + " 项待办。恢复将替换当前记录，并保留当前文件的上一个版本备份。继续吗？", "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    controller.RestoreBackup(imported); time.Text = imported.ReminderTime; sound.IsChecked = imported.Sound; agentEnabled.IsChecked = imported.Agent.Enabled; agentTime.Text = imported.Agent.DailyTime; model.Text = imported.Agent.Model; message.Text = "备份已恢复；API Key 独立保存，未被替换。";
                } catch (Exception e) { message.Text = "恢复失败：" + e.Message; }
            }));
            panel.Children.Add(actions);
            var directory = new TextBox { Text = Path.GetDirectoryName(controller.Store.FilePath), IsReadOnly = true, FontSize = 11, Background = System.Windows.Media.Brushes.Transparent }; panel.Children.Add(directory);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = UI.Button("取消", delegate { DialogResult = false; }); cancel.IsCancel = true; footer.Children.Add(cancel);
            var save = UI.Button("保存设置", delegate {
                if (!Dates.IsTime(time.Text.Trim())) { message.Text = "请填写 19:00 这样的时间。"; return; }
                if (!Dates.IsTime(agentTime.Text.Trim())) { message.Text = "每日总结时间请填写 09:00 这样的格式。"; return; }
                if (String.IsNullOrWhiteSpace(model.Text)) { message.Text = "DeepSeek 模型名不能为空。"; return; }
                if (agentEnabled.IsChecked == true && String.IsNullOrWhiteSpace(key.Password) && !secrets.HasKey) { message.Text = "启用每日智能整理前，请先填写 DeepSeek API Key。"; return; }
                try {
                    bool requested = startup.IsChecked == true;
                    if (requested != originalStartup) Startup.Set(requested);
                    try {
                        if (!String.IsNullOrWhiteSpace(key.Password)) secrets.Save(key.Password);
                        var appearance = savedAppearance.Copy();
                        appearance.Theme = (string)((ComboBoxItem)theme.SelectedItem).Tag;
                        appearance.BackgroundOpacity = (int)opacity.Value;
                        appearance.BackgroundMode = (string)((ComboBoxItem)backgroundMode.SelectedItem).Tag;
                        string importedBackground = "";
                        if (!String.IsNullOrWhiteSpace(pendingBackgroundPath)) {
                            importedBackground = AppearanceFiles.Import(dataDirectory, pendingBackgroundPath);
                            appearance.BackgroundFile = importedBackground;
                        } else if (removeBackground) appearance.BackgroundFile = "";
                        AppearancePalette.Normalize(appearance);
                        try {
                            controller.Commit(data => { data.ReminderTime = time.Text.Trim(); data.Sound = sound.IsChecked == true; data.Agent.Enabled = agentEnabled.IsChecked == true; data.Agent.DailyTime = agentTime.Text.Trim(); data.Agent.Model = model.Text.Trim(); data.Appearance = appearance.Copy(); });
                        } catch {
                            if (!String.IsNullOrWhiteSpace(importedBackground)) AppearanceFiles.Delete(dataDirectory, importedBackground);
                            throw;
                        }
                        if (!String.Equals(savedAppearance.BackgroundFile, appearance.BackgroundFile, StringComparison.Ordinal)) AppearanceFiles.Delete(dataDirectory, savedAppearance.BackgroundFile);
                        if (mailEnabled.IsChecked == true && String.IsNullOrWhiteSpace(mailAuthorization.Password) && !mailSecrets.HasKey)
                            throw new ArgumentException("启用邮箱同步前，请填写网易邮箱授权码。");
                        if (!String.IsNullOrWhiteSpace(mailAuthorization.Password)) mailSecrets.Save(mailAuthorization.Password);
                        mailState.Account.Enabled = mailEnabled.IsChecked == true;
                        mailState.Account.ManualSyncDays = selectedSyncDays();
                        if (mailState.Account.Enabled || !String.IsNullOrWhiteSpace(mailAddress.Text)) {
                            MailConnectionOptions mailOptions = connectionOptions();
                            mailState.Account.Address = mailOptions.Address; mailState.Account.Host = mailOptions.Host;
                            mailState.Account.Port = mailOptions.Port; mailState.Account.UseSsl = true;
                        }
                        foreach (var pair in folderChecks) {
                            MailFolderState folder = mailState.Folders.FirstOrDefault(x => x.FolderId == pair.Key);
                            if (folder == null) { folder = new MailFolderState { FolderId = pair.Key, DisplayName = pair.Value.Content as string }; mailState.Folders.Add(folder); }
                            folder.Enabled = pair.Value.IsChecked == true;
                        }
                        mailStateStore.Save(mailState);
                    }
                    catch { if (requested != originalStartup) Startup.Set(originalStartup); throw; }
                    DialogResult = true;
                } catch (Exception e) { message.Text = "保存失败：" + e.Message; }
            }, true); save.IsDefault = true; footer.Children.Add(save);

            var contentScroll = new ScrollViewer { Content = reminderPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) };
            var right = new Grid { Margin = new Thickness(26, 24, 18, 20) };
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.Children.Add(contentScroll);
            message.Margin = new Thickness(2, 4, 0, 12); Grid.SetRow(message, 1); right.Children.Add(message);
            Grid.SetRow(footer, 2); right.Children.Add(footer);

            var navigation = new StackPanel { Margin = new Thickness(14, 22, 12, 18) };
            var navTitle = UI.Text("设置", 19, settingsPalette.Accent); navTitle.Margin = new Thickness(10, 0, 0, 4); navigation.Children.Add(navTitle);
            var navHint = UI.Text("提醒、外观与同步", 11, settingsPalette.TabText); navHint.Margin = new Thickness(10, 0, 0, 20); navigation.Children.Add(navHint);
            var navButtons = new List<Button>();
            StackPanel[] pages = { reminderPanel, appearancePanel, agentPanel, mailPanel, dataPanel };
            string[] pageNames = { "提醒", "外观", "智能整理", "邮箱同步", "数据与备份" };
            int selectedPage = 0;
            Action<int> showPage = null;
            showPage = delegate(int index) {
                selectedPage = index; contentScroll.Content = pages[index];
                for (int i = 0; i < navButtons.Count; i++) {
                    bool selected = i == selectedPage;
                    navButtons[i].Background = UI.Brush(selected ? settingsPalette.SelectedDay : "Transparent");
                    navButtons[i].Foreground = UI.Brush(selected ? settingsPalette.Accent : settingsPalette.TabText);
                    navButtons[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            for (int i = 0; i < pageNames.Length; i++) {
                int captured = i; var button = UI.Button(pageNames[i], delegate { showPage(captured); });
                button.Margin = new Thickness(0, 0, 0, 4); button.Padding = new Thickness(12, 10, 12, 10); button.HorizontalContentAlignment = HorizontalAlignment.Left;
                navigation.Children.Add(button); navButtons.Add(button);
            }
            showPage(0);
            var left = new Border { Width = 156, Background = UI.Brush(settingsPalette.AgentBackground), BorderBrush = UI.Brush(settingsPalette.AgentBorder), BorderThickness = new Thickness(0, 0, 1, 0), Child = navigation };
            var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) }); root.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(left); Grid.SetColumn(right, 1); root.Children.Add(right); Content = root;
            Loaded += delegate { ApplyThemeToVisibleControls(this, settingsPalette); };
        }
        private static void ApplyThemeToVisibleControls(DependencyObject root, AppearanceProfile palette)
        {
            if (root == null) return;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var textBox = child as TextBox;
                if (textBox != null) { textBox.BorderBrush = UI.Brush(palette.SelectedBorder); textBox.Background = System.Windows.Media.Brushes.White; }
                var password = child as PasswordBox;
                if (password != null) { password.BorderBrush = UI.Brush(palette.SelectedBorder); password.Background = System.Windows.Media.Brushes.White; }
                var combo = child as ComboBox;
                if (combo != null) { combo.BorderBrush = UI.Brush(palette.SelectedBorder); combo.Background = UI.Brush(palette.TabBackground); }
                var check = child as CheckBox;
                if (check != null) { check.BorderBrush = UI.Brush(palette.SelectedBorder); check.Background = UI.Brush(palette.AgentBackground); check.Foreground = UI.Brush(palette.AccentText); }
                ApplyThemeToVisibleControls(child, palette);
            }
        }
    }

    public sealed class CalendarRuntime : IDisposable
    {
        public CalendarController Controller { get; private set; }
        public CalendarWindow Window { get; private set; }
        public ReminderWindow ActiveReminder { get; private set; }
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        private readonly Icon icon;
        private bool exiting;
        private bool explainedTray;
        private volatile bool sessionLocked;
        private DateTime lastDay = DateTime.Today;
        private readonly Func<DateTime> clock;
        private readonly SecretStore secrets;
        private readonly IWorkAgent workAgent;
        private readonly AgentCoordinator agentCoordinator;
        private readonly IMailSyncService mailSync;
        private readonly MailStateStore mailStateStore;
        private readonly MailSecretStore mailSecrets;
        private readonly IMailboxClientFactory mailFactory;
        private bool agentBusy;
        private bool mailBusy;
        public CalendarRuntime(CalendarController controller, Func<DateTime> clock = null, IWorkAgent workAgent = null, IMailSyncService mailSync = null)
        {
            Controller = controller; this.clock = clock ?? (() => DateTime.Now);
            secrets = new SecretStore(Path.GetDirectoryName(controller.Store.FilePath)); this.workAgent = workAgent ?? new DeepSeekAgent(); agentCoordinator = new AgentCoordinator(controller, secrets, this.clock);
            string dataDirectory = Path.GetDirectoryName(controller.Store.FilePath);
            mailStateStore = new MailStateStore(dataDirectory); mailSecrets = new MailSecretStore(dataDirectory); mailFactory = new MailKitClientFactory(dataDirectory);
            var mailDiagnostics = new MailDiagnosticLog(dataDirectory);
            this.mailSync = mailSync ?? new MailSyncCoordinator(controller, mailStateStore, mailSecrets, secrets,
                mailFactory, new DeepSeekMailActionAnalyzer(this.workAgent as DeepSeekAgent ?? new DeepSeekAgent(), mailDiagnostics), this.clock, mailDiagnostics);
            Window = new CalendarWindow(controller, this.clock); Window.SettingsRequested = OpenSettings; Window.AgentSummaryRequested = delegate { StartAgentSummary(false); };
            lastDay = this.clock().Date;
            icon = CreateIcon(); tray.Icon = icon; tray.Text = "小日历 · 双击查看待办"; tray.Visible = true;
            Window.Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            tray.DoubleClick += delegate { ShowMain(); };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开日历", null, delegate { ShowMain(); });
            menu.Items.Add("新建待办", null, delegate { ShowMain(); Window.Edit(null); });
            menu.Items.Add("提醒设置", null, delegate { OpenSettings(); });
            menu.Items.Add("测试右下角提醒", null, delegate { PreviewReminder(); });
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, delegate { Exit(); }); tray.ContextMenuStrip = menu;
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (exiting) return;
                e.Cancel = true; Window.Hide();
                if (!explainedTray) { tray.ShowBalloonTip(3000, "小日历仍在提醒", "双击托盘图标打开日历，右键菜单可退出。", Forms.ToolTipIcon.Info); explainedTray = true; }
            };
            timer.Interval = TimeSpan.FromSeconds(20); timer.Tick += delegate { Tick(); }; timer.Start();
            SystemEvents.PowerModeChanged += OnPowerChanged; SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        public void ShowMain()
        {
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action(ShowMain)); return; }
            Window.Show(); if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate(); Window.Refresh();
        }
        public void OpenSettings()
        {
            ShowMain(); var settings = new SettingsWindow(Controller, PreviewReminder, secrets, workAgent, mailStateStore, mailSecrets, mailFactory, delegate(int days) { StartMailSync(false, days); }) { Owner = Window };
            settings.ShowDialog(); Tick();
        }
        public void StartAgentSummary(bool automatic)
        {
            if (exiting || sessionLocked || agentBusy) return;
            AgentJob job;
            try { job = agentCoordinator.Prepare(automatic); }
            catch (Exception e) { Window.SetAgentBusy(false, e.Message); return; }
            if (job == null) return;
            agentBusy = true; Window.SetAgentBusy(true, automatic ? "正在进行每日整理…" : "正在整理近期待办…");
            ThreadPool.QueueUserWorkItem(delegate {
                AgentSummary summary = null; Exception error = null;
                try { summary = workAgent.Summarize(job.Data, job.Now, job.ApiKey, job.Model); }
                catch (Exception e) { error = e; }
                Window.Dispatcher.BeginInvoke(new Action(delegate {
                    try {
                        if (error == null) { agentCoordinator.Complete(job, summary); Window.SetAgentBusy(false, "总结已更新"); }
                        else { agentCoordinator.Fail(job); Window.SetAgentBusy(false, "整理失败：" + error.Message); }
                    } catch (Exception e) { Window.SetAgentBusy(false, "保存总结失败：" + e.Message); }
                    finally { agentBusy = false; }
                }));
            });
        }
        public void StartMailSync(bool automatic, int days = 0)
        {
            if (exiting || sessionLocked || mailBusy) return;
            if (automatic && !mailSync.IsDue(clock())) return;
            mailBusy = true; Window.SetAgentBusy(true, automatic ? "正在同步招聘邮件…" : "正在读取邮箱…");
            ThreadPool.QueueUserWorkItem(delegate {
                MailSyncResult result = null; Exception error = null;
                try { result = mailSync.Run(automatic, days); }
                catch (Exception e) { error = e; }
                Window.Dispatcher.BeginInvoke(new Action(delegate {
                    try {
                        if (error == null && result.Errors.Count == 0) Window.SetAgentBusy(false, "邮箱同步完成，新增 " + result.CreatedCount + " 项待办");
                        else if (error == null) Window.SetAgentBusy(false, "邮箱同步部分完成：新增 " + result.CreatedCount + " 项，" + result.Errors.Count + " 处失败；详情见邮箱日志");
                        else Window.SetAgentBusy(false, "邮箱同步失败：" + error.Message);
                    } finally {
                        mailBusy = false;
                        StartAgentSummary(automatic);
                    }
                }));
            });
        }
        public void Tick()
        {
            if (exiting || sessionLocked) return;
            DateTime now = clock();
            if (lastDay != now.Date) { lastDay = now.Date; Window.Refresh(); }
            else if (Window.IsVisible) Window.RefreshCountdowns();
            if (mailSync.IsDue(now)) StartMailSync(true, 2);
            else if (!mailBusy) StartAgentSummary(true);
            if (ActiveReminder != null) return;
            List<Todo> due = Reminders.Due(Controller.Data, now);
            if (due.Count == 0) return;
            try {
                ShowReminder(due, now, false);
                var ids = new HashSet<string>(due.Select(x => x.Id));
                Controller.Commit(data => Reminders.MarkShown(data, data.Items.Where(x => ids.Contains(x.Id))));
            } catch (Exception e) { tray.ShowBalloonTip(4000, "提醒记录保存失败", e.Message, Forms.ToolTipIcon.Warning); }
        }
        public void PreviewReminder()
        {
            if (ActiveReminder != null) { ActiveReminder.Close(); }
            var example = new Todo { Title = "为明天安排一件重要的小事", Date = Dates.Key(clock().Date.AddDays(1)), Time = "09:00" };
            ShowReminder(new List<Todo> { example }, clock(), true);
        }
        private void ShowReminder(List<Todo> items, DateTime now, bool test)
        {
            var ids = new HashSet<string>(items.Select(x => x.Id));
            var toast = new ReminderWindow(items, now, delegate { ShowMain(); Window.SelectDate(Dates.Parse(items[0].Date)); }, delegate {
                if (test) return;
                Window.Guard(delegate { Controller.Commit(data => Reminders.Snooze(data.Items.Where(x => ids.Contains(x.Id)), clock())); });
            }, test);
            ActiveReminder = toast; toast.Closed += delegate { if (ActiveReminder == toast) ActiveReminder = null; }; toast.Show();
            if (Controller.Data.Sound) System.Media.SystemSounds.Asterisk.Play();
        }
        private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Window.Dispatcher.BeginInvoke(new Action(Tick)); }
        public void SessionLockChanged(bool locked)
        {
            sessionLocked = locked;
            if (!locked) Window.Dispatcher.BeginInvoke(new Action(Tick));
        }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) SessionLockChanged(true);
            if (e.Reason == SessionSwitchReason.SessionUnlock) SessionLockChanged(false);
        }
        public void Exit() { exiting = true; Application.Current.Shutdown(); }
        public void Dispose()
        {
            exiting = true;
            timer.Stop(); SystemEvents.PowerModeChanged -= OnPowerChanged; SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (ActiveReminder != null) ActiveReminder.Close(); Window.Close(); tray.Visible = false; tray.Dispose(); icon.Dispose();
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        private static Icon CreateIcon()
        {
            using (var bitmap = new Bitmap(32, 32)) {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (var accent = new SolidBrush(Color.FromArgb(25, 123, 104))) {
                    g.Clear(Color.Transparent); g.FillRectangle(accent, 2, 4, 28, 26);
                    g.FillRectangle(System.Drawing.Brushes.White, 5, 11, 22, 16); g.FillRectangle(System.Drawing.Brushes.White, 9, 1, 3, 7); g.FillRectangle(System.Drawing.Brushes.White, 20, 1, 3, 7);
                    using (var font = new Font("Segoe UI", 10, System.Drawing.FontStyle.Bold)) g.DrawString("✓", font, accent, 10, 11);
                }
                IntPtr handle = bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (Stream theme = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml")) app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(theme));
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleCalendar");
            int directoryOption = Array.IndexOf(args, "--data-dir");
            if (directoryOption >= 0 && args.Length > directoryOption + 1) directory = Path.GetFullPath(args[directoryOption + 1]);
            bool created;
            string suffix = Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(directory.ToLowerInvariant()))).Replace('/', '_').Replace('+', '-');
            using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LittleCalendar.Open." + suffix))
            using (var instance = new Mutex(true, @"Local\LittleCalendar.Instance." + suffix, out created)) {
                if (!created) { wake.Set(); return 0; }
                try {
                    var store = new CalendarStore(directory);
                    using (var runtime = new CalendarRuntime(new CalendarController(store))) {
                        var wait = ThreadPool.RegisterWaitForSingleObject(wake, delegate { app.Dispatcher.BeginInvoke(new Action(runtime.ShowMain)); }, null, Timeout.Infinite, false);
                        try {
                            if (!args.Contains("--background")) runtime.ShowMain();
                            if (!String.IsNullOrEmpty(store.LoadWarning)) MessageBox.Show(store.LoadWarning, "已恢复日历备份");
                            app.Dispatcher.BeginInvoke(new Action(runtime.Tick), DispatcherPriority.ApplicationIdle);
                            app.Run();
                        } finally { wait.Unregister(null); }
                    }
                    return 0;
                } catch (Exception error) { MessageBox.Show(error.Message, "小日历无法启动", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
