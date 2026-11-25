using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace WinShutDown
{
    /// <summary>
    /// Простой протокол обмена со службой: строки «ключ=значение» (значения URL-кодированы),
    /// сообщение заканчивается пустой строкой.
    /// </summary>
    internal static class Protocol
    {
        public const int MaxMessageBytes = 16 * 1024;

        public static byte[] Encode(IDictionary<string, string> msg)
        {
            var sb = new StringBuilder();
            foreach (var kv in msg)
                sb.Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value ?? "")).Append('\n');
            sb.Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static Dictionary<string, string> Read(Stream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[2048];
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                if (ms.Length > MaxMessageBytes) throw new InvalidDataException("Слишком длинное сообщение");
                if (IsComplete(ms)) break;
            }
            return Decode(ms.ToArray());
        }

        static bool IsComplete(MemoryStream ms)
        {
            byte[] b = ms.GetBuffer();
            long len = ms.Length;
            if (len == 1) return b[0] == (byte)'\n';
            return len >= 2 && b[len - 1] == (byte)'\n' && b[len - 2] == (byte)'\n';
        }

        public static Dictionary<string, string> Decode(byte[] data)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in Encoding.UTF8.GetString(data).Split('\n'))
            {
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq)] = Uri.UnescapeDataString(line.Substring(eq + 1));
            }
            return d;
        }

        public static Dictionary<string, string> Ok() => new Dictionary<string, string> { ["ok"] = "1" };

        public static Dictionary<string, string> Error(string code, string message) =>
            new Dictionary<string, string> { ["ok"] = "0", ["error"] = code, ["message"] = message };

        public static bool IsOk(Dictionary<string, string> r) => r != null && r.TryGetValue("ok", out string v) && v == "1";

        public static string Message(Dictionary<string, string> r) =>
            r != null && r.TryGetValue("message", out string m) ? m : "Неизвестная ошибка";

        public static string Get(Dictionary<string, string> r, string key) =>
            r != null && r.TryGetValue(key, out string v) ? v : null;
    }

    /// <summary>Клиент канала управления (окно настроек и окно предупреждения).</summary>
    internal sealed class ControlClient
    {
        readonly Options opt;

        public ControlClient(Options opt) { this.opt = opt; }

        public Dictionary<string, string> Call(Dictionary<string, string> request, int timeoutMs = 15000)
        {
            using (var pipe = new NamedPipeClientStream(".", opt.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(3000);
                if (!opt.Test) VerifyServer(pipe);
                byte[] data = Protocol.Encode(request);
                pipe.Write(data, 0, data.Length);
                pipe.Flush();
                var read = Task.Run(() => Protocol.Read(pipe));
                try
                {
                    if (!read.Wait(timeoutMs)) throw new TimeoutException("Служба не ответила вовремя");
                }
                catch (AggregateException ae)
                {
                    throw ae.GetBaseException();
                }
                return read.Result;
            }
        }

        /// <summary>
        /// Защита от подмены: канал должен принадлежать именно процессу службы WinShutDown,
        /// иначе пароль родителя мог бы «перехватить» посторонний процесс.
        /// </summary>
        static void VerifyServer(NamedPipeClientStream pipe)
        {
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
                throw new SecurityException("Не удалось проверить службу WinShutDown");
            uint servicePid = ServiceUtil.GetServicePid();
            if (servicePid == 0 || servicePid != serverPid)
                throw new SecurityException("Канал управления открыт посторонней программой.");
        }

        public StatusInfo Status()
        {
            var r = Call(new Dictionary<string, string> { ["cmd"] = "status" }, 5000);
            if (!Protocol.IsOk(r)) throw new InvalidOperationException(Protocol.Message(r));
            return StatusInfo.Parse(r);
        }

        public Dictionary<string, string> Login(string pwd) =>
            Call(new Dictionary<string, string> { ["cmd"] = "login", ["pwd"] = pwd });

        public Dictionary<string, string> Save(string pwd, bool enabled, DayRule[] days, int[] warnings, bool voice, bool adminExempt,
                                               bool remote, string tz) =>
            Call(new Dictionary<string, string>
            {
                ["cmd"] = "save",
                ["pwd"] = pwd,
                ["enabled"] = enabled ? "1" : "0",
                ["voice"] = voice ? "1" : "0",
                ["adminexempt"] = adminExempt ? "1" : "0",
                ["remote"] = remote ? "1" : "0",
                ["days"] = Schedule.FormatDays(days),
                ["warn"] = Schedule.FormatWarnings(warnings),
                ["tz"] = tz,
            });

        public Dictionary<string, string> Extend(string pwd, int minutes) =>
            Call(new Dictionary<string, string> { ["cmd"] = "extend", ["pwd"] = pwd, ["minutes"] = minutes.ToString(CultureInfo.InvariantCulture) });

        public Dictionary<string, string> ChangePassword(string pwd, string newPwd) =>
            Call(new Dictionary<string, string> { ["cmd"] = "passwd", ["pwd"] = pwd, ["newpwd"] = newPwd });

        public Dictionary<string, string> Uninstall(string pwd) =>
            Call(new Dictionary<string, string> { ["cmd"] = "uninstall", ["pwd"] = pwd });

        /// <summary>
        /// Остановить службу для обновления (по паролю). Служба первой версии не знает команды «update»
        /// (отвечает «unknown» без проверки пароля) — тогда останавливаем её командой «uninstall»: в старой
        /// службе она тоже только останавливает службу, а файлы и настройки удаляет окно удаления, которое здесь не участвует.
        /// </summary>
        public Dictionary<string, string> StopForUpdate(string pwd)
        {
            var r = Call(new Dictionary<string, string> { ["cmd"] = "update", ["pwd"] = pwd });
            if (Protocol.Get(r, "error") == "unknown") r = Uninstall(pwd);
            return r;
        }
    }

    internal sealed class StatusInfo
    {
        public bool Enabled, Active, Warning, Voice = true, AdminExempt = true, RemoteEnabled;
        public bool Exempt;          // сейчас за компьютером администратор — ограничения приостановлены
        public string ExemptUsers;
        public DayRule[] Days = Schedule.DefaultDays();
        public int[] Warnings = { 5 };

        /// <summary>false — выключение не запланировано (все дни без ограничений или расписание выключено).</summary>
        public bool HasDeadline => DeadlineUtc != DateTime.MinValue;
        public string TimeZoneId, DeadlineLocal, DeadlineDate, Today, ExtLocal;
        public DateTime DeadlineUtc, ExtUtc;

        public static StatusInfo Parse(Dictionary<string, string> d)
        {
            var s = new StatusInfo
            {
                Enabled = Protocol.Get(d, "enabled") == "1",
                Active = Protocol.Get(d, "active") == "1",
                Warning = Protocol.Get(d, "warning") == "1",
                Voice = Protocol.Get(d, "voice") != "0",
                AdminExempt = Protocol.Get(d, "adminexempt") != "0",
                RemoteEnabled = Protocol.Get(d, "remote") == "1",
                Exempt = Protocol.Get(d, "exempt") == "1",
                ExemptUsers = Protocol.Get(d, "exemptUsers") ?? "",
                TimeZoneId = Protocol.Get(d, "tz"),
                DeadlineLocal = Protocol.Get(d, "deadlineLocal") ?? "",
                DeadlineDate = Protocol.Get(d, "deadlineDate") ?? "",
                Today = Protocol.Get(d, "today") ?? "",
                ExtLocal = Protocol.Get(d, "extLocal") ?? "",
                DeadlineUtc = Ticks(Protocol.Get(d, "deadline")),
                ExtUtc = Ticks(Protocol.Get(d, "ext")),
            };
            if (Schedule.TryParseDays(Protocol.Get(d, "days"), out DayRule[] days, out _)) s.Days = days;
            if (Schedule.TryParseWarnings(Protocol.Get(d, "warn"), out int[] w, out _)) s.Warnings = w;
            return s;
        }

        static DateTime Ticks(string v) =>
            long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t > 0 && t <= DateTime.MaxValue.Ticks
                ? new DateTime(t, DateTimeKind.Utc)
                : DateTime.MinValue;
    }
}
