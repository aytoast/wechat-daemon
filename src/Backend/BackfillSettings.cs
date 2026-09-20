using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WeChatSidekick.Backend
{
    public sealed class BackfillSettings
    {
        public int StartMinutes { get; set; }
        public int EndMinutes { get; set; }

        public static BackfillSettings Default()
        {
            return new BackfillSettings
            {
                StartMinutes = Constants.NightBackfillStartHour * 60,
                EndMinutes = Constants.NightBackfillEndHour * 60
            };
        }
    }

    public static class BackfillSettingsStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static BackfillSettings Load()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path)) return BackfillSettings.Default();
                BackfillSettings settings = Serializer.Deserialize<BackfillSettings>(File.ReadAllText(path, Encoding.UTF8));
                if (settings == null || !IsValid(settings)) return BackfillSettings.Default();
                return settings;
            }
            catch { }
            return BackfillSettings.Default();
        }

        public static void Save(BackfillSettings settings)
        {
            if (!IsValid(settings)) throw new ArgumentException("Backfill schedule is invalid.");
            string path = GetPath();
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer.Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static bool IsValid(BackfillSettings settings)
        {
            return settings.StartMinutes >= 0 && settings.StartMinutes < 1440
                && settings.EndMinutes >= 0 && settings.EndMinutes < 1440
                && settings.StartMinutes != settings.EndMinutes;
        }

        private static string GetPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stringem", "wechat-daemon");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "backfill-settings.json");
        }
    }
}
