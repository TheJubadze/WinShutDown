using System;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace WinShutDown
{
    internal static class Constants
    {
        public const string ServiceName = "WinShutDown";
        public const string DisplayName = "WinShutDown — выключение по расписанию";
        public const string Description = "Принудительно выключает компьютер в заданное время (родительский контроль).";
        public const string PipeName = "WinShutDown.Control.v1";
        public const string TestPipeName = "WinShutDown.Test";
        public const string AppTitle = "WinShutDown";
    }

    /// <summary>Параметры командной строки. Один exe работает в нескольких режимах.</summary>
    internal sealed class Options
    {
        public string Mode = "ui";            // ui | service | agent | console | uninstall | update | reset-password | selftest
        public string PipeName = Constants.PipeName;
        public string ConfigPath = AppConfig.DefaultPath;
        public bool DryRun;                   // не выключать компьютер по-настоящему
        public bool Test;                     // тестовое окружение без установленной службы
        public bool Elevated;                 // процесс уже перезапускался с запросом прав — второй раз не перезапускаем
        public string InitPassword;           // только для --console
        public int InitShutdown = -1, InitAllow = -1;
        public int[] InitWarnings;
        public string OutFile;                // только для --selftest

        public static Options Parse(string[] args)
        {
            var o = new Options();
            bool configGiven = false, pipeGiven = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--service": o.Mode = "service"; break;
                    case "--agent": o.Mode = "agent"; break;
                    case "--console": o.Mode = "console"; break;
                    case "--uninstall": o.Mode = "uninstall"; break;
                    case "--update": o.Mode = "update"; break;
                    case "--reset-password": o.Mode = "reset-password"; break;
                    case "--selftest": o.Mode = "selftest"; break;
                    case "--test": o.Test = true; break;
                    case "--elevated": o.Elevated = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--pipe": o.PipeName = Value(args, ref i); pipeGiven = true; break;
                    case "--config": o.ConfigPath = Path.GetFullPath(Value(args, ref i)); configGiven = true; break;
                    case "--password": o.InitPassword = Value(args, ref i); break;
                    case "--shutdown": o.InitShutdown = Hm(Value(args, ref i)); break;
                    case "--allow": o.InitAllow = Hm(Value(args, ref i)); break;
                    case "--warn":
                        if (!Schedule.TryParseWarnings(Value(args, ref i), out o.InitWarnings, out string err))
                            throw new ArgumentException(err);
                        break;
                    case "--out": o.OutFile = Value(args, ref i); break;
                }
            }

            if (o.Mode == "console")
            {
                // Консольный режим — только для проверки: никогда не выключает компьютер.
                o.Test = true;
                o.DryRun = true;
                if (!pipeGiven) o.PipeName = Constants.TestPipeName;
                if (!configGiven) o.ConfigPath = Path.Combine(Path.GetTempPath(), "WinShutDown-test", "config.ini");
            }
            return o;
        }

        static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Не указано значение для " + args[i]);
            return args[++i];
        }

        static int Hm(string s)
        {
            if (!Schedule.TryParseHm(s, out int m)) throw new ArgumentException("Неверное время: " + s);
            return m;
        }

        /// <summary>Аргументы для запуска окна предупреждения.</summary>
        public string AgentArgs()
        {
            if (!Test) return "--agent";
            return $"--agent --test --pipe \"{PipeName}\" --config \"{ConfigPath}\"";
        }
    }

    /// <summary>Версия .NET Framework: программе нужен 4.7.2+ (есть в Windows 10 начиная с версии 1803).</summary>
    internal static class NetFramework
    {
        const int Release472 = 461808;

        public static int Release()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return k?.GetValue("Release") is int r ? r : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsSupported() => Release() >= Release472;

        public static string ReleaseText()
        {
            int r = Release();
            if (r >= 528040) return "4.8+ (" + r + ")";
            if (r >= Release472) return "4.7.2 (" + r + ")";
            return "старше 4.7.2 (" + r + ")";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options opt;
            try { opt = Options.Parse(args); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            switch (opt.Mode)
            {
                case "service": return RunService(opt);
                case "console": return RunConsole(opt);
                case "agent": return Agent.Run(opt);
                case "selftest": return SelfTest.Run(opt);
                default:
                    // Журнал окон программы — во временной папке текущего пользователя (%TEMP%\WinShutDown.log).
                    Log.FilePath = Path.Combine(Path.GetTempPath(), "WinShutDown.log");
                    Log.Info($"Запуск: «{string.Join(" ", args)}», пользователь {Environment.UserName}, " +
                             $"Windows {Environment.OSVersion.Version}, .NET Framework {NetFramework.ReleaseText()}");
                    return UiFlows.Run(opt);
            }
        }

        static int RunService(Options opt)
        {
            Log.FilePath = Path.Combine(AppConfig.DataDir, "log.txt");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("Необработанная ошибка службы", e.ExceptionObject as Exception);
                Environment.Exit(1); // Windows перезапустит службу (настроено при установке)
            };
            ServiceBase.Run(new ShutdownService(opt));
            return 0;
        }

        /// <summary>Тестовый запуск логики службы от имени текущего пользователя, без выключения.</summary>
        static int RunConsole(Options opt)
        {
            string dir = Path.GetDirectoryName(opt.ConfigPath);
            Directory.CreateDirectory(dir);
            Log.FilePath = Path.Combine(dir, "log.txt");

            if (!File.Exists(opt.ConfigPath) && opt.InitPassword != null)
            {
                var c = new AppConfig { PasswordHash = PasswordHasher.Hash(opt.InitPassword) };
                if (opt.InitShutdown >= 0 || opt.InitAllow >= 0)
                    c.Days = Schedule.SameEveryDay(opt.InitAllow >= 0 ? opt.InitAllow : 7 * 60, opt.InitShutdown >= 0 ? opt.InitShutdown : 22 * 60);
                if (opt.InitWarnings != null) c.Warnings = opt.InitWarnings;
                c.Save(opt.ConfigPath);
            }

            var engine = new Engine(opt);
            var done = new ManualResetEvent(false);
            engine.ExitRequested += () => done.Set();
            engine.Start();
            done.WaitOne();
            engine.Stop();
            Thread.Sleep(300);
            return 0;
        }
    }
}
