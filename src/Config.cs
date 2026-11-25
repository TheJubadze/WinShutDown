using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WinShutDown
{
    /// <summary>Настройки. Хранятся в %ProgramData%\WinShutDown\config.ini (доступ только SYSTEM и администраторам).</summary>
    internal sealed class AppConfig
    {
        public bool Enabled = true;
        public DayRule[] Days = Schedule.DefaultDays(); // расписание по дням, индекс = DayOfWeek (0 — воскресенье)
        public int[] Warnings = { 30, 10, 5 }; // за сколько минут предупреждать (по убыванию)
        public bool Voice = true;              // говорить «Осталось X минут» голосом (если есть русский голос)
        public bool AdminExempt = true;        // не предупреждать и не выключать, пока за компьютером только администраторы
        public string TimeZoneId = TimeZoneInfo.Local.Id;
        public string PasswordHash = "";
        public long ExtensionUntilUtcTicks;    // «продлить на сегодня»: до какого момента (UTC)

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinShutDown");

        public static string DefaultPath => Path.Combine(DataDir, "config.ini");

        /// <summary>Самое раннее предупреждение (с него начинается отсчёт), например 30.</summary>
        public int FirstWarning => Warnings.Length == 0 ? 5 : Warnings.Max();

        /// <summary>Последнее предупреждение — остаётся на экране до выключения, например 5.</summary>
        public int FinalWarning => Warnings.Length == 0 ? 5 : Warnings.Min();

        public AppConfig Clone()
        {
            var c = (AppConfig)MemberwiseClone();
            c.Warnings = (int[])Warnings.Clone();
            c.Days = (DayRule[])Days.Clone();
            return c;
        }

        /// <summary>Изменилось ли что-то, влияющее на расчёт времени выключения.</summary>
        public bool SameSchedule(AppConfig o) =>
            o != null && Enabled == o.Enabled && Schedule.FormatDays(Days) == Schedule.FormatDays(o.Days) &&
            Warnings.SequenceEqual(o.Warnings) && TimeZoneId == o.TimeZoneId &&
            !string.IsNullOrEmpty(PasswordHash) == !string.IsNullOrEmpty(o.PasswordHash);

        public static AppConfig Load(string path)
        {
            var c = new AppConfig();
            int legacyShutdown = -1, legacyAllow = -1;
            bool anyDay = false;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "Enabled": c.Enabled = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "Voice": c.Voice = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "AdminExempt": c.AdminExempt = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    // формат первой версии — одно время на все дни
                    case "Shutdown": if (Schedule.TryParseHm(v, out int s)) legacyShutdown = s; break;
                    case "AllowFrom": if (Schedule.TryParseHm(v, out int a)) legacyAllow = a; break;
                    case "Warnings":
                        if (Schedule.TryParseWarnings(v, out int[] list, out _)) c.Warnings = list;
                        break;
                    case "WarningMinutes": // формат первой версии — одно число
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w >= 1 && w <= Schedule.MaxWarningMinutes)
                            c.Warnings = new[] { w };
                        break;
                    case "TimeZone": if (v.Length > 0) c.TimeZoneId = v; break;
                    case "Password": c.PasswordHash = v; break;
                    case "ExtensionUntilUtc":
                        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t >= 0 && t <= DateTime.MaxValue.Ticks)
                            c.ExtensionUntilUtcTicks = t;
                        break;
                    default:
                        // Monday=07:00-22:00, Saturday=09:00-00:30, Sunday=free
                        if (k.Length > 0 && char.IsLetter(k[0]) && Enum.TryParse(k, true, out DayOfWeek day) &&
                            DayRule.TryParse(v, out DayRule rule))
                        {
                            c.Days[(int)day] = rule;
                            anyDay = true;
                        }
                        break;
                }
            }
            if (!anyDay && (legacyShutdown >= 0 || legacyAllow >= 0))
                c.Days = Schedule.SameEveryDay(legacyAllow >= 0 ? legacyAllow : 7 * 60, legacyShutdown >= 0 ? legacyShutdown : 22 * 60);
            return c;
        }

        public void Save(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# WinShutDown — настройки.");
            sb.AppendLine("# Удобнее менять их в окне «Пуск → WinShutDown — настройки».");
            sb.AppendLine("# Файл можно править и вручную (Блокнот, запущенный от имени администратора) —");
            sb.AppendLine("# служба сама перечитает его в течение секунды. Строки с ошибками игнорируются.");
            sb.AppendLine("#");
            sb.AppendLine("# Enabled — 1: выключать по расписанию, 0: не выключать.");
            sb.AppendLine("# Дни недели: «можно с»-«выключать в». Если «выключать в» меньше, чем «можно с», —");
            sb.AppendLine("# это время после полуночи (Saturday=09:00-00:30 — в ночь на воскресенье в 00:30).");
            sb.AppendLine("# free — день без ограничений (время после free хранит значения для окна настроек).");
            sb.AppendLine("# Вне указанного времени компьютер выключится через время последнего предупреждения.");
            sb.AppendLine("# Warnings — за сколько минут предупреждать, через запятую.");
            sb.AppendLine("# Voice — 1: говорить «Осталось X минут» голосом, 0: только звуковой сигнал.");
            sb.AppendLine("# AdminExempt — 1: не выключать, пока за компьютером работает администратор Windows.");
            sb.AppendLine("Enabled=" + (Enabled ? "1" : "0"));
            foreach (DayOfWeek d in Schedule.WeekOrder)
                sb.AppendLine(d + "=" + Days[(int)d].Format());
            sb.AppendLine("Warnings=" + Schedule.FormatWarnings(Warnings));
            sb.AppendLine("Voice=" + (Voice ? "1" : "0"));
            sb.AppendLine("AdminExempt=" + (AdminExempt ? "1" : "0"));
            sb.AppendLine("TimeZone=" + TimeZoneId);
            sb.AppendLine("# Служебные значения — не меняйте:");
            sb.AppendLine("Password=" + PasswordHash);
            sb.AppendLine("ExtensionUntilUtc=" + ExtensionUntilUtcTicks.ToString(inv));

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }

    /// <summary>Хранение пароля: PBKDF2-HMAC-SHA256 с солью, сам пароль нигде не сохраняется.</summary>
    internal static class PasswordHasher
    {
        const int Iterations = 100000;
        const string Scheme = "pbkdf2-sha256";

        public static string Hash(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Derive(password, salt, Iterations, 32);
            return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string stored)
        {
            if (password == null || string.IsNullOrEmpty(stored)) return false;
            string[] p = stored.Split('$');
            if (p.Length != 4 || p[0] != Scheme) return false;
            try
            {
                int iter = int.Parse(p[1], CultureInfo.InvariantCulture);
                byte[] salt = Convert.FromBase64String(p[2]);
                byte[] expected = Convert.FromBase64String(p[3]);
                if (iter < 1000 || expected.Length < 16) return false;
                byte[] actual = Derive(password, salt, iter, expected.Length);
                int diff = 0;
                for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
                return diff == 0;
            }
            catch (FormatException) { return false; }
        }

        static byte[] Derive(string password, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }
    }

    internal static class Log
    {
        static readonly object Sync = new object();
        public static string FilePath; // null — журнал не ведётся

        public static void Info(string msg) => Write("INFO ", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : msg + ": " + ex);

        static void Write(string level, string msg)
        {
            string path = FilePath;
            if (path == null) return;
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}\r\n", Encoding.UTF8);
                }
                catch
                {
                    // журнал не должен ломать работу
                }
            }
        }
    }
}
