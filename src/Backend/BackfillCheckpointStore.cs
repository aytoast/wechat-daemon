using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WeChatSidekick.Backend
{
    public sealed class BackfillCheckpoint
    {
        public string ViewportFingerprint { get; set; }
        public string CompletedAt { get; set; }
        public int Version { get; set; }
        public List<string> Anchor { get; set; }
    }

    public static class BackfillCheckpointStore
    {
        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static Dictionary<string, BackfillCheckpoint> _checkpoints;

        public static List<string> GetAnchor(string chatName)
        {
            lock (Gate)
            {
                EnsureLoaded(); BackfillCheckpoint item;
                return _checkpoints.TryGetValue(chatName, out item) && item.Version == 2 && item.Anchor != null
                    ? new List<string>(item.Anchor) : null;
            }
        }
        public static void CommitAnchor(string chatName, List<string> savedMessages)
        {
            lock (Gate)
            {
                EnsureLoaded();
                BackfillCheckpoint old; _checkpoints.TryGetValue(chatName, out old);
                _checkpoints[chatName] = new BackfillCheckpoint { Version = 2, Anchor = Reconciliation.CreateAnchor(savedMessages), CompletedAt = DateTime.Now.ToString("o") };
                try { Save(); }
                catch { if (old == null) _checkpoints.Remove(chatName); else _checkpoints[chatName] = old; throw; }
            }
        }
        public static DateTime SweepCutoff(DateTime now)
        {
            try { return DateTime.Parse(File.ReadAllText(GetPath() + ".sweep"), null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().Date.AddDays(-1); }
            catch { return now.Date.AddDays(-1); }
        }
        public static void CommitSweep(DateTime startedUtc)
        {
            string path = GetPath() + ".sweep", temp = path + ".tmp";
            File.WriteAllText(temp, startedUtc.ToString("o"));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        public static bool IsComplete(string chatName, string viewportFingerprint)
        {
            lock (Gate)
            {
                EnsureLoaded();
                BackfillCheckpoint checkpoint;
                return _checkpoints.TryGetValue(chatName, out checkpoint)
                    && checkpoint != null
                    && checkpoint.ViewportFingerprint == viewportFingerprint;
            }
        }

        public static void MarkComplete(string chatName, string viewportFingerprint)
        {
            if (string.IsNullOrWhiteSpace(chatName) || string.IsNullOrWhiteSpace(viewportFingerprint)) return;
            lock (Gate)
            {
                EnsureLoaded();
                _checkpoints[chatName] = new BackfillCheckpoint
                {
                    ViewportFingerprint = viewportFingerprint,
                    CompletedAt = DateTime.Now.ToString("s")
                };
                Save();
            }
        }

        public static string CreateFingerprint(List<string> messages)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string value = messages == null ? "" : string.Join("\n", messages.ToArray());
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }

        private static void EnsureLoaded()
        {
            if (_checkpoints != null) return;
            try
            {
                string path = GetPath();
                if (File.Exists(path))
                {
                    _checkpoints = Serializer.Deserialize<Dictionary<string, BackfillCheckpoint>>(File.ReadAllText(path, Encoding.UTF8));
                }
            }
            catch { }
            if (_checkpoints == null) _checkpoints = new Dictionary<string, BackfillCheckpoint>();
        }

        private static void Save()
        {
            string path = GetPath();
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer.Serialize(_checkpoints), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static string GetPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stringem", "wechat-daemon");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "backfill-checkpoints.json");
        }
    }
}
