using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WinShutDown
{
    /// <summary>
    /// Постоянный адрес веб-админки: компьютер ребёнка сам отвечает на имена «winshutdown.local» и «ИМЯ-ПК.local»
    /// по протоколу mDNS (RFC 6762) — так находятся принтеры и устройства Apple. Адрес не зависит от IP и от типа сети.
    /// Работает, только пока включено управление через браузер, и отвечает только устройствам домашней сети.
    /// </summary>
    internal sealed class MdnsResponder
    {
        public const int StandardPort = 5353;
        public const string FixedName = "winshutdown.local";
        public const ushort TypeA = 1, TypeAAAA = 28, TypeANY = 255, TypeNSEC = 47;
        static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

        readonly int port;
        readonly bool loopbackOnly;
        Socket socket;
        volatile bool running;
        bool startErrorLogged;

        public MdnsResponder(int port, bool loopbackOnly)
        {
            this.port = port;
            this.loopbackOnly = loopbackOnly;
        }

        /// <summary>Имена, на которые отвечает компьютер.</summary>
        public static string[] Names => new[] { FixedName, MachineLocalName };

        public static string MachineLocalName => Environment.MachineName.ToLowerInvariant() + ".local";

        public void SetEnabled(bool enabled)
        {
            if (enabled == running) return;
            if (enabled) Start();
            else Stop();
        }

        void Start()
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (loopbackOnly)
                {
                    s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                else
                {
                    // Порт 5353 делят все программы с mDNS (в том числе сама Windows) — поэтому ReuseAddress.
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.Bind(new IPEndPoint(IPAddress.Any, port));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    JoinGroup(s);
                }
                s.ReceiveTimeout = 5000;
                socket = s;
                running = true;
                new Thread(Loop) { IsBackground = true, Name = "mdns" }.Start();
                Log.Info($"Постоянный адрес включён: {string.Join(", ", Names)} (mDNS, порт {port})");
                startErrorLogged = false;
            }
            catch (Exception ex)
            {
                if (!startErrorLogged) Log.Error("Не удалось включить постоянный адрес winshutdown.local (mDNS)", ex);
                startErrorLogged = true;
                running = false;
                try { socket?.Close(); } catch { }
                socket = null;
            }
        }

        void Stop()
        {
            running = false;
            try { socket?.Close(); } catch { }
            socket = null;
        }

        /// <summary>Подписаться на группу mDNS на каждом сетевом подключении (повторяется — адреса могут меняться).</summary>
        static void JoinGroup(Socket s)
        {
            bool any = false;
            foreach (IPAddress local in LocalAddresses().Select(a => a.Address))
            {
                try
                {
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
                    any = true;
                }
                catch (SocketException) { any = true; /* уже подписаны на этом подключении */ }
            }
            if (!any)
            {
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group)); }
                catch (SocketException) { }
            }
        }

        void Loop()
        {
            Socket s = socket;
            var buf = new byte[9000];
            DateTime lastJoin = DateTime.UtcNow;
            while (running)
            {
                try
                {
                    if (!loopbackOnly && (DateTime.UtcNow - lastJoin).TotalSeconds >= 30)
                    {
                        JoinGroup(s);
                        lastJoin = DateTime.UtcNow;
                    }
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref from);
                    Handle(s, buf, n, (IPEndPoint)from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                }
                catch
                {
                    if (!running) return;
                    Thread.Sleep(200);
                }
            }
        }

        void Handle(Socket s, byte[] data, int length, IPEndPoint from)
        {
            if (!NetInfo.IsLocalNetwork(from.Address)) return;
            if (!TryParseQuery(data, length, out ushort id, out List<Question> questions)) return;
            bool legacy = from.Port != StandardPort; // простой DNS-запрос не с порта 5353 — ответ только ему, с тем же id
            foreach (Question q in questions)
            {
                if (!Names.Any(n => n.Equals(q.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (q.Type != TypeA && q.Type != TypeANY && q.Type != TypeAAAA) continue;
                IPAddress mine = AddressFor(from.Address);
                if (mine == null) return;
                byte[] response = BuildResponse(legacy ? id : (ushort)0, q.Name, q.Type, mine, legacy, legacy ? 10 : 120);
                if (legacy || q.Unicast || loopbackOnly)
                    s.SendTo(response, from);
                if (!legacy && !loopbackOnly)
                {
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, BitConverter.ToInt32(mine.GetAddressBytes(), 0));
                    s.SendTo(response, new IPEndPoint(Group, StandardPort));
                }
            }
        }

        static List<UnicastIPAddressInformation> LocalAddresses()
        {
            var list = new List<UnicastIPAddressInformation>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && NetInfo.IsLocalNetwork(u.Address) && !IPAddress.IsLoopback(u.Address))
                            list.Add(u);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Свой адрес в той же подсети, откуда пришёл вопрос (у компьютера может быть несколько подключений, например VPN).</summary>
        static IPAddress AddressFor(IPAddress remote)
        {
            List<UnicastIPAddressInformation> all = LocalAddresses();
            if (remote.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(remote))
            {
                byte[] r = remote.GetAddressBytes();
                foreach (UnicastIPAddressInformation u in all)
                {
                    if (u.IPv4Mask == null) continue;
                    byte[] a = u.Address.GetAddressBytes(), m = u.IPv4Mask.GetAddressBytes();
                    bool same = true;
                    for (int i = 0; i < 4; i++) same &= (a[i] & m[i]) == (r[i] & m[i]);
                    if (same) return u.Address;
                }
            }
            return all.Count > 0 ? all[0].Address : (IPAddress.IsLoopback(remote) ? IPAddress.Loopback : null);
        }

        // ---------------- разбор и сборка DNS-пакетов (проверяются в --selftest) ----------------

        public struct Question
        {
            public string Name;
            public ushort Type;
            public bool Unicast; // бит QU: просят ответить напрямую
        }

        public static bool TryParseQuery(byte[] d, int len, out ushort id, out List<Question> questions)
        {
            id = 0;
            questions = new List<Question>();
            if (len < 12) return false;
            id = (ushort)(d[0] << 8 | d[1]);
            int flags = d[2] << 8 | d[3];
            if ((flags & 0x8000) != 0 || (flags & 0x7800) != 0) return false; // это ответ или не обычный запрос
            int count = d[4] << 8 | d[5];
            int pos = 12;
            for (int i = 0; i < count && i < 64; i++)
            {
                if (!TryReadName(d, len, ref pos, out string name) || pos + 4 > len) return false;
                ushort type = (ushort)(d[pos] << 8 | d[pos + 1]);
                int cls = d[pos + 2] << 8 | d[pos + 3];
                pos += 4;
                if ((cls & 0x7FFF) == 1 || (cls & 0x7FFF) == 255)
                    questions.Add(new Question { Name = name, Type = type, Unicast = (cls & 0x8000) != 0 });
            }
            return true;
        }

        static bool TryReadName(byte[] d, int len, ref int pos, out string name)
        {
            name = null;
            var labels = new List<string>();
            int p = pos, hops = 0;
            bool jumped = false;
            while (true)
            {
                if (p >= len) return false;
                int l = d[p];
                if (l == 0)
                {
                    p++;
                    break;
                }
                if ((l & 0xC0) == 0xC0) // ссылка на имя выше в пакете (сжатие)
                {
                    if (p + 1 >= len || ++hops > 16) return false;
                    if (!jumped) pos = p + 2;
                    jumped = true;
                    p = ((l & 0x3F) << 8) | d[p + 1];
                    continue;
                }
                if ((l & 0xC0) != 0 || p + 1 + l > len) return false;
                labels.Add(Encoding.UTF8.GetString(d, p + 1, l));
                p += 1 + l;
            }
            if (!jumped) pos = p;
            name = string.Join(".", labels);
            return true;
        }

        /// <summary>
        /// Ответ по RFC 6762 (§6.1, §6.2), чтобы клиенту не пришлось ждать ответа впустую:
        ///  • на A/ANY — адрес IPv4, а в дополнительном разделе — NSEC «у этого имени есть только IPv4»;
        ///  • на AAAA — NSEC «IPv6-адреса нет», а в дополнительном разделе — сам адрес IPv4.
        /// legacy — ответ на обычный DNS-запрос: с вопросом, тем же id и без бита cache-flush.
        /// </summary>
        public static byte[] BuildResponse(ushort id, string name, ushort qtype, IPAddress ip, bool legacy, int ttl)
        {
            var ms = new MemoryStream();
            void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }

            U16(id);
            U16(0x8400);                // ответ, авторитетный
            U16(legacy ? 1 : 0);        // вопросов
            U16(1);                     // ответов
            U16(0);
            U16(1);                     // дополнительных записей
            int nameOffset = (int)ms.Length;
            WriteName(ms, name);
            if (legacy)
            {
                U16(qtype);
                U16(1);
                U16(0xC000 | nameOffset); // имя в ответе — ссылкой на вопрос
            }
            int cls = legacy ? 0x0001 : 0x8001; // IN; в mDNS-ответе ещё бит «заменить старые записи»

            void WriteA()
            {
                U16(TypeA);
                U16(cls);
                U32((uint)ttl);
                U16(4);
                byte[] a = ip.GetAddressBytes();
                ms.Write(a, 0, 4);
            }

            void WriteNsec()
            {
                U16(TypeNSEC);
                U16(cls);
                U32((uint)ttl);
                U16(5);
                U16(0xC000 | nameOffset); // следующее имя — то же самое
                ms.WriteByte(0);          // окно 0
                ms.WriteByte(1);          // 1 байт битовой карты
                ms.WriteByte(0x40);       // есть только тип A (1)
            }

            if (qtype == TypeAAAA) WriteNsec();
            else WriteA();

            U16(0xC000 | nameOffset);     // дополнительная запись — для того же имени
            if (qtype == TypeAAAA) WriteA();
            else WriteNsec();
            return ms.ToArray();
        }

        static void WriteName(Stream ms, string name)
        {
            foreach (string label in name.Split('.'))
            {
                byte[] b = Encoding.UTF8.GetBytes(label);
                if (b.Length == 0 || b.Length > 63) throw new ArgumentException("Неверное имя: " + name);
                ms.WriteByte((byte)b.Length);
                ms.Write(b, 0, b.Length);
            }
            ms.WriteByte(0);
        }

        /// <summary>DNS-вопрос «A для name» (для проверок).</summary>
        public static byte[] BuildQuery(ushort id, string name, ushort type, bool unicast)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)(id >> 8));
            ms.WriteByte((byte)id);
            ms.Write(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
            WriteName(ms, name);
            ms.WriteByte((byte)(type >> 8));
            ms.WriteByte((byte)type);
            ms.WriteByte((byte)(unicast ? 0x80 : 0x00));
            ms.WriteByte(1);
            return ms.ToArray();
        }
    }
}
