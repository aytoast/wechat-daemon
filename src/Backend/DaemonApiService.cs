using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WeChatSidekick.Backend
{
    public static class DaemonApiService
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private const string ApiPrefix = "/wechat/daemon";
        private static readonly HashSet<string> VisibleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "message" };
        private const int SnapshotKeepCount = 10;

        public static bool IngestVisibleState(string chatName, List<string> visibleMessages)
        {
            if (string.IsNullOrWhiteSpace(chatName) || visibleMessages == null || visibleMessages.Count == 0)
            {
                return false;
            }

            string contactDir = GetContactDir(chatName);
            if (!Directory.Exists(contactDir)) Directory.CreateDirectory(contactDir);
            EnsureInfoFile(contactDir, chatName);

            List<Dictionary<string, object>> stored = LoadChat(contactDir);
            List<string> storedText = new List<string>();
            foreach (Dictionary<string, object> record in stored)
            {
                storedText.Add(GetString(record, "Text"));
            }

            List<string> mergedText = MessageProcessor.SelfCorrect(MessageProcessor.MergeMessages(storedText, visibleMessages, false));
            if (MessageProcessor.ListsAreEqual(storedText, mergedText))
            {
                return false;
            }

            bool bridgeMerged = CountIslandBoundaries(storedText) > CountIslandBoundaries(mergedText);
            Dictionary<string, Queue<Dictionary<string, object>>> existingByText = new Dictionary<string, Queue<Dictionary<string, object>>>();
            foreach (Dictionary<string, object> record in stored)
            {
                string text = GetString(record, "Text");
                if (!existingByText.ContainsKey(text))
                {
                    existingByText[text] = new Queue<Dictionary<string, object>>();
                }
                existingByText[text].Enqueue(record);
            }

            List<Dictionary<string, object>> merged = new List<Dictionary<string, object>>();
            foreach (string text in mergedText)
            {
                Dictionary<string, object> existing = null;
                if (existingByText.ContainsKey(text) && existingByText[text].Count > 0)
                {
                    existing = existingByText[text].Dequeue();
                }

                if (existing != null)
                {
                    existing["Text"] = text;
                    if (string.IsNullOrEmpty(GetString(existing, "Status")))
                    {
                        existing["Status"] = GetRecordType(text) == "message" ? "pending" : "processed";
                    }
                    if (string.IsNullOrEmpty(GetString(existing, "CapturedAt")))
                    {
                        existing["CapturedAt"] = DateTime.Now.ToString("s");
                    }
                    EnsureRecordMetadata(existing);
                    merged.Add(existing);
                }
                else
                {
                    string recordType = GetRecordType(text);
                    Dictionary<string, object> record = new Dictionary<string, object>();
                    record["Id"] = "rec_" + Guid.NewGuid().ToString("N");
                    record["Text"] = text;
                    record["Status"] = recordType == "message" ? "pending" : "processed";
                    record["Type"] = recordType;
                    record["CapturedAt"] = DateTime.Now.ToString("s");
                    merged.Add(record);
                }
            }

            AssignIslandIds(merged);
            if (bridgeMerged)
            {
                foreach (Dictionary<string, object> record in merged)
                {
                    if (GetRecordType(GetString(record, "Text")) == "message")
                    {
                        record["Status"] = "pending";
                    }
                }
            }

            SaveChat(contactDir, merged);
            return true;
        }

        public static bool TryHandle(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath.TrimEnd('/');

            if (!path.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (path.Equals(ApiPrefix + "/health", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "name", "wechat-daemon-api" },
                        { "profilesDir", GetProfilesDir() },
                        { "sidecars", BuildSidecarStatus() },
                        { "endpoints", new[] { "GET /jobs", "GET /jobs/{id}", "POST /jobs/{id}/result", "GET /contacts", "GET /contacts/{contact}", "GET /contacts/{contact}/chat-history", "DELETE /contacts/{contact}/chat-history", "PUT /contacts/{contact}/profile", "PATCH /contacts/{contact}/insight" } }
                    });
                    return true;
                }

                if (path.Equals(ApiPrefix + "/contacts", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, BuildContactsList());
                    return true;
                }

                string contactsPrefix = ApiPrefix + "/contacts/";
                if (path.StartsWith(contactsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = path.Substring(contactsPrefix.Length);
                    if (suffix.EndsWith("/chat-history", StringComparison.OrdinalIgnoreCase))
                    {
                        string contact = DecodePath(suffix.Substring(0, suffix.Length - "/chat-history".Length));
                        if (StringEquals(context.Request.HttpMethod, "GET"))
                        {
                            WriteJson(context, BuildContactChatHistory(contact));
                            return true;
                        }
                        if (StringEquals(context.Request.HttpMethod, "DELETE"))
                        {
                            ClearContactChatHistory(contact);
                            WriteJson(context, new Dictionary<string, object>
                            {
                                { "ok", true },
                                { "contact", contact },
                                { "messages", new object[0] }
                            });
                            return true;
                        }

                        WriteError(context, 405, "method not allowed.");
                        return true;
                    }

                    if (suffix.EndsWith("/profile", StringComparison.OrdinalIgnoreCase))
                    {
                        string contact = DecodePath(suffix.Substring(0, suffix.Length - "/profile".Length));
                        if (!StringEquals(context.Request.HttpMethod, "PUT"))
                        {
                            WriteError(context, 405, "method not allowed.");
                            return true;
                        }
                        SaveContactProfile(context, contact);
                        return true;
                    }

                    if (suffix.EndsWith("/insight", StringComparison.OrdinalIgnoreCase))
                    {
                        string contact = DecodePath(suffix.Substring(0, suffix.Length - "/insight".Length));
                        if (!StringEquals(context.Request.HttpMethod, "PATCH"))
                        {
                            WriteError(context, 405, "method not allowed.");
                            return true;
                        }
                        UpdateContactInsight(context, contact);
                        return true;
                    }

                    if (suffix.IndexOf('/') < 0)
                    {
                        if (!StringEquals(context.Request.HttpMethod, "GET"))
                        {
                            WriteError(context, 405, "method not allowed.");
                            return true;
                        }
                        WriteJson(context, BuildContactInfo(DecodePath(suffix)));
                        return true;
                    }
                }

                if (path.Equals(ApiPrefix + "/jobs", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, BuildJobsList());
                    return true;
                }

                string jobsPrefix = ApiPrefix + "/jobs/";
                if (path.StartsWith(jobsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = path.Substring(jobsPrefix.Length);
                    if (suffix.EndsWith("/result", StringComparison.OrdinalIgnoreCase))
                    {
                        string jobId = suffix.Substring(0, suffix.Length - "/result".Length);
                        if (!StringEquals(context.Request.HttpMethod, "POST"))
                        {
                            WriteError(context, 405, "method not allowed.");
                            return true;
                        }
                        ApplyJobResult(context, DecodePath(jobId));
                        return true;
                    }

                    if (!StringEquals(context.Request.HttpMethod, "GET"))
                    {
                        WriteError(context, 405, "method not allowed.");
                        return true;
                    }
                    WriteJson(context, BuildJobDetail(DecodePath(suffix)));
                    return true;
                }

                WriteError(context, 404, "wechat daemon api route not found.");
                return true;
            }
            catch (Exception ex)
            {
                WriteError(context, 500, ex.Message);
                return true;
            }
        }

        private static Dictionary<string, object> BuildJobsList()
        {
            List<Dictionary<string, object>> jobs = new List<Dictionary<string, object>>();
            foreach (string contactDir in GetContactDirs())
            {
                string contact = Path.GetFileName(contactDir);
                List<Dictionary<string, object>> chat = LoadChat(contactDir);
                Dictionary<string, object> info = LoadInfo(contactDir);
                EnsureInsightIds(info, contactDir);
                List<Dictionary<string, object>> pending = GetPendingVisibleRecords(chat);

                if (pending.Count > 0)
                {
                    jobs.Add(new Dictionary<string, object>
                    {
                        { "id", "review:" + contact },
                        { "type", "review" },
                        { "contact", contact },
                        { "reason", "pending_records" },
                        { "pendingCount", pending.Count },
                        { "firstRecordId", GetString(pending[0], "Id") },
                        { "lastRecordId", GetString(pending[pending.Count - 1], "Id") }
                    });
                }

                List<Dictionary<string, object>> insights = GetInsights(info);
                if (insights.Count > 1)
                {
                    jobs.Add(new Dictionary<string, object>
                    {
                        { "id", "consolidate:" + contact },
                        { "type", "consolidation" },
                        { "contact", contact },
                        { "reason", "incident_threading" },
                        { "insightCount", insights.Count },
                        { "threadCount", GetThreads(info).Count }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "jobs", jobs },
                { "count", jobs.Count }
            };
        }

        private static Dictionary<string, object> BuildContactChatHistory(string contact)
        {
            if (string.IsNullOrWhiteSpace(contact))
            {
                throw new InvalidOperationException("contact is required.");
            }

            string contactDir = GetContactDir(contact);
            Dictionary<string, object> info = LoadInfo(contactDir);
            List<Dictionary<string, object>> chat = LoadChat(contactDir);

            return new Dictionary<string, object>
            {
                { "contact", contact },
                { "wechatid", GetString(info, "wechatid") },
                { "messages", chat }
            };
        }

        private static Dictionary<string, object> BuildContactInfo(string contact)
        {
            if (string.IsNullOrWhiteSpace(contact))
            {
                throw new InvalidOperationException("contact is required.");
            }

            string contactDir = GetContactDir(contact);
            Dictionary<string, object> info = LoadInfo(contactDir);
            if (info.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    { "contact", contact },
                    { "nickname", contact },
                    { "wechatid", "" },
                    { "profile", new Dictionary<string, object>() },
                    { "insights", new object[0] },
                    { "threads", new object[0] }
                };
            }

            EnsureInsightIds(info, contactDir);
            return new Dictionary<string, object>
            {
                { "contact", contact },
                { "nickname", GetString(info, "nickname") },
                { "wechatid", GetString(info, "wechatid") },
                { "profile", GetProfile(info) },
                { "insights", GetInsights(info) },
                { "threads", GetThreads(info) }
            };
        }

        private static void ClearContactChatHistory(string contact)
        {
            if (string.IsNullOrWhiteSpace(contact))
            {
                throw new InvalidOperationException("contact is required.");
            }

            string contactDir = GetContactDir(contact);
            if (!Directory.Exists(contactDir)) Directory.CreateDirectory(contactDir);
            EnsureInfoFile(contactDir, contact);
            SnapshotContact(contactDir, "chat_clear");
            SaveChat(contactDir, new List<Dictionary<string, object>>());
        }

        private static void SaveContactProfile(HttpListenerContext context, string contact)
        {
            if (string.IsNullOrWhiteSpace(contact))
            {
                throw new InvalidOperationException("contact is required.");
            }

            Dictionary<string, object> body = Serializer.Deserialize<Dictionary<string, object>>(ReadBody(context.Request));
            if (body == null)
            {
                throw new InvalidOperationException("invalid profile json.");
            }

            Dictionary<string, object> profile = GetDictionary(body, "profile") ?? new Dictionary<string, object>();
            string contactDir = GetContactDir(contact);
            if (!Directory.Exists(contactDir)) Directory.CreateDirectory(contactDir);
            EnsureInfoFile(contactDir, contact);

            Dictionary<string, object> info = LoadInfo(contactDir);
            if (!info.ContainsKey("nickname")) info["nickname"] = contact;
            if (!info.ContainsKey("wechatid")) info["wechatid"] = "";
            if (!info.ContainsKey("insights")) info["insights"] = new object[0];
            if (!info.ContainsKey("threads")) info["threads"] = new object[0];
            info["profile"] = profile;

            SnapshotContact(contactDir, "profile_update");
            SaveInfo(contactDir, info);
            WriteJson(context, new Dictionary<string, object> { { "ok", true }, { "contact", contact }, { "profile", profile } });
        }

        private static void UpdateContactInsight(HttpListenerContext context, string contact)
        {
            if (string.IsNullOrWhiteSpace(contact))
            {
                throw new InvalidOperationException("contact is required.");
            }

            Dictionary<string, object> body = Serializer.Deserialize<Dictionary<string, object>>(ReadBody(context.Request));
            if (body == null)
            {
                throw new InvalidOperationException("invalid insight json.");
            }

            string id = GetString(body, "id");
            string oldSummary = GetString(body, "oldSummary");
            string summary = NormalizeActorTokens(GetString(body, "summary")).Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException("summary is required.");
            }

            string contactDir = GetContactDir(contact);
            if (!Directory.Exists(contactDir))
            {
                throw new InvalidOperationException("contact not found: " + contact);
            }

            Dictionary<string, object> info = LoadInfo(contactDir);
            bool changed = UpdateInsightInList(GetInsights(info), id, oldSummary, summary);
            bool threadChanged = UpdateInsightInList(GetThreads(info), id, oldSummary, summary);

            if (!changed && !threadChanged)
            {
                throw new InvalidOperationException("insight not found.");
            }

            SnapshotContact(contactDir, "insight_update");
            SaveInfo(contactDir, info);
            WriteJson(context, new Dictionary<string, object> { { "ok", true }, { "contact", contact }, { "id", id }, { "summary", summary } });
        }

        private static bool UpdateInsightInList(List<Dictionary<string, object>> items, string id, string oldSummary, string summary)
        {
            bool changed = false;
            foreach (Dictionary<string, object> item in items)
            {
                string itemId = GetString(item, "id");
                string itemSummary = GetString(item, "summary");
                bool idMatches = !string.IsNullOrEmpty(id) && itemId == id;
                bool summaryMatches = string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(oldSummary) && itemSummary == oldSummary;
                if (!idMatches && !summaryMatches) continue;

                if (item.ContainsKey("summary")) item["summary"] = summary;
                else item["Summary"] = summary;
                changed = true;
                break;
            }
            return changed;
        }

        private static Dictionary<string, object> BuildJobDetail(string jobId)
        {
            string type = "review";
            string contact = jobId;
            int sep = jobId.IndexOf(':');
            if (sep > 0)
            {
                type = jobId.Substring(0, sep);
                contact = jobId.Substring(sep + 1);
            }

            string contactDir = Path.Combine(GetProfilesDir(), contact);
            if (!Directory.Exists(contactDir))
            {
                throw new InvalidOperationException("contact not found: " + contact);
            }

            List<Dictionary<string, object>> chat = LoadChat(contactDir);
            Dictionary<string, object> info = LoadInfo(contactDir);
            List<Dictionary<string, object>> pending = GetPendingVisibleRecords(chat);
            List<Dictionary<string, object>> insights = GetInsights(info);
            List<Dictionary<string, object>> threads = GetThreads(info);

            if (type == "consolidate")
            {
                EnsureInsightIds(info, contactDir);
                return new Dictionary<string, object>
                {
                    { "id", jobId },
                    { "type", "consolidation" },
                    { "contact", contact },
                    { "reason", "incident_threading" },
                    { "profile", GetProfile(info) },
                    { "insights", insights },
                    { "existingThreads", threads },
                    { "semanticRouting", GetSemanticRoutingRules() },
                    { "resultSchema", new Dictionary<string, object>
                        {
                            { "profileUpdates", new Dictionary<string, object>() },
                            { "threads", new[] { new Dictionary<string, object>
                                {
                                    { "id", "optional stable thread id" },
                                    { "title", "incident/project/topic title" },
                                    { "category", "职业与项目|生活与近况|偏好与兴趣|资源与合作|随便聊聊" },
                                    { "summary", "thread-level summary" },
                                    { "childInsightIds", new string[0] },
                                    { "sourceRecordIds", new string[0] },
                                    { "highlightRecordIds", new string[0] },
                                    { "latestDate", "yyyy-mm-dd" }
                                }
                            } },
                            { "archiveInsightIds", new string[0] }
                        }
                    }
                };
            }

            List<Dictionary<string, object>> nearbyInsights = new List<Dictionary<string, object>>();
            int takeFrom = Math.Max(0, insights.Count - 5);
            for (int i = takeFrom; i < insights.Count; i++)
            {
                nearbyInsights.Add(insights[i]);
            }

            return new Dictionary<string, object>
            {
                { "id", jobId },
                { "type", "review" },
                { "contact", contact },
                { "reason", "pending_records" },
                { "profile", GetProfile(info) },
                { "records", pending },
                { "existingInsights", nearbyInsights },
                { "semanticRouting", GetSemanticRoutingRules() },
                { "resultSchema", new Dictionary<string, object>
                    {
                        { "replaceInsightIds", new string[0] },
                        { "newInsights", new[] { new Dictionary<string, object>
                            {
                                { "summary", "required chinese summary or 忽略" },
                                { "category", "职业与项目|生活与近况|偏好与兴趣|资源与合作|随便聊聊|忽略" },
                                { "date", "yyyy-mm-dd; infer from source record CapturedAt when present" },
                                { "sourceRecordIds", new string[0] },
                                { "highlightRecordIds", new string[0] }
                            }
                        } },
                        { "markRecordIdsProcessed", new string[0] },
                        { "markRecordIdsSkipped", new string[0] },
                        { "profileUpdates", new Dictionary<string, object>() }
                    }
                }
            };
        }

        private static Dictionary<string, object> GetSemanticRoutingRules()
        {
            return new Dictionary<string, object>
            {
                { "profile", "stable reusable facts about the contact: identity, work, preferences, constraints, resources, recurring traits" },
                { "insights", "dated relationship history: what contact did, what i did, what we discussed, promised, sent, planned, rejected, resolved, or left open" },
                { "both", "if stable contact fact appears inside meaningful interaction, write profileUpdates and newInsights" },
                { "actorRule", "mention my action when useful relationship memory, but write 我 / 对方; never write raw actor tokens like [me], [other], or [system] in summaries" },
                { "uncertainRule", "uncertain facts stay in insights, not profile" },
                { "dateRule", "date insights from source record CapturedAt when present; use chat timestamp text only as context" },
                { "sourceRule", "every insight must use exact sourceRecordIds from payload and 1 to 3 highlightRecordIds" }
            };
        }

        private static void ApplyJobResult(HttpListenerContext context, string jobId)
        {
            string type = "review";
            string contact = jobId;
            int sep = jobId.IndexOf(':');
            if (sep > 0)
            {
                type = jobId.Substring(0, sep);
                contact = jobId.Substring(sep + 1);
            }

            string body = ReadBody(context.Request);
            Dictionary<string, object> result = Serializer.Deserialize<Dictionary<string, object>>(body);
            if (result == null)
            {
                throw new InvalidOperationException("invalid result json.");
            }

            string contactDir = Path.Combine(GetProfilesDir(), contact);
            if (!Directory.Exists(contactDir))
            {
                throw new InvalidOperationException("contact not found: " + contact);
            }

            List<Dictionary<string, object>> chat = LoadChat(contactDir);
            Dictionary<string, object> info = LoadInfo(contactDir);
            EnsureInsightIds(info, contactDir);
            List<Dictionary<string, object>> insights = GetInsights(info);
            SnapshotContact(contactDir, type);

            if (type == "consolidate")
            {
                ApplyConsolidationResult(context, contact, contactDir, info, result);
                return;
            }

            HashSet<string> replaceIds = ToStringSet(GetArray(result, "replaceInsightIds"));
            if (replaceIds.Count > 0)
            {
                insights.RemoveAll(delegate(Dictionary<string, object> insight)
                {
                    return replaceIds.Contains(GetInsightId(insight));
                });
            }

            List<Dictionary<string, object>> acceptedInsights = new List<Dictionary<string, object>>();
            foreach (object raw in GetArray(result, "newInsights"))
            {
                Dictionary<string, object> insight = raw as Dictionary<string, object>;
                if (insight == null) continue;

                string category = GetString(insight, "category");
                string summary = GetString(insight, "summary");
                if (string.IsNullOrWhiteSpace(summary)) continue;
                insight["summary"] = NormalizeActorTokens(summary);
                if (string.IsNullOrWhiteSpace(category)) insight["category"] = "随便聊聊";

                if (summary == "忽略" || category == "忽略")
                {
                    continue;
                }

                if (string.IsNullOrEmpty(GetString(insight, "id")))
                {
                    insight["id"] = "ins_" + Guid.NewGuid().ToString("N");
                }
                if (!insight.ContainsKey("sourceRecordIds"))
                {
                    insight["sourceRecordIds"] = GetArray(result, "markRecordIdsProcessed");
                }
                if (string.IsNullOrWhiteSpace(GetString(insight, "date")))
                {
                    insight["date"] = ResolveInsightDate(chat, insight);
                }
                acceptedInsights.Add(insight);
            }

            foreach (Dictionary<string, object> insight in acceptedInsights)
            {
                insights.Add(insight);
            }
            info["insights"] = insights;

            Dictionary<string, object> profile = GetDictionary(info, "profile");
            if (profile == null)
            {
                profile = new Dictionary<string, object>();
                info["profile"] = profile;
            }
            Dictionary<string, object> updates = GetDictionary(result, "profileUpdates");
            if (updates != null)
            {
                foreach (KeyValuePair<string, object> kv in updates)
                {
                    if (kv.Value != null && kv.Value.ToString() != "")
                    {
                        profile[kv.Key] = kv.Value;
                    }
                }
            }

            HashSet<string> processed = ToStringSet(GetArray(result, "markRecordIdsProcessed"));
            HashSet<string> skipped = ToStringSet(GetArray(result, "markRecordIdsSkipped"));
            if (processed.Count == 0 && skipped.Count == 0)
            {
                foreach (Dictionary<string, object> record in GetPendingVisibleRecords(chat))
                {
                    processed.Add(GetString(record, "Id"));
                }
            }

            int changedRecords = 0;
            foreach (Dictionary<string, object> record in chat)
            {
                string id = GetString(record, "Id");
                if (skipped.Contains(id))
                {
                    record["Status"] = "skipped";
                    changedRecords++;
                }
                else if (processed.Contains(id))
                {
                    record["Status"] = "processed";
                    changedRecords++;
                }
            }

            SaveChat(contactDir, chat);
            SaveInfo(contactDir, info);

            WriteJson(context, new Dictionary<string, object>
            {
                { "ok", true },
                { "contact", contact },
                { "type", "review" },
                { "addedInsights", acceptedInsights.Count },
                { "changedRecords", changedRecords }
            });
        }

        private static void ApplyConsolidationResult(HttpListenerContext context, string contact, string contactDir, Dictionary<string, object> info, Dictionary<string, object> result)
        {
            List<Dictionary<string, object>> existingThreads = GetThreads(info);
            List<Dictionary<string, object>> insights = GetInsights(info);
            HashSet<string> archiveIds = ToStringSet(GetArray(result, "archiveInsightIds"));
            foreach (Dictionary<string, object> thread in ToDictionaryList(GetArray(result, "threads")))
            {
                if (string.IsNullOrEmpty(GetString(thread, "id")))
                {
                    thread["id"] = "thread_" + Guid.NewGuid().ToString("N");
                }
                string summary = GetString(thread, "summary");
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    thread["summary"] = NormalizeActorTokens(summary);
                }
                RepairCorruptedThreadText(thread, insights);
                existingThreads.RemoveAll(delegate(Dictionary<string, object> oldThread)
                {
                    return GetString(oldThread, "id") == GetString(thread, "id");
                });
                existingThreads.Add(thread);
            }
            info["threads"] = existingThreads;

            if (archiveIds.Count > 0)
            {
                foreach (Dictionary<string, object> insight in GetInsights(info))
                {
                    if (archiveIds.Contains(GetInsightId(insight)))
                    {
                        insight["status"] = "threaded";
                    }
                }
            }

            Dictionary<string, object> profile = GetDictionary(info, "profile");
            if (profile == null)
            {
                profile = new Dictionary<string, object>();
                info["profile"] = profile;
            }
            int profileUpdates = 0;
            Dictionary<string, object> updates = GetDictionary(result, "profileUpdates");
            if (updates != null)
            {
                foreach (KeyValuePair<string, object> kv in updates)
                {
                    if (kv.Value != null && kv.Value.ToString() != "")
                    {
                        profile[kv.Key] = kv.Value;
                        profileUpdates++;
                    }
                }
            }

            SaveInfo(contactDir, info);

            WriteJson(context, new Dictionary<string, object>
            {
                { "ok", true },
                { "contact", contact },
                { "type", "consolidation" },
                { "threadCount", existingThreads.Count },
                { "archivedInsights", archiveIds.Count },
                { "profileFieldsUpdated", profileUpdates }
            });
        }

        private static List<Dictionary<string, object>> GetPendingVisibleRecords(List<Dictionary<string, object>> chat)
        {
            List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> record in chat)
            {
                EnsureRecordMetadata(record);
                string status = GetString(record, "Status").ToLowerInvariant();
                if (status != "pending") continue;

                if (IsHiddenOrGap(record))
                {
                    record["Status"] = "processed";
                    continue;
                }
                pending.Add(record);
            }
            return pending;
        }

        private static void RepairCorruptedThreadText(Dictionary<string, object> thread, List<Dictionary<string, object>> insights)
        {
            List<Dictionary<string, object>> children = GetChildInsights(thread, insights);
            if (children.Count == 0) return;

            if (LooksCorruptedText(GetString(thread, "category")))
            {
                string category = MostCommonCategory(children);
                if (!string.IsNullOrEmpty(category)) thread["category"] = category;
            }

            if (LooksCorruptedText(GetString(thread, "summary")))
            {
                string summary = BuildThreadSummary(children);
                if (!string.IsNullOrEmpty(summary)) thread["summary"] = summary;
            }
        }

        private static List<Dictionary<string, object>> GetChildInsights(Dictionary<string, object> thread, List<Dictionary<string, object>> insights)
        {
            HashSet<string> ids = ToStringSet(GetArray(thread, "childInsightIds"));
            List<Dictionary<string, object>> children = new List<Dictionary<string, object>>();
            if (ids.Count == 0 || insights == null) return children;

            foreach (Dictionary<string, object> insight in insights)
            {
                if (ids.Contains(GetInsightId(insight)))
                {
                    children.Add(insight);
                }
            }
            return children;
        }

        private static bool LooksCorruptedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int questionMarks = 0;
            int nonSpace = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                nonSpace++;
                if (c == '?') questionMarks++;
            }
            return questionMarks >= 3 && questionMarks * 3 >= nonSpace;
        }

        private static string MostCommonCategory(List<Dictionary<string, object>> children)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            string best = "";
            int bestCount = 0;
            foreach (Dictionary<string, object> child in children)
            {
                string category = GetString(child, "category");
                if (string.IsNullOrWhiteSpace(category) || LooksCorruptedText(category)) continue;
                if (!counts.ContainsKey(category)) counts[category] = 0;
                counts[category]++;
                if (counts[category] > bestCount)
                {
                    best = category;
                    bestCount = counts[category];
                }
            }
            return best;
        }

        private static string BuildThreadSummary(List<Dictionary<string, object>> children)
        {
            List<string> parts = new List<string>();
            foreach (Dictionary<string, object> child in children)
            {
                string summary = NormalizeActorTokens(GetString(child, "summary"));
                if (string.IsNullOrWhiteSpace(summary) || LooksCorruptedText(summary)) continue;
                parts.Add(summary);
                if (parts.Count >= 3) break;
            }
            if (parts.Count == 0) return "";

            string merged = string.Join("；", parts.ToArray());
            if (merged.Length > 260)
            {
                merged = merged.Substring(0, 260).TrimEnd('；', '，', '。') + "。";
            }
            return merged;
        }

        private static Dictionary<string, object> BuildContactsList()
        {
            List<Dictionary<string, object>> contacts = new List<Dictionary<string, object>>();
            foreach (string contactDir in GetContactDirs())
            {
                string contact = Path.GetFileName(contactDir);
                List<Dictionary<string, object>> chat = LoadChat(contactDir);
                Dictionary<string, object> info = LoadInfo(contactDir);
                contacts.Add(new Dictionary<string, object>
                {
                    { "contact", contact },
                    { "recordCount", chat.Count },
                    { "insightCount", GetInsights(info).Count },
                    { "threadCount", GetThreads(info).Count }
                });
            }
            return new Dictionary<string, object>
            {
                { "contacts", contacts },
                { "count", contacts.Count }
            };
        }

        private static Dictionary<string, object> BuildSidecarStatus()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity", "sidecar_data");
            result["wechat-queue-processor"] = ReadSidecarStatus(Path.Combine(baseDir, "wechat-queue-processor", "logs"));
            result["wechat-insight-consolidator"] = ReadSidecarStatus(Path.Combine(baseDir, "wechat-insight-consolidator", "logs"));
            return result;
        }

        private static Dictionary<string, object> ReadSidecarStatus(string logDir)
        {
            Dictionary<string, object> status = new Dictionary<string, object>();
            status["logDir"] = logDir;
            if (!Directory.Exists(logDir))
            {
                status["exists"] = false;
                return status;
            }

            FileInfo newest = null;
            foreach (string path in Directory.GetFiles(logDir, "*.log"))
            {
                FileInfo file = new FileInfo(path);
                if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = file;
                }
            }

            status["exists"] = true;
            if (newest != null)
            {
                status["latestLog"] = newest.FullName;
                status["latestLogTime"] = newest.LastWriteTime.ToString("s");
                status["latestLogBytes"] = newest.Length;
            }
            return status;
        }

        private static bool IsHiddenOrGap(Dictionary<string, object> record)
        {
            string text = GetString(record, "Text");
            string type = GetString(record, "Type").ToLowerInvariant();
            return text == Constants.IslandBoundary || !VisibleTypes.Contains(type);
        }

        private static int CountIslandBoundaries(List<string> items)
        {
            if (items == null) return 0;
            int count = 0;
            foreach (string item in items)
            {
                if (item == Constants.IslandBoundary) count++;
            }
            return count;
        }

        private static void EnsureRecordMetadata(Dictionary<string, object> record)
        {
            if (string.IsNullOrEmpty(GetString(record, "Id")))
            {
                record["Id"] = "rec_" + Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrEmpty(GetString(record, "Status")))
            {
                record["Status"] = "pending";
            }
            if (string.IsNullOrEmpty(GetString(record, "Type")))
            {
                record["Type"] = GetRecordType(GetString(record, "Text"));
            }
        }

        private static string GetRecordType(string text)
        {
            if (text == Constants.IslandBoundary) return "gap";
            if (MessageProcessor.IsTimestamp(MessageProcessor.StripPrefix(text))) return "timestamp";
            if (MessageProcessor.IsCallNotice(text)) return "call";
            if (MessageProcessor.IsMediaNotice(text)) return "media";
            if (MessageProcessor.IsSystemNotice(MessageProcessor.StripPrefix(text))) return "system";
            return "message";
        }

        private static string NormalizeActorTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("[me]:", "我：")
                .Replace("[other]:", "对方：")
                .Replace("[system]:", "")
                .Replace("[me]", "我")
                .Replace("[other]", "对方")
                .Replace("[system]", "");
        }

        private static string GetInsightId(Dictionary<string, object> insight)
        {
            string id = GetString(insight, "id");
            if (!string.IsNullOrEmpty(id)) return id;
            string summary = GetString(insight, "summary");
            return "summary:" + summary;
        }

        private static List<Dictionary<string, object>> LoadChat(string contactDir)
        {
            string path = Path.Combine(contactDir, "chat_history.json");
            if (!File.Exists(path)) return new List<Dictionary<string, object>>();
            object raw = Serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            IEnumerable arr = raw as IEnumerable;
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            if (arr == null) return result;

            foreach (object item in arr)
            {
                Dictionary<string, object> dict = item as Dictionary<string, object>;
                if (dict != null)
                {
                    EnsureRecordMetadata(dict);
                    result.Add(dict);
                }
                else if (item != null)
                {
                    Dictionary<string, object> legacy = new Dictionary<string, object>();
                    legacy["Text"] = item.ToString();
                    legacy["Status"] = "pending";
                    EnsureRecordMetadata(legacy);
                    result.Add(legacy);
                }
            }
            AssignIslandIds(result);
            return result;
        }

        private static void AssignIslandIds(List<Dictionary<string, object>> records)
        {
            string currentIslandId = null;
            foreach (Dictionary<string, object> record in records)
            {
                if (GetString(record, "Text") == Constants.IslandBoundary)
                {
                    record["IslandId"] = "gap";
                    currentIslandId = null;
                    continue;
                }
                if (string.IsNullOrEmpty(currentIslandId))
                {
                    currentIslandId = string.IsNullOrEmpty(GetString(record, "IslandId"))
                        ? "island_" + Guid.NewGuid().ToString("N")
                        : GetString(record, "IslandId");
                }
                record["IslandId"] = currentIslandId;
            }
        }

        private static Dictionary<string, object> LoadInfo(string contactDir)
        {
            string path = Path.Combine(contactDir, "info.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, object>();
            }
            Dictionary<string, object> info = Serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            return info ?? new Dictionary<string, object>();
        }

        private static List<Dictionary<string, object>> GetInsights(Dictionary<string, object> info)
        {
            List<Dictionary<string, object>> insights = new List<Dictionary<string, object>>();
            object raw;
            if (!info.TryGetValue("insights", out raw) || raw == null) return insights;
            IEnumerable arr = raw as IEnumerable;
            if (arr == null) return insights;

            foreach (object item in arr)
            {
                Dictionary<string, object> insight = item as Dictionary<string, object>;
                if (insight != null) insights.Add(insight);
            }
            return insights;
        }

        private static List<Dictionary<string, object>> GetThreads(Dictionary<string, object> info)
        {
            List<Dictionary<string, object>> threads = new List<Dictionary<string, object>>();
            object raw;
            if (!info.TryGetValue("threads", out raw) || raw == null) return threads;
            IEnumerable arr = raw as IEnumerable;
            if (arr == null) return threads;

            foreach (object item in arr)
            {
                Dictionary<string, object> thread = item as Dictionary<string, object>;
                if (thread != null) threads.Add(thread);
            }
            return threads;
        }

        private static List<Dictionary<string, object>> ToDictionaryList(ArrayList arr)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            foreach (object raw in arr)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item != null) items.Add(item);
            }
            return items;
        }

        private static void EnsureInsightIds(Dictionary<string, object> info, string contactDir)
        {
            bool changed = false;
            List<Dictionary<string, object>> chat = LoadChat(contactDir);
            List<Dictionary<string, object>> insights = GetInsights(info);
            foreach (Dictionary<string, object> insight in insights)
            {
                if (string.IsNullOrEmpty(GetString(insight, "id")))
                {
                    insight["id"] = "ins_" + Guid.NewGuid().ToString("N");
                    changed = true;
                }
                if (!insight.ContainsKey("sourceRecordIds"))
                {
                    ArrayList sourceIds = ResolveInsightSourceIds(chat, insight);
                    if (sourceIds.Count > 0)
                    {
                        insight["sourceRecordIds"] = sourceIds;
                        changed = true;
                    }
                }
                if (!insight.ContainsKey("highlightRecordIds") && insight.ContainsKey("sourceRecordIds"))
                {
                    ArrayList source = GetArray(insight, "sourceRecordIds");
                    ArrayList highlights = new ArrayList();
                    for (int i = 0; i < source.Count && i < 3; i++)
                    {
                        highlights.Add(source[i]);
                    }
                    insight["highlightRecordIds"] = highlights;
                    changed = true;
                }
            }
            if (changed)
            {
                info["insights"] = insights;
                SaveInfo(contactDir, info);
            }
        }

        private static ArrayList ResolveInsightSourceIds(List<Dictionary<string, object>> chat, Dictionary<string, object> insight)
        {
            ArrayList ids = new ArrayList();
            ArrayList start = GetArray(insight, "start");
            ArrayList end = GetArray(insight, "end");
            if (start.Count == 0 || end.Count == 0) return ids;

            int startIndex = FindSequenceIndex(chat, start, 0);
            int endIndex = FindSequenceIndex(chat, end, startIndex >= 0 ? startIndex : 0);
            if (startIndex < 0 || endIndex < 0) return ids;

            int endLast = endIndex + end.Count - 1;
            for (int i = startIndex; i <= endLast && i < chat.Count; i++)
            {
                string id = GetString(chat[i], "Id");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }

        private static string ResolveInsightDate(List<Dictionary<string, object>> chat, Dictionary<string, object> insight)
        {
            HashSet<string> sourceIds = ToStringSet(GetArray(insight, "sourceRecordIds"));
            if (sourceIds.Count > 0)
            {
                foreach (Dictionary<string, object> record in chat)
                {
                    if (!sourceIds.Contains(GetString(record, "Id"))) continue;
                    string date = DateFromCapturedAt(GetString(record, "CapturedAt"));
                    if (!string.IsNullOrEmpty(date)) return date;
                }
            }
            return DateTime.Today.ToString("yyyy-MM-dd");
        }

        private static string DateFromCapturedAt(string capturedAt)
        {
            if (string.IsNullOrWhiteSpace(capturedAt)) return "";
            DateTime parsed;
            if (DateTime.TryParse(capturedAt, out parsed))
            {
                return parsed.ToString("yyyy-MM-dd");
            }
            if (capturedAt.Length >= 10)
            {
                string prefix = capturedAt.Substring(0, 10);
                DateTime prefixDate;
                if (DateTime.TryParse(prefix, out prefixDate))
                {
                    return prefixDate.ToString("yyyy-MM-dd");
                }
            }
            return "";
        }

        private static int FindSequenceIndex(List<Dictionary<string, object>> chat, ArrayList seq, int startFrom)
        {
            if (seq == null || seq.Count == 0) return -1;
            for (int i = Math.Max(0, startFrom); i <= chat.Count - seq.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < seq.Count; j++)
                {
                    if (Normalize(GetString(chat[i + j], "Text")) != Normalize(seq[j] != null ? seq[j].ToString() : ""))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string Normalize(string text)
        {
            if (text == null) return "";
            return text.Trim()
                .Replace("\u2018", "'")
                .Replace("\u2019", "'")
                .Replace("\u201c", "\"")
                .Replace("\u201d", "\"")
                .Replace("\u00a0", "");
        }

        private static void SnapshotContact(string contactDir, string reason)
        {
            try
            {
                string chatPath = Path.Combine(contactDir, "chat_history.json");
                string infoPath = Path.Combine(contactDir, "info.json");
                if (!File.Exists(chatPath) && !File.Exists(infoPath)) return;

                string snapshotDir = Path.Combine(contactDir, "snapshots");
                if (!Directory.Exists(snapshotDir)) Directory.CreateDirectory(snapshotDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string safeReason = reason.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
                string outPath = Path.Combine(snapshotDir, stamp + "_" + safeReason + ".json");
                Dictionary<string, object> snapshot = new Dictionary<string, object>();
                snapshot["reason"] = reason;
                snapshot["createdAt"] = DateTime.Now.ToString("s");
                snapshot["chat_history"] = File.Exists(chatPath) ? Serializer.DeserializeObject(File.ReadAllText(chatPath, Encoding.UTF8)) : new object[0];
                snapshot["info"] = File.Exists(infoPath) ? Serializer.DeserializeObject(File.ReadAllText(infoPath, Encoding.UTF8)) : new Dictionary<string, object>();
                File.WriteAllText(outPath, Serializer.Serialize(snapshot), new UTF8Encoding(false));

                FileInfo[] files = new DirectoryInfo(snapshotDir).GetFiles("*.json");
                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = SnapshotKeepCount; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        private static void SaveChat(string contactDir, List<Dictionary<string, object>> chat)
        {
            File.WriteAllText(Path.Combine(contactDir, "chat_history.json"), Serializer.Serialize(chat), new UTF8Encoding(false));
        }

        private static void SaveInfo(string contactDir, Dictionary<string, object> info)
        {
            File.WriteAllText(Path.Combine(contactDir, "info.json"), Serializer.Serialize(info), new UTF8Encoding(false));
        }

        private static void EnsureInfoFile(string contactDir, string chatName)
        {
            string path = Path.Combine(contactDir, "info.json");
            if (File.Exists(path)) return;

            Dictionary<string, object> info = new Dictionary<string, object>();
            info["nickname"] = chatName;
            info["wechatid"] = "";
            info["profile"] = new Dictionary<string, object>();
            info["insights"] = new object[0];
            info["threads"] = new object[0];
            SaveInfo(contactDir, info);
        }

        private static IEnumerable<string> GetContactDirs()
        {
            string dir = GetProfilesDir();
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetDirectories(dir);
        }

        private static string GetProfilesDir()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        }

        private static string GetContactDir(string chatName)
        {
            return Path.Combine(GetProfilesDir(), GetSafeContactFolderName(chatName));
        }

        private static string GetSafeContactFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Trim();
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (dict == null || !dict.TryGetValue(key, out raw)) return null;
            return raw as Dictionary<string, object>;
        }

        private static Dictionary<string, object> GetProfile(Dictionary<string, object> info)
        {
            Dictionary<string, object> raw = GetDictionary(info, "profile");
            Dictionary<string, object> profile = new Dictionary<string, object>();
            if (raw == null) return profile;

            foreach (KeyValuePair<string, object> kv in raw)
            {
                if (kv.Value == null) continue;
                string value = kv.Value.ToString();
                if (value == "") continue;
                profile[kv.Key] = kv.Value;
            }
            return profile;
        }

        private static ArrayList GetArray(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (dict == null || !dict.TryGetValue(key, out raw) || raw == null) return new ArrayList();
            ArrayList arr = raw as ArrayList;
            if (arr != null) return arr;
            object[] objArr = raw as object[];
            if (objArr != null) return new ArrayList(objArr);
            return new ArrayList();
        }

        private static HashSet<string> ToStringSet(ArrayList arr)
        {
            HashSet<string> set = new HashSet<string>();
            foreach (object item in arr)
            {
                if (item != null && item.ToString() != "") set.Add(item.ToString());
            }
            return set;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return "";
            object value;
            if (dict.TryGetValue(key, out value) && value != null) return value.ToString();
            string lower = key.Length > 0 ? char.ToLowerInvariant(key[0]) + key.Substring(1) : key;
            if (dict.TryGetValue(lower, out value) && value != null) return value.ToString();
            return "";
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJson(HttpListenerContext context, object value)
        {
            byte[] body = Encoding.UTF8.GetBytes(Serializer.Serialize(value));
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }

        private static void WriteError(HttpListenerContext context, int statusCode, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(Serializer.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", message } }));
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }

        private static string DecodePath(string value)
        {
            return Uri.UnescapeDataString(value ?? "");
        }

        private static bool StringEquals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
