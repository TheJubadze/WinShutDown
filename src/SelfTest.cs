using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WinShutDown
{
    /// <summary>Самопроверка логики: WinShutDown.exe --selftest --out result.txt</summary>
    internal static class SelfTest
    {
        public static int Run(Options opt)
        {
            var sb = new StringBuilder();
            int fails = 0;
            void Check(string name, bool ok)
            {
                sb.AppendLine((ok ? "OK    " : "FAIL  ") + name);
                if (!ok) fails++;
            }
            DateTime U(int h, int m, int day = 10, int sec = 0) => new DateTime(2026, 11, day, h, m, sec, DateTimeKind.Utc);

            try
            {
                // --- одно время на все дни 07:00–22:00, часовой пояс UTC ---
                var c = new AppConfig { Days = Schedule.SameEveryDay(7 * 60, 22 * 60), Warnings = new[] { 30, 10, 5 }, TimeZoneId = "UTC" };
                Check("20:00 -> выключение сегодня в 22:00", Schedule.BaseDeadlineUtc(c, U(20, 0)) == U(22, 0));
                Check("21:59:59 -> 22:00", Schedule.BaseDeadlineUtc(c, U(21, 59, 10, 59)) == U(22, 0));
                Check("включили в 22:30 -> выключение через 5 мин", Schedule.BaseDeadlineUtc(c, U(22, 30)) == U(22, 35));
                Check("включили в 03:00 -> через 5 мин", Schedule.BaseDeadlineUtc(c, U(3, 0)) == U(3, 5));
                Check("включили в 06:50 -> через 5 мин", Schedule.BaseDeadlineUtc(c, U(6, 50)) == U(6, 55));
                Check("включили в 06:57 -> в 07:00 уже можно, выключение в 22:00", Schedule.BaseDeadlineUtc(c, U(6, 57)) == U(22, 0));
                Check("07:00 -> разрешено, выключение в 22:00", Schedule.BaseDeadlineUtc(c, U(7, 0)) == U(22, 0));

                // --- выключение после полуночи: 07:00 — 00:30 ---
                var c2 = new AppConfig { Days = Schedule.SameEveryDay(7 * 60, 30), Warnings = new[] { 10 }, TimeZoneId = "UTC" };
                Check("23:00 -> завтра в 00:30", Schedule.BaseDeadlineUtc(c2, U(23, 0)) == U(0, 30, 11));
                Check("02:00 -> через 10 мин", Schedule.BaseDeadlineUtc(c2, U(2, 0)) == U(2, 10));
                Check("12:00 -> завтра в 00:30", Schedule.BaseDeadlineUtc(c2, U(12, 0)) == U(0, 30, 11));

                // --- разные дни недели ---
                Check("23.11.2026 — понедельник (основа тестов)", new DateTime(2026, 11, 23).DayOfWeek == DayOfWeek.Monday);
                DateTime N(int day, int h, int m) => new DateTime(2026, 11, day, h, m, 0, DateTimeKind.Utc);
                DayRule[] week = Schedule.SameEveryDay(7 * 60, 22 * 60);
                week[(int)DayOfWeek.Friday] = new DayRule(7 * 60, 23 * 60 + 30);
                week[(int)DayOfWeek.Saturday] = new DayRule(9 * 60, 30); // до 00:30 в ночь на воскресенье
                week[(int)DayOfWeek.Sunday] = new DayRule(10 * 60, 21 * 60);
                var cw = new AppConfig { Days = week, Warnings = new[] { 5 }, TimeZoneId = "UTC" };
                Check("вт 20:00 -> вт 22:00", Schedule.BaseDeadlineUtc(cw, N(24, 20, 0)) == N(24, 22, 0));
                Check("пт 22:30 -> пт 23:30", Schedule.BaseDeadlineUtc(cw, N(27, 22, 30)) == N(27, 23, 30));
                Check("сб 23:00 -> вс 00:30", Schedule.BaseDeadlineUtc(cw, N(28, 23, 0)) == N(29, 0, 30));
                Check("вс 00:10 (ещё субботний вечер) -> 00:30", Schedule.BaseDeadlineUtc(cw, N(29, 0, 10)) == N(29, 0, 30));
                Check("вс 00:40 -> через 5 мин", Schedule.BaseDeadlineUtc(cw, N(29, 0, 40)) == N(29, 0, 45));
                Check("сб 08:00 (можно с 09:00) -> через 5 мин", Schedule.BaseDeadlineUtc(cw, N(28, 8, 0)) == N(28, 8, 5));
                Check("сб 08:57 -> скоро можно, выключение в 00:30", Schedule.BaseDeadlineUtc(cw, N(28, 8, 57)) == N(29, 0, 30));
                Check("вс 21:30 (в воскресенье до 21:00) -> через 5 мин", Schedule.BaseDeadlineUtc(cw, N(29, 21, 30)) == N(29, 21, 35));
                Check("пн 06:00 -> через 5 мин", Schedule.BaseDeadlineUtc(cw, N(30, 6, 0)) == N(30, 6, 5));
                Check("пн 12:00 -> пн 22:00", Schedule.BaseDeadlineUtc(cw, N(30, 12, 0)) == N(30, 22, 0));

                // --- день без ограничений (среда) ---
                DayRule[] withFree = Schedule.SameEveryDay(7 * 60, 22 * 60);
                withFree[(int)DayOfWeek.Wednesday] = new DayRule(7 * 60, 22 * 60) { Limited = false };
                var cf = new AppConfig { Days = withFree, Warnings = new[] { 5 }, TimeZoneId = "UTC" };
                Check("вт 23:00 -> через 5 мин", Schedule.BaseDeadlineUtc(cf, N(24, 23, 0)) == N(24, 23, 5));
                Check("ср 00:30 -> можно, выключение в чт 00:00", Schedule.BaseDeadlineUtc(cf, N(25, 0, 30)) == N(26, 0, 0));
                Check("ср 23:00 -> чт 00:00", Schedule.BaseDeadlineUtc(cf, N(25, 23, 0)) == N(26, 0, 0));
                DayRule[] allFree = Enumerable.Repeat(new DayRule(7 * 60, 22 * 60) { Limited = false }, 7).ToArray();
                Check("все дни без ограничений -> не выключать",
                    Schedule.BaseDeadlineUtc(new AppConfig { Days = allFree, TimeZoneId = "UTC" }, N(25, 12, 0)) == Schedule.Never);

                // --- разбор правил дня ---
                Check("«07:00-22:00»", DayRule.TryParse("07:00-22:00", out DayRule r1) && r1.Limited && r1.AllowFrom == 420 && r1.Shutdown == 1320);
                Check("«09:00-00:30» — после полуночи", DayRule.TryParse("09:00-00:30", out DayRule r2) && r2.Limited && r2.AfterMidnight);
                Check("«free»", DayRule.TryParse("free", out DayRule r3) && !r3.Limited);
                Check("«free 08:00-21:00» хранит время", DayRule.TryParse("free 08:00-21:00", out DayRule r4) && !r4.Limited && r4.AllowFrom == 480);
                Check("ошибка: «07:00-07:00»", !DayRule.TryParse("07:00-07:00", out _));
                Check("ошибка: «25:00-22:00»", !DayRule.TryParse("25:00-22:00", out _));
                Check("расписание недели туда-обратно",
                    Schedule.TryParseDays(Schedule.FormatDays(week), out DayRule[] wk, out _) && Schedule.FormatDays(wk) == Schedule.FormatDays(week));

                // --- часовой пояс из настроек (Москва, UTC+3) ---
                var c3 = new AppConfig { Days = Schedule.SameEveryDay(7 * 60, 22 * 60), Warnings = new[] { 5 }, TimeZoneId = "Russian Standard Time" };
                Check("Москва: 22:00 местного = 19:00 UTC", Schedule.BaseDeadlineUtc(c3, U(18, 0)) == U(19, 0));
                Check("Москва: 23:00 местного (20:00 UTC) -> через 5 мин", Schedule.BaseDeadlineUtc(c3, U(20, 0)) == U(20, 5));

                // --- несколько предупреждений ---
                Check("разбор «30, 10, 5»", Schedule.TryParseWarnings("30, 10, 5", out int[] w1, out _) && Schedule.FormatWarnings(w1) == "30,10,5");
                Check("разбор «5,30;10 10» -> по убыванию, без повторов", Schedule.TryParseWarnings("5,30;10 10", out int[] w2, out _) && Schedule.FormatWarnings(w2) == "30,10,5");
                Check("разбор «15» (одно)", Schedule.TryParseWarnings("15", out int[] w3, out _) && w3.Length == 1 && w3[0] == 15);
                Check("ошибка: пусто", !Schedule.TryParseWarnings(" , ", out _, out _));
                Check("ошибка: 0", !Schedule.TryParseWarnings("10, 0", out _, out _));
                Check("ошибка: буквы", !Schedule.TryParseWarnings("10, пять", out _, out _));
                Check("ошибка: больше 180", !Schedule.TryParseWarnings("200", out _, out _));
                int[] ws = { 30, 10, 5 };
                Check("этап: 45 мин -> нет", Schedule.Stage(TimeSpan.FromMinutes(45), ws) == null);
                Check("этап: ровно 30 мин -> 30", Schedule.Stage(TimeSpan.FromMinutes(30), ws) == 30);
                Check("этап: 25 мин -> 30", Schedule.Stage(TimeSpan.FromMinutes(25), ws) == 30);
                Check("этап: 10 мин -> 10", Schedule.Stage(TimeSpan.FromMinutes(10), ws) == 10);
                Check("этап: 7 мин -> 10", Schedule.Stage(TimeSpan.FromMinutes(7), ws) == 10);
                Check("этап: 4 мин -> 5", Schedule.Stage(TimeSpan.FromMinutes(4), ws) == 5);
                Check("первое/последнее предупреждение 30/5", c.FirstWarning == 30 && c.FinalWarning == 5);
                Check("«30, 10 и 5»", Ru.JoinAnd(ws) == "30, 10 и 5");
                var legacy = Path.Combine(Path.GetTempPath(), "winshutdown-legacy.ini");
                File.WriteAllText(legacy, "WarningMinutes=7\r\n");
                Check("старый формат WarningMinutes=7", AppConfig.Load(legacy).Warnings.Length == 1 && AppConfig.Load(legacy).Warnings[0] == 7);
                File.Delete(legacy);

                File.WriteAllText(legacy, "Shutdown=21:00\r\nAllowFrom=08:00\r\n");
                Check("старый формат Shutdown/AllowFrom -> на все дни", AppConfig.Load(legacy).Days.All(d => d.Limited && d.AllowFrom == 480 && d.Shutdown == 1260));
                File.WriteAllText(legacy, "# ручная правка\r\nMonday=07:00-21:00\r\nSaturday=free\r\nsunday = 10:00-23:00\r\nTuesday=ерунда\r\n");
                AppConfig hand = AppConfig.Load(legacy);
                Check("ручная правка файла: понедельник, суббота, воскресенье; ошибочный вторник — по умолчанию",
                    hand.Days[1].Shutdown == 1260 && !hand.Days[6].Limited && hand.Days[0].AllowFrom == 600 &&
                    hand.Days[2].Limited && hand.Days[2].AllowFrom == 420 && hand.Days[2].Shutdown == 1320);
                File.Delete(legacy);

                // --- тексты ---
                Check("5 минут", Ru.Remaining(TimeSpan.FromMinutes(5)) == "Осталось 5 минут");
                Check("4:59.5 -> 5 минут", Ru.Remaining(TimeSpan.FromSeconds(299.5)) == "Осталось 5 минут");
                Check("3 минуты", Ru.Remaining(TimeSpan.FromMinutes(3)) == "Осталось 3 минуты");
                Check("1 минута", Ru.Remaining(TimeSpan.FromMinutes(1)) == "Осталась 1 минута");
                Check("11 минут", Ru.Remaining(TimeSpan.FromMinutes(11)) == "Осталось 11 минут");
                Check("21 минута", Ru.Remaining(TimeSpan.FromMinutes(21)) == "Осталась 21 минута");
                Check("22 минуты", Ru.Remaining(TimeSpan.FromMinutes(22)) == "Осталось 22 минуты");
                Check("30 секунд", Ru.Remaining(TimeSpan.FromSeconds(30)) == "Осталось 30 секунд");
                Check("1 секунда", Ru.Remaining(TimeSpan.FromSeconds(1)) == "Осталась 1 секунда");
                Check("2 секунды", Ru.Remaining(TimeSpan.FromSeconds(2)) == "Осталось 2 секунды");

                // --- пароль ---
                var sw = Stopwatch.StartNew();
                string hash = PasswordHasher.Hash("Секрет 123!");
                long ms = sw.ElapsedMilliseconds;
                Check("пароль верный", PasswordHasher.Verify("Секрет 123!", hash));
                Check("пароль неверный", !PasswordHasher.Verify("Секрет 124!", hash));
                Check("пустой пароль", !PasswordHasher.Verify("", hash));
                Check("пустой хэш", !PasswordHasher.Verify("x", ""));
                sb.AppendLine($"      (вычисление хэша: {ms} мс)");

                // --- протокол ---
                var msg = new Dictionary<string, string> { ["a"] = "x=y&z\n\nq%", ["b"] = "Привет, мир", ["c"] = "" };
                Dictionary<string, string> back = Protocol.Decode(Protocol.Encode(msg));
                Check("протокол: кодирование туда-обратно", back["a"] == msg["a"] && back["b"] == msg["b"] && back["c"] == "");

                // --- файл настроек ---
                string tmp = Path.Combine(Path.GetTempPath(), "winshutdown-selftest.ini");
                var saved = new AppConfig { Enabled = false, Days = withFree, Warnings = new[] { 20, 7 }, Voice = false, AdminExempt = false, RemoteEnabled = true, TimeZoneId = "UTC", PasswordHash = hash, ExtensionUntilUtcTicks = 123456789 };
                saved.Days[(int)DayOfWeek.Saturday] = new DayRule(9 * 60, 30);
                saved.Save(tmp);
                saved.Save(tmp); // повторная запись (через File.Replace)
                AppConfig loaded = AppConfig.Load(tmp);
                File.Delete(tmp);
                Check("настройки: запись и чтение", !loaded.Enabled && Schedule.FormatDays(loaded.Days) == Schedule.FormatDays(saved.Days) &&
                      Schedule.FormatWarnings(loaded.Warnings) == "20,7" && !loaded.Voice && !loaded.AdminExempt && loaded.RemoteEnabled && loaded.TimeZoneId == "UTC" &&
                      loaded.PasswordHash == hash && loaded.ExtensionUntilUtcTicks == 123456789);

                // --- управление через браузер ---
                Check("управление через браузер по умолчанию выключено", !new AppConfig().RemoteEnabled);
                Func<string, bool> local = ip => NetInfo.IsLocalNetwork(System.Net.IPAddress.Parse(ip));
                Check("домашняя сеть: 192.168.1.5, 10.0.0.7, 172.16.3.4, 169.254.1.1, 127.0.0.1",
                      local("192.168.1.5") && local("10.0.0.7") && local("172.16.3.4") && local("169.254.1.1") && local("127.0.0.1"));
                Check("не домашняя сеть: 8.8.8.8, 172.32.0.1, 100.64.0.1", !local("8.8.8.8") && !local("172.32.0.1") && !local("100.64.0.1"));
                Check("IPv6: ::1, fe80::1, fd00::5 — домашняя; 2001:db8::1 — нет",
                      local("::1") && local("fe80::1") && local("fd00::5") && !local("2001:db8::1"));
                Check("IPv4 в виде IPv6 (::ffff:192.168.1.5) — домашняя", local("::ffff:192.168.1.5"));

                string remoteHash = PasswordHasher.Hash("Пароль родителя 1");
                Check("разбор хэша пароля", PasswordHasher.TryParse(remoteHash, out int rIter, out byte[] rSalt, out byte[] rKey) && rKey.Length == 32);
                byte[] browserKey = PasswordHasher.DeriveKey("Пароль родителя 1", rSalt, rIter, 32);
                byte[] payloadBytes = Encoding.UTF8.GetBytes("cmd=status\nnonce=abc\n");
                string browserMac = RemoteAuth.Mac(browserKey, payloadBytes);
                Check("подпись запроса верным паролем совпадает", RemoteAuth.SameMac(browserMac, RemoteAuth.Mac(rKey, payloadBytes)));
                byte[] wrongKey = PasswordHasher.DeriveKey("Пароль родителя 2", rSalt, rIter, 32);
                Check("подпись неверным паролем не совпадает", !RemoteAuth.SameMac(RemoteAuth.Mac(wrongKey, payloadBytes), RemoteAuth.Mac(rKey, payloadBytes)));
                Check("изменённый запрос не проходит проверку",
                      !RemoteAuth.SameMac(browserMac, RemoteAuth.Mac(rKey, Encoding.UTF8.GetBytes("cmd=save\nnonce=abc\n"))));
                Check("одноразовые числа не повторяются", RemoteAuth.NewNonce() != RemoteAuth.NewNonce());
                using (Stream page = typeof(SelfTest).Assembly.GetManifestResourceStream("WinShutDown.admin.html"))
                using (var reader = new StreamReader(page ?? Stream.Null, Encoding.UTF8))
                {
                    string html = reader.ReadToEnd();
                    Check("страница веб-админки вшита в программу", html.Contains("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad") && html.Length > 10000);
                }

                // --- постоянный адрес winshutdown.local (mDNS) ---
                byte[] query = MdnsResponder.BuildQuery(0x1234, "WinShutDown.Local", MdnsResponder.TypeA, false);
                Check("mDNS: разбор вопроса",
                      MdnsResponder.TryParseQuery(query, query.Length, out ushort qid, out var qs) && qid == 0x1234 && qs.Count == 1 &&
                      qs[0].Name == "WinShutDown.Local" && qs[0].Type == MdnsResponder.TypeA && !qs[0].Unicast);
                byte[] quQuery = MdnsResponder.BuildQuery(7, "winshutdown.local", MdnsResponder.TypeAAAA, true);
                Check("mDNS: бит «ответить напрямую» и тип AAAA",
                      MdnsResponder.TryParseQuery(quQuery, quQuery.Length, out _, out var qs2) && qs2[0].Unicast && qs2[0].Type == MdnsResponder.TypeAAAA);
                // два вопроса, второй — сжатой ссылкой на имя первого
                var two = new List<byte>(MdnsResponder.BuildQuery(1, "winshutdown.local", MdnsResponder.TypeA, false));
                two[5] = 2;
                two.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x1C, 0x00, 0x01 });
                Check("mDNS: сжатые имена",
                      MdnsResponder.TryParseQuery(two.ToArray(), two.Count, out _, out var qs3) && qs3.Count == 2 &&
                      qs3[1].Name == "winshutdown.local" && qs3[1].Type == MdnsResponder.TypeAAAA);
                byte[] loop = { 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1 };
                Check("mDNS: зацикленная ссылка и обрезанный пакет не ломают разбор",
                      !MdnsResponder.TryParseQuery(loop, loop.Length, out _, out _) && !MdnsResponder.TryParseQuery(query, 15, out _, out _));
                byte[] answerFlag = (byte[])query.Clone();
                answerFlag[2] = 0x84;
                Check("mDNS: чужие ответы не считаются вопросами", !MdnsResponder.TryParseQuery(answerFlag, answerFlag.Length, out _, out _));

                var ip = System.Net.IPAddress.Parse("192.168.1.23");
                byte[] legacyResp = MdnsResponder.BuildResponse(0x1234, "winshutdown.local", MdnsResponder.TypeA, ip, true, 10);
                Check("mDNS: ответ на обычный DNS-запрос — тот же id, вопрос, адрес, TTL 10, без cache-flush",
                      legacyResp[0] == 0x12 && legacyResp[1] == 0x34 && legacyResp[2] == 0x84 && legacyResp[5] == 1 && legacyResp[7] == 1 &&
                      legacyResp[35] == 0xC0 && legacyResp[36] == 12 && legacyResp[38] == 1 && legacyResp[39] == 0 && legacyResp[40] == 1 &&
                      legacyResp[44] == 10 && legacyResp[46] == 4 &&
                      legacyResp[47] == 192 && legacyResp[48] == 168 && legacyResp[49] == 1 && legacyResp[50] == 23 &&
                      legacyResp[11] == 1 && legacyResp[53] == 0 && legacyResp[54] == MdnsResponder.TypeNSEC && legacyResp.Length == 68);
                byte[] mdnsResp = MdnsResponder.BuildResponse(0, "winshutdown.local", MdnsResponder.TypeA, ip, false, 120);
                Check("mDNS: ответ в группу — id 0, без вопроса, cache-flush, TTL 120",
                      mdnsResp[0] == 0 && mdnsResp[1] == 0 && mdnsResp[5] == 0 && mdnsResp[7] == 1 &&
                      mdnsResp[32] == 1 && mdnsResp[33] == 0x80 && mdnsResp[34] == 1 && mdnsResp[38] == 120 && mdnsResp[44] == 23);
                byte[] nsec = MdnsResponder.BuildResponse(0, "winshutdown.local", MdnsResponder.TypeAAAA, ip, false, 120);
                Check("mDNS: на вопрос об IPv6 — NSEC «есть только IPv4»",
                      nsec[32] == MdnsResponder.TypeNSEC && nsec[40] == 5 && nsec[45] == 0x40 && nsec[43] == 0);
                Check("mDNS: к ответу «нет IPv6» приложен адрес IPv4, к адресу IPv4 — «нет IPv6»",
                      nsec[11] == 1 && nsec[49] == MdnsResponder.TypeA && nsec[nsec.Length - 1] == 23 && nsec.Length == 62 &&
                      mdnsResp[11] == 1 && mdnsResp[48] == MdnsResponder.TypeNSEC && mdnsResp.Length == 62);

                // --- переход с имени на IP-адрес ---
                var lanEp = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.23"), 47800);
                var mappedEp = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("::ffff:192.168.1.23"), 47800);
                Check("веб-админка: открыли по имени — переход на IP",
                      WebAdminServer.RedirectToIp("winshutdown.local:47800", lanEp) == "http://192.168.1.23:47800/" &&
                      WebAdminServer.RedirectToIp("desktop-abc.local:47800", mappedEp) == "http://192.168.1.23:47800/");
                Check("веб-админка: открыли по IP — без перехода",
                      WebAdminServer.RedirectToIp("192.168.1.23:47800", lanEp) == null &&
                      WebAdminServer.RedirectToIp("[fe80::1]:47800", lanEp) == null &&
                      WebAdminServer.RedirectToIp(null, lanEp) == null &&
                      WebAdminServer.RedirectToIp("winshutdown.local:47800", new System.Net.IPEndPoint(System.Net.IPAddress.Parse("fe80::1"), 47800)) == null);
                Check("mDNS: имена — winshutdown.local и имя компьютера",
                      MdnsResponder.Names.Contains("winshutdown.local") && MdnsResponder.Names[1] == Environment.MachineName.ToLowerInvariant() + ".local");

                Check("голос по умолчанию включён", new AppConfig().Voice);
                Check("«не выключать при администраторе» по умолчанию включено", new AppConfig().AdminExempt);
                using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    bool elevated = new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    sb.AppendLine($"      (текущий пользователь {me.Name}: в группе «Администраторы»={Sessions.IsAdminMember(me.Token)}, " +
                                  $"права администратора сейчас={elevated})");
                }
                Speaker sp = Speaker.TryCreate();
                sb.AppendLine("      (русский голос на этом компьютере: " + (sp != null ? sp.VoiceName : "не найден") + ")");

                // --- защита от перевода часов назад (поддельные часы и счётчик) ---
                string clockFile = Path.Combine(Path.GetTempPath(), "winshutdown-selftest-clock.dat");
                if (File.Exists(clockFile)) File.Delete(clockFile);
                DateTime C(int h, int m) => new DateTime(2026, 11, 27, h, m, 0, DateTimeKind.Utc);
                DateTime fakeSys = C(21, 0);
                long fakeTicks = 1000000;
                Func<DateTime> sysClock = () => fakeSys;
                Func<long> tickCount = () => fakeTicks;

                var clk = new TrustedClock(clockFile, sysClock, tickCount);
                Check("часы: без сохранённого времени — обычные часы Windows", clk.UtcNow == C(21, 0) && !clk.InRollback);
                clk.Save(force: true); // компьютер выключился в 21:00

                // В BIOS перевели часы на 3 часа назад и включили компьютер; счётчик после включения начинается заново.
                fakeSys = C(18, 2);
                fakeTicks = 5000;
                var clk2 = new TrustedClock(clockFile, sysClock, tickCount);
                Check("часы: перевод назад в BIOS замечен после включения", clk2.InRollback && clk2.UtcNow == C(21, 0));
                fakeSys = fakeSys.AddMinutes(10);
                fakeTicks += 600000;
                Check("часы: время идёт от последнего известного по счётчику (21:10, а не 18:12)", clk2.UtcNow == C(21, 10));
                Check("часы: видно, на сколько отстают часы Windows (2 ч 58 мин)", Math.Abs((clk2.Behind - TimeSpan.FromMinutes(178)).TotalSeconds) < 1);

                fakeSys = C(21, 15); // Windows сверила время с интернетом — часы снова верные
                fakeTicks += 60000;
                Check("часы: после сверки с интернетом — снова обычные часы", clk2.UtcNow == C(21, 15) && !clk2.InRollback);

                clk2.Save(force: true);
                fakeSys = C(21, 14); // отстают на минуту — это не перевод (сохранение раз в минуту, подстройка часов)
                var clk3 = new TrustedClock(clockFile, sysClock, tickCount);
                Check("часы: расхождение меньше 2 минут — не перевод", !clk3.InRollback);

                fakeSys = C(21, 20);
                Check("часы: обычная работа", clk3.UtcNow == C(21, 20));
                fakeSys = C(19, 0); // часы перевели назад прямо во время работы
                Check("часы: перевод назад во время работы замечен", clk3.UtcNow == C(21, 20) && clk3.InRollback);

                clk3.AcceptSystemClock();
                Check("часы: «принять время компьютера» — снова обычные часы", !clk3.InRollback && clk3.UtcNow == C(19, 0));
                var clk4 = new TrustedClock(clockFile, sysClock, tickCount);
                Check("часы: принятое время сохранено и после перезапуска", !clk4.InRollback && clk4.UtcNow == C(19, 0));
                File.Delete(clockFile);

                // --- ярлык в меню «Пуск» (создаём во временной папке, с русскими буквами и тире в имени) ---
                string lnkDir = Path.Combine(Path.GetTempPath(), "winshutdown-selftest-lnk");
                string lnkPath = Path.Combine(lnkDir, "WinShutDown — настройки.lnk");
                string lnkTarget = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                if (Directory.Exists(lnkDir)) Directory.Delete(lnkDir, true);
                Shortcut.Create(lnkPath, lnkTarget, Environment.SystemDirectory, "Настройки WinShutDown");
                Check("ярлык создан (имя с русскими буквами и тире)", File.Exists(lnkPath));
                Check("ярлык ведёт на нужную программу",
                    string.Equals(Shortcut.ReadTarget(lnkPath), lnkTarget, StringComparison.OrdinalIgnoreCase));
                Directory.Delete(lnkDir, true);

                byte[] wav = Sounds.Alarm();
                Check("звук сгенерирован", wav.Length > 1000 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF");
            }
            catch (Exception ex)
            {
                sb.AppendLine("FAIL  исключение: " + ex);
                fails++;
            }

            sb.AppendLine(fails == 0 ? "ИТОГ: все проверки пройдены" : $"ИТОГ: ошибок — {fails}");
            File.WriteAllText(opt.OutFile ?? Path.Combine(Path.GetTempPath(), "winshutdown-selftest.txt"), sb.ToString(), Encoding.UTF8);
            return fails == 0 ? 0 : 1;
        }
    }
}
