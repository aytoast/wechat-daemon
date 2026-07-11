using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

namespace WeChatSidekick
{
    /// <summary>
    /// message parsing, filtering, merging, and deduplication logic.
    /// </summary>
    public static class MessageProcessor
    {
        // pre-compiled regex patterns
        private static readonly Regex TimestampPattern = new Regex(
            @"^(\d{1,2}:\d{2}|(Yesterday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|昨天|前天|星期[一二三四五六日]|周[一二三四五六日]|[上下]午|\d{1,2}月\d{1,2}日|\d{4}年\d{1,2}月\d{1,2}日|\d{1,2}/\d{1,2}|\d{4}/\d{1,2}/\d{1,2}|\d{4}-\d{1,2}-\d{1,2})\s*([上下]午)?\s*\d{1,2}:\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VoiceNoTranscriptPattern = new Regex(
            @"^语音\d+""秒(未播放)?$", RegexOptions.Compiled);

        private static readonly Regex VoiceBracketPattern = new Regex(
            @"^\[语音\]$", RegexOptions.Compiled);

        private static readonly Regex VideoProgressPattern = new Regex(
            @"^视频 \d+:\d{2}$", RegexOptions.Compiled);

        /// <summary>
        /// removes the [me]: or [other]: sender prefix from the message text.
        /// </summary>
        public static string StripPrefix(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            if (msg.StartsWith(Constants.SenderPrefixMe)) return msg.Substring(Constants.SenderPrefixMe.Length);
            if (msg.StartsWith(Constants.SenderPrefixOther)) return msg.Substring(Constants.SenderPrefixOther.Length);
            if (msg.StartsWith(Constants.SenderPrefixSystem)) return msg.Substring(Constants.SenderPrefixSystem.Length);
            return msg;
        }

        /// <summary>
        /// checks if a message has a sender classification prefix.
        /// </summary>
        public static bool HasSenderPrefix(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.StartsWith(Constants.SenderPrefixMe) || 
                   msg.StartsWith(Constants.SenderPrefixOther) ||
                   msg.StartsWith(Constants.SenderPrefixSystem);
        }

        /// <summary>
        /// chooses the better version of a message by prioritizing the fresh stable classification.
        /// msg1 is the stored message, msg2 is the currently visible message.
        /// </summary>
        public static string ChooseBetterMessage(string msg1, string msg2)
        {
            if (string.IsNullOrEmpty(msg1)) return msg2;
            if (string.IsNullOrEmpty(msg2)) return msg1;

            if (HasSenderPrefix(msg2))
            {
                return msg2;
            }
            return msg1;
        }

        /// <summary>
        /// checks if the input text matches common WeChat timestamp formats.
        /// </summary>
        public static bool IsTimestamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return TimestampPattern.IsMatch(text);
        }

        /// <summary>
        /// checks if the input text matches noisy UI elements that are not chat messages.
        /// </summary>
        public static bool IsIgnoredNode(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = StripPrefix(text).Trim();
            if (t.StartsWith("视频 进度:")) return true;
            if (t.StartsWith("文件\n进度:")) return true;
            if (t == "微信") return true;
            if (VideoProgressPattern.IsMatch(t)) return true;
            return false;
        }

        public static bool IsCallNotice(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = StripPrefix(text).Trim();
            return t.Contains("语音通话") || t.Contains("视频通话");
        }

        public static bool IsMediaNotice(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = StripPrefix(text).Trim();
            return t.StartsWith("动画表情")
                || t == "图片"
                || t == "[图片]"
                || t == "image"
                || t == "[image]"
                || VoiceNoTranscriptPattern.IsMatch(t)
                || VoiceBracketPattern.IsMatch(t);
        }

        public static bool IsHiddenMetadata(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string stripped = StripPrefix(text);
            return text.StartsWith(Constants.SenderPrefixSystem)
                || IsTimestamp(stripped)
                || IsSystemNotice(stripped)
                || IsCallNotice(stripped)
                || IsMediaNotice(stripped);
        }

        /// <summary>
        /// checks if the input text matches common WeChat system notifications.
        /// </summary>
        public static bool IsSystemNotice(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            if (text.Contains("邀请") && (text.Contains("加入了") || text.Contains("加入群聊")))
                return true;

            if (text.Contains("不是朋友关系") || text.Contains("隐私安全"))
                return true;

            if (text.Contains("撤回了一条消息") || 
                text.Contains("现在可以开始聊天了") || 
                text.Contains("通过了你的朋友验证请求") ||
                text.Contains("微信安全中心提醒您") ||
                text.Contains("以上是打招呼的消息") ||
                text.Contains("拍了拍"))
                return true;

            return false;
        }

        /// <summary>
        /// checks if the input should be skipped during scanning (timestamp or ignored node).
        /// </summary>
        public static bool ShouldSkip(string text)
        {
            return IsHiddenMetadata(text) || IsIgnoredNode(text);
        }

        /// <summary>
        /// determines if two lists of strings are identical in content and order.
        /// </summary>
        public static bool ListsAreEqual(List<string> list1, List<string> list2)
        {
            if (list1 == null || list2 == null) return list1 == list2;
            if (list1.Count != list2.Count) return false;
            for (int i = 0; i < list1.Count; i++)
            {
                if (list1[i] != list2[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// determines if a sequence can be safely removed as a duplicate based on length and contents.
        /// </summary>
        public static bool IsSafeToRemoveDuplicate(List<string> list, int start, int len)
        {
            if (len >= 3) return true;
            for (int k = 0; k < len; k++)
            {
                if (IsTimestamp(StripPrefix(list[start + k]))) return true;
            }
            int totalLen = 0;
            for (int k = 0; k < len; k++)
            {
                if (list[start + k] != null)
                {
                    totalLen += StripPrefix(list[start + k]).Length;
                }
            }
            if (totalLen >= 15) return true;
            return false;
        }

        /// <summary>
        /// finds the longest common substring match between an island and the visible items.
        /// returns the match length, island start index, and visible start index.
        /// </summary>
        private static void FindBestMatch(
            List<string> filteredIsl, List<string> filteredVis,
            out int maxLen, out int bestIslStart, out int bestVisStart)
        {
            maxLen = 0;
            bestIslStart = -1;
            bestVisStart = -1;
            int n = filteredIsl.Count;
            int m = filteredVis.Count;

            if (n == 0 || m == 0) return;

            int[,] dp = new int[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    if (filteredIsl[i - 1] == filteredVis[j - 1])
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                        if (dp[i, j] > maxLen)
                        {
                            maxLen = dp[i, j];
                            bestIslStart = i - maxLen;
                            bestVisStart = j - maxLen;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// filters a stripped list to only non-timestamp, non-system items, tracking original indices.
        /// </summary>
        private static void FilterItems(
            List<string> original, out List<string> filtered, out List<int> origIndices)
        {
            filtered = new List<string>();
            origIndices = new List<int>();
            for (int j = 0; j < original.Count; j++)
            {
                string orig = original[j];
                string stripped = StripPrefix(orig);
                bool isSystem = orig.StartsWith(Constants.SenderPrefixSystem) || IsTimestamp(stripped) || IsSystemNotice(stripped);
                if (!isSystem)
                {
                    filtered.Add(stripped);
                    origIndices.Add(j);
                }
            }
        }

        private static bool AllVisibleMessagesAlreadyStored(List<string> stored, List<string> visible)
        {
            if (stored == null || visible == null || visible.Count == 0) return false;

            Dictionary<string, int> storedCounts = new Dictionary<string, int>();
            foreach (string item in stored)
            {
                if (item == Constants.IslandBoundary) continue;
                if (IsIgnoredNode(item)) continue;
                string key = GetRecordKey(item);
                if (!storedCounts.ContainsKey(key)) storedCounts[key] = 0;
                storedCounts[key]++;
            }

            foreach (string item in visible)
            {
                if (IsIgnoredNode(item)) continue;
                string key = GetRecordKey(item);
                int count;
                if (!storedCounts.TryGetValue(key, out count) || count <= 0)
                {
                    return false;
                }
                storedCounts[key] = count - 1;
            }

            return true;
        }

        private static string GetRecordKey(string item)
        {
            string stripped = StripPrefix(item);
            if (IsHiddenMetadata(item)) return "metadata:" + stripped;
            return "message:" + stripped;
        }

        /// <summary>
        /// flattens a list of islands back into a single list with boundary markers.
        /// </summary>
        private static List<string> FlattenIslands(List<List<string>> islands)
        {
            List<string> res = new List<string>();
            for (int i = 0; i < islands.Count; i++)
            {
                res.AddRange(islands[i]);
                if (i < islands.Count - 1) res.Add(Constants.IslandBoundary);
            }
            return res;
        }

        private static void NormalizeIslandBoundaries(List<string> list)
        {
            while (list.Count > 0 && list[0] == Constants.IslandBoundary)
            {
                list.RemoveAt(0);
            }

            while (list.Count > 0 && list[list.Count - 1] == Constants.IslandBoundary)
            {
                list.RemoveAt(list.Count - 1);
            }

            for (int i = list.Count - 1; i > 0; i--)
            {
                if (list[i] == Constants.IslandBoundary && list[i - 1] == Constants.IslandBoundary)
                {
                    list.RemoveAt(i);
                }
            }
        }

        private static void CollapseAdjacentDuplicateMessages(List<string> list)
        {
            for (int i = list.Count - 2; i >= 0; i--)
            {
                string current = list[i];
                string next = list[i + 1];
                if (current == Constants.IslandBoundary || next == Constants.IslandBoundary) continue;
                if (current.StartsWith(Constants.SenderPrefixSystem) || next.StartsWith(Constants.SenderPrefixSystem)) continue;
                if (current == next)
                {
                    list.RemoveAt(i + 1);
                }
            }
        }

        private static int SharedSuffixPrefixLength(List<string> left, List<string> right, int maxLen)
        {
            int best = 0;
            int limit = Math.Min(Math.Min(left.Count, right.Count), maxLen);
            for (int len = 1; len <= limit; len++)
            {
                bool match = true;
                int leftStart = left.Count - len;
                for (int k = 0; k < len; k++)
                {
                    if (left[leftStart + k] != right[k])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) best = len;
            }
            return best;
        }

        private static int SharedPrefixSuffixLength(List<string> left, List<string> right, int maxLen)
        {
            int best = 0;
            int limit = Math.Min(Math.Min(left.Count, right.Count), maxLen);
            for (int len = 1; len <= limit; len++)
            {
                bool match = true;
                int rightStart = right.Count - len;
                for (int k = 0; k < len; k++)
                {
                    if (left[k] != right[rightStart + k])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) best = len;
            }
            return best;
        }

        private static bool TryBridgeAdjacentIslands(
            List<List<string>> islands,
            List<string> visible,
            List<string> filteredVis,
            List<int> origVisIndices,
            out List<string> merged)
        {
            merged = null;
            if (filteredVis.Count < 2 || islands.Count < 2) return false;

            for (int i = 0; i < islands.Count - 1; i++)
            {
                List<string> leftFiltered;
                List<int> leftOrigIndices;
                List<string> rightFiltered;
                List<int> rightOrigIndices;
                FilterItems(islands[i], out leftFiltered, out leftOrigIndices);
                FilterItems(islands[i + 1], out rightFiltered, out rightOrigIndices);

                int leftLen = SharedSuffixPrefixLength(leftFiltered, filteredVis, filteredVis.Count);
                int rightLen = SharedPrefixSuffixLength(rightFiltered, filteredVis, filteredVis.Count);
                if (leftLen == 0 || rightLen == 0 || leftLen + rightLen > filteredVis.Count) continue;
                if (leftLen + rightLen < 3 && filteredVis.Count > 2) continue;

                List<List<string>> rebuilt = new List<List<string>>();
                for (int j = 0; j < i; j++)
                {
                    rebuilt.Add(islands[j]);
                }

                List<string> bridgedIsland = new List<string>();
                int leftKeepEnd = leftOrigIndices[leftFiltered.Count - leftLen];
                for (int j = 0; j < leftKeepEnd; j++)
                {
                    bridgedIsland.Add(islands[i][j]);
                }

                for (int j = 0; j < filteredVis.Count; j++)
                {
                    bridgedIsland.Add(visible[origVisIndices[j]]);
                }

                int rightSkipEnd = rightOrigIndices[rightLen - 1];
                for (int j = rightSkipEnd + 1; j < islands[i + 1].Count; j++)
                {
                    bridgedIsland.Add(islands[i + 1][j]);
                }

                rebuilt.Add(bridgedIsland);
                for (int j = i + 2; j < islands.Count; j++)
                {
                    rebuilt.Add(islands[j]);
                }

                merged = FlattenIslands(rebuilt);
                return true;
            }

            return false;
        }

        /// <summary>
        /// merges stored chat messages with currently visible UI items using anchor-based positional insertion.
        /// </summary>
        public static List<string> MergeMessages(List<string> stored, List<string> visible, bool isPrepend)
        {
            if (stored != null) stored.RemoveAll(s => IsIgnoredNode(s));
            if (visible != null) visible.RemoveAll(s => IsIgnoredNode(s));

            if (stored == null || stored.Count == 0) return new List<string>(visible ?? new List<string>());
            if (visible == null || visible.Count == 0) return new List<string>(stored);
            if (AllVisibleMessagesAlreadyStored(stored, visible)) return new List<string>(stored);

            // parse stored into islands
            List<List<string>> islands = new List<List<string>>();
            List<string> currentIsland = new List<string>();
            foreach (var s in stored)
            {
                if (IsTimestamp(StripPrefix(s))) continue;
                if (s == Constants.IslandBoundary)
                {
                    if (currentIsland.Count > 0) { islands.Add(currentIsland); currentIsland = new List<string>(); }
                }
                else
                {
                    currentIsland.Add(s);
                }
            }
            if (currentIsland.Count > 0) islands.Add(currentIsland);

            int m = visible.Count;
            List<string> visibleStripped = visible.Select(StripPrefix).ToList();

            List<string> filteredVis;
            List<int> origVisIndices;
            FilterItems(visible, out filteredVis, out origVisIndices);
            int filteredM = filteredVis.Count;

            List<string> bridged;
            if (TryBridgeAdjacentIslands(islands, visible, filteredVis, origVisIndices, out bridged))
            {
                return bridged;
            }

            // build frequency maps
            Dictionary<string, int> freqStored = new Dictionary<string, int>();
            foreach (var s in stored)
            {
                if (s == Constants.IslandBoundary) continue;
                string st = StripPrefix(s);
                bool isSystem = s.StartsWith(Constants.SenderPrefixSystem) || IsTimestamp(st) || IsSystemNotice(st);
                if (!isSystem)
                {
                    if (freqStored.ContainsKey(st)) freqStored[st]++;
                    else freqStored[st] = 1;
                }
            }
            Dictionary<string, int> freqVisible = new Dictionary<string, int>();
            foreach (var s in filteredVis)
            {
                if (freqVisible.ContainsKey(s)) freqVisible[s]++;
                else freqVisible[s] = 1;
            }
            Func<string, bool> isUnique = delegate(string s)
            {
                return freqStored.ContainsKey(s) && freqStored[s] == 1
                    && freqVisible.ContainsKey(s) && freqVisible[s] == 1;
            };

            // phase 1 & 2: check for subsumption and partial overlap
            List<List<string>> newIslands = new List<List<string>>();
            bool vSubsumed = false;

            for (int i = 0; i < islands.Count; i++)
            {
                var island = islands[i];

                List<string> filteredIsl;
                List<int> origIslIndices;
                FilterItems(island, out filteredIsl, out origIslIndices);

                int maxLen, bestIslStart, bestVisStart;
                FindBestMatch(filteredIsl, filteredVis, out maxLen, out bestIslStart, out bestVisStart);

                if (!vSubsumed && maxLen > 0 && (maxLen >= 2 || (maxLen == 1 && isUnique(filteredVis[bestVisStart]))))
                {
                    int islandReplaceStart = origIslIndices[bestIslStart];
                    int islandReplaceEnd = origIslIndices[bestIslStart + maxLen - 1];
                    List<string> mergedIsland = new List<string>();

                    for (int k = 0; k < islandReplaceStart; k++)
                    {
                        mergedIsland.Add(island[k]);
                    }

                    for (int k = 0; k < visible.Count; k++)
                    {
                        mergedIsland.Add(visible[k]);
                    }

                    for (int k = islandReplaceEnd + 1; k < island.Count; k++)
                    {
                        mergedIsland.Add(island[k]);
                    }

                    island = mergedIsland;
                    vSubsumed = true;
                }
                newIslands.Add(island);
            }

            if (vSubsumed) return FlattenIslands(newIslands);

            // phase 3: no overlap found - append as a disconnected island.
            newIslands.Add(visible);
            return FlattenIslands(newIslands);
        }

        /// <summary>
        /// identifies and removes duplicate sequences and consecutive timestamps from the message history list.
        /// </summary>
        public static List<string> SelfCorrect(List<string> list)
        {
            if (list == null) return null;
            list.RemoveAll(s => IsIgnoredNode(s));
            NormalizeIslandBoundaries(list);
            CollapseAdjacentDuplicateMessages(list);

            for (int i = 0; i < list.Count; i++)
            {
                if (!string.IsNullOrEmpty(list[i]))
                {
                    string stripped = StripPrefix(list[i]);
                    if (list[i].StartsWith(Constants.SenderPrefixSystem))
                    {
                        // keep prefix
                    }
                    else if (IsTimestamp(stripped) || IsSystemNotice(stripped))
                    {
                        list[i] = Constants.SenderPrefixSystem + stripped;
                    }
                }
            }

            if (list.Count < 2) return list;

            bool changed = true;
            int passes = 0;
            while (changed && passes < 10)
            {
                changed = false;
                passes++;

                List<string> strippedList = list.Select(StripPrefix).ToList();
                List<bool> isTimestampList = strippedList.Select(IsTimestamp).ToList();

                // 1. consecutive identical timestamps
                for (int i = list.Count - 2; i >= 0; i--)
                {
                    if (strippedList[i] == strippedList[i + 1] && isTimestampList[i])
                    {
                        list.RemoveAt(i + 1);
                        strippedList.RemoveAt(i + 1);
                        isTimestampList.RemoveAt(i + 1);
                        changed = true;
                    }
                }
                if (changed) continue;

                // 2. adjacent duplicate subsequences (requires timestamp anchor to avoid false positives)
                for (int len = Math.Min(50, list.Count / 2); len >= 2; len--)
                {
                    for (int i = 0; i <= list.Count - 2 * len; i++)
                    {
                        bool match = true;
                        bool hasTimestamp = false;
                        for (int k = 0; k < len; k++)
                        {
                            if (strippedList[i + k] != strippedList[i + len + k])
                            {
                                match = false;
                                break;
                            }
                            if (isTimestampList[i + k]) hasTimestamp = true;
                        }
                        if (match && hasTimestamp)
                        {
                            list.RemoveRange(i + len, len);
                            strippedList.RemoveRange(i + len, len);
                            isTimestampList.RemoveRange(i + len, len);
                            changed = true;
                            break;
                        }
                    }
                    if (changed) break;
                }
                if (changed) continue;

                // 3. non-adjacent duplicate subsequences of length >= 1
                for (int len = Math.Min(30, list.Count / 2); len >= 1; len--)
                {
                    for (int i = 0; i <= list.Count - 2 * len; i++)
                    {
                        bool hasDuplicate = false;
                        int dupIndex = -1;

                        for (int j = i + len; j <= list.Count - len; j++)
                        {
                            bool match = true;
                            for (int k = 0; k < len; k++)
                            {
                                if (strippedList[i + k] != strippedList[j + k])
                                {
                                    match = false;
                                    break;
                                }
                            }
                            if (match)
                            {
                                bool safe = false;
                                if (len >= 3) safe = true;
                                else
                                {
                                    for (int k = 0; k < len; k++)
                                        if (isTimestampList[i + k]) { safe = true; break; }
                                    if (!safe)
                                    {
                                        int totalLen = 0;
                                        for (int k = 0; k < len; k++)
                                            if (strippedList[i + k] != null) totalLen += strippedList[i + k].Length;
                                        if (totalLen >= 5) safe = true;
                                    }
                                }

                                if (safe)
                                {
                                    hasDuplicate = true;
                                    dupIndex = j;
                                    break;
                                }
                            }
                        }

                        if (hasDuplicate && dupIndex != -1)
                        {
                            list.RemoveRange(dupIndex, len);
                            strippedList.RemoveRange(dupIndex, len);
                            isTimestampList.RemoveRange(dupIndex, len);
                            changed = true;
                            break;
                        }
                    }
                    if (changed) break;
                }
            }
            return list;
        }
    }
}
