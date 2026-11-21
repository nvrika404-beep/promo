// BatteryTray — заряд наушников и других Bluetooth-устройств в трее Windows.
//
// Для каждого подключённого устройства, которое сообщает Windows уровень заряда,
// в трее появляется своя иконка с числом процентов. Данные берутся из того же
// свойства DEVPKEY_Bluetooth_Battery, что и в «Параметры → Bluetooth и устройства».
//
// Собирается встроенным в Windows компилятором .NET Framework 4.x (C# 5), см. build.cmd.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("BatteryTray")]
[assembly: AssemblyDescription("Заряд Bluetooth-устройств в трее")]
[assembly: AssemblyProduct("BatteryTray")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace BatteryTray
{
    sealed class Device
    {
        public string Id;
        public string Name;
        public int Percent;
        public bool Connected;
        public bool NodePresent;
    }

    // -----------------------------------------------------------------------
    // WinAPI
    // -----------------------------------------------------------------------
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
            public DEVPROPKEY(string guid, uint p) { fmtid = new Guid(guid); pid = p; }
        }

        public const int CR_SUCCESS = 0x0;
        public const int CR_BUFFER_SMALL = 0x1A;
        public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        public const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
        public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;

        public const uint DEVPROP_TYPE_BYTE = 0x3;
        public const uint DEVPROP_TYPE_BOOLEAN = 0x11;
        public const uint DEVPROP_TYPE_STRING = 0x12;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint len, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint propertyType,
            [Out] byte[] buffer, ref uint bufferSize, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }

    // -----------------------------------------------------------------------
    // Опрос Bluetooth-устройств
    // -----------------------------------------------------------------------
    static class Bluetooth
    {
        static Native.DEVPROPKEY BatteryKey      = new Native.DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);   // DEVPKEY_Bluetooth_Battery
        static Native.DEVPROPKEY ConnectedKey    = new Native.DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);  // «подключено»
        static Native.DEVPROPKEY FriendlyNameKey = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);  // DEVPKEY_Device_FriendlyName
        static Native.DEVPROPKEY DeviceDescKey   = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);   // DEVPKEY_Device_DeviceDesc

        static readonly Regex MacRx  = new Regex("(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])");
        static readonly Regex RootRx = new Regex(@"^(BTHENUM|BTHLE)\\DEV_([0-9A-F]{12})", RegexOptions.IgnoreCase);
        static readonly Regex SuffixRx = new Regex(@"\s+(Hands-Free( AG)?( Audio)?|Avrcp Transport|Stereo|Headset)$", RegexOptions.IgnoreCase);

        public static List<Device> Query()
        {
            var ids = new List<string>();
            foreach (var e in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" }) ids.AddRange(ListIds(e));

            // «Корневые» узлы: BTHENUM\DEV_<MAC> (классический Bluetooth) и BTHLE\DEV_<MAC> (BLE)
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var m = RootRx.Match(id);
                if (m.Success) roots[m.Groups[2].Value.ToUpperInvariant()] = id;
            }

            var found = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                uint inst;
                if (Native.CM_Locate_DevNodeW(out inst, id, Native.CM_LOCATE_DEVNODE_PHANTOM) != Native.CR_SUCCESS) continue;

                uint type;
                var raw = GetProp(inst, BatteryKey, out type);
                if (raw == null || raw.Length < 1 || type != Native.DEVPROP_TYPE_BYTE) continue;
                int pct = raw[0];
                if (pct > 100) continue;

                var mac = GetMac(id);
                if (mac == null) continue;

                bool present = IsPresent(id);
                Device prev;
                // при нескольких значениях для одного устройства предпочитаем активный узел
                if (found.TryGetValue(mac, out prev) && !(present && !prev.NodePresent)) continue;

                string rootId;
                roots.TryGetValue(mac, out rootId);
                uint rootInst = 0;
                bool hasRoot = rootId != null &&
                    Native.CM_Locate_DevNodeW(out rootInst, rootId, Native.CM_LOCATE_DEVNODE_PHANTOM) == Native.CR_SUCCESS;

                string name = hasRoot ? GetName(rootInst) : null;
                if (string.IsNullOrEmpty(name))
                {
                    name = GetName(inst);
                    if (name != null) name = SuffixRx.Replace(name, "");
                }
                if (string.IsNullOrEmpty(name)) name = mac;

                bool? connected = hasRoot ? GetBool(rootInst, ConnectedKey) : null;
                if (connected == null) connected = present && (!hasRoot || IsPresent(rootId));

                found[mac] = new Device
                {
                    Id = mac,
                    Name = name,
                    Percent = pct,
                    Connected = connected.Value,
                    NodePresent = present
                };
            }

            return found.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static List<string> ListIds(string enumerator)
        {
            var result = new List<string>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint len;
                if (Native.CM_Get_Device_ID_List_SizeW(out len, enumerator, Native.CM_GETIDLIST_FILTER_ENUMERATOR) != Native.CR_SUCCESS || len == 0)
                    return result;
                var buf = new char[len];
                int cr = Native.CM_Get_Device_ID_ListW(enumerator, buf, len, Native.CM_GETIDLIST_FILTER_ENUMERATOR);
                if (cr == Native.CR_BUFFER_SMALL) continue;   // список изменился между вызовами
                if (cr != Native.CR_SUCCESS) return result;
                foreach (var s in new string(buf).Split('\0'))
                    if (s.Length > 0) result.Add(s);
                return result;
            }
            return result;
        }

        static bool IsPresent(string id)
        {
            uint inst;
            return Native.CM_Locate_DevNodeW(out inst, id, Native.CM_LOCATE_DEVNODE_NORMAL) == Native.CR_SUCCESS;
        }

        static string GetMac(string id)
        {
            var ms = MacRx.Matches(id.ToUpperInvariant());
            for (int i = ms.Count - 1; i >= 0; i--)
            {
                // хвост стандартного Bluetooth GUID — не адрес устройства
                if (ms[i].Value != "00805F9B34FB") return ms[i].Value;
            }
            return null;
        }

        static byte[] GetProp(uint inst, Native.DEVPROPKEY key, out uint type)
        {
            uint size = 0;
            int cr = Native.CM_Get_DevNode_PropertyW(inst, ref key, out type, null, ref size, 0);
            if (cr != Native.CR_BUFFER_SMALL || size == 0) return null;
            var buf = new byte[size];
            cr = Native.CM_Get_DevNode_PropertyW(inst, ref key, out type, buf, ref size, 0);
            return cr == Native.CR_SUCCESS ? buf : null;
        }

        static string GetString(uint inst, Native.DEVPROPKEY key)
        {
            uint type;
            var buf = GetProp(inst, key, out type);
            if (buf == null || type != Native.DEVPROP_TYPE_STRING) return null;
            return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        }

        static string GetName(uint inst)
        {
            var n = GetString(inst, FriendlyNameKey);
            return string.IsNullOrEmpty(n) ? GetString(inst, DeviceDescKey) : n;
        }

        static bool? GetBool(uint inst, Native.DEVPROPKEY key)
        {
            uint type;
            var buf = GetProp(inst, key, out type);
            if (buf == null || buf.Length < 1 || type != Native.DEVPROP_TYPE_BOOLEAN) return null;
            return buf[0] != 0;
        }
    }

    // -----------------------------------------------------------------------
    // Настройки: %APPDATA%\BatteryTray\settings.ini
    // -----------------------------------------------------------------------
    sealed class Settings
    {
        public int IntervalSec = 30;
        public int LowThreshold = 20;
        public HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryTray"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "IntervalSec" && int.TryParse(v, out n) && n >= 5) s.IntervalSec = n;
                    else if (k == "LowThreshold" && int.TryParse(v, out n) && n >= 0 && n <= 100) s.LowThreshold = n;
                    else if (k == "Hidden")
                        foreach (var id in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) s.Hidden.Add(id.Trim());
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "IntervalSec=" + IntervalSec,
                    "LowThreshold=" + LowThreshold,
                    "Hidden=" + string.Join(",", Hidden)
                });
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------------
    // Автозапуск: HKCU\...\Run
    // -----------------------------------------------------------------------
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BatteryTray";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue(ValueName) as string;
                    return v != null && string.Equals(v.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
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

    // -----------------------------------------------------------------------
    // Рисование иконки с числом
    // -----------------------------------------------------------------------
    static class IconPainter
    {
        public static bool LightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        static FontFamily NewFamily()
        {
            try { return new FontFamily("Segoe UI"); }
            catch { return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif); }
        }

        // Возвращает HICON; освобождать через Native.DestroyIcon.
        public static IntPtr Render(string text, Color color)
        {
            int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            using (var family = NewFamily())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                if (b.Width > 0 && b.Height > 0)
                {
                    // цифры занимают ~70% высоты и не шире иконки
                    float s = Math.Min((sz - 0.5f) / b.Width, sz * 0.72f / b.Height);
                    using (var m = new Matrix())
                    {
                        m.Translate((sz - b.Width * s) / 2f, (sz - b.Height * s) / 2f);
                        m.Scale(s, s);
                        m.Translate(-b.X, -b.Y);
                        path.Transform(m);
                    }
                }
                g.FillPath(brush, path);
                return bmp.GetHicon();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Приложение в трее
    // -----------------------------------------------------------------------
    sealed class Slot
    {
        public NotifyIcon Icon;
        public IntPtr HIcon = IntPtr.Zero;
        public string Key = "";
    }

    sealed class TrayApp : ApplicationContext
    {
        // Иконки создаются заранее в фиксированном порядке, чтобы у каждой был постоянный ID:
        // тогда Windows запоминает, что иконку вытащили из-под «^» на панель задач.
        const int MaxSlots = 6;

        readonly Settings settings = Settings.Load();
        readonly Slot[] slots = new Slot[MaxSlots];
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Control invoker = new Control();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();
        readonly Dictionary<string, bool> lowNotified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        List<Device> devices = new List<Device>();
        bool firstRun = true;
        bool busy;
        DateTime busySince;
        int generation;

        public TrayApp()
        {
            var unused = invoker.Handle;   // окно для возврата результатов в UI-поток

            for (int i = 0; i < MaxSlots; i++)
            {
                var ni = new NotifyIcon();
                ni.ContextMenuStrip = menu;
                ni.MouseClick += OnIconClick;
                slots[i] = new Slot { Icon = ni };
            }

            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                BuildMenu();
                e.Cancel = false;
            };
            BuildMenu();

            timer.Interval = settings.IntervalSec * 1000;
            timer.Tick += delegate { StartRefresh(); };

            // после сна устройствам нужно время, чтобы переподключиться
            resumeTimer.Interval = 10000;
            resumeTimer.Tick += delegate { resumeTimer.Stop(); StartRefresh(); };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            UpdateTray();       // серая иконка, пока идёт первый опрос
            StartRefresh();
            timer.Start();
        }

        // --- обновление ----------------------------------------------------

        void StartRefresh()
        {
            // опрос, висящий дольше 2 минут (сбой драйвера Bluetooth), бросаем и начинаем новый
            if (busy && (DateTime.UtcNow - busySince).TotalSeconds < 120) return;
            busy = true;
            busySince = DateTime.UtcNow;
            int gen = ++generation;

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Device> result;
                try { result = Bluetooth.Query(); }
                catch { result = new List<Device>(); }
                try { invoker.BeginInvoke((MethodInvoker)delegate { OnResult(gen, result); }); }
                catch { }
            });
        }

        void OnResult(int gen, List<Device> result)
        {
            if (gen != generation) return;
            busy = false;
            devices = result;
            UpdateTray();
            firstRun = false;
        }

        void UpdateTray()
        {
            bool light = IconPainter.LightTaskbar();
            int th = settings.LowThreshold;

            // предупреждение о низком заряде — один раз при переходе через порог
            foreach (var d in devices)
            {
                bool low = th > 0 && d.Connected && d.Percent <= th;
                bool notified;
                lowNotified.TryGetValue(d.Id, out notified);
                if (low && !notified && !firstRun)
                {
                    ShowSlotFor(0);
                    slots[0].Icon.ShowBalloonTip(8000, "Низкий заряд", d.Name + ": " + d.Percent + "%", ToolTipIcon.Warning);
                }
                if (low || firstRun) lowNotified[d.Id] = low;
                else if (d.Percent > th + 2) lowNotified[d.Id] = false;   // небольшой гистерезис
            }

            var visible = devices.Where(d => d.Connected && !settings.Hidden.Contains(d.Id)).ToList();
            for (int i = 0; i < slots.Length; i++)
            {
                if (i < visible.Count)
                {
                    var d = visible[i];
                    SetSlot(slots[i], d.Percent.ToString(), LevelColor(d.Percent, light), d.Name + ": " + d.Percent + "%");
                }
                else if (i == 0)
                {
                    // нечего показывать — оставляем одну серую иконку, чтобы было меню
                    string tip = firstRun && devices.Count == 0
                        ? "BatteryTray: поиск устройств…"
                        : "BatteryTray: нет подключённых устройств";
                    SetSlot(slots[0], "–", light ? Color.FromArgb(120, 120, 120) : Color.FromArgb(160, 160, 160), tip);
                }
                else
                {
                    slots[i].Icon.Visible = false;
                }
            }
        }

        void ShowSlotFor(int i)
        {
            if (slots[i].HIcon != IntPtr.Zero) slots[i].Icon.Visible = true;
        }

        Color LevelColor(int pct, bool light)
        {
            if (settings.LowThreshold > 0 && pct <= settings.LowThreshold)
                return light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 88);
            return light ? Color.FromArgb(25, 25, 25) : Color.White;
        }

        static void SetSlot(Slot slot, string text, Color color, string tip)
        {
            string key = text + "|" + color.ToArgb();
            if (slot.Key != key)
            {
                IntPtr h = IconPainter.Render(text, color);
                slot.Icon.Icon = Icon.FromHandle(h);
                if (slot.HIcon != IntPtr.Zero) Native.DestroyIcon(slot.HIcon);
                slot.HIcon = h;
                slot.Key = key;
            }
            if (tip.Length > 63) tip = tip.Substring(0, 62) + "…";   // ограничение Windows
            slot.Icon.Text = tip;
            slot.Icon.Visible = true;
        }

        // --- события -------------------------------------------------------

        void OnIconClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) StartRefresh();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try { invoker.BeginInvoke((MethodInvoker)delegate { resumeTimer.Stop(); resumeTimer.Start(); }); }
            catch { }
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // смена светлой/тёмной темы — перерисовать цифры
            if (e.Category != UserPreferenceCategory.General) return;
            try
            {
                invoker.BeginInvoke((MethodInvoker)delegate
                {
                    foreach (var s in slots) s.Key = "";
                    UpdateTray();
                });
            }
            catch { }
        }

        // --- меню ----------------------------------------------------------

        static ToolStripMenuItem Item(string text, EventHandler onClick, bool isChecked, object tag)
        {
            var it = new ToolStripMenuItem(text);
            it.Checked = isChecked;
            it.Tag = tag;
            if (onClick != null) it.Click += onClick;
            return it;
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem("BatteryTray") { Enabled = false });

            if (devices.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem(firstRun ? "Поиск устройств…" : "Устройства с данными о заряде не найдены") { Enabled = false });
            }
            else
            {
                foreach (var d in devices.OrderBy(x => x.Connected ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    string state = d.Connected ? d.Percent + "%" : "не подключено";
                    var it = Item(d.Name + " — " + state, OnToggleDevice, !settings.Hidden.Contains(d.Id), d.Id);
                    it.ToolTipText = "Галочка — показывать иконку этого устройства в трее";
                    if (!d.Connected) it.ForeColor = SystemColors.GrayText;
                    menu.Items.Add(it);
                }
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Обновить сейчас", delegate { StartRefresh(); }, false, null));

            var interval = new ToolStripMenuItem("Обновлять каждые");
            foreach (var opt in new[] { new KeyValuePair<int, string>(15, "15 секунд"), new KeyValuePair<int, string>(30, "30 секунд"),
                                        new KeyValuePair<int, string>(60, "1 минуту"), new KeyValuePair<int, string>(300, "5 минут") })
                interval.DropDownItems.Add(Item(opt.Value, OnInterval, settings.IntervalSec == opt.Key, opt.Key));
            menu.Items.Add(interval);

            var low = new ToolStripMenuItem("Предупреждать о низком заряде");
            foreach (var opt in new[] { new KeyValuePair<int, string>(0, "Не предупреждать"), new KeyValuePair<int, string>(10, "При 10% и ниже"),
                                        new KeyValuePair<int, string>(20, "При 20% и ниже"), new KeyValuePair<int, string>(30, "При 30% и ниже") })
                low.DropDownItems.Add(Item(opt.Value, OnThreshold, settings.LowThreshold == opt.Key, opt.Key));
            menu.Items.Add(low);

            menu.Items.Add(Item("Запускать вместе с Windows", OnAutostart, Autostart.IsEnabled(), null));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Выход", delegate { Quit(); }, false, null));
        }

        void OnToggleDevice(object sender, EventArgs e)
        {
            var id = (string)((ToolStripItem)sender).Tag;
            if (!settings.Hidden.Remove(id)) settings.Hidden.Add(id);
            settings.Save();
            UpdateTray();
        }

        void OnInterval(object sender, EventArgs e)
        {
            settings.IntervalSec = (int)((ToolStripItem)sender).Tag;
            settings.Save();
            timer.Stop();
            timer.Interval = settings.IntervalSec * 1000;
            timer.Start();
        }

        void OnThreshold(object sender, EventArgs e)
        {
            settings.LowThreshold = (int)((ToolStripItem)sender).Tag;
            settings.Save();
            lowNotified.Clear();
            foreach (var d in devices)
                lowNotified[d.Id] = settings.LowThreshold > 0 && d.Percent <= settings.LowThreshold;
            UpdateTray();
        }

        void OnAutostart(object sender, EventArgs e)
        {
            try { Autostart.Set(!Autostart.IsEnabled()); }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось изменить автозапуск:\n" + ex.Message, "BatteryTray",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Quit()
        {
            timer.Stop();
            resumeTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            foreach (var s in slots)
            {
                s.Icon.Visible = false;
                s.Icon.Dispose();
                if (s.HIcon != IntPtr.Zero) Native.DestroyIcon(s.HIcon);
            }
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\BatteryTray_6c1f0e2a", out createdNew))
            {
                if (!createdNew) return;   // уже запущено
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
