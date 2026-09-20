using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WeChatSidekick.Backend
{
    public sealed class NightBackfillState
    {
        public string CompletedSchedule { get; set; }
        public string LastAttemptUtc { get; set; }
        public string LastError { get; set; }
        public int CapturedRecords { get; set; }
        private static string PathName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stringem", "wechat-daemon", "night-backfill-state.json"); } }

        public static NightBackfillState Load()
        {
            try { return new JavaScriptSerializer().Deserialize<NightBackfillState>(File.ReadAllText(PathName)) ?? new NightBackfillState(); }
            catch { return new NightBackfillState(); }
        }
        public bool CanAttempt(string schedule, DateTime utcNow)
        {
            if (CompletedSchedule == schedule) return false;
            DateTime last;
            return !DateTime.TryParse(LastAttemptUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out last)
                || utcNow - last.ToUniversalTime() >= TimeSpan.FromMinutes(15);
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            string temp = PathName + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(PathName)) File.Replace(temp, PathName, null);
            else File.Move(temp, PathName);
        }
    }
}
