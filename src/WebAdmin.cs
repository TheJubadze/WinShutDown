using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WinShutDown
{
    internal static class NetInfo
    {
        /// <summary>Адрес из домашней (локальной) сети: 10.x, 172.16–31.x, 192.168.x, 169.254.x, IPv6 link-local/ULA, этот компьютер.</summary>
        public static bool IsLocalNetwork(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC; // fe80::/10, fec0::/10, fc00::/7
            return false;
        }

        /// <summary>IPv4-адреса этого компьютера в домашней сети (для подсказки, какой адрес открыть в браузере).</summary>
        public static List<string> LocalIPv4()
        {
            var list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress a = u.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        byte[] b = a.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue; // адрес без сети
                        if (IsLocalNetwork(a) && !list.Contains(a.ToString())) list.Add(a.ToString());
                    }
                }
            }
            catch { }
            return list;
        }

        public static string AdminUrl(string host, int port) => $"http://{host}:{port}";
    }

    internal static class RemoteAuth
    {
        public static string Mac(byte[] key, byte[] payload)
        {
            using (var h = new HMACSHA256(key)) return Convert.ToBase64String(h.ComputeHash(payload));
        }

        public static bool SameMac(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.ASCII.GetBytes(a), y = Encoding.ASCII.GetBytes(b);
            if (x.Length != y.Length) return false;
            int diff = 0;
            for (int i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        public static string NewNonce()
        {
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }

    /// <summary>
    /// Веб-админка: маленький HTTP-сервер внутри службы (без http.sys — только TcpListener).
    /// Пароль по сети не передаётся: страница получает одноразовое число (nonce), вычисляет из пароля тот же ключ,
    /// что хранится на компьютере ребёнка (PBKDF2), и подписывает каждый запрос HMAC-SHA256.
    /// Подключения принимаются только из домашней сети.
    /// </summary>
    internal sealed class WebAdminServer
    {
        public const int DefaultPort = 47800;
        const int MaxHeaderBytes = 8192, MaxBodyBytes = 16384, MaxNonces = 500;
        static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(2);

        readonly Engine engine;
        readonly int port;
        readonly bool loopbackOnly;
        readonly ConcurrentDictionary<string, DateTime> nonces = new ConcurrentDictionary<string, DateTime>();
        readonly byte[] page = LoadPage();
        TcpListener listener;
        volatile bool running;
        bool startErrorLogged;

        public WebAdminServer(Engine engine, int port, bool loopbackOnly)
        {
            this.engine = engine;
            this.port = port;
            this.loopbackOnly = loopbackOnly;
        }

        static byte[] LoadPage()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinShutDown.admin.html"))
            using (var ms = new MemoryStream())
            {
                s?.CopyTo(ms);
                return ms.ToArray();
            }
        }

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
                listener = CreateListener();
                running = true;
                new Thread(AcceptLoop) { IsBackground = true, Name = "web-admin" }.Start();
                Log.Info($"Управление через браузер включено: порт {port}");
                startErrorLogged = false;
            }
            catch (Exception ex)
            {
                if (!startErrorLogged) Log.Error($"Не удалось включить управление через браузер (порт {port} занят?)", ex);
                startErrorLogged = true;
                running = false;
                listener = null;
            }
        }

        void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            listener = null;
            Log.Info("Управление через браузер выключено");
        }

        TcpListener CreateListener()
        {
            if (loopbackOnly)
            {
                var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                l.Start();
                return l;
            }
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, port) { ExclusiveAddressUse = true };
                l.Server.DualMode = true; // IPv4 и IPv6 одним сокетом
                l.Start();
                return l;
            }
            catch (SocketException)
            {
                var l = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                l.Start();
                return l;
            }
        }

        void AcceptLoop()
        {
            TcpListener l = listener;
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch
                {
                    if (!running) return;
                    Thread.Sleep(500);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    IPAddress from = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
                    if (!NetInfo.IsLocalNetwork(from))
                    {
                        Log.Info("Отклонено подключение не из домашней сети: " + from);
                        return;
                    }
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    NetworkStream s = client.GetStream();
                    if (!ReadRequest(s, out string method, out string path, out byte[] body))
                    {
                        Respond(s, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad request"));
                        return;
                    }
                    if (method == "GET" && (path == "/" || path == "/index.html"))
                        Respond(s, 200, "text/html; charset=utf-8", page);
                    else if (method == "POST" && path == "/api/hello")
                        Respond(s, 200, "text/plain; charset=utf-8", Protocol.Encode(Hello()));
                    else if (method == "POST" && path == "/api/call")
                        Respond(s, 200, "text/plain; charset=utf-8", Protocol.Encode(Call(body, from)));
                    else
                        Respond(s, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
                }
            }
            catch
            {
                // клиент отключился или прислал мусор
            }
        }

        Dictionary<string, string> Hello()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var kv in nonces)
                if (now - kv.Value > NonceLifetime) nonces.TryRemove(kv.Key, out _);
            if (nonces.Count >= MaxNonces)
                foreach (var kv in nonces.OrderBy(k => k.Value).Take(nonces.Count - MaxNonces + 1).ToList())
                    nonces.TryRemove(kv.Key, out _);
            string nonce = RemoteAuth.NewNonce();
            nonces[nonce] = now;
            return engine.RemoteHello(nonce);
        }

        /// <summary>Тело: первая строка «mac=…», дальше — сам запрос (строки «ключ=значение»), подписанный этим MAC.</summary>
        Dictionary<string, string> Call(byte[] body, IPAddress from)
        {
            int nl = Array.IndexOf(body, (byte)'\n');
            if (nl <= 4) return Protocol.Error("bad", "Неверный запрос");
            string macLine = Encoding.ASCII.GetString(body, 0, nl).Trim();
            if (!macLine.StartsWith("mac=", StringComparison.Ordinal)) return Protocol.Error("bad", "Неверный запрос");
            var payload = new byte[body.Length - nl - 1];
            Array.Copy(body, nl + 1, payload, 0, payload.Length);
            Dictionary<string, string> req = Protocol.Decode(payload);

            string nonce = Protocol.Get(req, "nonce");
            if (nonce == null || !nonces.TryRemove(nonce, out DateTime issued) || DateTime.UtcNow - issued > NonceLifetime)
                return Protocol.Error("nonce", "Сеанс устарел — повторите действие");
            return engine.HandleRemote(req, macLine.Substring(4), payload, from.ToString());
        }

        static bool ReadRequest(Stream s, out string method, out string path, out byte[] body)
        {
            method = path = null;
            body = new byte[0];
            var head = new MemoryStream();
            var buf = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) return false;
                head.Write(buf, 0, n);
                if (head.Length > MaxHeaderBytes + MaxBodyBytes) return false;
                headerEnd = IndexOf(head.GetBuffer(), (int)head.Length, new byte[] { 13, 10, 13, 10 });
                if (headerEnd < 0 && head.Length > MaxHeaderBytes) return false;
            }
            byte[] all = head.ToArray();
            string[] lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0].ToUpperInvariant();
            path = first[1].Split('?')[0];

            int length = 0;
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    (!int.TryParse(line.Substring(colon + 1).Trim(), out length) || length < 0 || length > MaxBodyBytes))
                    return false;
            }

            var bodyStream = new MemoryStream();
            int already = all.Length - (headerEnd + 4);
            bodyStream.Write(all, headerEnd + 4, Math.Min(already, length));
            while (bodyStream.Length < length)
            {
                int n = s.Read(buf, 0, (int)Math.Min(buf.Length, length - bodyStream.Length));
                if (n <= 0) return false;
                bodyStream.Write(buf, 0, n);
            }
            body = bodyStream.ToArray();
            return true;
        }

        static int IndexOf(byte[] data, int length, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        static void Respond(Stream s, int code, string contentType, byte[] content)
        {
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : "Bad Request";
            string header =
                $"HTTP/1.1 {code} {reason}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {content.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "X-Frame-Options: DENY\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; frame-ancestors 'none'\r\n" +
                "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(header);
            s.Write(h, 0, h.Length);
            s.Write(content, 0, content.Length);
            s.Flush();
        }
    }
}
