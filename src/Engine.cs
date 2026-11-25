using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace WinShutDown
{
    internal struct Snapshot
    {
        public bool Active;     // расписание включено и пароль задан
        public bool Exempt;     // за компьютером администратор — не предупреждаем и не выключаем
        public bool Warning;    // идёт предупреждение
        public bool Due;        // пора выключать
        public DateTime DeadlineUtc;
    }

    /// <summary>Ядро: следит за временем, показывает предупреждение и выключает компьютер.</summary>
    internal sealed class Engine
    {
        const int MaxFailedLogins = 5;
        static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);

        readonly Options opt;
        readonly object sync = new object();
        readonly object authSync = new object();
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);

        AppConfig cfg = new AppConfig { PasswordHash = "" }; // объект не изменяется — только заменяется целиком
        DateTime cfgStamp = DateTime.MinValue;
        bool cfgMissingLogged;
        DateTime? baseDeadlineUtc;
        DateTime lastTickUtc = DateTime.MinValue;
        DateTime lastShutdownAttemptUtc = DateTime.MinValue;
        bool lastWarning;
        int failedLogins;
        DateTime lockedUntilUtc = DateTime.MinValue;
        AgentManager agents;
        bool exempt;            // сейчас за компьютером только администраторы
        string exemptUsers;
        bool sessionErrorLogged;
        WebAdminServer web;     // управление через браузер
        MdnsResponder mdns;     // постоянный адрес winshutdown.local
        string voiceName;       // русский голос на этом компьютере (для веб-админки)

        public event Action ExitRequested;

        TrustedClock clock;     // время, защищённое от перевода часов назад
        DateTime lastResyncUtc = DateTime.MinValue;

        public Engine(Options opt) { this.opt = opt; }

        /// <summary>Текущее время (UTC), которому можно доверять — см. TrustedClock.</summary>
        DateTime Now => clock.UtcNow;

        public void Start()
        {
            clock = new TrustedClock(Path.Combine(Path.GetDirectoryName(opt.ConfigPath), "clock.dat"));
            lock (sync) ReloadConfigIfChanged(true);
            agents = new AgentManager(opt);
            if (!opt.Test) StartTimeSync();
            voiceName = Speaker.FindRussianVoiceName();
            // В тестовом режиме — только на этом компьютере (127.0.0.1), чтобы не открывать порт в сеть.
            web = new WebAdminServer(this, opt.RemotePort, loopbackOnly: opt.Test);
            mdns = new MdnsResponder(opt.MdnsPort, loopbackOnly: opt.Test);
            new PipeServer(opt.PipeName, Handle).Start();
            new Thread(Loop) { IsBackground = true, Name = "engine" }.Start();
            Log.Info($"Запущено, версия {Installer.AppVersion}" + (opt.DryRun ? " — ТЕСТОВЫЙ режим (компьютер не выключается)" : ""));
        }

        public void Stop()
        {
            stopEvent.Set();
            clock?.Save(force: true); // при выключении Windows — запомнить время
        }

        /// <summary>Попросить Windows сверить часы с интернетом (в фоне, не чаще раза в 10 минут).</summary>
        void StartTimeSync()
        {
            DateTime real = DateTime.UtcNow;
            if (real - lastResyncUtc < TimeSpan.FromMinutes(10) && lastResyncUtc <= real) return;
            lastResyncUtc = real;
            ThreadPool.QueueUserWorkItem(_ => TimeSync.TryResync());
        }

        void Loop()
        {
            Tick();
            while (!stopEvent.WaitOne(1000)) Tick();
        }

        void Tick()
        {
            try
            {
                DateTime now = Now;
                clock.Save(); // раз в минуту запоминаем время
                if (clock.InRollback && !opt.Test) StartTimeSync(); // часы отстают — пробуем исправить их сверкой с интернетом
                Snapshot s;
                AppConfig c;
                string admins = cfg.AdminExempt ? AdminsOnlyAtComputer() : null; // обращения к Windows — вне блокировки
                lock (sync)
                {
                    ReloadConfigIfChanged(false);
                    // Компьютер проснулся после сна/гибернации — пересчитываем (иначе выключился бы без предупреждения).
                    if (lastTickUtc != DateTime.MinValue && Math.Abs((now - lastTickUtc).TotalSeconds) > 30)
                    {
                        Log.Info("Обнаружен перерыв в работе (сон/гибернация) — пересчёт времени выключения");
                        baseDeadlineUtc = null;
                    }
                    lastTickUtc = now;
                    UpdateExemption(admins);
                    s = Evaluate(now);
                    c = cfg;
                }

                if (s.Warning != lastWarning)
                {
                    lastWarning = s.Warning;
                    Log.Info(s.Warning
                        ? $"Начались предупреждения (за {Ru.JoinAnd(c.Warnings)} мин): выключение в {LocalHm(s.DeadlineUtc, c)}"
                        : "Предупреждения сняты");
                }

                agents.Update(s.Warning);
                bool remote = c.RemoteEnabled && !string.IsNullOrEmpty(c.PasswordHash);
                web.SetEnabled(remote);
                mdns.SetEnabled(remote);

                if (s.Due && (now - lastShutdownAttemptUtc).TotalSeconds >= 60)
                {
                    lastShutdownAttemptUtc = now;
                    clock.Save(force: true); // запомнить время выключения — перевод часов назад после этого не поможет
                    Power.ForceShutdown(opt.DryRun);
                    if (opt.DryRun) lock (sync) baseDeadlineUtc = null; // имитация «компьютер включили снова»
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка в основном цикле", ex);
            }
        }

        /// <summary>
        /// Имена пользователей, если за компьютером сейчас работают только администраторы Windows; иначе null.
        /// Экран входа (никто не вошёл) не считается — иначе выход на экран входа давал бы «ещё несколько минут».
        /// </summary>
        string AdminsOnlyAtComputer()
        {
            try
            {
                List<UserSession> users = Sessions.ActiveUsers(opt.Test);
                sessionErrorLogged = false;
                if (users.Count == 0 || users.Any(u => !u.Admin)) return null;
                return string.Join(", ", users.Select(u => u.User).Distinct());
            }
            catch (Exception ex)
            {
                if (!sessionErrorLogged) Log.Error("Не удалось проверить, кто работает за компьютером", ex);
                sessionErrorLogged = true;
                return null;
            }
        }

        /// <summary>Вызывать под lock(sync).</summary>
        void UpdateExemption(string admins)
        {
            bool nowExempt = admins != null && cfg.AdminExempt;
            if (nowExempt == exempt)
            {
                if (nowExempt) exemptUsers = admins;
                return;
            }
            exempt = nowExempt;
            exemptUsers = admins;
            // Пересчёт: если администратор ушёл в запретное время, ребёнок получит обычное последнее предупреждение,
            // а не мгновенное выключение.
            baseDeadlineUtc = null;
            Log.Info(nowExempt
                ? $"За компьютером администратор ({admins}) — предупреждения и выключение приостановлены"
                : "Администратор больше не работает за компьютером — ограничения снова действуют");
        }

        /// <summary>Вызывать под lock(sync).</summary>
        Snapshot Evaluate(DateTime now)
        {
            if (!cfg.Enabled || string.IsNullOrEmpty(cfg.PasswordHash))
            {
                baseDeadlineUtc = null;
                return new Snapshot();
            }
            if (exempt)
            {
                // Время выключения по расписанию показываем справочно (и от него считается продление), но не действуем.
                DateTime planned = Schedule.BaseDeadlineUtc(cfg, now);
                if (cfg.ExtensionUntilUtcTicks > planned.Ticks) planned = new DateTime(cfg.ExtensionUntilUtcTicks, DateTimeKind.Utc);
                return new Snapshot { Active = true, Exempt = true, DeadlineUtc = planned };
            }
            if (baseDeadlineUtc == null)
            {
                baseDeadlineUtc = Schedule.BaseDeadlineUtc(cfg, now);
                Log.Info(baseDeadlineUtc.Value == Schedule.Never
                    ? "Выключение не запланировано: все дни без ограничений"
                    : "Следующее выключение: " + LocalText(baseDeadlineUtc.Value, cfg));
            }
            DateTime deadline = baseDeadlineUtc.Value;
            if (cfg.ExtensionUntilUtcTicks > deadline.Ticks)
                deadline = new DateTime(cfg.ExtensionUntilUtcTicks, DateTimeKind.Utc);
            TimeSpan left = deadline - now;
            return new Snapshot
            {
                Active = true,
                DeadlineUtc = deadline,
                Warning = left <= TimeSpan.FromMinutes(cfg.FirstWarning), // окно предупреждений нужно с первого порога
                Due = left <= TimeSpan.Zero,
            };
        }

        /// <summary>Вызывать под lock(sync).</summary>
        void ReloadConfigIfChanged(bool force)
        {
            string path = opt.ConfigPath;
            try
            {
                if (!File.Exists(path))
                {
                    if (!cfgMissingLogged)
                    {
                        Log.Error("Файл настроек не найден: " + path + " — выключение по расписанию неактивно");
                        cfgMissingLogged = true;
                    }
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (!force && stamp == cfgStamp) return;
                AppConfig loaded = AppConfig.Load(path);
                if (!loaded.SameSchedule(cfg)) baseDeadlineUtc = null;
                cfg = loaded;
                cfgStamp = stamp;
                cfgMissingLogged = false;
                if (!force) Log.Info("Настройки перечитаны с диска");
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось прочитать настройки", ex);
            }
        }

        /// <summary>Вызывать под lock(sync).</summary>
        void SaveConfig(AppConfig next)
        {
            next.Save(opt.ConfigPath);
            cfg = next;
            cfgStamp = File.GetLastWriteTimeUtc(opt.ConfigPath);
        }

        // ---------------- запросы по каналу управления ----------------

        Dictionary<string, string> Handle(Dictionary<string, string> req)
        {
            switch (Protocol.Get(req, "cmd"))
            {
                case "status": return StatusResponse();
                case "login": return Authorized(req, Protocol.Ok);
                case "save": return Authorized(req, () => SaveSettings(req));
                case "extend": return Authorized(req, () => Extend(req));
                case "passwd": return Authorized(req, () => ChangePassword(req));
                case "uninstall": return Authorized(req, Uninstall);
                case "update": return Authorized(req, StopForUpdate);
                case "trustclock": return Authorized(req, AcceptClock);
                default: return Protocol.Error("unknown", "Неизвестная команда");
            }
        }

        /// <summary>Родитель подтвердил, что часы компьютера верные (например, после замены батарейки BIOS).</summary>
        Dictionary<string, string> AcceptClock()
        {
            clock.AcceptSystemClock();
            lock (sync) baseDeadlineUtc = null;
            return StatusResponse();
        }

        /// <summary>Проверка пароля с защитой от подбора: после 5 ошибок — блокировка на 5 минут.</summary>
        Dictionary<string, string> Authorized(Dictionary<string, string> req, Func<Dictionary<string, string>> action)
        {
            string pwd = Protocol.Get(req, "pwd") ?? "";
            lock (authSync) // проверки идут строго по одной — параллельный перебор не поможет
            {
                DateTime now = DateTime.UtcNow;
                if (now < lockedUntilUtc) return LockedError(now);

                string hash;
                lock (sync) hash = cfg.PasswordHash;
                if (!PasswordHasher.Verify(pwd, hash)) return RegisterFailure(now, "");
                failedLogins = 0;
            }
            return action();
        }

        /// <summary>Вызывать под lock(authSync).</summary>
        Dictionary<string, string> LockedError(DateTime now) =>
            Protocol.Error("locked", "Слишком много неверных попыток. Подождите " + Ru.Duration(lockedUntilUtc - now) + ".");

        /// <summary>Вызывать под lock(authSync). Счётчик ошибок общий для входа на компьютере и через браузер.</summary>
        Dictionary<string, string> RegisterFailure(DateTime now, string where)
        {
            failedLogins++;
            Log.Info($"Неверный пароль{where} (попытка {failedLogins})");
            if (failedLogins >= MaxFailedLogins)
            {
                failedLogins = 0;
                lockedUntilUtc = now + LockoutTime;
                Log.Info("Вход заблокирован на 5 минут");
                return Protocol.Error("locked", "Слишком много неверных попыток. Вход заблокирован на 5 минут.");
            }
            return Protocol.Error("badpwd", "Неверный пароль");
        }

        // ---------------- управление через браузер ----------------

        /// <summary>Первый шаг запроса из браузера: одноразовое число и параметры, чтобы страница вычислила ключ из пароля.</summary>
        public Dictionary<string, string> RemoteHello(string nonce)
        {
            string hash;
            lock (sync) hash = cfg.PasswordHash;
            if (!PasswordHasher.TryParse(hash, out int iter, out byte[] salt, out byte[] key))
                return Protocol.Error("nopwd", "На компьютере ребёнка не задан пароль родителя");
            var r = Protocol.Ok();
            r["nonce"] = nonce;
            r["salt"] = Convert.ToBase64String(salt);
            r["iter"] = iter.ToString(CultureInfo.InvariantCulture);
            r["len"] = key.Length.ToString(CultureInfo.InvariantCulture);
            r["machine"] = Environment.MachineName;
            r["version"] = Installer.AppVersion;
            return r;
        }

        /// <summary>
        /// Запрос из браузера: подпись HMAC-SHA256 (ключ = хэш пароля) над присланным текстом запроса.
        /// По сети доступны только статус, сохранение настроек и продление; пароль, удаление и обновление — на месте.
        /// </summary>
        public Dictionary<string, string> HandleRemote(Dictionary<string, string> req, string mac, byte[] payload, string from)
        {
            lock (authSync)
            {
                DateTime now = DateTime.UtcNow;
                if (now < lockedUntilUtc) return LockedError(now);
                string hash;
                lock (sync) hash = cfg.PasswordHash;
                bool ok = PasswordHasher.TryParse(hash, out _, out _, out byte[] key) &&
                          RemoteAuth.SameMac(mac, RemoteAuth.Mac(key, payload));
                if (!ok) return RegisterFailure(now, $" (через браузер с {from})");
                failedLogins = 0;
            }

            switch (Protocol.Get(req, "cmd"))
            {
                case "status":
                    return StatusResponse();
                case "login":
                    Log.Info($"Вход через браузер с {from}");
                    return Protocol.Ok();
                case "save":
                    Log.Info($"Изменение настроек через браузер с {from}");
                    return SaveSettings(req);
                case "extend":
                    Log.Info($"Продление через браузер с {from}");
                    return Extend(req);
                default:
                    return Protocol.Error("localonly", "Это можно сделать только на компьютере ребёнка");
            }
        }

        Dictionary<string, string> SaveSettings(Dictionary<string, string> req)
        {
            if (!Schedule.TryParseDays(Protocol.Get(req, "days"), out DayRule[] days, out string daysError))
                return Protocol.Error("bad", daysError);
            if (!Schedule.TryParseWarnings(Protocol.Get(req, "warn"), out int[] warn, out string warnError))
                return Protocol.Error("bad", warnError);
            string tz = Protocol.Get(req, "tz");
            try { TimeZoneInfo.FindSystemTimeZoneById(tz); }
            catch { return Protocol.Error("bad", "Неизвестный часовой пояс"); }
            bool enabled = Protocol.Get(req, "enabled") == "1";
            bool voice = Protocol.Get(req, "voice") != "0";
            bool adminExempt = Protocol.Get(req, "adminexempt") != "0";
            string remoteParam = Protocol.Get(req, "remote"); // нет — оставить как есть (окна настроек прежних версий)

            lock (sync)
            {
                AppConfig next = cfg.Clone();
                next.Enabled = enabled;
                next.Voice = voice;
                next.AdminExempt = adminExempt;
                if (remoteParam != null) next.RemoteEnabled = remoteParam == "1";
                next.Days = days;
                next.Warnings = warn;
                next.TimeZoneId = tz;
                SaveConfig(next);
                baseDeadlineUtc = null;
                if (!adminExempt && exempt) UpdateExemption(null); // сразу, не дожидаясь следующей проверки сеансов
            }
            Log.Info($"Настройки изменены: включено={(enabled ? "да" : "нет")}, расписание [{DescribeDays(days)}], " +
                     $"предупреждения за {Ru.JoinAnd(warn)} мин{(voice ? " голосом" : " звуком")}, " +
                     $"не выключать при администраторе={(adminExempt ? "да" : "нет")}, пояс {tz}" +
                     (remoteParam != null ? $", управление через браузер={(remoteParam == "1" ? "да" : "нет")}" : ""));
            return StatusResponse();
        }

        Dictionary<string, string> Extend(Dictionary<string, string> req)
        {
            if (!int.TryParse(Protocol.Get(req, "minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
                minutes < 0 || minutes > 12 * 60)
                return Protocol.Error("bad", "Неверное количество минут");

            DateTime now = Now;
            lock (sync)
            {
                AppConfig next = cfg.Clone();
                if (minutes == 0)
                {
                    next.ExtensionUntilUtcTicks = 0;
                    SaveConfig(next);
                    baseDeadlineUtc = null; // пересчёт, чтобы не выключить мгновенно без предупреждения
                }
                else
                {
                    DateTime from = now;
                    Snapshot s = Evaluate(now);
                    if (s.Active && s.DeadlineUtc == Schedule.Never)
                        return Protocol.Error("bad", "Сейчас нет ограничений — продлевать нечего");
                    if (s.Active && s.DeadlineUtc > from) from = s.DeadlineUtc;
                    next.ExtensionUntilUtcTicks = from.AddMinutes(minutes).Ticks;
                    SaveConfig(next);
                }
            }
            Log.Info(minutes == 0 ? "Продление отменено" : $"Продлено на {minutes} мин");
            return StatusResponse();
        }

        Dictionary<string, string> ChangePassword(Dictionary<string, string> req)
        {
            string newPwd = Protocol.Get(req, "newpwd") ?? "";
            if (newPwd.Length < 4) return Protocol.Error("bad", "Пароль должен быть не короче 4 символов");
            string hash = PasswordHasher.Hash(newPwd);
            lock (sync)
            {
                AppConfig next = cfg.Clone();
                next.PasswordHash = hash;
                SaveConfig(next);
            }
            Log.Info("Пароль изменён");
            return Protocol.Ok();
        }

        Dictionary<string, string> Uninstall()
        {
            Log.Info("Получена команда удаления — служба останавливается");
            RequestExit();
            return Protocol.Ok();
        }

        /// <summary>Остановка для обновления: файл программы заменит окно обновления, затем служба будет запущена снова.</summary>
        Dictionary<string, string> StopForUpdate()
        {
            Log.Info("Получена команда обновления — служба останавливается для замены файла программы");
            RequestExit();
            return Protocol.Ok();
        }

        void RequestExit()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(500);
                ExitRequested?.Invoke();
            });
        }

        Dictionary<string, string> StatusResponse()
        {
            DateTime now = Now;
            Snapshot s;
            AppConfig c;
            string users;
            lock (sync)
            {
                s = Evaluate(now);
                c = cfg;
                users = exemptUsers;
            }
            var inv = CultureInfo.InvariantCulture;
            TimeZoneInfo zone = Schedule.Zone(c.TimeZoneId);
            var r = Protocol.Ok();
            r["enabled"] = c.Enabled ? "1" : "0";
            r["active"] = s.Active ? "1" : "0";
            r["warning"] = s.Warning ? "1" : "0";
            r["exempt"] = s.Exempt ? "1" : "0";
            if (s.Exempt) r["exemptUsers"] = users ?? "";
            r["days"] = Schedule.FormatDays(c.Days);
            r["warn"] = Schedule.FormatWarnings(c.Warnings);
            r["voice"] = c.Voice ? "1" : "0";
            r["adminexempt"] = c.AdminExempt ? "1" : "0";
            r["remote"] = c.RemoteEnabled ? "1" : "0";
            r["tz"] = c.TimeZoneId;
            r["tzName"] = zone.DisplayName;
            r["machine"] = Environment.MachineName;
            r["version"] = Installer.AppVersion;
            if (voiceName != null) r["voiceName"] = voiceName;
            r["now"] = now.Ticks.ToString(inv); // время службы — окна считают обратный отсчёт по нему, а не по своим часам
            TimeSpan behind = clock.Behind;
            if (behind > TimeSpan.Zero) r["clockBehindMin"] = ((int)Math.Ceiling(behind.TotalMinutes)).ToString(inv);
            r["today"] = TimeZoneInfo.ConvertTimeFromUtc(now, zone).ToString("yyyy-MM-dd", inv);
            if (s.Active && s.DeadlineUtc != Schedule.Never)
            {
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(s.DeadlineUtc, zone);
                r["deadline"] = s.DeadlineUtc.Ticks.ToString(inv);
                r["deadlineLocal"] = local.ToString("HH:mm", inv);
                r["deadlineDate"] = local.ToString("yyyy-MM-dd", inv);
                r["leftMin"] = Math.Max(0, (int)Math.Ceiling((s.DeadlineUtc - now).TotalMinutes)).ToString(inv);
            }
            if (c.ExtensionUntilUtcTicks > now.Ticks)
            {
                var ext = new DateTime(c.ExtensionUntilUtcTicks, DateTimeKind.Utc);
                r["ext"] = ext.Ticks.ToString(inv);
                r["extLocal"] = TimeZoneInfo.ConvertTimeFromUtc(ext, zone).ToString("HH:mm", inv);
            }
            return r;
        }

        static string DescribeDays(DayRule[] days) =>
            string.Join(", ", Schedule.WeekOrder.Select(d =>
                Ru.DayShort(d) + " " + (days[(int)d].Limited
                    ? Schedule.FormatHm(days[(int)d].AllowFrom) + "–" + Schedule.FormatHm(days[(int)d].Shutdown)
                    : "без ограничений")));

        static string LocalHm(DateTime utc, AppConfig c) =>
            TimeZoneInfo.ConvertTimeFromUtc(utc, Schedule.Zone(c.TimeZoneId)).ToString("HH:mm", CultureInfo.InvariantCulture);

        static string LocalText(DateTime utc, AppConfig c) =>
            TimeZoneInfo.ConvertTimeFromUtc(utc, Schedule.Zone(c.TimeZoneId)).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Служба Windows. Остановить её штатно нельзя — только удалить программу с паролем.</summary>
    internal sealed class ShutdownService : ServiceBase
    {
        readonly Options opt;
        Engine engine;

        public ShutdownService(Options opt)
        {
            this.opt = opt;
            ServiceName = Constants.ServiceName;
            CanStop = false;
            CanPauseAndContinue = false;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            engine = new Engine(opt);
            engine.ExitRequested += () =>
            {
                try { Stop(); }
                catch (Exception ex) { Log.Error("Остановка службы", ex); Environment.Exit(0); }
            };
            engine.Start();
        }

        protected override void OnStop()
        {
            engine?.Stop();
            Log.Info("Служба остановлена");
        }

        protected override void OnShutdown() => engine?.Stop();
    }

    internal static class Power
    {
        const uint SHUTDOWN_FORCE_OTHERS = 0x1, SHUTDOWN_FORCE_SELF = 0x2, SHUTDOWN_POWEROFF = 0x8;
        const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

        public static void ForceShutdown(bool dryRun)
        {
            if (dryRun)
            {
                Log.Info("ТЕСТ: в этот момент компьютер был бы выключен");
                return;
            }
            Log.Info("Время вышло — принудительное выключение компьютера");
            EnableShutdownPrivilege();

            // Без задержки (grace = 0): выключение нельзя отменить командой «shutdown /a».
            uint rc = Native.InitiateShutdown(null, null, 0, SHUTDOWN_FORCE_OTHERS | SHUTDOWN_FORCE_SELF | SHUTDOWN_POWEROFF, SHTDN_REASON_FLAG_PLANNED);
            if (rc == 0) return;
            Log.Error("InitiateShutdown: код " + rc);

            if (Native.InitiateSystemShutdownEx(null, null, 0, true, false, SHTDN_REASON_FLAG_PLANNED)) return;
            Log.Error("InitiateSystemShutdownEx: код " + Marshal.GetLastWin32Error());

            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /f /t 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex) { Log.Error("shutdown.exe", ex); }
        }

        static void EnableShutdownPrivilege()
        {
            const int TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return;
            try
            {
                if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid)) return;
                var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Native.CloseHandle(token); }
        }
    }
}
