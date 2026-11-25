using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinShutDown
{
    /// <summary>
    /// Время, которому можно доверять. Обычный пользователь не может менять часы Windows, но если на BIOS/UEFI нет пароля,
    /// часы можно перевести назад до загрузки Windows. Поэтому служба запоминает «последнее известное время» (раз в минуту
    /// и перед выключением) в защищённом файле. Если после включения часы Windows оказались раньше него, служба им не верит
    /// и считает время сама: от сохранённого момента по внутреннему счётчику Windows (GetTickCount64), который с BIOS не связан.
    /// Как только часы снова станут верными (например, Windows сверит их с интернетом), служба вернётся к обычным часам.
    /// </summary>
    internal sealed class TrustedClock
    {
        /// <summary>Расхождение, которое считается переводом часов (меньше — обычная подстройка или запоздалое сохранение).</summary>
        public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);
        static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

        readonly string path;
        readonly Func<DateTime> system;
        readonly Func<long> ticksMs;
        readonly object sync = new object();
        bool rollback;              // часы Windows отстают — считаем время сами
        DateTime anchorUtc;         // в этом режиме: время = anchorUtc + сколько прошло по счётчику с anchorTicks
        long anchorTicks;
        DateTime lastGiven = DateTime.MinValue; // время никогда не идёт назад
        DateTime lastSaved = DateTime.MinValue;

        public TrustedClock(string path) : this(path, () => DateTime.UtcNow, () => (long)Native.GetTickCount64()) { }

        public TrustedClock(string path, Func<DateTime> system, Func<long> ticksMs)
        {
            this.path = path;
            this.system = system;
            this.ticksMs = ticksMs;
            DateTime saved = Load();
            DateTime now = system();
            if (saved != DateTime.MinValue && saved - now > Tolerance)
                EnterRollback(saved, now);
            lastGiven = saved > now ? saved : now;
        }

        /// <summary>Часы Windows сейчас отстают от настоящего времени (их перевели назад).</summary>
        public bool InRollback
        {
            get { lock (sync) return rollback; }
        }

        /// <summary>На сколько часы Windows отстают (0 — не отстают).</summary>
        public TimeSpan Behind
        {
            get
            {
                DateTime trusted = UtcNow;
                TimeSpan d = trusted - system();
                return InRollback && d > TimeSpan.Zero ? d : TimeSpan.Zero;
            }
        }

        public DateTime UtcNow
        {
            get
            {
                lock (sync)
                {
                    DateTime sys = system();
                    DateTime t;
                    if (rollback)
                    {
                        DateTime counted = anchorUtc + TimeSpan.FromMilliseconds(ticksMs() - anchorTicks);
                        if (sys >= counted)
                        {
                            rollback = false; // часы догнали (сверка с интернетом или просто прошло время)
                            Log.Info("Часы компьютера снова показывают верное время — используются обычные часы Windows");
                            t = sys;
                        }
                        else
                        {
                            t = counted;
                        }
                    }
                    else if (lastGiven != DateTime.MinValue && lastGiven - sys > Tolerance)
                    {
                        EnterRollback(lastGiven, sys); // часы перевели назад во время работы
                        t = lastGiven;
                    }
                    else
                    {
                        t = sys;
                    }
                    if (t < lastGiven) t = lastGiven; // мелкие подстройки часов назад — время просто ненадолго «стоит»
                    lastGiven = t;
                    return t;
                }
            }
        }

        void EnterRollback(DateTime markUtc, DateTime sys)
        {
            rollback = true;
            anchorUtc = markUtc;
            anchorTicks = ticksMs();
            Log.Info($"Часы компьютера переведены назад: они отстают от последнего известного времени на {Ru.Duration(markUtc - sys)}. " +
                     "Программа считает время сама, от последнего известного момента");
        }

        /// <summary>Родитель подтвердил, что часы Windows верные (например, села батарейка BIOS и время выставили заново).</summary>
        public void AcceptSystemClock()
        {
            lock (sync)
            {
                rollback = false;
                lastGiven = system();
            }
            Save(force: true);
            Log.Info("Время компьютера принято родителем как верное");
        }

        /// <summary>Сохранить «последнее известное время»: раз в минуту, а с force — сразу (перед выключением).</summary>
        public void Save(bool force = false)
        {
            DateTime now = UtcNow;
            lock (sync)
            {
                if (!force && now - lastSaved < SaveInterval) return;
                lastSaved = now;
            }
            if (path == null) return;
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, now.Ticks.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
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
            catch (Exception ex)
            {
                Log.Error("Не удалось сохранить последнее известное время", ex);
            }
        }

        DateTime Load()
        {
            try
            {
                if (path != null && File.Exists(path) &&
                    long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) &&
                    t > 0 && t <= DateTime.MaxValue.Ticks)
                    return new DateTime(t, DateTimeKind.Utc);
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось прочитать последнее известное время", ex);
            }
            return DateTime.MinValue;
        }
    }

    /// <summary>Попросить Windows сверить часы с интернетом (служба времени Windows, w32tm).</summary>
    internal static class TimeSync
    {
        public static bool TryResync()
        {
            try
            {
                Run("net.exe", "start w32time", 30000); // если служба времени уже работает — ошибка, это нормально
                int code = Run("w32tm.exe", "/resync", 60000);
                Log.Info(code == 0 ? "Windows сверила время с интернетом" : $"Не удалось сверить время с интернетом (код {code}) — нет сети?");
                return code == 0;
            }
            catch (Exception ex)
            {
                Log.Error("Сверка времени с интернетом", ex);
                return false;
            }
        }

        static int Run(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return -1;
                }
                return p.ExitCode;
            }
        }
    }
}
