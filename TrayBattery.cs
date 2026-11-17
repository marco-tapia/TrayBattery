// TrayBattery - shows the battery level of wireless devices on the Windows taskbar,
// just to the left of the notification area ("Show hidden icons" arrow).
//
// Sources:
//   * Logitech G HUB  - local websocket API (ws://localhost:9010) used by the G HUB UI.
//   * Bluetooth       - battery level Windows itself reads from Bluetooth devices
//                       (hands-free headsets and Bluetooth LE battery service).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace TrayBattery
{
    enum DeviceKind { Mouse = 0, Keyboard = 1, Headset = 2, Gamepad = 3, Speaker = 4, Other = 5 }

    class DeviceReading
    {
        public string Key;
        public string Name;
        public DeviceKind Kind;
        public int Percent;
        public bool Charging;
        public string Source;

        public bool SameAs(DeviceReading o)
        {
            return o != null && o.Name == Name && o.Kind == Kind && o.Percent == Percent && o.Charging == Charging;
        }

        public override string ToString()
        {
            return Name + " " + Percent + "%" + (Charging ? " (charging)" : "") + " [" + Source + "]";
        }
    }

    // Thread-safe collection of the latest reading per device.
    class DeviceStore
    {
        readonly object _lock = new object();
        readonly Dictionary<string, DeviceReading> _map = new Dictionary<string, DeviceReading>();
        public event Action Changed;

        public void Set(DeviceReading r)
        {
            bool changed;
            lock (_lock)
            {
                DeviceReading old;
                changed = !_map.TryGetValue(r.Key, out old) || !old.SameAs(r);
                _map[r.Key] = r;
            }
            if (changed) Raise();
        }

        public void Remove(string key)
        {
            bool changed;
            lock (_lock) changed = _map.Remove(key);
            if (changed) Raise();
        }

        public void RemoveWhere(Func<string, bool> pred)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var k in _map.Keys.Where(pred).ToList()) { _map.Remove(k); changed = true; }
            }
            if (changed) Raise();
        }

        // Replace every reading whose key starts with prefix by the given list.
        public void ReplacePrefix(string prefix, List<DeviceReading> list)
        {
            bool changed = false;
            lock (_lock)
            {
                var keep = new HashSet<string>(list.Select(r => r.Key));
                foreach (var k in _map.Keys.Where(k => k.StartsWith(prefix) && !keep.Contains(k)).ToList())
                {
                    _map.Remove(k);
                    changed = true;
                }
                foreach (var r in list)
                {
                    DeviceReading old;
                    if (!_map.TryGetValue(r.Key, out old) || !old.SameAs(r)) { _map[r.Key] = r; changed = true; }
                }
            }
            if (changed) Raise();
        }

        public List<DeviceReading> Snapshot()
        {
            lock (_lock) return _map.Values.OrderBy(r => (int)r.Kind).ThenBy(r => r.Name).ToList();
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }

    static class Log
    {
        static readonly object L = new object();
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayBattery");
        public static string FilePath { get { return Path.Combine(Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            try
            {
                lock (L)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        public static string Err(Exception ex)
        {
            return ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message;
        }
    }

    class Settings
    {
        public List<string> Hidden { get; set; }
        public string Layout { get; set; }       // "stacked" (icon above %) or "inline" (icon beside %)
        public int OffsetX { get; set; }         // extra pixels to move the widget left
        public int LowThreshold { get; set; }    // percent at or below which the value turns red

        public Settings()
        {
            Hidden = new List<string>();
            Layout = "stacked";
            LowThreshold = 20;
        }

        static string FileName { get { return Path.Combine(Log.Dir, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FileName))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FileName));
                    if (s != null)
                    {
                        if (s.Hidden == null) s.Hidden = new List<string>();
                        if (s.Layout != "inline") s.Layout = "stacked";
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Could not read settings: " + Log.Err(ex)); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FileName, new JavaScriptSerializer().Serialize(this));
            }
            catch (Exception ex) { Log.Write("Could not save settings: " + Log.Err(ex)); }
        }
    }

    static class Kinds
    {
        public static DeviceKind FromName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, @"mouse|superlight|g502|g305|g703|g903|mx master|mx anywhere|viper|deathadder|basilisk|pulsefire")) return DeviceKind.Mouse;
            if (Regex.IsMatch(n, @"keyboard|g915|g715|g613|k\d{3}\b|mx keys")) return DeviceKind.Keyboard;
            if (Regex.IsMatch(n, @"controller|gamepad|xbox|dualsense|dualshock|8bitdo|joy-?con")) return DeviceKind.Gamepad;
            if (Regex.IsMatch(n, @"head|buds|airpods|earbud|ear |wh-|wf-|bose|jabra|arctis|hyperx|pods")) return DeviceKind.Headset;
            if (Regex.IsMatch(n, @"speaker|soundlink|boombox")) return DeviceKind.Speaker;
            return DeviceKind.Other;
        }

        public static DeviceKind FromGHubType(string type, string name)
        {
            string t = (type ?? "").ToUpperInvariant();
            if (t.Contains("MOUSE")) return DeviceKind.Mouse;
            if (t.Contains("KEYBOARD")) return DeviceKind.Keyboard;
            if (t.Contains("HEADSET") || t.Contains("HEADPHONE") || t.Contains("AUDIO") || t.Contains("EARBUD")) return DeviceKind.Headset;
            if (t.Contains("SPEAKER")) return DeviceKind.Speaker;
            if (t.Contains("GAMEPAD") || t.Contains("CONTROLLER") || t.Contains("WHEEL")) return DeviceKind.Gamepad;
            return FromName(name);
        }

        // Bluetooth classic "class of device" field.
        public static DeviceKind FromClassOfDevice(uint raw)
        {
            uint major = (raw >> 8) & 0x1F;
            uint minor = (raw >> 2) & 0x3F;
            if (major == 4) return minor == 5 ? DeviceKind.Speaker : DeviceKind.Headset;
            if (major == 5)
            {
                uint kb = (minor >> 4) & 3;
                if (kb == 2) return DeviceKind.Mouse;
                if (kb == 1 || kb == 3) return DeviceKind.Keyboard;
                uint sub = minor & 0xF;
                if (sub == 1 || sub == 2) return DeviceKind.Gamepad;
            }
            return DeviceKind.Other;
        }

        // Bluetooth LE GAP appearance value.
        public static DeviceKind FromAppearance(ushort raw)
        {
            int cat = raw >> 6, sub = raw & 0x3F;
            if (cat == 15)
            {
                if (sub == 1) return DeviceKind.Keyboard;
                if (sub == 2) return DeviceKind.Mouse;
                if (sub == 3 || sub == 4) return DeviceKind.Gamepad;
            }
            if (cat == 37) return DeviceKind.Headset;
            if (cat == 33) return DeviceKind.Speaker;
            return DeviceKind.Other;
        }

        public static DeviceKind Pick(DeviceKind structural, string name)
        {
            return structural != DeviceKind.Other ? structural : FromName(name);
        }

        // Glyphs from Segoe Fluent Icons / Segoe MDL2 Assets.
        public static string Glyph(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return "\uE962";
                case DeviceKind.Keyboard: return "\uE765";
                case DeviceKind.Headset: return "\uE7F6";
                case DeviceKind.Gamepad: return "\uE7FC";
                case DeviceKind.Speaker: return "\uE7F5";
                default: return "\uE702"; // Bluetooth
            }
        }
    }

    // ---------------------------------------------------------------- Logitech G HUB

    class GHubSource
    {
        public const string GHubExe = @"C:\Program Files\LGHUB\lghub.exe";
        public const string GHubTrayExe = @"C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe";
        readonly DeviceStore _store;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        readonly Dictionary<string, Tuple<string, DeviceKind>> _devices = new Dictionary<string, Tuple<string, DeviceKind>>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        ClientWebSocket _ws;
        volatile bool _refresh;
        string _lastSummary;
        public volatile bool Connected;
        static readonly bool Debug = Environment.GetCommandLineArgs().Contains("--debug");

        public GHubSource(DeviceStore store)
        {
            _store = store;
            _json.MaxJsonLength = int.MaxValue;
        }

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "G HUB";
            t.Start();
        }

        public void RefreshNow() { _refresh = true; _wake.Set(); }

        static void EnsureTrayRunning()
        {
            try
            {
                if (!File.Exists(GHubTrayExe)) return;
                if (Process.GetProcessesByName("lghub_agent").Length > 0 ||
                    Process.GetProcessesByName("lghub_system_tray").Length > 0) return;
                Log.Write("Starting G HUB in system tray (--minimized)");
                Process.Start(new ProcessStartInfo
                {
                    FileName = GHubTrayExe,
                    Arguments = "--minimized",
                    WorkingDirectory = Path.GetDirectoryName(GHubTrayExe),
                    UseShellExecute = false
                });
            }
            catch (Exception ex) { Log.Write("Could not start G HUB tray: " + Log.Err(ex)); }
        }

        void Loop()
        {
            EnsureTrayRunning();
            bool reported = false;
            while (true)
            {
                try
                {
                    if (_refresh) { _refresh = false; EnsureTrayRunning(); }
                    RunSession();
                    reported = false;
                }
                catch (Exception ex)
                {
                    if (!reported) { Log.Write("G HUB not reachable (" + Log.Err(ex) + "); retrying every 10 s"); reported = true; }
                }
                Connected = false;
                _devices.Clear();
                _store.RemoveWhere(k => k.StartsWith("ghub:"));
                _wake.WaitOne(10000);
            }
        }

        void RunSession()
        {
            using (var ws = new ClientWebSocket())
            {
                _ws = ws;
                ws.Options.AddSubProtocol("json");
                try { ws.Options.SetRequestHeader("Origin", "file://"); }
                catch (Exception ex) { Log.Write("Origin header: " + Log.Err(ex)); }
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                using (var cts = new CancellationTokenSource(5000))
                    ws.ConnectAsync(new Uri("ws://localhost:9010"), cts.Token).Wait();

                Connected = true;
                Log.Write("Connected to G HUB");
                Send("SUBSCRIBE", "/devices/state/changed");
                Send("SUBSCRIBE", "/battery/state/changed");
                Send("GET", "/devices/list");
                DateTime lastList = DateTime.UtcNow;

                var buf = new byte[64 * 1024];
                var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open)
                {
                    var rt = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    while (!rt.Wait(2000))
                    {
                        if (_refresh || (DateTime.UtcNow - lastList).TotalSeconds >= 60)
                        {
                            _refresh = false;
                            lastList = DateTime.UtcNow;
                            Send("GET", "/devices/list");
                        }
                    }
                    var r = rt.Result;
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Write("G HUB closed the connection");
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    if (Debug) Log.Write("G HUB <- " + Trim(text, 4000));
                    try { Handle(text); }
                    catch (Exception ex) { Log.Write("G HUB message error: " + Log.Err(ex) + " in " + Trim(text, 300)); }
                }
            }
        }

        void Send(string verb, string path)
        {
            string msg = "{\"msgId\":\"\",\"verb\":\"" + verb + "\",\"path\":\"" + path + "\"}";
            var bytes = Encoding.UTF8.GetBytes(msg);
            _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
        }

        void Handle(string text)
        {
            var msg = _json.DeserializeObject(text) as Dictionary<string, object>;
            if (msg == null) return;
            string path = S(msg, "path") ?? "";
            var payload = D(msg, "payload");
            var result = D(msg, "result");
            string code = result != null ? S(result, "code") : null;
            bool ok = code == null || code == "SUCCESS";

            if (path == "/devices/list")
            {
                if (!ok || payload == null) { Log.Write("G HUB device list failed: " + Trim(text, 500)); return; }
                var infos = payload.ContainsKey("deviceInfos") ? payload["deviceInfos"] as object[] : null;
                var seen = new HashSet<string>();
                _devices.Clear();
                var names = new List<string>();
                if (infos != null)
                {
                    foreach (var o in infos)
                    {
                        var info = o as Dictionary<string, object>;
                        if (info == null) continue;
                        string id = S(info, "id");
                        if (string.IsNullOrEmpty(id)) continue;
                        var caps = D(info, "capabilities");
                        string name = First(S(info, "extendedDisplayName"), S(info, "displayName"), S(info, "name"), id);
                        string state = S(info, "state") ?? "";
                        names.Add(name + " [" + state + (caps != null && B(caps, "hasBatteryStatus") ? ", battery" : "") + "]");
                        if (caps == null || !B(caps, "hasBatteryStatus")) continue;
                        if (state == "NOT_CONNECTED" || state == "DISCONNECTED") continue;
                        _devices[id] = Tuple.Create(name, Kinds.FromGHubType(S(info, "deviceType"), name));
                        seen.Add(id);
                        Send("GET", "/battery/" + id + "/state");
                    }
                }
                string summary = names.Count == 0 ? "(none)" : string.Join(", ", names);
                if (summary != _lastSummary) Log.Write("G HUB devices: " + summary);
                _lastSummary = summary;
                _store.RemoveWhere(k => k.StartsWith("ghub:") && !seen.Contains(k.Substring(5)));
                return;
            }

            if (path == "/devices/state/changed")
            {
                Send("GET", "/devices/list");
                return;
            }

            var m = Regex.Match(path, @"^/battery/([^/]+)/state$");
            if (path == "/battery/state/changed" || m.Success)
            {
                string id = payload != null ? S(payload, "deviceId") : null;
                if (string.IsNullOrEmpty(id) && m.Success) id = m.Groups[1].Value;
                if (string.IsNullOrEmpty(id)) return;
                // Errors happen transiently (e.g. while G HUB starts); keep the last value.
                // Devices that really go away are removed via the device list's state.
                if (!ok || payload == null || !payload.ContainsKey("percentage") || payload["percentage"] == null) return;
                if (!_devices.ContainsKey(id)) return; // not connected / unknown device
                int pct = (int)Math.Round(Convert.ToDouble(payload["percentage"]));
                pct = Math.Max(0, Math.Min(100, pct));
                Tuple<string, DeviceKind> dev;
                if (!_devices.TryGetValue(id, out dev)) dev = Tuple.Create(id, DeviceKind.Other);
                _store.Set(new DeviceReading
                {
                    Key = "ghub:" + id,
                    Name = dev.Item1,
                    Kind = dev.Item2,
                    Percent = pct,
                    Charging = B(payload, "charging"),
                    Source = "G HUB"
                });
            }
        }

        static string First(params string[] values)
        {
            foreach (var v in values) if (!string.IsNullOrEmpty(v)) return v;
            return null;
        }
        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }
        static Dictionary<string, object> D(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
        static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }
        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "..."; }
    }

    // ---------------------------------------------------------------- Bluetooth

    static class WinRt
    {
        // Blocks a background thread until a Windows Runtime async operation finishes.
        public static T Wait<T>(this Windows.Foundation.IAsyncOperation<T> op)
        {
            var sw = Stopwatch.StartNew();
            while (op.Status == Windows.Foundation.AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > 15000) { op.Cancel(); throw new TimeoutException("Windows Runtime call timed out"); }
                Thread.Sleep(5);
            }
            if (op.Status != Windows.Foundation.AsyncStatus.Completed)
                throw op.ErrorCode ?? new InvalidOperationException("Windows Runtime call " + op.Status);
            return op.GetResults();
        }
    }

    class BluetoothSource
    {
        // DEVPKEY_Bluetooth_Battery: Windows stores the level it reads from hands-free (HFP)
        // headsets and from the Bluetooth LE battery service here.
        static readonly Guid BatteryFmt = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
        readonly DeviceStore _store;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public BluetoothSource(DeviceStore store) { _store = store; }

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "Bluetooth";
            t.Start();
        }

        public void RefreshNow() { _wake.Set(); }

        void Loop()
        {
            while (true)
            {
                try { Poll(); }
                catch (Exception ex) { Log.Write("Bluetooth poll failed: " + Log.Err(ex)); }
                _wake.WaitOne(30000);
            }
        }

        void Poll()
        {
            // Only connected devices: Windows keeps the last level of disconnected devices around.
            var connected = new Dictionary<string, Tuple<string, DeviceKind>>();

            var classic = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).Wait();
            foreach (var info in classic)
            {
                try
                {
                    using (var d = BluetoothDevice.FromIdAsync(info.Id).Wait())
                    {
                        if (d == null || d.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                        connected[d.BluetoothAddress.ToString("X12")] = Tuple.Create(d.Name,
                            Kinds.Pick(Kinds.FromClassOfDevice(d.ClassOfDevice.RawValue), d.Name));
                    }
                }
                catch (Exception ex) { Log.Write("Bluetooth device " + info.Name + ": " + Log.Err(ex)); }
            }

            var le = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).Wait();
            foreach (var info in le)
            {
                try
                {
                    using (var d = BluetoothLEDevice.FromIdAsync(info.Id).Wait())
                    {
                        if (d == null || d.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                        ushort appearance = 0;
                        try { appearance = d.Appearance.RawValue; } catch { }
                        connected[d.BluetoothAddress.ToString("X12")] = Tuple.Create(d.Name,
                            Kinds.Pick(Kinds.FromAppearance(appearance), d.Name));
                    }
                }
                catch (Exception ex) { Log.Write("Bluetooth LE device " + info.Name + ": " + Log.Err(ex)); }
            }

            var list = new List<DeviceReading>();
            if (connected.Count > 0)
            {
                var levels = ReadPnpBatteryLevels();
                foreach (var kv in connected)
                {
                    int best = -1;
                    foreach (var lv in levels)
                        if (lv.Key.Contains(kv.Key)) best = Math.Max(best, lv.Value);
                    if (best < 0) continue;
                    list.Add(new DeviceReading
                    {
                        Key = "bt:" + kv.Key,
                        Name = kv.Value.Item1,
                        Kind = kv.Value.Item2,
                        Percent = best,
                        Source = "Bluetooth"
                    });
                }
            }
            _store.ReplacePrefix("bt:", list);
        }

        // Returns (device instance id, battery %) for every present device that has the property.
        static List<KeyValuePair<string, int>> ReadPnpBatteryLevels()
        {
            var res = new List<KeyValuePair<string, int>>();
            IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
            try
            {
                var data = new Native.SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                var key = new Native.DEVPROPKEY { fmtid = BatteryFmt, pid = 2 };
                var buf = new byte[16];
                var sb = new StringBuilder(512);
                for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    uint type;
                    int req;
                    if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, buf.Length, out req, 0)) continue;
                    if (type != 3 /* DEVPROP_TYPE_BYTE */ || req < 1) continue;
                    if (!Native.SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out req)) continue;
                    res.Add(new KeyValuePair<string, int>(sb.ToString().ToUpperInvariant(), Math.Min(100, (int)buf[0])));
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return res;
        }
    }

    // ---------------------------------------------------------------- Startup

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TrayBattery";

        public static bool IsEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue(ValueName) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    // ---------------------------------------------------------------- Taskbar widget

    class Widget : Form
    {
        class Cell
        {
            public DeviceReading R;
            public RectangleF Bounds;
        }

        readonly DeviceStore _store;
        readonly Settings _settings;
        readonly BluetoothSource _bt;
        readonly GHubSource _ghub;
        readonly System.Windows.Forms.Timer _timer;
        readonly ToolTip _tip;
        readonly FontFamily _iconFamily, _textFamily;
        readonly Native.WinEventDelegate _winEvent;
        IntPtr _hook;

        Bitmap _bmp;
        List<Cell> _cells = new List<Cell>();
        Rectangle _lastBounds;
        float _lastScale;
        int _lastHeight;
        bool _dirty = true, _shown, _menuOpen;
        int _hover = -1;
        ContextMenuStrip _menu;

        public Widget(DeviceStore store, Settings settings, BluetoothSource bt, GHubSource ghub)
        {
            _store = store;
            _settings = settings;
            _bt = bt;
            _ghub = ghub;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "TrayBattery";
            Size = new Size(1, 1);
            Location = new Point(-32000, -32000);

            _iconFamily = FindFamily("Segoe Fluent Icons", "Segoe MDL2 Assets");
            _textFamily = FindFamily("Segoe UI Variable Text", "Segoe UI");

            _tip = new ToolTip { ShowAlways = true, InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 15000 };

            _store.Changed += () =>
            {
                _dirty = true;
                try { if (IsHandleCreated) BeginInvoke(new Action(Tick)); } catch { }
            };
            SystemEvents.UserPreferenceChanged += (s, e) => { _dirty = true; };

            _timer = new System.Windows.Forms.Timer { Interval = 400 };
            _timer.Tick += (s, e) => Tick();

            // Re-check z-order immediately whenever the foreground window changes
            // (clicking the taskbar raises it above us).
            _winEvent = (h, ev, hwnd, idObj, idChild, th, time) => Tick();
        }

        static FontFamily FindFamily(params string[] names)
        {
            using (var fonts = new InstalledFontCollection())
            {
                foreach (var n in names)
                    foreach (var f in fonts.Families)
                        if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return new FontFamily(n);
            }
            return FontFamily.GenericSansSerif;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /* LAYERED */ | 0x80 /* TOOLWINDOW */ | 0x8 /* TOPMOST */ | 0x8000000 /* NOACTIVATE */;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _hook = Native.SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _winEvent, 0, 0, 0 /* OUTOFCONTEXT */);
            _timer.Start();
            BeginInvoke(new Action(Tick));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
            base.OnFormClosed(e);
        }

        protected override void SetVisibleCore(bool value)
        {
            // Visibility is driven by Tick(); never let WinForms show an empty window.
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3 /* MA_NOACTIVATE */; return; }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------ positioning

        void Tick()
        {
            if (IsDisposed) return;
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT tbr;
            bool show = tb != IntPtr.Zero && Native.IsWindowVisible(tb) && Native.GetWindowRect(tb, out tbr);
            if (!show) { HideWidget(); return; }
            Native.GetWindowRect(tb, out tbr);

            Rectangle scr = Screen.FromHandle(tb).Bounds;
            bool bottom = tbr.Top > scr.Top + scr.Height / 2;
            if (!bottom || tbr.Top >= scr.Bottom - 4 || tbr.Height < 16 || (!_menuOpen && FullscreenAppActive(scr)))
            {
                HideWidget();
                return;
            }

            float scale = Native.GetDpiForWindow(tb) / 96f;
            if (scale <= 0) scale = 1;
            int height = tbr.Height;

            int trayLeft = tbr.Right - (int)(300 * scale);
            IntPtr tn = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT tnr;
            if (tn != IntPtr.Zero && Native.GetWindowRect(tn, out tnr) && tnr.Right > tnr.Left) trayLeft = tnr.Left;

            if (_dirty || _bmp == null || scale != _lastScale || height != _lastHeight)
            {
                Render(scale, height);
                _lastScale = scale;
                _lastHeight = height;
                _lastBounds = Rectangle.Empty;
                _dirty = false;
            }

            int x = trayLeft - _bmp.Width - (int)Math.Round(2 * scale) - _settings.OffsetX;
            var bounds = new Rectangle(x, tbr.Top, _bmp.Width, _bmp.Height);
            if (bounds != _lastBounds)
            {
                Push(_bmp, bounds);
                _lastBounds = bounds;
            }

            if (!_shown)
            {
                Native.ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
                _shown = true;
            }
            EnsureAbove(tb);
        }

        void HideWidget()
        {
            if (_shown && IsHandleCreated)
            {
                Native.ShowWindow(Handle, 0 /* SW_HIDE */);
                _shown = false;
            }
        }

        void EnsureAbove(IntPtr taskbar)
        {
            // Walk the windows above ours; if the taskbar is among them, bring ourselves back on top.
            IntPtr h = Native.GetWindow(Handle, 3 /* GW_HWNDPREV */);
            for (int guard = 0; h != IntPtr.Zero && guard < 1000; guard++)
            {
                if (h == taskbar)
                {
                    Native.SetWindowPos(Handle, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0,
                        0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
                    return;
                }
                h = Native.GetWindow(h, 3);
            }
        }

        bool FullscreenAppActive(Rectangle scr)
        {
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && (state == 3 /* D3D full screen */ || state == 4 /* presentation */))
                return true;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            var cls = new StringBuilder(64);
            Native.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            return r.Left <= scr.Left && r.Top <= scr.Top && r.Right >= scr.Right && r.Bottom >= scr.Bottom;
        }

        // ------------------------------------------------------------ drawing

        List<DeviceReading> VisibleItems()
        {
            return _store.Snapshot().Where(r => !_settings.Hidden.Contains(r.Key)).ToList();
        }

        static bool LightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        GraphicsPath MakePath(string text, FontFamily fam, float em)
        {
            var p = new GraphicsPath();
            p.AddString(text, fam, (int)FontStyle.Regular, em, new PointF(0, 0), StringFormat.GenericTypographic);
            return p;
        }

        static void Shift(GraphicsPath p, float dx, float dy)
        {
            using (var m = new Matrix()) { m.Translate(dx, dy); p.Transform(m); }
        }

        void Render(float s, int height)
        {
            var items = VisibleItems();
            bool light = LightTaskbar();
            Color fg = light ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 255, 255, 255);
            Color low = light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 120, 120);
            Color chg = light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95);
            Color hoverBg = light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255);
            bool stacked = _settings.Layout != "inline";

            float iconEm = (stacked ? 15f : 16f) * s;
            float textEm = (stacked ? 11.5f : 12.5f) * s;
            float boltEm = 10f * s;
            float pad = (stacked ? 4.5f : 6f) * s;

            // Reference metrics for the digits so every value sits on the same baseline.
            RectangleF digit;
            using (var p = MakePath("0", _textFamily, textEm)) digit = p.GetBounds();

            var layouts = new List<Tuple<DeviceReading, GraphicsPath, GraphicsPath, GraphicsPath, float>>();
            float total = 0;
            if (items.Count == 0)
            {
                var icon = MakePath("\uE850", _iconFamily, iconEm); // empty battery as a placeholder
                float w = icon.GetBounds().Width + 2 * pad;
                layouts.Add(Tuple.Create<DeviceReading, GraphicsPath, GraphicsPath, GraphicsPath, float>(null, icon, null, null, w));
                total = w;
            }
            foreach (var r in items)
            {
                var icon = MakePath(Kinds.Glyph(r.Kind), _iconFamily, iconEm);
                var bolt = r.Charging ? MakePath("\uE945", _iconFamily, boltEm) : null;
                var text = MakePath(r.Percent + "%", _textFamily, textEm);
                float iw = icon.GetBounds().Width, bw = bolt != null ? bolt.GetBounds().Width : 0, tw = text.GetBounds().Width;
                float content = stacked
                    ? Math.Max(iw + (bolt != null ? bw + 1.5f * s : 0), tw)
                    : iw + 5f * s + (bolt != null ? bw + 2f * s : 0) + tw;
                float w = (float)Math.Ceiling(content + 2 * pad);
                layouts.Add(Tuple.Create(r, icon, bolt, text, w));
                total += w;
            }

            var bmp = new Bitmap(Math.Max(1, (int)Math.Ceiling(total)), height, PixelFormat.Format32bppArgb);
            var cells = new List<Cell>();
            using (var g = Graphics.FromImage(bmp))
            {
                // Alpha 1 keeps the whole area clickable while looking fully transparent.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float x = 0;
                float iconRowH = 0;
                foreach (var l in layouts) iconRowH = Math.Max(iconRowH, l.Item2.GetBounds().Height);
                float gapRows = 3f * s;
                float blockTop = (height - (iconRowH + gapRows + digit.Height)) / 2f;

                for (int i = 0; i < layouts.Count; i++)
                {
                    var l = layouts[i];
                    var r = l.Item1;
                    float w = l.Item5;
                    var cellRect = new RectangleF(x, 0, w, height);
                    cells.Add(new Cell { R = r, Bounds = cellRect });

                    if (i == _hover)
                    {
                        float inset = 4f * s;
                        using (var path = Rounded(new RectangleF(x + 1, inset, w - 2, height - 2 * inset), 4f * s))
                        using (var b = new SolidBrush(hoverBg))
                            g.FillPath(b, path);
                    }

                    bool isLow = r != null && !r.Charging && r.Percent <= _settings.LowThreshold;
                    Color main = r == null ? Color.FromArgb(150, fg) : (isLow ? low : fg);
                    var icon = l.Item2; var bolt = l.Item3; var text = l.Item4;
                    var ib = icon.GetBounds();
                    var bb = bolt != null ? bolt.GetBounds() : RectangleF.Empty;
                    float cx = x + w / 2f;

                    if (r == null || stacked)
                    {
                        float rowCenter = r == null ? height / 2f : blockTop + iconRowH / 2f;
                        float rowW = ib.Width + (bolt != null ? bb.Width + 1.5f * s : 0);
                        float left = cx - rowW / 2f;
                        Shift(icon, left - ib.Left, rowCenter - (ib.Top + ib.Height / 2f));
                        if (bolt != null) Shift(bolt, left + ib.Width + 1.5f * s - bb.Left, rowCenter - (bb.Top + bb.Height / 2f));
                        if (text != null)
                        {
                            var tb = text.GetBounds();
                            Shift(text, cx - (tb.Left + tb.Width / 2f), blockTop + iconRowH + gapRows - digit.Top);
                        }
                    }
                    else
                    {
                        float mid = height / 2f;
                        var tb = text.GetBounds();
                        float left = x + pad;
                        Shift(icon, left - ib.Left, mid - (ib.Top + ib.Height / 2f));
                        left += ib.Width + 5f * s;
                        if (bolt != null)
                        {
                            Shift(bolt, left - bb.Left, mid - (bb.Top + bb.Height / 2f));
                            left += bb.Width + 2f * s;
                        }
                        Shift(text, left - tb.Left, mid - (digit.Top + digit.Height / 2f));
                    }

                    using (var b = new SolidBrush(main))
                    {
                        g.FillPath(b, icon);
                        if (text != null) g.FillPath(b, text);
                    }
                    if (bolt != null) using (var b = new SolidBrush(chg)) g.FillPath(b, bolt);

                    icon.Dispose();
                    if (bolt != null) bolt.Dispose();
                    if (text != null) text.Dispose();
                    x += w;
                }
            }

            if (_bmp != null) _bmp.Dispose();
            _bmp = bmp;
            _cells = cells;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Push(Bitmap bmp, Rectangle bounds)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(bounds.X, bounds.Y);
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBmp);
                }
                Native.DeleteDC(memDc);
            }
        }

        // ------------------------------------------------------------ mouse

        int HitTest(Point p)
        {
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i].Bounds.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h == _hover) return;
            _hover = h;
            _dirty = true;
            Tick();
            string tipText = "";
            if (h >= 0 && h < _cells.Count)
            {
                var r = _cells[h].R;
                tipText = r == null
                    ? "No wireless devices are reporting battery right now.\nRight-click for options."
                    : r.Name + "\n" + r.Percent + "%" + (r.Charging ? " - charging" : "") + "  (" + r.Source + ")";
            }
            _tip.SetToolTip(this, tipText);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; _dirty = true; Tick(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            var r = h >= 0 && h < _cells.Count ? _cells[h].R : null;
            if (r != null && r.Source == "G HUB" && File.Exists(GHubSource.GHubExe)) Launch(GHubSource.GHubExe);
            else Launch("ms-settings:bluetooth");
        }

        static void Launch(string target)
        {
            try { Process.Start(target); }
            catch (Exception ex) { Log.Write("Could not open " + target + ": " + Log.Err(ex)); }
        }

        void ShowMenu()
        {
            if (_menu != null) _menu.Dispose();
            _menu = new ContextMenuStrip();
            var all = _store.Snapshot();

            if (all.Count == 0)
                _menu.Items.Add(new ToolStripMenuItem("No devices reporting battery") { Enabled = false });
            foreach (var r in all)
            {
                var key = r.Key;
                var item = new ToolStripMenuItem(r.Name + "   " + r.Percent + "%" + (r.Charging ? " (charging)" : "") + "   - " + r.Source);
                item.Checked = !_settings.Hidden.Contains(key);
                item.ToolTipText = "Show or hide this device on the taskbar";
                item.Click += (s, e) =>
                {
                    if (_settings.Hidden.Contains(key)) _settings.Hidden.Remove(key); else _settings.Hidden.Add(key);
                    _settings.Save();
                    _dirty = true;
                    Tick();
                };
                _menu.Items.Add(item);
            }
            _menu.Items.Add(new ToolStripSeparator());

            var layout = new ToolStripMenuItem("Layout");
            var stacked = new ToolStripMenuItem("Icon above percentage") { Checked = _settings.Layout != "inline" };
            var inline = new ToolStripMenuItem("Icon beside percentage") { Checked = _settings.Layout == "inline" };
            stacked.Click += (s, e) => { _settings.Layout = "stacked"; _settings.Save(); _dirty = true; Tick(); };
            inline.Click += (s, e) => { _settings.Layout = "inline"; _settings.Save(); _dirty = true; Tick(); };
            layout.DropDownItems.Add(stacked);
            layout.DropDownItems.Add(inline);
            _menu.Items.Add(layout);

            var startup = new ToolStripMenuItem("Start with Windows") { Checked = Startup.IsEnabled };
            startup.Click += (s, e) =>
            {
                try { Startup.Set(!Startup.IsEnabled); }
                catch (Exception ex) { Log.Write("Startup toggle failed: " + Log.Err(ex)); }
            };
            _menu.Items.Add(startup);

            var refresh = new ToolStripMenuItem("Refresh now");
            refresh.Click += (s, e) => { _bt.RefreshNow(); _ghub.RefreshNow(); };
            _menu.Items.Add(refresh);

            _menu.Items.Add(new ToolStripSeparator());
            if (File.Exists(GHubSource.GHubExe))
            {
                var gh = new ToolStripMenuItem("Open Logitech G HUB" + (_ghub.Connected ? "" : "  (not running)"));
                gh.Click += (s, e) => Launch(GHubSource.GHubExe);
                _menu.Items.Add(gh);
            }
            var bts = new ToolStripMenuItem("Bluetooth settings");
            bts.Click += (s, e) => Launch("ms-settings:bluetooth");
            _menu.Items.Add(bts);
            var folder = new ToolStripMenuItem("Open settings and log folder");
            folder.Click += (s, e) => { Directory.CreateDirectory(Log.Dir); Launch(Log.Dir); };
            _menu.Items.Add(folder);

            _menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => { _timer.Stop(); HideWidget(); Application.Exit(); };
            _menu.Items.Add(exit);

            _menuOpen = true;
            _menu.Closed += (s, e) => { _menuOpen = false; };
            Native.SetForegroundWindow(Handle);
            _menu.Show(Cursor.Position);
        }
    }

    // ---------------------------------------------------------------- Win32

    static class Native
    {
        public const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
            out uint type, byte[] buffer, int size, out int required, uint flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\TrayBattery.SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Write("TrayBattery started");

                var store = new DeviceStore();
                store.Changed += () =>
                    Log.Write("Readings: " + string.Join(", ", store.Snapshot().Select(r => r.ToString())));
                var settings = Settings.Load();
                var bt = new BluetoothSource(store);
                var ghub = new GHubSource(store);
                if (Environment.GetCommandLineArgs().Contains("--demo"))
                {
                    store.Set(new DeviceReading { Key = "demo:1", Name = "Demo mouse", Kind = DeviceKind.Mouse, Percent = 15, Source = "Demo" });
                    store.Set(new DeviceReading { Key = "demo:2", Name = "Demo headset", Kind = DeviceKind.Headset, Percent = 62, Charging = true, Source = "Demo" });
                    store.Set(new DeviceReading { Key = "demo:3", Name = "Demo controller", Kind = DeviceKind.Gamepad, Percent = 100, Source = "Demo" });
                }
                else
                {
                    bt.Start();
                    ghub.Start();
                }
                Application.Run(new Widget(store, settings, bt, ghub));
            }
        }
    }
}
