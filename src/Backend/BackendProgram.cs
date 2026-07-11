using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WeChatSidekick.Backend
{
    public class BackendProgram
    {
        private static HttpListener _listener;
        private static readonly List<WebSocket> Clients = new List<WebSocket>();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly WeChatAutomationService WeChat = new WeChatAutomationService();
        private static string _lastChatName;
        private static List<string> _lastMessages = new List<string>();
        private static int[] _lastRect;
        private static bool _forceUpdate = true;
        private static bool _isScanning;
        private const int PollIntervalMs = 500;

        public static void Main(string[] args)
        {
            string prefix = GetArg(args, "--listen", "http://127.0.0.1:8081/wechat/");
            Console.WriteLine("starting windows wechat backend on " + prefix);

            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            Task acceptTask = Task.Run(() => AcceptClientsAsync());

            Console.WriteLine("backend running. tools: fetch_messages_by_chat, reply_to_messages_by_chat, get_current_state");
            while (true)
            {
                Thread.Sleep(PollIntervalMs);
                PollWeChat();
            }
        }

        private static string GetArg(string[] args, string name, string fallback)
        {
            for (int i = 0; args != null && i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return fallback;
        }

        private static async Task AcceptClientsAsync()
        {
            while (true)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    if (!context.Request.IsWebSocketRequest)
                    {
                        if (DaemonApiService.TryHandle(context))
                        {
                            continue;
                        }
                        WriteHttpInfo(context);
                        continue;
                    }

                    HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                    WebSocket webSocket = wsContext.WebSocket;
                    lock (Clients)
                    {
                        Clients.Add(webSocket);
                    }
                    Console.WriteLine("client connected.");
                    _forceUpdate = true;
                    Task receiveTask = Task.Run(() => ReceiveLoopAsync(webSocket));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("accept error: " + ex.Message);
                }
            }
        }

        private static void WriteHttpInfo(HttpListenerContext context)
        {
            var info = new Dictionary<string, object>
            {
                { "name", "windows-wechat-backend" },
                { "transport", "websocket" },
                { "tools", new[] { "get_current_state", "fetch_messages_by_chat", "reply_to_messages_by_chat" } },
                { "daemonApi", new[] { "GET /wechat/daemon/health", "GET /wechat/daemon/jobs", "GET /wechat/daemon/jobs/{id}", "POST /wechat/daemon/jobs/{id}/result" } }
            };
            byte[] body = Encoding.UTF8.GetBytes(Serializer.Serialize(info));
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }

        private static async Task ReceiveLoopAsync(WebSocket webSocket)
        {
            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    string message = await ReceiveTextAsync(webSocket);
                    if (message == null) break;
                    await HandleRequest(webSocket, message);
                }
                catch
                {
                    break;
                }
            }

            lock (Clients)
            {
                Clients.Remove(webSocket);
            }
        }

        private static async Task<string> ReceiveTextAsync(WebSocket webSocket)
        {
            byte[] buffer = new byte[8192];
            MemoryStream ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    return null;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static async Task HandleRequest(WebSocket webSocket, string json)
        {
            BackendRequest request = null;
            BackendResponse response;
            try
            {
                request = Serializer.Deserialize<BackendRequest>(json);
                object result = ExecuteTool(request);
                response = new BackendResponse
                {
                    Type = "Response",
                    Id = request != null ? request.Id : null,
                    Method = request != null ? request.Method : null,
                    Ok = true,
                    Result = result
                };
            }
            catch (Exception ex)
            {
                response = new BackendResponse
                {
                    Type = "Response",
                    Id = request != null ? request.Id : null,
                    Method = request != null ? request.Method : null,
                    Ok = false,
                    Error = ex.Message
                };
            }

            await SendMessage(webSocket, Serializer.Serialize(response));
        }

        private static object ExecuteTool(BackendRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Method))
            {
                throw new ArgumentException("method is required.");
            }

            Dictionary<string, object> p = request.Params ?? new Dictionary<string, object>();
            if (request.Method == "get_current_state")
            {
                return WeChat.GetCurrentState();
            }
            if (request.Method == "fetch_messages_by_chat")
            {
                string chatName = GetString(p, "chat_name", GetString(p, "chatName", null));
                int lastN = GetInt(p, "last_n", GetInt(p, "lastN", 50));
                return WeChat.FetchMessagesByChat(chatName, lastN);
            }
            if (request.Method == "reply_to_messages_by_chat")
            {
                string chatName = GetString(p, "chat_name", GetString(p, "chatName", null));
                string reply = GetString(p, "reply_message", GetString(p, "replyMessage", null));
                return WeChat.ReplyToMessagesByChat(chatName, reply);
            }

            throw new ArgumentException("unknown method: " + request.Method);
        }

        private static string GetString(Dictionary<string, object> values, string key, string fallback)
        {
            if (values.ContainsKey(key) && values[key] != null) return values[key].ToString();
            return fallback;
        }

        private static int GetInt(Dictionary<string, object> values, string key, int fallback)
        {
            if (!values.ContainsKey(key) || values[key] == null) return fallback;
            int value;
            if (int.TryParse(values[key].ToString(), out value)) return value;
            return fallback;
        }

        private static async Task SendMessage(WebSocket webSocket, string jsonMessage)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(jsonMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task BroadcastMessage(string jsonMessage)
        {
            List<WebSocket> deadClients = new List<WebSocket>();
            WebSocket[] clientsArray;
            lock (Clients)
            {
                clientsArray = Clients.ToArray();
            }

            foreach (var client in clientsArray)
            {
                if (client.State == WebSocketState.Open)
                {
                    try { await SendMessage(client, jsonMessage); }
                    catch { deadClients.Add(client); }
                }
                else
                {
                    deadClients.Add(client);
                }
            }

            if (deadClients.Count > 0)
            {
                lock (Clients)
                {
                    foreach (var dead in deadClients)
                    {
                        Clients.Remove(dead);
                    }
                }
            }
        }

        private static void PollWeChat()
        {
            if (_isScanning) return;
            _isScanning = true;
            try
            {
                WeChatStateDto state = WeChat.GetCurrentState(true);
                bool hidden = state.WechatRect == null || state.WechatRect.Length != 4;
                bool chatChanged = state.ChatName != _lastChatName;
                bool messagesChanged = !SameMessages(_lastMessages, state.Messages);

                if (_forceUpdate || hidden || chatChanged || messagesChanged)
                {
                    bool forced = _forceUpdate;
                    _forceUpdate = false;
                    _lastChatName = state.ChatName;
                    _lastMessages = state.Messages ?? new List<string>();
                    _lastRect = state.WechatRect;

                    bool recorded = false;
                    if (!hidden && !string.IsNullOrEmpty(state.ChatName) && (forced || chatChanged || messagesChanged))
                    {
                        recorded = DaemonApiService.IngestVisibleState(state.ChatName, _lastMessages);
                    }

                    Task broadcastTask = Task.Run(() => BroadcastMessage(Serializer.Serialize(ToStatePayload(state))));

                    if (chatChanged || messagesChanged)
                    {
                        Console.WriteLine(string.Format(
                            "state updated: {0} ({1} messages visible, recorded={2})",
                            state.ChatName,
                            _lastMessages.Count,
                            recorded));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("poll error: " + ex.Message);
            }
            finally
            {
                _isScanning = false;
            }
        }

        private static bool SameRect(int[] a, int[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static bool SameMessages(List<string> a, List<string> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static Dictionary<string, object> ToStatePayload(WeChatStateDto state)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["type"] = state.Type;
            payload["chatName"] = state.ChatName;
            payload["messages"] = state.Messages ?? new List<string>();
            payload["records"] = ToTypedRecords(state.Messages ?? new List<string>());
            if (state.WechatRect != null && state.WechatRect.Length == 4)
            {
                payload["wechatRect"] = state.WechatRect;
            }
            return payload;
        }

        private static List<Dictionary<string, object>> ToTypedRecords(List<string> messages)
        {
            List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
            foreach (string message in messages)
            {
                string sender = "unknown";
                if (message != null && message.StartsWith(Constants.SenderPrefixMe)) sender = "me";
                else if (message != null && message.StartsWith(Constants.SenderPrefixOther)) sender = "other";
                else if (message != null && message.StartsWith(Constants.SenderPrefixSystem)) sender = "system";

                string type = "message";
                if (message == Constants.IslandBoundary) type = "gap";
                else if (MessageProcessor.IsTimestamp(MessageProcessor.StripPrefix(message))) type = "timestamp";
                else if (MessageProcessor.IsCallNotice(message)) type = "call";
                else if (MessageProcessor.IsMediaNotice(message)) type = "media";
                else if (MessageProcessor.IsSystemNotice(MessageProcessor.StripPrefix(message))) type = "system";

                records.Add(new Dictionary<string, object>
                {
                    { "type", type },
                    { "sender", sender },
                    { "text", MessageProcessor.StripPrefix(message) },
                    { "raw", message }
                });
            }
            return records;
        }
    }
}
