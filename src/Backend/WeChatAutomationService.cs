using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Text.RegularExpressions;

namespace WeChatSidekick.Backend
{
    public class WeChatAutomationService
    {
        private AutomationElement _cachedMsgList;
        private string _lastVisibleMessageSignature;
        private List<string> _lastClassifiedMessages = new List<string>();
        public int LastBackfillMessageCount { get; private set; }
        public int LastBackfillPages { get; private set; }
        public int LastBackfillHeight { get; private set; }
        public int LastSessionPages { get; private set; }
        public int LastEligibleChats { get; private set; }
        public int LastAnchorStops { get; private set; }
        public Func<bool> ShouldCancelBackfill { get; set; }

        private void CheckBackfillFocus(IntPtr hwnd)
        {
            if (Win32Helper.GetForegroundWindow() != hwnd || (Win32Helper.GetAsyncKeyState(0x1B) & 0x8000) != 0
                || (ShouldCancelBackfill != null && ShouldCancelBackfill()))
                throw new InvalidOperationException("Backfill interrupted by focus, Escape, or schedule end.");
        }

        private static bool FocusWithin(AutomationElement container)
        {
            Thread.Sleep(150);
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                for (int i = 0; focused != null && i < 64; i++)
                {
                    if (Automation.Compare(focused, container)) return true;
                    focused = TreeWalker.RawViewWalker.GetParent(focused);
                }
            }
            catch { }
            return false;
        }

        public WeChatStateDto GetCurrentState()
        {
            return GetCurrentState(true);
        }

        public WeChatStateDto GetCurrentState(bool includeMessages)
        {
            IntPtr hwnd = Win32Helper.FindWeChatWindow();
            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindowVisible(hwnd))
            {
                _cachedMsgList = null;
                return new WeChatStateDto
                {
                    Type = "StateUpdate",
                    Messages = new List<string>()
                };
            }

            AutomationElement wechat = AutomationElement.FromHandle(hwnd);
            if (wechat == null)
            {
                return new WeChatStateDto
                {
                    Type = "StateUpdate",
                    Messages = new List<string>()
                };
            }

            Win32Helper.RECT rect;
            Win32Helper.GetWindowRect(hwnd, out rect);

            string activeChat = GetActiveChatName(wechat);
            List<string> messages = new List<string>();

            if (includeMessages)
            {
                AutomationElement msgList = FindMessageList(wechat, activeChat);
                if (msgList != null && !IsChatCovered(wechat))
                {
                    messages = ReadVisibleMessages(msgList);
                }
            }

            return new WeChatStateDto
            {
                Type = "StateUpdate",
                ChatName = activeChat,
                Messages = messages,
                WechatRect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom }
            };
        }

        public List<WeChatMessageDto> FetchMessagesByChat(string chatName, int lastN)
        {
            if (!string.IsNullOrEmpty(chatName))
            {
                string currentChat = GetCurrentChatName();
                if (currentChat != chatName)
                {
                    OpenChatForContact(chatName);
                }
            }

            WeChatStateDto state = GetCurrentState();
            List<WeChatMessageDto> result = new List<WeChatMessageDto>();
            List<string> messages = state.Messages ?? new List<string>();
            int start = Math.Max(0, messages.Count - Math.Max(1, lastN));
            for (int i = start; i < messages.Count; i++)
            {
                result.Add(ToMessageDto(messages[i]));
            }
            return result;
        }

        public Dictionary<string, object> ReplyToMessagesByChat(string chatName, string replyMessage)
        {
            if (!string.IsNullOrEmpty(chatName))
            {
                string currentChat = GetCurrentChatName();
                if (currentChat != chatName)
                {
                    OpenChatForContact(chatName);
                }
            }

            bool sent = false;
            if (!string.IsNullOrWhiteSpace(replyMessage))
            {
                SendMessage(replyMessage);
                sent = true;
            }

            return new Dictionary<string, object>
            {
                { "chat_name", chatName },
                { "reply_message", replyMessage },
                { "sent", sent }
            };
        }

        public string GetCurrentChatName()
        {
            IntPtr hwnd = Win32Helper.FindWeChatWindow();
            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindowVisible(hwnd)) return null;
            AutomationElement wechat = AutomationElement.FromHandle(hwnd);
            if (wechat == null) return null;
            return GetActiveChatName(wechat);
        }

        public int BackfillVisibleChats()
        {
            return BackfillChats(false);
        }

        public int BackfillRecentChats()
        {
            return BackfillChats(true);
        }

        public int BackfillContact(string chatName)
        {
            return BackfillChats(false, chatName);
        }

        private int BackfillChats(bool recentOnly, string onlyChat = null)
        {
            using (var gate = new Mutex(false, @"Local\Stringem.WeChat.Backfill"))
            {
                bool acquired;
                try { acquired = gate.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Backfill already running.");
                try { return BackfillChatsCore(recentOnly, onlyChat); }
                finally { gate.ReleaseMutex(); }
            }
        }

        private int BackfillChatsCore(bool recentOnly, string onlyChat)
        {
            LastBackfillMessageCount = 0;
            LastBackfillPages = 0;
            LastBackfillHeight = 0;
            LastSessionPages = 0;
            LastEligibleChats = 0;
            LastAnchorStops = 0;
            IntPtr hwnd = Win32Helper.FindWeChatWindow();
            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindowVisible(hwnd)) throw new InvalidOperationException("WeChat is unavailable.");

            IntPtr previousForeground = Win32Helper.GetForegroundWindow();
            AutomationElement wechat = AutomationElement.FromHandle(hwnd);
            if (wechat == null) throw new InvalidOperationException("WeChat automation tree unavailable.");

            string originalChat = GetActiveChatName(wechat);
            BackfillWindowScope oversized = null;
            int ingestedViewports = 0;
            DateTime sweepStarted = DateTime.UtcNow;
            try
            {
                Win32Helper.SetForegroundWindow(hwnd);
                EnsureSessionList(wechat);
                List<string> chats = onlyChat != null ? new List<string> { onlyChat }
                    : DiscoverSessions(wechat, recentOnly, BackfillCheckpointStore.SweepCutoff(DateTime.Now));
                LastEligibleChats = chats.Count;
                if (chats.Count == 0 && onlyChat != null) throw new InvalidOperationException("Requested chat not found in session list.");
                var failures = new List<string>();
                foreach (string chatName in chats)
                {
                    try
                    {
                    if (!OpenChatForContact(wechat, chatName)) throw new InvalidOperationException("Could not verify opened chat: " + chatName);
                    Thread.Sleep(Constants.NightBackfillSettleMs);

                    CheckBackfillFocus(hwnd);
                    oversized = new BackfillWindowScope(hwnd);
                    LastBackfillHeight = oversized.ActualHeight;
                    _cachedMsgList = null;
                    AutomationElement messageList = FindMessageList(wechat, chatName);
                    if (messageList == null || IsChatCovered(wechat)) throw new InvalidOperationException("Chat message list unavailable: " + chatName);

                    ReturnToLatest(messageList);
                    List<string> firstViewport = ReadVisibleMessages(messageList);
                    // Revalidate the whole contiguous history; legacy checkpoints could
                    // mark a stalled wheel event complete and must not suppress capture.
                    List<string> history = new List<string>(firstViewport);
                    List<string> anchor = BackfillCheckpointStore.GetAnchor(chatName);
                    List<string> stored = DaemonApiService.GetSavedMessages(chatName);
                    if (Reconciliation.FindAnchorEnd(stored, anchor) < 0) anchor = null;

                    int pages = 0;
                    bool reachedTop = false;
                    int unchanged = 0;
                    bool reachedAnchor = Reconciliation.FindAnchorEnd(history, anchor) >= 0;
                    while (pages < 500 && !reachedAnchor)
                    {
                        CheckBackfillFocus(hwnd);
                        if (Win32Helper.GetForegroundWindow() != hwnd || GetActiveChatName(wechat) != chatName)
                            throw new InvalidOperationException("Backfill interrupted by focus or contact change.");
                        List<string> messages = pages == 0 ? firstViewport : ReadVisibleMessages(messageList);
                        ScrollMessageListUp(messageList);
                        Thread.Sleep(600);
                        List<string> nextMessages = ReadVisibleMessages(messageList);
                        if (MessageProcessor.ListsAreEqual(messages, nextMessages))
                        {
                            unchanged++;
                            if (unchanged >= 3) { reachedTop = true; break; }
                        }
                        else { unchanged = 0; history = JoinOlderPage(nextMessages, history); }
                        reachedAnchor = Reconciliation.FindAnchorEnd(history, anchor) >= 0;
                        pages++;
                    }
                    if (!reachedTop && !reachedAnchor) throw new InvalidOperationException("Backfill page limit reached; coverage incomplete.");
                    if (history.Count == 0) throw new InvalidOperationException("Backfill captured no messages.");
                    if (reachedAnchor) LastAnchorStops++;
                    LastBackfillMessageCount += history.Count;
                    LastBackfillPages += pages;
                    List<string> reconciled = reachedAnchor ? Reconciliation.MergeAtAnchor(stored, history, anchor) : history;
                    if (DaemonApiService.IngestVisibleState(chatName, reconciled, true, reachedAnchor)) ingestedViewports++;
                    ReturnToLatest(messageList);
                    Thread.Sleep(Constants.NightBackfillSettleMs);
                    oversized.Dispose();
                    oversized = null;
                    _cachedMsgList = null;
                    messageList = FindMessageList(wechat, chatName);
                    if (messageList != null) ReturnToLatest(messageList);
                    // Only reconciliation moves this anchor, and only after durable save.
                    BackfillCheckpointStore.CommitAnchor(chatName, DaemonApiService.GetSavedMessages(chatName));
                    }
                    catch (Exception ex)
                    {
                        failures.Add(chatName + ": " + ex.Message);
                        // User interruption stops the run; one bad chat does not starve others.
                        CheckBackfillFocus(hwnd);
                    }
                    finally
                    {
                        if (oversized != null) { oversized.Dispose(); oversized = null; }
                        _cachedMsgList = null;
                    }
                }
                if (failures.Count > 0) throw new InvalidOperationException("Sweep incomplete: " + string.Join("; ", failures.ToArray()));
                if (onlyChat == null) BackfillCheckpointStore.CommitSweep(sweepStarted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("night backfill error: " + ex.Message);
                throw;
            }
            finally
            {
                if (oversized != null) oversized.Dispose();
                _cachedMsgList = null;
                if (!string.IsNullOrEmpty(originalChat) && Win32Helper.GetForegroundWindow() == hwnd)
                {
                    try { OpenChatForContact(wechat, originalChat); } catch { }
                }
                if (previousForeground != IntPtr.Zero && Win32Helper.GetForegroundWindow() == hwnd)
                {
                    try { Win32Helper.SetForegroundWindow(previousForeground); } catch { }
                }
            }
            return ingestedViewports;
        }

        public static List<string> JoinOlderPage(List<string> older, List<string> history)
        {
            for (int overlap = Math.Min(older.Count, history.Count); overlap > 0; overlap--)
            {
                bool matches = true;
                for (int i = 0; i < overlap; i++)
                    if (MessageProcessor.StripPrefix(older[older.Count - overlap + i]) != MessageProcessor.StripPrefix(history[i])) { matches = false; break; }
                if (!matches) continue;
                List<string> joined = older.GetRange(0, older.Count - overlap);
                for (int i = 0; i < history.Count; i++)
                {
                    string value = history[i];
                    if (i < overlap && !MessageProcessor.HasSenderPrefix(value))
                        value = MessageProcessor.ChooseBetterMessage(value, older[older.Count - overlap + i]);
                    joined.Add(value);
                }
                return joined;
            }
            throw new InvalidOperationException("Backfill viewport gap; refusing to claim complete history.");
        }

        private AutomationElement FindMessageList(AutomationElement wechat, string activeChat)
        {
            if (string.IsNullOrEmpty(activeChat)) return null;

            try
            {
                if (_cachedMsgList != null)
                {
                    try
                    {
                        var rect = _cachedMsgList.Current.BoundingRectangle;
                        if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0 && !_cachedMsgList.Current.IsOffscreen)
                        {
                            return _cachedMsgList;
                        }
                    }
                    catch
                    {
                        _cachedMsgList = null;
                    }
                }

                var listCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdChatMessageList);
                AutomationElement msgList = wechat.FindFirst(TreeScope.Descendants, listCondition);
                if (msgList == null) return null;

                var listRect = msgList.Current.BoundingRectangle;
                if (listRect.IsEmpty || listRect.Width <= 0 || listRect.Height <= 0 || msgList.Current.IsOffscreen)
                {
                    return null;
                }

                _cachedMsgList = msgList;
                return msgList;
            }
            catch
            {
                return null;
            }
        }

        private List<string> ReadVisibleMessages(AutomationElement msgList)
        {
            List<string> currentUIItems = new List<string>();
            List<VisibleMessageNode> visibleNodes = new List<VisibleMessageNode>();

            int listLeft = 0;
            int listTop = 0;
            int listWidth = 0;
            int listHeight = 0;
            try
            {
                var listBounds = msgList.Current.BoundingRectangle;
                listLeft = (int)listBounds.Left;
                listTop = (int)listBounds.Top;
                listWidth = (int)listBounds.Width;
                listHeight = (int)listBounds.Height;
            }
            catch { }

            ScreenCapture cap = null;
            try
            {
                var itemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                CacheRequest cacheRequest = new CacheRequest();
                cacheRequest.Add(AutomationElement.NameProperty);
                cacheRequest.Add(AutomationElement.ClassNameProperty);
                cacheRequest.Add(AutomationElement.BoundingRectangleProperty);
                cacheRequest.TreeScope = TreeScope.Element;

                AutomationElementCollection items;
                using (cacheRequest.Activate())
                {
                    items = msgList.FindAll(TreeScope.Children, itemCondition);
                }

                foreach (AutomationElement item in items)
                {
                    string msgText = "";
                    string className = "";
                    System.Windows.Rect bounds;
                    try
                    {
                        msgText = item.Cached.Name ?? "";
                        className = item.Cached.ClassName ?? "";
                        bounds = item.Cached.BoundingRectangle;
                    }
                    catch
                    {
                        try
                        {
                            msgText = item.Current.Name ?? "";
                            className = item.Current.ClassName ?? "";
                            bounds = item.Current.BoundingRectangle;
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(msgText)) continue;
                    if (MessageProcessor.IsIgnoredNode(msgText)) continue;

                    Win32Helper.RECT itemRect = new Win32Helper.RECT
                    {
                        Left = (int)bounds.Left,
                        Top = (int)bounds.Top,
                        Right = (int)bounds.Right,
                        Bottom = (int)bounds.Bottom
                    };

                    visibleNodes.Add(new VisibleMessageNode
                    {
                        Text = msgText,
                        ClassName = className,
                        Rect = itemRect
                    });
                }

                string signature = BuildVisibleMessageSignature(visibleNodes);
                if (signature == _lastVisibleMessageSignature)
                {
                    return new List<string>(_lastClassifiedMessages);
                }

                if (listWidth > 0 && listHeight > 0)
                {
                    var visible = Win32Helper.VisibleScreenPart(new Win32Helper.RECT {
                        Left = listLeft, Top = listTop, Right = listLeft + listWidth, Bottom = listTop + listHeight });
                    try { cap = new ScreenCapture(visible.Left, visible.Top, visible.Right - visible.Left, visible.Bottom - visible.Top); }
                    catch { }
                }

                foreach (VisibleMessageNode node in visibleNodes)
                {
                    string stripped = MessageProcessor.StripPrefix(node.Text);
                    if (MessageProcessor.IsHiddenMetadata(node.Text))
                    {
                        currentUIItems.Add(Constants.SenderPrefixSystem + stripped);
                        continue;
                    }

                    bool? isMe = Win32Helper.IsMessageFromMe(cap, node.Rect);
                    if (isMe.HasValue)
                    {
                        string prefix = isMe.Value ? Constants.SenderPrefixMe : Constants.SenderPrefixOther;
                        currentUIItems.Add(prefix + node.Text);
                    }
                    else currentUIItems.Add(node.Text);
                }

                _lastVisibleMessageSignature = signature;
                _lastClassifiedMessages = new List<string>(currentUIItems);
            }
            finally
            {
                if (cap != null) cap.Dispose();
            }

            return currentUIItems;
        }

        private class VisibleMessageNode
        {
            public string Text { get; set; }
            public string ClassName { get; set; }
            public Win32Helper.RECT Rect { get; set; }
        }

        private string BuildVisibleMessageSignature(List<VisibleMessageNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return "";
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < nodes.Count; i++)
                {
                    VisibleMessageNode node = nodes[i];
                    hash = hash * 31 + (node.Text != null ? node.Text.GetHashCode() : 0);
                    hash = hash * 31 + (node.ClassName != null ? node.ClassName.GetHashCode() : 0);
                    hash = hash * 31 + node.Rect.Left;
                    hash = hash * 31 + node.Rect.Top;
                    hash = hash * 31 + node.Rect.Right;
                    hash = hash * 31 + node.Rect.Bottom;
                }
                return nodes.Count.ToString() + ":" + hash.ToString();
            }
        }

        private bool IsChatCovered(AutomationElement wechat)
        {
            try
            {
                var cardCondition = new OrCondition(
                    new PropertyCondition(AutomationElement.ClassNameProperty, "mmui::ProfileView"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "mmui::ProfileViewNormal"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "single_chat_info_view"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "room_info_view")
                );

                return wechat.FindFirst(TreeScope.Descendants, cardCondition) != null;
            }
            catch { }
            return false;
        }

        private string GetActiveChatName(AutomationElement wechat)
        {
            try
            {
                var titleCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdChatNameLabel);
                var titleEl = wechat.FindFirst(TreeScope.Descendants, titleCondition);
                if (titleEl != null && !string.IsNullOrEmpty(titleEl.Current.Name))
                {
                    return titleEl.Current.Name;
                }
            }
            catch { }

            try
            {
                var sessionListCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList);
                var sessionList = wechat.FindFirst(TreeScope.Descendants, sessionListCondition);

                if (sessionList != null)
                {
                    var selectionPattern = sessionList.GetCurrentPattern(SelectionPattern.Pattern) as SelectionPattern;
                    if (selectionPattern != null)
                    {
                        var selection = selectionPattern.Current.GetSelection();
                        if (selection.Length > 0)
                        {
                            var selectedItem = selection[0];
                            string autoid = selectedItem.Current.AutomationId;
                            if (!string.IsNullOrEmpty(autoid) && autoid.StartsWith(Constants.AutoIdSessionItemPrefix))
                            {
                                return autoid.Substring(Constants.AutoIdSessionItemPrefix.Length);
                            }
                            if (!string.IsNullOrEmpty(selectedItem.Current.Name))
                            {
                                return selectedItem.Current.Name;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private void OpenChatForContact(string chatName)
        {
            if (string.IsNullOrWhiteSpace(chatName)) return;

            IntPtr hwnd = Win32Helper.FindWeChatWindow();
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("WeChat window not found.");

            Win32Helper.SetForegroundWindow(hwnd);
            AutomationElement wechat = AutomationElement.FromHandle(hwnd);
            if (wechat == null) throw new InvalidOperationException("WeChat automation tree not found.");

            if (!OpenChatForContact(wechat, chatName))
            {
                throw new InvalidOperationException("Chat item cannot be opened: " + chatName);
            }
        }

        private bool OpenChatForContact(AutomationElement wechat, string chatName)
        {
            if (wechat == null || string.IsNullOrWhiteSpace(chatName)) return false;
            if (GetActiveChatName(wechat) == chatName && FindMessageList(wechat, chatName) != null) return true;
            EnsureSessionList(wechat);

            AutomationElement match = FindSessionByName(wechat, chatName);
            if (match == null || match.Current.IsOffscreen)
            {
                ScanSessions(wechat, delegate(AutomationElement item) {
                    if (item.Current.AutomationId != Constants.AutoIdSessionItemPrefix + chatName) return false;
                    match = item; return true;
                });
                if (match == null || match.Current.IsOffscreen) return false;
            }

            object invokeObj;
            if (match.TryGetCurrentPattern(InvokePattern.Pattern, out invokeObj))
            {
                ((InvokePattern)invokeObj).Invoke();
                Thread.Sleep(400);
                if (GetActiveChatName(wechat) == chatName && FindMessageList(wechat, chatName) != null) return true;
            }

            object selectionObj;
            if (match.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selectionObj))
            {
                ((SelectionItemPattern)selectionObj).Select();
                Thread.Sleep(400);
                if (GetActiveChatName(wechat) == chatName && FindMessageList(wechat, chatName) != null) return true;
            }
            if (match.Current.IsOffscreen) return false;
            var rect = match.Current.BoundingRectangle;
            Win32Helper.ClickAt((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
            _cachedMsgList = null;
            return GetActiveChatName(wechat) == chatName && FindMessageList(wechat, chatName) != null;
        }

        private void EnsureSessionList(AutomationElement wechat)
        {
            if (wechat.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList)) != null) return;
            var back = wechat.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, "返回"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            if (back == null || back.Current.IsOffscreen) throw new InvalidOperationException("Session list unavailable.");
            var rect = back.Current.BoundingRectangle;
            Win32Helper.ClickAt((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
            _cachedMsgList = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (wechat.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList)) != null) return;
                Thread.Sleep(150);
            }
            throw new InvalidOperationException("Session list did not appear after navigation.");
        }

        private List<string> DiscoverSessions(AutomationElement wechat, bool recentOnly, DateTime since)
        {
            var names = new List<string>();
            var seen = new HashSet<string>();
            DateTime now = DateTime.Now;
            using (var expandedList = new BackfillWindowScope(Win32Helper.FindWeChatWindow()))
            {
            ScanSessions(wechat, delegate(AutomationElement item) {
                string name = item.Current.AutomationId.Substring(Constants.AutoIdSessionItemPrefix.Length);
                if ((!recentOnly || Reconciliation.Eligible(item.Current.Name, since, now)) && seen.Add(name)) names.Add(name);
                return false;
            }, true);
            }
            return names;
        }

        private void ScanSessions(AutomationElement wechat, Func<AutomationElement, bool> visit, bool pageMode = false)
        {
            EnsureSessionList(wechat);
            var list = wechat.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList));
            if (list == null) throw new InvalidOperationException("Session list unavailable.");
            CheckBackfillFocus(Win32Helper.FindWeChatWindow());
            list.SetFocus();
            object pattern;
            if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll, 0);
            else
            {
                if (!FocusWithin(list)) throw new InvalidOperationException("Could not focus session list.");
                System.Windows.Forms.SendKeys.SendWait("^{HOME}");
            }
            Thread.Sleep(600);
            string previous = null; int unchanged = 0;
            for (int page = 0; page < 1000; page++)
            {
                LastSessionPages++;
                CheckBackfillFocus(Win32Helper.FindWeChatWindow());
                var labels = new List<string>();
                foreach (AutomationElement item in list.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string id = item.Current.AutomationId ?? "";
                    if (!id.StartsWith(Constants.AutoIdSessionItemPrefix) || item.Current.IsOffscreen) continue;
                    labels.Add(id + "\n" + item.Current.Name);
                    if (visit(item)) return;
                }
                if (labels.Count == 0) throw new InvalidOperationException("Session enumeration returned no rows; sweep incomplete.");
                string signature = string.Join("\n", labels.ToArray());
                unchanged = signature == previous ? unchanged + 1 : 0;
                if (unchanged >= 3) return;
                previous = signature;
                if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                {
                    var scroll = (ScrollPattern)pattern;
                    if (!scroll.Current.VerticallyScrollable || scroll.Current.VerticalScrollPercent >= 100) return;
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                }
                else
                {
                    var r = list.Current.BoundingRectangle;
                    if (pageMode)
                    {
                        list.SetFocus();
                        if (!FocusWithin(list)) throw new InvalidOperationException("Could not focus session list for paging.");
                        System.Windows.Forms.SendKeys.SendWait("{PGDN}");
                    }
                    else if (!Win32Helper.ScrollSessionListDown(new Win32Helper.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom }))
                        throw new InvalidOperationException("Could not scroll session list.");
                }
                Thread.Sleep(500);
            }
            throw new InvalidOperationException("Session enumeration limit reached; sweep incomplete.");
        }

        private List<string> GetVisibleSessionNames(AutomationElement wechat, bool recentOnly)
        {
            List<string> names = new List<string>();
            try
            {
                var listCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList);
                AutomationElement sessionList = wechat.FindFirst(TreeScope.Descendants, listCondition);
                if (sessionList == null) return names;

                AutomationElementCollection items = sessionList.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement item in items)
                {
                    string autoId = item.Current.AutomationId ?? "";
                    if (!autoId.StartsWith(Constants.AutoIdSessionItemPrefix)) continue;
                    if (recentOnly && !IsRecentSession(item.Current.Name)) continue;
                    string name = autoId.Substring(Constants.AutoIdSessionItemPrefix.Length);
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                }
            }
            catch { }
            return names;
        }

        private static bool IsRecentSession(string sessionText)
        {
            if (string.IsNullOrWhiteSpace(sessionText)) return false;
            string[] lines = sessionText.Trim().Replace("\r", "").Split('\n');
            // Timestamp is last line; previews may themselves contain date-like text.
            return IsRecentSessionMarker(lines[lines.Length - 1].Trim());
        }

        private static bool IsRecentSessionMarker(string marker)
        {
            if (Regex.IsMatch(marker, "^\\d{1,2}:\\d{2}$")) return true;
            if (Regex.IsMatch(marker, @"^(昨天|yesterday)(\s+\d{1,2}:\d{2})?$", RegexOptions.IgnoreCase)) return true;

            DateTime listedDate;
            if (!DateTime.TryParseExact(marker, "MM/dd", null, System.Globalization.DateTimeStyles.None, out listedDate)) return false;
            DateTime today = DateTime.Today;
            DateTime date = new DateTime(today.Year, listedDate.Month, listedDate.Day);
            if (date > today) date = date.AddYears(-1);
            return date == today || date == today.AddDays(-1);
        }

        private static bool ScrollMessageListUp(AutomationElement messageList)
        {
            try
            {
                object scrollObject;
                if (messageList.TryGetCurrentPattern(ScrollPattern.Pattern, out scrollObject))
                {
                    ScrollPattern scroll = (ScrollPattern)scrollObject;
                    if (scroll.Current.VerticalScrollPercent <= 0) return false;
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
                    return true;
                }

                System.Windows.Rect bounds = messageList.Current.BoundingRectangle;
                Win32Helper.RECT target = new Win32Helper.RECT
                {
                    Left = (int)bounds.Left,
                    Top = (int)bounds.Top,
                    Right = (int)bounds.Right,
                    Bottom = (int)bounds.Bottom
                };
                return Win32Helper.ScrollMouseWheelUp(target);
            }
            catch { }
            return false;
        }

        private void ReturnToLatest(AutomationElement messageList)
        {
            try
            {
                CheckBackfillFocus(Win32Helper.FindWeChatWindow());
                // Focus the list, never the compose box. Ctrl+End was verified on Qt WeChat.
                messageList.SetFocus();
                if (FocusWithin(messageList))
                {
                    System.Windows.Forms.SendKeys.SendWait("^{END}");
                    Thread.Sleep(800);
                }
                object scrollObject;
                if (messageList.TryGetCurrentPattern(ScrollPattern.Pattern, out scrollObject))
                {
                    ((ScrollPattern)scrollObject).SetScrollPercent(ScrollPattern.NoScroll, 100.0);
                    return;
                }

                System.Windows.Rect bounds = messageList.Current.BoundingRectangle;
                var target = new Win32Helper.RECT
                {
                    Left = (int)bounds.Left,
                    Top = (int)bounds.Top,
                    Right = (int)bounds.Right,
                    Bottom = (int)bounds.Bottom
                };
                string previous = null;
                int stable = 0;
                for (int i = 0; i < 500; i++)
                {
                    CheckBackfillFocus(Win32Helper.FindWeChatWindow());
                    if (Win32Helper.GetForegroundWindow() != Win32Helper.FindWeChatWindow())
                        throw new InvalidOperationException("Backfill interrupted by focus change.");
                    Win32Helper.ScrollMouseWheelDown(target);
                    Thread.Sleep(350);
                    string current = BackfillCheckpointStore.CreateFingerprint(ReadVisibleMessages(messageList));
                    stable = current == previous ? stable + 1 : 0;
                    if (stable >= 3) return;
                    previous = current;
                }
                throw new InvalidOperationException("Could not reach latest messages.");
            }
            catch (Exception ex) { throw new InvalidOperationException("Could not restore latest messages.", ex); }
        }

        private AutomationElement FindSessionByName(AutomationElement wechat, string chatName)
        {
            try
            {
                var listCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, Constants.AutoIdSessionList);
                var sessionList = wechat.FindFirst(TreeScope.Descendants, listCondition);
                if (sessionList == null) return null;

                var items = sessionList.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement item in items)
                {
                    string name = item.Current.Name;
                    string autoId = item.Current.AutomationId;
                    if (name == chatName || autoId == Constants.AutoIdSessionItemPrefix + chatName)
                    {
                        return item;
                    }
                }
            }
            catch { }
            return null;
        }

        private void SendMessage(string message)
        {
            IntPtr hwnd = Win32Helper.FindWeChatWindow();
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("WeChat window not found.");

            Win32Helper.SetForegroundWindow(hwnd);
            System.Windows.Forms.SendKeys.SendWait(message);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }

        private WeChatMessageDto ToMessageDto(string raw)
        {
            string sender = "UNKNOWN";
            if (raw != null && raw.StartsWith(Constants.SenderPrefixMe)) sender = "ME";
            else if (raw != null && raw.StartsWith(Constants.SenderPrefixOther)) sender = "OTHER";
            else if (raw != null && raw.StartsWith(Constants.SenderPrefixSystem)) sender = "SYSTEM";

            return new WeChatMessageDto
            {
                Sender = sender,
                Text = MessageProcessor.StripPrefix(raw)
            };
        }
    }
}
