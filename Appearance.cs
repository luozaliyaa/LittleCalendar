using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LittleCalendar
{
    public sealed class AppearanceSettings
    {
        public string Theme { get; set; }
        public int BackgroundOpacity { get; set; }
        public string BackgroundFile { get; set; }
        public string BackgroundMode { get; set; }
        public AppearanceSettings()
        {
            Theme = "emerald"; BackgroundOpacity = 100; BackgroundFile = ""; BackgroundMode = "cover";
        }
        public AppearanceSettings Copy() { return (AppearanceSettings)MemberwiseClone(); }
    }

    public sealed class AppearanceProfile
    {
        public string Canvas;
        public string Accent;
        public string AccentText;
        public string SelectedDay;
        public string SelectedBorder;
        public string AgentBackground;
        public string AgentBorder;
        public string TabBackground;
        public string TabText;
        public string[] Bubbles;
    }

    public static class AppearancePalette
    {
        public static AppearanceSettings CurrentSettings = new AppearanceSettings();
        public static AppearanceProfile Current { get { return For(CurrentSettings); } }

        public static void Normalize(AppearanceSettings value)
        {
            if (value == null) return;
            if (!new[] { "emerald", "blue", "purple", "sakura" }.Contains(value.Theme)) value.Theme = "emerald";
            if (value.BackgroundOpacity < 70 || value.BackgroundOpacity > 100) value.BackgroundOpacity = 100;
            value.BackgroundFile = value.BackgroundFile ?? "";
            if (!new[] { "cover", "contain" }.Contains(value.BackgroundMode)) value.BackgroundMode = "cover";
        }

        public static AppearanceProfile For(AppearanceSettings settings)
        {
            settings = settings ?? new AppearanceSettings(); Normalize(settings);
            if (settings.Theme == "blue") return new AppearanceProfile {
                Canvas = "#F3F7FB", Accent = "#2D6FA9", AccentText = "#25577F", SelectedDay = "#E0EEF9", SelectedBorder = "#86B9DF",
                AgentBackground = "#EFF6FC", AgentBorder = "#C9E0F2", TabBackground = "#F0F5F9", TabText = "#6E8597",
                Bubbles = new[] { "#D9EAF8", "#E0E8F7", "#DCEFF0", "#EAE2F7", "#E7EEF5" }
            };
            if (settings.Theme == "purple") return new AppearanceProfile {
                Canvas = "#F7F5FA", Accent = "#7655A7", AccentText = "#573E81", SelectedDay = "#EEE6F7", SelectedBorder = "#B69ADB",
                AgentBackground = "#F6F0FB", AgentBorder = "#E1D2F0", TabBackground = "#F5F1F8", TabText = "#88799A",
                Bubbles = new[] { "#EADFF6", "#E5E3F7", "#F2E2F1", "#DFEAF7", "#EEE7DA" }
            };
            if (settings.Theme == "sakura") return new AppearanceProfile {
                Canvas = "#FFF7FA", Accent = "#C65E82", AccentText = "#98465F", SelectedDay = "#FBE3EC", SelectedBorder = "#E7A7BA",
                AgentBackground = "#FFF0F5", AgentBorder = "#F3CCD9", TabBackground = "#FCF1F5", TabText = "#9B7784",
                Bubbles = new[] { "#F9DCE6", "#F5E4EF", "#F9E5D7", "#E9E0F6", "#F7EAEA" }
            };
            return new AppearanceProfile {
                Canvas = "#F4F7F7", Accent = "#197B68", AccentText = "#197B68", SelectedDay = "#E2F2EC", SelectedBorder = "#75BCA6",
                AgentBackground = "#F1F8F5", AgentBorder = "#D2E7DF", TabBackground = "#F4F7F5", TabText = "#789088",
                Bubbles = new[] { "#DCEFE8", "#DCEAF7", "#E9E0F5", "#F7E4D8", "#F4E1EC" }
            };
        }
    }

    public static class AppearanceFiles
    {
        private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp" };
        public static string BackgroundDirectory(string dataDirectory) { return Path.Combine(dataDirectory, "backgrounds"); }
        public static string Resolve(string dataDirectory, string relativeName)
        {
            if (String.IsNullOrWhiteSpace(relativeName) || Path.GetFileName(relativeName) != relativeName) return "";
            return Path.Combine(BackgroundDirectory(dataDirectory), relativeName);
        }
        public static string Import(string dataDirectory, string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new ArgumentException("找不到所选背景图片。");
            var info = new FileInfo(sourcePath); string extension = Path.GetExtension(info.Name).ToLowerInvariant();
            if (!Extensions.Contains(extension)) throw new ArgumentException("仅支持 JPG、PNG 和 BMP 图片。");
            if (info.Length > 10 * 1024 * 1024) throw new ArgumentException("背景图片请控制在 10 MB 以内。");
            try { using (FileStream stream = File.OpenRead(sourcePath)) BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
            catch { throw new ArgumentException("无法读取这张图片，请选择有效的 JPG、PNG 或 BMP 文件。"); }
            string directory = BackgroundDirectory(dataDirectory); Directory.CreateDirectory(directory);
            string name = Guid.NewGuid().ToString("N") + extension;
            File.Copy(sourcePath, Path.Combine(directory, name), false);
            return name;
        }
        public static void Delete(string dataDirectory, string relativeName)
        {
            string path = Resolve(dataDirectory, relativeName);
            if (!String.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        public static BitmapImage Load(string dataDirectory, string relativeName)
        {
            string path = Resolve(dataDirectory, relativeName);
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image;
            } catch { return null; }
        }
    }
}
