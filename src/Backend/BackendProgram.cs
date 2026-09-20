using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

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
        private static bool _isStopping;
        private static int _backfillRunning;
        private static readonly Mutex CaptureGate = new Mutex(false, @"Local\Stringem.WeChat.Backfill");
        private static string _lastNightBackfillDate;
        private static NightBackfillState _nightState = NightBackfillState.Load();
        private static BackfillSettings _backfillSettings = BackfillSettingsStore.Load();
        private static System.Threading.Timer _pollTimer;
        private const int PollIntervalMs = Constants.TimerIntervalMs;

        public static void Main(string[] args)
        {
            Win32Helper.SetProcessDPIAware();
            if (HasArg(args, "--backfill-recent-now"))
            {
                string target = GetArg(args, "--chat", null);
                string report = GetArg(args, "--report", null);
                var result = new Dictionary<string, object>();
                try
                {
                    result["changedContacts"] = target == null ? WeChat.BackfillRecentChats() : WeChat.BackfillContact(target);
                    result["success"] = true;
                }
                catch (Exception ex)
                {
                    result["success"] = false;
                    result["error"] = ex.Message;
                    Environment.ExitCode = 1;
                }
                result["contact"] = target;
                result["messages"] = WeChat.LastBackfillMessageCount;
                result["pages"] = WeChat.LastBackfillPages;
                result["viewportWindowHeight"] = WeChat.LastBackfillHeight;
                result["sessionPages"] = WeChat.LastSessionPages;
                result["eligibleChats"] = WeChat.LastEligibleChats;
                result["anchorStops"] = WeChat.LastAnchorStops;
                result["finishedAt"] = DateTime.Now.ToString("s");
                if (report != null) File.WriteAllText(report, Serializer.Serialize(result), Encoding.UTF8);
                return;
            }

            string prefix = GetArg(args, "--listen", "http://127.0.0.1:8081/wechat/");
            try
            {
                Start(prefix);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext(prefix));
            }
            catch (Exception ex)
            {
                MessageBox.Show("wechat-daemon could not start.\n\n" + ex.Message, "wechat-daemon", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Stop();
            }
        }

        private static void Start(string prefix)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            Task.Run(() => AcceptClientsAsync());
            _pollTimer = new System.Threading.Timer(delegate(object state) { PollWeChat(); }, null, 0, PollIntervalMs);
        }

        private static void Stop()
        {
            if (_isStopping) return;
            _isStopping = true;
            if (_pollTimer != null) _pollTimer.Dispose();
            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
            lock (Clients)
            {
                foreach (WebSocket client in Clients)
                {
                    try { client.Abort(); } catch { }
                }
                Clients.Clear();
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

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; args != null && i < args.Length; i++)
            {
                if (args[i] == name) return true;
            }
            return false;
        }

        private static async Task AcceptClientsAsync()
        {
            while (!_isStopping)
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
                    Trace.WriteLine("client connected.");
                    _forceUpdate = true;
                    Task receiveTask = Task.Run(() => ReceiveLoopAsync(webSocket));
                }
                catch (Exception ex)
                {
                    if (_isStopping) return;
                    Trace.WriteLine("accept error: " + ex.Message);
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
            if (_isScanning || Thread.VolatileRead(ref _backfillRunning) != 0) return;
            bool acquired;
            try { acquired = CaptureGate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
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
                        Trace.WriteLine(string.Format(
                            "state updated: {0} ({1} messages visible, recorded={2})",
                            state.ChatName,
                            _lastMessages.Count,
                            recorded));
                    }
                }

                RunNightBackfillIfDue();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("poll error: " + ex.Message);
            }
            finally
            {
                _isScanning = false;
                CaptureGate.ReleaseMutex();
            }
        }

        private static void RunNightBackfillIfDue()
        {
            DateTime now = DateTime.Now;
            BackfillSettings settings = GetBackfillSettings();
            if (!IsWithinBackfillWindow(now, settings)) return;
            string date = GetBackfillScheduleDate(now, settings).ToString("yyyy-MM-dd");
            string schedule = date + ":" + settings.StartMinutes + ":" + settings.EndMinutes;
            if (_lastNightBackfillDate == date) return;
            if (!_nightState.CanAttempt(schedule, DateTime.UtcNow)) return;
            if (!Win32Helper.HasBeenIdleFor(TimeSpan.FromMinutes(Constants.NightBackfillIdleMinutes))) return;

            _nightState.LastAttemptUtc = DateTime.UtcNow.ToString("o");
            _nightState.LastError = "Run started; completion pending.";
            _nightState.Save();
            try
            {
                WeChat.ShouldCancelBackfill = delegate { return !IsWithinBackfillWindow(DateTime.Now, settings); };
                RunBackfill("night", true);
                _lastNightBackfillDate = date;
                _nightState.CompletedSchedule = schedule;
                _nightState.LastError = null;
                _nightState.CapturedRecords = WeChat.LastBackfillMessageCount;
            }
            catch (Exception ex) { _nightState.LastError = ex.Message; throw; }
            finally { WeChat.ShouldCancelBackfill = null; _nightState.Save(); }
        }

        private static bool IsWithinBackfillWindow(DateTime now, BackfillSettings settings)
        {
            int minutes = now.Hour * 60 + now.Minute;
            if (settings.StartMinutes < settings.EndMinutes)
            {
                return minutes >= settings.StartMinutes && minutes < settings.EndMinutes;
            }
            return minutes >= settings.StartMinutes || minutes < settings.EndMinutes;
        }

        private static DateTime GetBackfillScheduleDate(DateTime now, BackfillSettings settings)
        {
            int minutes = now.Hour * 60 + now.Minute;
            if (settings.StartMinutes > settings.EndMinutes && minutes < settings.EndMinutes)
            {
                return now.Date.AddDays(-1);
            }
            return now.Date;
        }

        private static void RunBackfill(string source, bool recentOnly)
        {
            if (Interlocked.Exchange(ref _backfillRunning, 1) != 0) throw new InvalidOperationException("Backfill already running.");
            try
            {
                int viewports = recentOnly ? WeChat.BackfillRecentChats() : WeChat.BackfillVisibleChats();
                _forceUpdate = true;
                Trace.WriteLine(source + " backfill finished: " + viewports + " changed viewports.");
            }
            finally
            {
                Interlocked.Exchange(ref _backfillRunning, 0);
            }
        }

        private static BackfillSettings GetBackfillSettings()
        {
            lock (typeof(BackendProgram))
            {
                return new BackfillSettings { StartMinutes = _backfillSettings.StartMinutes, EndMinutes = _backfillSettings.EndMinutes };
            }
        }

        private static void SaveBackfillSettings(int startMinutes, int endMinutes)
        {
            BackfillSettings settings = new BackfillSettings { StartMinutes = startMinutes, EndMinutes = endMinutes };
            BackfillSettingsStore.Save(settings);
            lock (typeof(BackendProgram))
            {
                _backfillSettings = settings;
                _lastNightBackfillDate = null;
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

        private sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon _trayIcon;
            private readonly string _prefix;
            private DashboardForm _dashboard;

            public TrayApplicationContext(string prefix)
            {
                _prefix = prefix;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("Open dashboard", null, OpenDashboard);
                menu.Items.Add("Open local API", null, OpenLocalApi);
                menu.Items.Add("Quit", null, Quit);

                _trayIcon = new NotifyIcon
                {
                    Icon = LoadTrayIcon(),
                    Text = "wechat-daemon running",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _trayIcon.MouseClick += TrayIconMouseClick;
            }

            private void TrayIconMouseClick(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenDashboard(sender, e);
            }

            private static Icon LoadTrayIcon()
            {
                try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { return SystemIcons.Application; }
            }

            private void OpenLocalApi(object sender, EventArgs e)
            {
                try { Process.Start(_prefix); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "wechat-daemon", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }

            private void OpenDashboard(object sender, EventArgs e)
            {
                if (_dashboard == null || _dashboard.IsDisposed)
                {
                    _dashboard = new DashboardForm();
                }
                _dashboard.Show();
                _dashboard.BringToFront();
                _dashboard.Activate();
            }

            private void Quit(object sender, EventArgs e)
            {
                Stop();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                if (_dashboard != null && !_dashboard.IsDisposed) _dashboard.Dispose();
                ExitThread();
            }
        }

        private sealed class DashboardForm : Form
        {
            private readonly DateTimePicker _startPicker;
            private readonly DateTimePicker _endPicker;
            private readonly Label _status;
            private readonly Button _saveButton;
            private readonly Button _runNowButton;

            public DashboardForm()
            {
                Text = "wechat-daemon";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(390, 220);
                Font = SystemFonts.MessageBoxFont;

                Label title = new Label { Text = "Context backfill", Left = 22, Top = 20, Width = 340, Font = new Font(Font, FontStyle.Bold) };
                Label detail = new Label { Text = "Runs once during schedule after 5 minutes without input.", Left = 22, Top = 50, Width = 345, Height = 32 };
                Label startLabel = new Label { Text = "Starts", Left = 22, Top = 99, Width = 90 };
                Label endLabel = new Label { Text = "Ends", Left = 207, Top = 99, Width = 60 };
                _startPicker = CreateTimePicker(115, 94);
                _endPicker = CreateTimePicker(265, 94);
                _status = new Label { Left = 22, Top = 143, Width = 345, Height = 20 };
                _saveButton = new Button { Text = "Save", Left = 207, Top = 174, Width = 75 };
                _runNowButton = new Button { Text = "Run now", Left = 292, Top = 174, Width = 75 };
                _saveButton.Click += SaveSchedule;
                _runNowButton.Click += RunNow;
                Controls.AddRange(new Control[] { title, detail, startLabel, endLabel, _startPicker, _endPicker, _status, _saveButton, _runNowButton });
                LoadSchedule();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
                base.OnFormClosing(e);
            }

            private static DateTimePicker CreateTimePicker(int left, int top)
            {
                return new DateTimePicker
                {
                    Left = left,
                    Top = top,
                    Width = 82,
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = "HH:mm",
                    ShowUpDown = true
                };
            }

            private void LoadSchedule()
            {
                BackfillSettings settings = GetBackfillSettings();
                _startPicker.Value = DateTime.Today.AddMinutes(settings.StartMinutes);
                _endPicker.Value = DateTime.Today.AddMinutes(settings.EndMinutes);
                _status.Text = "Saved schedule: " + FormatTime(settings.StartMinutes) + "–" + FormatTime(settings.EndMinutes);
            }

            private void SaveSchedule(object sender, EventArgs e)
            {
                int start = _startPicker.Value.Hour * 60 + _startPicker.Value.Minute;
                int end = _endPicker.Value.Hour * 60 + _endPicker.Value.Minute;
                if (start == end)
                {
                    _status.Text = "Start and end time must differ.";
                    return;
                }
                try
                {
                    SaveBackfillSettings(start, end);
                    _status.Text = "Saved. Next run: " + FormatTime(start) + "–" + FormatTime(end);
                }
                catch (Exception ex)
                {
                    _status.Text = ex.Message;
                }
            }

            private void RunNow(object sender, EventArgs e)
            {
                _runNowButton.Enabled = false;
                _status.Text = "Backfill running.";
                Task.Factory.StartNew(delegate
                {
                    string status;
                    try { RunBackfill("manual", true); status = "Backfill finished."; }
                    catch (Exception ex) { status = "Backfill failed: " + ex.Message; }
                    BeginInvoke((Action)delegate
                    {
                        _status.Text = status;
                        _runNowButton.Enabled = true;
                    });
                });
            }

            private static string FormatTime(int minutes)
            {
                return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
            }
        }
    }
}
