using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WeChatSidekick.Backend
{
    public class WeChatAutomationService
    {
        private AutomationElement _cachedMsgList;
        private string _lastVisibleMessageSignature;
        private List<string> _lastClassifiedMessages = new List<string>();

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
                    if (className == "mmui::ChatBubbleItemView" ||
                        className == "mmui::ChatBubbleReferItemView" ||
                        className == "mmui::ChatItemView")
                    {
                        if (!MessageProcessor.IsHiddenMetadata(msgText))
                        {
                            continue;
                        }
                    }

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
                    try { cap = new ScreenCapture(listLeft, listTop, listWidth, listHeight); }
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

            AutomationElement match = FindSessionByName(wechat, chatName);
            if (match == null)
            {
                throw new InvalidOperationException("Chat not found in current session list: " + chatName);
            }

            object invokeObj;
            if (match.TryGetCurrentPattern(InvokePattern.Pattern, out invokeObj))
            {
                ((InvokePattern)invokeObj).Invoke();
                return;
            }

            object selectionObj;
            if (match.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selectionObj))
            {
                ((SelectionItemPattern)selectionObj).Select();
                return;
            }

            throw new InvalidOperationException("Chat item cannot be opened: " + chatName);
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
