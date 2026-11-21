// Заряд гарнитур Razer Barracuda, подключённых через 2.4 ГГц USB-приёмник (HyperSpeed).
//
// Windows сама заряд таких гарнитур не знает: приёмник отдаёт его только по собственному
// протоколу поверх HID. Здесь реализованы три известных варианта:
//
//   * Airoha-туннель (Barracuda X 2022, приёмники 1532:0550 / 054E — мост YS-Tech, report 02 с CRC;
//     1532:0552 — мост Macronix, report 01 без CRC). Приёмник пересылает команды на чип Airoha в
//     гарнитуре: включаем «remote mode» (группа 0E, E1 01), читаем customer data 0x21 (заряд 0–100)
//     и 0x2A (кабель 0/1), выключаем remote mode (E1 00).
//   * Feature-отчёт 0xFF (Barracuda X 2021, 1532:0536): SET [FF 0A 00 FD 04 12 F1 02 05], затем GET;
//     байт 9 — состояние (05 — заряжается), байт 14 — процент.
//   * Report 02 с XOR-суммой (Barracuda X Chroma, 1532:0574): запрос свойства 0x21 / 0x2A,
//     значение в байте 13 ответа.
//
// Протоколы разобраны сообществом: github.com/nightdevx/barracuda-x-battery,
// github.com/mehdibouchami/razer-barracuda-x-battery, github.com/leag/razer-barracuda,
// github.com/GigaVibeSlopper67/Barracuda_Pro_and_X_Helpers, github.com/itayavra/batterywatch.
// Все команды только читают состояние; remote mode всегда выключается обратно.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray
{
    // -----------------------------------------------------------------------
    // HID: перечисление коллекций и ввод-вывод
    // -----------------------------------------------------------------------
    sealed class HidInfo
    {
        public string Path;
        public ushort Vid, Pid;
        public int Interface = -1;
        public ushort UsagePage, Usage;
        public int InLen, OutLen, FeatLen;

        public override string ToString()
        {
            return string.Format("{0:X4}:{1:X4} MI {2} usage {3:X4}:{4:X4} in {5} out {6} feat {7}",
                Vid, Pid, Interface < 0 ? "-" : Interface.ToString("X2"), UsagePage, Usage, InLen, OutLen, FeatLen);
        }
    }

    static class Hid
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID, ProductID, VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        public static SafeFileHandle Open(string path, bool readWrite, uint flags)
        {
            return CreateFileW(path, readWrite ? (GENERIC_READ | GENERIC_WRITE) : 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        }

        static List<string> InterfacePaths()
        {
            var paths = new List<string>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    uint required;
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize структуры SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out required, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        public static List<HidInfo> Enumerate(ushort vid)
        {
            var result = new List<HidInfo>();
            string vidTag = "vid_" + vid.ToString("x4");
            foreach (var path in InterfacePaths())
            {
                if (path.IndexOf(vidTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (var h = Open(path, false, 0))
                {
                    if (h.IsInvalid) continue;
                    var attr = new HIDD_ATTRIBUTES();
                    attr.Size = (uint)Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != vid) continue;
                    IntPtr pp;
                    if (!HidD_GetPreparsedData(h, out pp)) continue;
                    try
                    {
                        HIDP_CAPS caps;
                        if (HidP_GetCaps(pp, out caps) != 0x00110000) continue;   // HIDP_STATUS_SUCCESS
                        var info = new HidInfo
                        {
                            Path = path,
                            Vid = attr.VendorID,
                            Pid = attr.ProductID,
                            UsagePage = caps.UsagePage,
                            Usage = caps.Usage,
                            InLen = caps.InputReportByteLength,
                            OutLen = caps.OutputReportByteLength,
                            FeatLen = caps.FeatureReportByteLength
                        };
                        int mi = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
                        int n;
                        if (mi >= 0 && path.Length >= mi + 6 &&
                            int.TryParse(path.Substring(mi + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out n))
                            info.Interface = n;
                        result.Add(info);
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
            }
            return result;
        }
    }

    sealed class HidDevice : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct OVERLAPPED
        {
            public IntPtr Internal, InternalHigh;
            public uint Offset, OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint size, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, uint size, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll")]
        static extern bool CancelIo(SafeFileHandle h);
        [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll")] static extern bool HidD_FlushQueue(SafeFileHandle h);

        const int ERROR_IO_PENDING = 997;

        public readonly HidInfo Info;
        readonly SafeFileHandle handle;
        readonly bool overlapped;

        HidDevice(HidInfo info, SafeFileHandle h, bool overlapped)
        {
            Info = info;
            handle = h;
            this.overlapped = overlapped;
        }

        // Чтение/запись. Если коллекцию нельзя открыть на запись, featureFallback открывает её без прав —
        // для feature-отчётов этого достаточно.
        public static HidDevice Open(HidInfo info, bool featureFallback)
        {
            var h = Hid.Open(info.Path, true, Hid.FILE_FLAG_OVERLAPPED);
            if (!h.IsInvalid) return new HidDevice(info, h, true);
            h.Dispose();
            if (!featureFallback) return null;
            h = Hid.Open(info.Path, false, 0);
            if (!h.IsInvalid) return new HidDevice(info, h, false);
            h.Dispose();
            return null;
        }

        public void Dispose() { handle.Dispose(); }

        public void Flush() { HidD_FlushQueue(handle); }

        // Записывает output-отчёт (report[0] — report ID), дополняя нулями до длины отчёта.
        public bool Write(byte[] report, int timeoutMs)
        {
            if (!overlapped || Info.OutLen == 0) return false;
            var buf = new byte[Info.OutLen];
            Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
            return Io(buf, true, timeoutMs) == buf.Length;
        }

        // Читает один input-отчёт; null — таймаут или ошибка.
        public byte[] Read(int timeoutMs)
        {
            if (!overlapped || Info.InLen == 0 || timeoutMs <= 0) return null;
            var buf = new byte[Info.InLen];
            int n = Io(buf, false, timeoutMs);
            if (n <= 0) return null;
            if (n < buf.Length) Array.Resize(ref buf, n);
            return buf;
        }

        public byte[] GetInputReport(byte reportId)
        {
            if (Info.InLen == 0) return null;
            var buf = new byte[Info.InLen];
            buf[0] = reportId;
            return HidD_GetInputReport(handle, buf, buf.Length) ? buf : null;
        }

        public bool SetFeature(byte[] report)
        {
            if (Info.FeatLen == 0) return false;
            var buf = new byte[Info.FeatLen];
            Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
            return HidD_SetFeature(handle, buf, buf.Length);
        }

        public byte[] GetFeature(byte reportId)
        {
            if (Info.FeatLen == 0) return null;
            var buf = new byte[Info.FeatLen];
            buf[0] = reportId;
            return HidD_GetFeature(handle, buf, buf.Length) ? buf : null;
        }

        int Io(byte[] data, bool write, int timeoutMs)
        {
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OVERLAPPED)));
            var ev = new ManualResetEvent(false);
            try
            {
                var o = new OVERLAPPED();
                o.hEvent = ev.SafeWaitHandle.DangerousGetHandle();
                Marshal.StructureToPtr(o, ov, false);
                if (write) Marshal.Copy(data, 0, buf, data.Length);

                bool started = write
                    ? WriteFile(handle, buf, (uint)data.Length, IntPtr.Zero, ov)
                    : ReadFile(handle, buf, (uint)data.Length, IntPtr.Zero, ov);
                if (!started && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return -1;

                uint n;
                bool done;
                if (ev.WaitOne(timeoutMs)) done = GetOverlappedResult(handle, ov, out n, false);
                else
                {
                    CancelIo(handle);
                    done = GetOverlappedResult(handle, ov, out n, true);   // операция могла успеть завершиться
                }
                if (!done) return -1;
                if (!write) Marshal.Copy(buf, data, 0, (int)Math.Min(n, (uint)data.Length));
                return (int)n;
            }
            finally
            {
                ev.Dispose();
                Marshal.FreeHGlobal(ov);
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Протоколы Razer
    // -----------------------------------------------------------------------
    enum RazerProto { None, YsTech, Macronix, Feature, Chroma }

    sealed class RazerReading
    {
        public bool HeadsetAnswered;   // false — приёмник есть, но гарнитура выключена или вне зоны
        public int Percent;
        public bool Charging;
    }

    static class Razer
    {
        public const ushort Vid = 0x1532;

        sealed class Known
        {
            public RazerProto Proto;
            public string Name;
            public Known(RazerProto p, string n) { Proto = p; Name = n; }
        }

        static readonly Dictionary<ushort, Known> KnownDongles = new Dictionary<ushort, Known>
        {
            { 0x0536, new Known(RazerProto.Feature,  "Razer Barracuda X") },
            { 0x054E, new Known(RazerProto.YsTech,   "Razer Barracuda X (2022)") },
            { 0x0550, new Known(RazerProto.YsTech,   "Razer Barracuda X (2022)") },
            { 0x0552, new Known(RazerProto.Macronix, "Razer Barracuda X (2022)") },
            { 0x0574, new Known(RazerProto.Chroma,   "Razer Barracuda X Chroma") },
        };

        static readonly RazerProto[] AllProtos = { RazerProto.YsTech, RazerProto.Macronix, RazerProto.Chroma, RazerProto.Feature };

        // Один опрос за раз: приёмник не любит параллельные запросы.
        static readonly object Gate = new object();

        public static string NameFor(ushort pid)
        {
            Known k;
            return KnownDongles.TryGetValue(pid, out k) ? k.Name : "Razer (приёмник " + pid.ToString("X4") + ")";
        }

        public static RazerProto ProtoFor(ushort pid, IDictionary<ushort, RazerProto> learned)
        {
            RazerProto p;
            if (learned != null && learned.TryGetValue(pid, out p)) return p;
            Known k;
            return KnownDongles.TryGetValue(pid, out k) ? k.Proto : RazerProto.None;
        }

        // Опрос всех известных (и выученных через диагностику) приёмников.
        public static List<Device> Query(IDictionary<ushort, RazerProto> learned)
        {
            var result = new List<Device>();
            List<HidInfo> all;
            try { all = Hid.Enumerate(Vid); }
            catch { return result; }

            lock (Gate)
            {
                foreach (var group in all.GroupBy(i => i.Pid))
                {
                    var proto = ProtoFor(group.Key, learned);
                    if (proto == RazerProto.None) continue;
                    RazerReading reading = null;
                    foreach (var info in Candidates(group, proto))
                    {
                        try { reading = Probe(info, proto, null); }
                        catch { reading = null; }
                        if (reading != null) break;
                    }
                    var d = new Device
                    {
                        Id = "RAZER-" + group.Key.ToString("X4"),
                        Name = NameFor(group.Key),
                        Connected = reading != null && reading.HeadsetAnswered,
                        Percent = reading != null && reading.HeadsetAnswered ? reading.Percent : 0,
                        Charging = reading != null && reading.HeadsetAnswered && reading.Charging,
                        Source = "2.4 ГГц приёмник"
                    };
                    result.Add(d);
                }
            }
            return result;
        }

        // Подробная проверка для меню «Диагностика Razer»: перебирает подходящие протоколы и пишет обмен в журнал.
        // Возвращает PID → протокол для приёмников, которые ответили не своим «известным» протоколом.
        public static Dictionary<ushort, RazerProto> Diagnose(StringBuilder log)
        {
            var learned = new Dictionary<ushort, RazerProto>();
            List<HidInfo> all;
            try { all = Hid.Enumerate(Vid); }
            catch (Exception ex) { log.AppendLine("Ошибка перечисления HID: " + ex.Message); return learned; }

            if (all.Count == 0)
            {
                log.AppendLine("Устройств Razer (VID 1532) не найдено. Приёмник вставлен?");
                return learned;
            }

            log.AppendLine("HID-коллекции Razer:");
            foreach (var i in all) log.AppendLine("  " + i);
            log.AppendLine();

            lock (Gate)
            {
                foreach (var group in all.GroupBy(i => i.Pid))
                {
                    var known = ProtoFor(group.Key, null);
                    log.AppendLine(string.Format("== PID {0:X4}: {1}, известный протокол: {2}", group.Key, NameFor(group.Key), known));

                    var order = new List<RazerProto>();
                    if (known != RazerProto.None) order.Add(known);
                    order.AddRange(AllProtos.Where(p => p != known));

                    bool done = false;
                    foreach (var proto in order)
                    {
                        foreach (var info in Candidates(group, proto))
                        {
                            log.AppendLine(string.Format("-- {0} через {1}", proto, info));
                            RazerReading r;
                            try { r = Probe(info, proto, log); }
                            catch (Exception ex) { log.AppendLine("   исключение: " + ex.Message); r = null; }

                            if (r == null) { log.AppendLine("   нет ответа"); continue; }
                            if (!r.HeadsetAnswered)
                            {
                                log.AppendLine("   приёмник ответил, гарнитура — нет (выключена, вне зоны или в режиме Bluetooth)");
                                continue;
                            }
                            log.AppendLine(string.Format("   ГОТОВО: заряд {0}%{1}", r.Percent, r.Charging ? ", заряжается" : ""));
                            if (proto != known) learned[group.Key] = proto;
                            done = true;
                            break;
                        }
                        if (done) break;
                    }
                    if (!done) log.AppendLine("   заряд получить не удалось");
                    log.AppendLine();
                }
            }
            return learned;
        }

        static IEnumerable<HidInfo> Candidates(IEnumerable<HidInfo> colls, RazerProto proto)
        {
            switch (proto)
            {
                case RazerProto.YsTech:
                case RazerProto.Macronix:
                    return colls.Where(c => c.UsagePage == 0xFF00 && c.InLen >= 64 && c.OutLen >= 64)
                                .OrderBy(c => proto == RazerProto.YsTech ? (c.Usage == 1 ? 0 : 1) : (c.Usage == 0 ? 0 : 1));
                case RazerProto.Chroma:
                    return colls.Where(c => c.UsagePage >= 0xFF00 && c.InLen >= 64 && c.OutLen >= 64)
                                .OrderBy(c => c.UsagePage == 0xFF14 ? 0 : 1);
                case RazerProto.Feature:
                    return colls.Where(c => c.UsagePage >= 0xFF00 && c.FeatLen >= 16);
            }
            return Enumerable.Empty<HidInfo>();
        }

        static RazerReading Probe(HidInfo info, RazerProto proto, StringBuilder log)
        {
            switch (proto)
            {
                case RazerProto.YsTech: return ProbeAiroha(info, false, log);
                case RazerProto.Macronix: return ProbeAiroha(info, true, log);
                case RazerProto.Feature: return ProbeFeature(info, log);
                case RazerProto.Chroma: return ProbeChroma(info, log);
            }
            return null;
        }

        internal static string Hex(byte[] data, int count)
        {
            if (data == null) return "(нет)";
            int n = Math.Min(count, data.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
            if (data.Length > n) sb.Append(" …");
            return sb.ToString();
        }

        // --- Airoha-туннель (Barracuda X 2022) --------------------------------

        static RazerReading ProbeAiroha(HidInfo info, bool macronix, StringBuilder log)
        {
            using (var link = AirohaLink.Open(info, macronix, log))
            {
                if (link == null) { if (log != null) log.AppendLine("   не удалось открыть коллекцию"); return null; }
                bool remote = link.RemoteMode(true);
                if (!remote)
                {
                    // Мост ответил на запись, но гарнитура молчит.
                    return link.BridgeAnswered ? new RazerReading { HeadsetAnswered = false } : null;
                }
                int level, cable;
                try
                {
                    level = link.Get(0x21);
                    cable = level >= 0 ? link.Get(0x2A) : -1;
                }
                finally { link.RemoteMode(false); }
                if (level < 0 || level > 100) return new RazerReading { HeadsetAnswered = false };
                return new RazerReading { HeadsetAnswered = true, Percent = level, Charging = cable == 1 };
            }
        }

        sealed class AirohaLink : IDisposable
        {
            static int seqCounter;

            readonly HidDevice dev;
            readonly bool macronix;
            readonly StringBuilder log;
            readonly List<byte> stream = new List<byte>();
            byte seq;

            public bool BridgeAnswered;

            AirohaLink(HidDevice dev, bool macronix, StringBuilder log)
            {
                this.dev = dev;
                this.macronix = macronix;
                this.log = log;
            }

            public static AirohaLink Open(HidInfo info, bool macronix, StringBuilder log)
            {
                var dev = HidDevice.Open(info, false);
                if (dev == null) return null;
                var link = new AirohaLink(dev, macronix, log);
                dev.Flush();
                if (macronix)
                {
                    // Перевод моста Macronix в режим приложения: [01 40] → [01 40 01 01].
                    // Некоторые прошивки не отвечают, но команды всё равно принимают.
                    if (dev.Write(new byte[] { 0x01, 0x40 }, 500))
                    {
                        DateTime end = DateTime.UtcNow.AddMilliseconds(500);
                        while (true)
                        {
                            int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                            var r = left > 0 ? dev.Read(left) : null;
                            if (r == null) { link.Log("   app mode: нет ответа"); break; }
                            if (r.Length >= 4 && r[0] == 0x01 && r[1] == 0x40)
                            {
                                link.BridgeAnswered = true;
                                link.Log("   app mode: " + Hex(r, 4));
                                break;
                            }
                        }
                    }
                }
                return link;
            }

            public void Dispose() { dev.Dispose(); }

            void Log(string s) { if (log != null) log.AppendLine(s); }

            static ushort Crc16Xmodem(byte[] data)
            {
                ushort crc = 0;
                foreach (byte b in data)
                {
                    crc ^= (ushort)(b << 8);
                    for (int i = 0; i < 8; i++)
                        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
                return crc;
            }

            // Команда Airoha: [50 41][группа][seq][тело…]
            bool Send(byte group, params byte[] body)
            {
                seq = (byte)(Interlocked.Increment(ref seqCounter) % 127 + 1);
                var payload = new byte[4 + body.Length];
                payload[0] = 0x50;
                payload[1] = 0x41;
                payload[2] = group;
                payload[3] = seq;
                Array.Copy(body, 0, payload, 4, body.Length);

                var f = new byte[dev.Info.OutLen];
                if (macronix)
                {
                    // [01][80][длина][payload…], без CRC и подтверждения
                    f[0] = 0x01; f[1] = 0x80; f[2] = (byte)payload.Length;
                    Array.Copy(payload, 0, f, 3, payload.Length);
                    Log("   > " + Hex(f, 3 + payload.Length));
                    bool ok = dev.Write(f, 500);
                    if (ok) BridgeAnswered = true;
                    return ok;
                }

                // [02][80][длина][crc lo][crc hi][payload…], CRC-16/XMODEM по всему отчёту с нулями на месте CRC
                f[0] = 0x02; f[1] = 0x80; f[2] = (byte)payload.Length;
                Array.Copy(payload, 0, f, 5, payload.Length);
                ushort crc = Crc16Xmodem(f);
                f[3] = (byte)(crc & 0xFF);
                f[4] = (byte)(crc >> 8);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(50);
                    Log("   > " + Hex(f, 5 + payload.Length));
                    if (!dev.Write(f, 500)) { Log("   запись не прошла"); return false; }
                    var status = dev.GetInputReport(0x02);
                    Log("   ? " + Hex(status, 2));
                    if (status == null || status.Length < 2) return false;
                    if (status[1] == (byte)'O') { BridgeAnswered = true; return true; }   // принято
                    if (status[1] != (byte)'F') return false;                               // 'F' — занят, повторяем
                    BridgeAnswered = true;
                }
                return false;
            }

            // Ждёт первый кадр [50 49 …] из потока, удовлетворяющий match; чужие кадры пропускаются.
            byte[] Await(Func<byte[], bool> match, int timeoutMs)
            {
                DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    while (stream.Count >= 2)
                    {
                        int start = -1;
                        for (int i = 0; i + 1 < stream.Count; i++)
                            if (stream[i] == 0x50 && stream[i + 1] == 0x49) { start = i; break; }
                        if (start < 0) { stream.RemoveRange(0, stream.Count - 1); break; }
                        if (start > 0) stream.RemoveRange(0, start);
                        if (stream.Count < 10) break;
                        int len = 10 + (stream[8] | (stream[9] << 8));
                        if (stream.Count < len) break;
                        var frame = stream.GetRange(0, len).ToArray();
                        stream.RemoveRange(0, len);
                        Log("   < " + Hex(frame, 24));
                        if (match(frame)) return frame;
                    }

                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    var r = dev.Read(left);
                    if (r == null) return null;
                    if (macronix)
                    {
                        if (r.Length >= 3 && r[0] == 0x01 && r[1] == 0x80 && r[2] + 3 <= r.Length)
                            for (int i = 0; i < r[2]; i++) stream.Add(r[3 + i]);
                    }
                    else if (r.Length >= 2 && r[0] == 0x02 && r[1] + 2 <= r.Length)
                    {
                        for (int i = 0; i < r[1]; i++) stream.Add(r[2 + i]);
                    }
                }
            }

            // Remote mode: приёмник начинает пересылать команды в гарнитуру. true — гарнитура подтвердила.
            public bool RemoteMode(bool on)
            {
                if (!Send(0x0E, 0x02, 0xE1, (byte)(on ? 1 : 0))) return false;
                byte s = seq;
                var f = Await(r => r.Length >= 13 && r[10] == 0x0E && r[11] == (byte)(0x80 | s), 800);
                return f != null && f[12] == 0;
            }

            // Customer data GET: первый байт значения или -1.
            public int Get(byte cmd)
            {
                if (!Send(0x08, 0x03, cmd, 0x00, 0x00)) return -1;
                var f = Await(r => r.Length >= 14 && r[2] == 0x08 && r[10] == cmd && r[11] == 0x01, 1000);
                return f != null && f[12] >= 1 ? f[13] : -1;
            }
        }

        // --- Feature-отчёт 0xFF (Barracuda X 2021) -----------------------------

        static RazerReading ProbeFeature(HidInfo info, StringBuilder log)
        {
            using (var dev = HidDevice.Open(info, true))
            {
                if (dev == null) { if (log != null) log.AppendLine("   не удалось открыть коллекцию"); return null; }
                var query = new byte[] { 0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05 };
                if (log != null) log.AppendLine("   > " + Hex(query, query.Length));
                if (!dev.SetFeature(query)) { if (log != null) log.AppendLine("   SET_FEATURE отклонён"); return null; }
                var d = dev.GetFeature(0xFF);
                if (log != null) log.AppendLine("   < " + Hex(d, 16));
                if (d == null || d.Length < 15 || d[0] != 0xFF) return null;
                // Пустой кэш (гарнитура выключена или только что вставлен приёмник): ff 01 00 …
                if (d[1] == 0x01 && d[14] == 0) return new RazerReading { HeadsetAnswered = false };
                if (d[1] != 0x0F || d[14] > 100) return null;
                return new RazerReading { HeadsetAnswered = true, Percent = d[14], Charging = d[9] == 0x05 };
            }
        }

        // --- Report 02 с XOR-суммой (Barracuda X Chroma) -------------------------

        static RazerReading ProbeChroma(HidInfo info, StringBuilder log)
        {
            using (var dev = HidDevice.Open(info, false))
            {
                if (dev == null) { if (log != null) log.AppendLine("   не удалось открыть коллекцию"); return null; }
                dev.Flush();
                int level = ChromaGet(dev, 0x21, log);
                if (level < 0 || level > 100) return null;
                int charging = ChromaGet(dev, 0x2A, log);
                return new RazerReading { HeadsetAnswered = true, Percent = level, Charging = charging == 1 };
            }
        }

        static int ChromaGet(HidDevice dev, byte prop, StringBuilder log)
        {
            var req = new byte[64];
            var head = new byte[] { 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80 };
            Array.Copy(head, req, head.Length);
            req[10] = prop;
            byte x = 0;
            for (int i = 0; i < 62; i++) x ^= req[i];
            req[62] = x;
            if (log != null) log.AppendLine("   > " + Hex(req, 11) + " … " + req[62].ToString("X2"));
            if (!dev.Write(req, 500)) { if (log != null) log.AppendLine("   запись не прошла"); return -1; }

            var expected = new byte[] { 0x02, 0x02, 0x60, 0x00, 0x00, 0x00, 0x05, 0x00, 0x80, 0x80, prop, 0x01, 0x01 };
            DateTime end = DateTime.UtcNow.AddMilliseconds(1000);
            while (true)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                var r = left > 0 ? dev.Read(left) : null;
                if (r == null) return -1;
                if (r.Length < 64 || r[0] != 0x02) continue;
                if (log != null) log.AppendLine("   < " + Hex(r, 16));
                bool ok = true;
                for (int i = 0; i < expected.Length && ok; i++) ok = r[i] == expected[i];
                byte sum = 0;
                for (int i = 1; i < 62; i++) sum ^= r[i];
                if (ok && sum == r[62]) return r[13];
            }
        }
    }
}
