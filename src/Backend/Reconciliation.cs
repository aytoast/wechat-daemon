using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WeChatSidekick.Backend
{
    public static class Reconciliation
    {
        // Timestamp separators are layout-dependent UI metadata. Retain old evidence
        // when Qt omits a separator in a larger viewport; never skip ordinary records.
        public static List<string> PreserveSeparators(List<string> stored, List<DateTime> capturedDates, List<string> incoming, DateTime now)
        {
            var result = new List<string>(incoming);
            var inserts = new Dictionary<int, List<string>>();
            var pending = new List<int>();
            var used = new HashSet<int>();
            int search = 0, previous = -1;
            for (int old = 0; old < stored.Count; old++)
            {
                if (IsSeparator(stored[old])) { pending.Add(old); continue; }
                int match = search;
                while (match < incoming.Count && (IsSeparator(incoming[match]) || MessageProcessor.StripPrefix(incoming[match]) != MessageProcessor.StripPrefix(stored[old]))) match++;
                if (match == incoming.Count) throw new InvalidOperationException("Backfill conflicts with saved message sequence at record " + old + "; records preserved.");
                PlaceSeparators(stored, capturedDates, result, pending, previous + 1, match, used, inserts, now);
                pending.Clear();
                previous = match; search = match + 1;
            }
            PlaceSeparators(stored, capturedDates, result, pending, previous + 1, incoming.Count, used, inserts, now);
            var merged = new List<string>();
            for (int i = 0; i <= result.Count; i++)
            {
                List<string> extra;
                if (inserts.TryGetValue(i, out extra)) merged.AddRange(extra);
                if (i < result.Count) merged.Add(result[i]);
            }
            return merged;
        }
        private static bool IsSeparator(string text)
        {
            return text.StartsWith(Constants.SenderPrefixSystem) && MessageProcessor.IsTimestamp(MessageProcessor.StripPrefix(text));
        }
        private static void PlaceSeparators(List<string> stored, List<DateTime> dates, List<string> result, List<int> pending,
            int from, int until, HashSet<int> used, Dictionary<int, List<string>> inserts, DateTime now)
        {
            foreach (int index in pending)
            {
                int found = -1;
                for (int i = from; i < until; i++)
                    if (!used.Contains(i) && IsSeparator(result[i]) && SameRecord(stored[index], dates[index], result[i], now)) { found = i; break; }
                if (found >= 0) { result[found] = stored[index]; used.Add(found); }
                else
                {
                    if (!inserts.ContainsKey(until)) inserts[until] = new List<string>();
                    inserts[until].Add(stored[index]);
                }
            }
        }

        public static bool SameRecord(string oldText, DateTime oldCapturedAt, string newText, DateTime newCapturedAt)
        {
            string left = MessageProcessor.StripPrefix(oldText), right = MessageProcessor.StripPrefix(newText);
            DateTime oldDate, newDate;
            if (oldText.StartsWith(Constants.SenderPrefixSystem) && newText.StartsWith(Constants.SenderPrefixSystem)
                && TryTimestamp(left, oldCapturedAt, out oldDate) && TryTimestamp(right, newCapturedAt, out newDate)) return oldDate == newDate;
            return left == right;
        }

        private static bool TryTimestamp(string text, DateTime captured, out DateTime value)
        {
            value = DateTime.MinValue;
            var match = Regex.Match(text ?? "", @"^(.*?)\s*(\d{1,2}):(\d{2})$");
            if (!match.Success) return false;
            int hour = int.Parse(match.Groups[2].Value), minute = int.Parse(match.Groups[3].Value);
            if (hour > 23 || minute > 59) return false;
            string marker = match.Groups[1].Value.Trim();
            DateTime date;
            if (marker == "") date = captured.Date;
            else if (marker == "昨天" || marker.Equals("yesterday", StringComparison.OrdinalIgnoreCase)) date = captured.Date.AddDays(-1);
            else if (Regex.IsMatch(marker, @"^(星期|周)[一二三四五六日天]$"))
            {
                int day = "日一二三四五六".IndexOf(marker[marker.Length - 1]);
                if (day < 0) day = 0;
                int delta = ((int)captured.DayOfWeek - day + 7) % 7;
                date = captured.Date.AddDays(-(delta == 0 ? 7 : delta));
            }
            else if (DateTime.TryParseExact(marker, new[] { "M月d日", "MM/dd", "M/d", "yyyy年M月d日", "yyyy/M/d", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                if (!Regex.IsMatch(marker, @"^\d{4}"))
                {
                    date = new DateTime(captured.Year, date.Month, date.Day);
                    if (date > captured.Date) date = date.AddYears(-1);
                }
            }
            else return false;
            value = date.Date.AddHours(hour).AddMinutes(minute);
            return true;
        }

        public static bool Eligible(string label, DateTime since, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(label)) return true; // Unknown dates must not hide chats.
            string[] lines = label.Trim().Replace("\r", "").Split('\n');
            string marker = lines[lines.Length - 1].Trim();
            DateTime date;
            if (Regex.IsMatch(marker, @"^\d{1,2}:\d{2}$")) date = now.Date;
            else if (Regex.IsMatch(marker, @"^(昨天|yesterday)(\s+\d{1,2}:\d{2})?$", RegexOptions.IgnoreCase)) date = now.Date.AddDays(-1);
            else if (Regex.IsMatch(marker, @"^(星期|周)[一二三四五六日天]$"))
            {
                int day = "日一二三四五六".IndexOf(marker[marker.Length - 1]);
                if (day < 0) day = 0;
                int delta = ((int)now.DayOfWeek - day + 7) % 7;
                date = now.Date.AddDays(-(delta == 0 ? 7 : delta));
            }
            else if (DateTime.TryParseExact(marker, new[] { "MM/dd", "M/d", "M月d日", "yyyy/M/d", "yyyy-MM-dd", "yyyy年M月d日" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                if (!Regex.IsMatch(marker, @"^\d{4}"))
                {
                    date = new DateTime(now.Year, date.Month, date.Day);
                    if (date > now.Date) date = date.AddYears(-1);
                }
            }
            else return true;
            return date.Date >= since.Date;
        }

        private static bool AnchorItem(string value)
        {
            string text = MessageProcessor.StripPrefix(value);
            return !MessageProcessor.IsHiddenMetadata(value) && !MessageProcessor.IsIgnoredNode(value)
                && text != Constants.IslandBoundary && !string.IsNullOrWhiteSpace(text);
        }
        public static List<string> CreateAnchor(List<string> messages)
        {
            var result = new List<string>();
            foreach (string message in messages) if (AnchorItem(message)) result.Add(MessageProcessor.StripPrefix(message));
            if (result.Count > 8) result = result.GetRange(result.Count - 8, 8);
            return result;
        }
        // Require an ordered, unique, nontrivial anchor; timestamps/media alone do not qualify.
        public static int FindAnchorEnd(List<string> messages, List<string> anchor)
        {
            if (anchor == null || anchor.Count < 3 || new HashSet<string>(anchor).Count < 3) return -1;
            var text = new List<string>(); var indices = new List<int>();
            for (int i = 0; i < messages.Count; i++) if (AnchorItem(messages[i])) { text.Add(MessageProcessor.StripPrefix(messages[i])); indices.Add(i); }
            int found = -1;
            for (int start = 0; start + anchor.Count <= text.Count; start++)
            {
                bool match = true;
                for (int j = 0; j < anchor.Count; j++) if (text[start + j] != anchor[j]) { match = false; break; }
                if (!match) continue;
                if (found >= 0) return -1;
                found = indices[start + anchor.Count - 1];
            }
            return found;
        }
        public static List<string> MergeAtAnchor(List<string> stored, List<string> captured, List<string> anchor)
        {
            int oldEnd = FindAnchorEnd(stored, anchor), newEnd = FindAnchorEnd(captured, anchor);
            if (oldEnd < 0 || newEnd < 0) throw new InvalidOperationException("Reconciliation anchor missing or ambiguous.");
            var merged = stored.GetRange(0, oldEnd + 1);
            merged.AddRange(captured.GetRange(newEnd + 1, captured.Count - newEnd - 1));
            // Live capture may already have newer records: never discard them.
            int next = oldEnd + 1;
            for (int i = oldEnd + 1; i < merged.Count && next < stored.Count; i++)
                if (MessageProcessor.StripPrefix(merged[i]) == MessageProcessor.StripPrefix(stored[next])) next++;
            if (next != stored.Count) throw new InvalidOperationException("Reconciliation would discard saved records; retry required.");
            return merged;
        }
    }
}
