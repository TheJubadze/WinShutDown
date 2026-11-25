using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinShutDown
{
    /// <summary>Сценарии запуска с окнами: установка, настройки, удаление, сброс пароля.</summary>
    internal static class UiFlows
    {
        public static int Run(Options opt)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (!NetFramework.IsSupported())
            {
                MessageBox.Show(
                    "Для WinShutDown нужен .NET Framework 4.7.2 или новее, а на этом компьютере установлена более старая версия.\n\n" +
                    "Обновите Windows через «Центр обновления Windows» или установите .NET Framework 4.8 с сайта Microsoft " +
                    "(поиск: «.NET Framework 4.8 download»), затем запустите WinShutDown.exe ещё раз.",
                    Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            try
            {
                switch (opt.Mode)
                {
                    case "uninstall": Uninstall(opt); break;
                    case "update": Update(opt); break;
                    case "reset-password": ResetPassword(opt); break;
                    default: OpenSettings(opt); break;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка", ex);
                MessageBox.Show(ex.Message, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static void OpenSettings(Options opt)
        {
            if (!opt.Test && !ServiceUtil.IsInstalled())
            {
                // Первый запуск — установка (нужны права администратора).
                if (!Elevation.EnsureAdmin(opt, "")) return;
                Application.Run(new SettingsForm(opt));
                return;
            }

            // Запущен другой файл программы, чем установлен (например, скачанная новая версия) — предлагаем обновить.
            if (!opt.Test && Installer.DiffersFromInstalled())
            {
                DialogResult answer = MessageBox.Show(
                    "На этом компьютере уже установлен WinShutDown\n" +
                    $"версии {Installer.InstalledVersionText()}.\n\n" +
                    "Вы запустили другую версию:\n" +
                    $"{Installer.ThisVersionText()}.\n\n" +
                    "Обновить программу до этой версии?\n" +
                    "Расписание, пароль и остальные настройки сохранятся.\n\n" +
                    "«Нет» — просто открыть настройки.",
                    Constants.AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes)
                {
                    Update(opt);
                    return;
                }
            }

            var client = new ControlClient(opt);
            try { client.Status(); }
            catch (Exception ex)
            {
                MessageBox.Show("Служба WinShutDown не отвечает.\n\n" + ex.Message + "\n\nПопробуйте перезагрузить компьютер.",
                    Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string pwd;
            using (var login = new LoginForm("WinShutDown", "Введите пароль родителя:", client.Login))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
                pwd = login.Password;
            }
            Application.Run(new SettingsForm(opt, client, pwd));
        }

        public static void Uninstall(Options opt)
        {
            if (!opt.Test && !Elevation.EnsureAdmin(opt, "--uninstall")) return;
            if (MessageBox.Show("Удалить WinShutDown с этого компьютера?\nПринудительное выключение перестанет работать.",
                    Constants.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var client = new ControlClient(opt);
            bool alive;
            try { client.Status(); alive = true; }
            catch { alive = false; }

            // Служба проверит пароль и сама остановится; если она не работает — проверяем пароль по файлу настроек.
            Func<string, Dictionary<string, string>> check = alive
                ? (Func<string, Dictionary<string, string>>)client.Uninstall
                : p => LocalPasswordCheck(opt, p);

            using (var login = new LoginForm("Удаление WinShutDown", "Введите пароль родителя:", check))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
            }

            if (opt.Test)
            {
                MessageBox.Show("Тестовый режим: служба остановлена, файлы не удаляются.", Constants.AppTitle);
                return;
            }
            Installer.Uninstall();
            MessageBox.Show("WinShutDown удалён с компьютера.", Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Проверка пароля по файлу настроек — когда служба не отвечает (для этого нужны права администратора).</summary>
        static Dictionary<string, string> LocalPasswordCheck(Options opt, string password)
        {
            AppConfig cfg = File.Exists(opt.ConfigPath) ? AppConfig.Load(opt.ConfigPath) : null;
            return cfg == null || PasswordHasher.Verify(password, cfg.PasswordHash)
                ? Protocol.Ok()
                : Protocol.Error("badpwd", "Неверный пароль");
        }

        /// <summary>
        /// Обновление поверх установленной версии: права администратора + пароль родителя. Служба по паролю
        /// останавливается, файл программы заменяется, служба запускается снова. Настройки сохраняются.
        /// </summary>
        static void Update(Options opt)
        {
            if (!Elevation.EnsureAdmin(opt, "--update")) return;
            if (!ServiceUtil.IsInstalled())
            {
                MessageBox.Show("WinShutDown на этом компьютере не установлен.\nЗапустите программу без параметров, чтобы установить её.",
                    Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Installer.DiffersFromInstalled())
            {
                MessageBox.Show($"Эта версия уже установлена: {Installer.InstalledVersionText()}.",
                    Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var client = new ControlClient(opt);
            bool alive;
            try { client.Status(); alive = true; }
            catch { alive = false; }
            Func<string, Dictionary<string, string>> check = alive
                ? (Func<string, Dictionary<string, string>>)client.StopForUpdate
                : p => LocalPasswordCheck(opt, p);

            using (var login = new LoginForm("Обновление WinShutDown", "Введите пароль родителя:", check))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
            }

            Cursor.Current = Cursors.WaitCursor;
            Installer.Update();
            Cursor.Current = Cursors.Default;
            MessageBox.Show(
                "WinShutDown обновлён.\n\n" +
                $"Установлена версия {Installer.InstalledVersionText()}.\n" +
                "Расписание, пароль и настройки сохранены, служба работает.",
                Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Если пароль забыт: запуск «WinShutDown.exe --reset-password» под администратором.</summary>
        static void ResetPassword(Options opt)
        {
            if (!Elevation.EnsureAdmin(opt, "--reset-password")) return;
            if (!File.Exists(opt.ConfigPath))
                throw new FileNotFoundException("Файл настроек не найден: " + opt.ConfigPath);
            using (var f = new NewPasswordForm())
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                AppConfig cfg = AppConfig.Load(opt.ConfigPath);
                cfg.PasswordHash = PasswordHasher.Hash(f.Password);
                cfg.Save(opt.ConfigPath); // служба перечитает файл сама
            }
            MessageBox.Show("Пароль изменён.", Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class Installer
    {
        const string Svc = Constants.ServiceName;

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinShutDown");
        public static string InstalledExe => Path.Combine(InstallDir, "WinShutDown.exe");
        static string SelfPath => Path.GetFullPath(Assembly.GetExecutingAssembly().Location);

        /// <summary>Версия этой программы, например «1.1.0».</summary>
        public static string AppVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        static string VersionText(string exe)
        {
            string v = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?";
            if (Version.TryParse(v, out Version parsed)) v = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
            return $"{v} (сборка от {File.GetLastWriteTime(exe):dd.MM.yyyy HH:mm})";
        }

        public static string InstalledVersionText() => File.Exists(InstalledExe) ? VersionText(InstalledExe) : "?";

        public static string ThisVersionText() => VersionText(SelfPath);

        /// <summary>Запущена не установленная копия программы, а другой файл с другим содержимым (другая версия или сборка).</summary>
        public static bool DiffersFromInstalled()
        {
            try
            {
                if (!File.Exists(InstalledExe)) return false;
                if (string.Equals(SelfPath, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;
                return !SameContent(SelfPath, InstalledExe);
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось сравнить с установленной версией", ex);
                return false;
            }
        }

        static bool SameContent(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(a))) ==
                       Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(b)));
        }
        static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "WinShutDown — настройки.lnk");

        static readonly string[] SafeBootKeys =
        {
            @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\" + Svc,
            @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + Svc,
        };

        public static void Install(AppConfig cfg)
        {
            if (ServiceUtil.IsInstalled()) throw new InvalidOperationException("WinShutDown уже установлен.");

            // 1. Программа — в Program Files: обычный пользователь не может её изменить или подменить.
            Directory.CreateDirectory(InstallDir);
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (!string.Equals(self, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, InstalledExe, true);

            // 2. Настройки — в ProgramData, доступ только у SYSTEM и администраторов.
            PrepareDataDir();
            cfg.Save(AppConfig.DefaultPath);

            // 3. Служба: автозапуск, перезапуск при сбое.
            Sc($"create {Svc} binPath= \"\\\"{InstalledExe}\\\" --service\" start= auto DisplayName= \"{Constants.DisplayName}\"");
            Sc($"description {Svc} \"{Constants.Description}\"", optional: true);
            Sc($"failure {Svc} reset= 86400 actions= restart/2000/restart/2000/restart/5000", optional: true);
            Sc($"failureflag {Svc} 1", optional: true);
            AddFirewallRule();

            // 4. Служба запускается и в безопасном режиме.
            foreach (string k in SafeBootKeys)
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(k))
                    key?.SetValue("", "Service");

            // 5. Старт.
            using (var sc = new ServiceController(Svc))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }

            // 6. Ярлык настроек в меню «Пуск» для всех пользователей.
            try { CreateShortcut(); }
            catch { /* не критично */ }
        }

        public static void Uninstall()
        {
            try
            {
                using (var sc = new ServiceController(Svc))
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            catch { }

            Sc($"delete {Svc}", optional: true);
            RemoveFirewallRule();

            uint pid = ServiceUtil.GetServicePid();
            if (pid != 0)
            {
                try
                {
                    Process.EnterDebugMode();
                    Process.GetProcessById((int)pid).Kill();
                }
                catch { }
            }

            KillOtherInstances();

            foreach (string k in SafeBootKeys)
                try { Registry.LocalMachine.DeleteSubKeyTree(k, false); } catch { }
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { if (Directory.Exists(AppConfig.DataDir)) Directory.Delete(AppConfig.DataDir, true); } catch { }

            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string dir = Path.GetFullPath(InstallDir);
            if (self.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleDelete(dir); // нельзя удалить запущенный exe — удалим через пару секунд
            }
            else
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { ScheduleDelete(dir); }
            }
        }

        /// <summary>
        /// Обновление на месте. Служба к этому моменту уже остановлена командой по паролю. Заменяет файл программы
        /// и снова запускает службу; настройки (ProgramData) не трогает. Если файл заменить не удалось, служба всё равно
        /// запускается — в прежней версии, чтобы защита не осталась выключенной.
        /// </summary>
        public static void Update()
        {
            Log.Info($"Обновление: {InstalledVersionText()} -> {ThisVersionText()}");
            Exception error = null;
            bool restartSuppressed = false;
            try
            {
                using (var sc = new ServiceController(Svc))
                {
                    try
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    catch
                    {
                        // Служба сама не остановилась — останавливаем принудительно, временно отключив её автоперезапуск.
                        Log.Info("Служба не остановилась сама — принудительная остановка");
                        Sc($"failure {Svc} reset= 0 actions= \"\"", optional: true);
                        restartSuppressed = true;
                        uint pid = ServiceUtil.GetServicePid();
                        if (pid != 0)
                        {
                            Process.EnterDebugMode();
                            Process.GetProcessById((int)pid).Kill();
                        }
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }

                KillOtherInstances(); // окна предупреждения и настроек, запущенные из Program Files, держат файл открытым
                CopyWithRetry(SelfPath, InstalledExe);
                Log.Info("Файл программы заменён");

                foreach (string k in SafeBootKeys)
                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey(k))
                        key?.SetValue("", "Service");
                try { CreateShortcut(); } catch { }
                AddFirewallRule(); // в прежних версиях правила не было
            }
            catch (Exception ex)
            {
                error = ex;
                Log.Error("Ошибка обновления", ex);
            }
            finally
            {
                if (restartSuppressed)
                    Sc($"failure {Svc} reset= 86400 actions= restart/2000/restart/2000/restart/5000", optional: true);
                using (var sc = new ServiceController(Svc))
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                Log.Info("Служба запущена после обновления");
            }
            if (error != null)
                throw new InvalidOperationException(
                    "Не удалось обновить файл программы, поэтому служба запущена в прежней версии — защита работает.\n\n" + error.Message, error);
        }

        static void CopyWithRetry(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(from, to, true);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(500); // процесс службы ещё не до конца завершился
                }
                catch (UnauthorizedAccessException) when (attempt < 20)
                {
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>Завершить все остальные копии WinShutDown (окна предупреждения, окна настроек).</summary>
        static void KillOtherInstances()
        {
            try { Process.EnterDebugMode(); } catch { }
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("WinShutDown"))
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        static void ScheduleDelete(string dir)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch { }
        }

        static void PrepareDataDir()
        {
            string dir = AppConfig.DataDir;
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            Directory.CreateDirectory(dir);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false); // не наследовать права «Пользователи: создание файлов» от ProgramData
            foreach (WellKnownSidType type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            Directory.SetAccessControl(dir, sec);
        }

        const string FirewallRuleName = "WinShutDown web admin";

        /// <summary>
        /// Разрешить в брандмауэре Windows входящие подключения к веб-админке: только порт программы, только её exe
        /// и только из локальной подсети. Сама служба слушает порт, только если управление через браузер включено.
        /// </summary>
        static void AddFirewallRule()
        {
            RemoveFirewallRule();
            Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{InstalledExe}\" " +
                  $"protocol=TCP localport={WebAdminServer.DefaultPort} remoteip=localsubnet profile=any enable=yes");
        }

        static void RemoveFirewallRule() => Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

        static void Netsh(string args)
        {
            try { Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args, optional: true); }
            catch (Exception ex) { Log.Error("netsh " + args, ex); }
        }

        static void Sc(string args, bool optional = false) => Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, optional);

        static void Run(string exe, string args, bool optional)
        {
            Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && !optional)
                    throw new InvalidOperationException($"Ошибка {Path.GetFileName(exe)} {args}\n\n{output.Trim()}");
            }
        }

        static void CreateShortcut()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                try
                {
                    Type t = lnk.GetType();
                    t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
                    t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
                    t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Настройки WinShutDown" });
                    t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                }
                finally { Marshal.FinalReleaseComObject(lnk); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }

    internal static class Elevation
    {
        public static bool IsAdmin()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Контроль учётных записей (UAC) отключён. Тогда Windows не может запросить пароль администратора:
        /// запуск «от имени администратора» из обычной учётной записи молча запускает программу без прав.
        /// </summary>
        public static bool UacDisabled()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                    return k?.GetValue("EnableLUA") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// true — права администратора уже есть, можно продолжать.
        /// false — текущий процесс должен завершиться: либо запущена копия с запросом прав (один раз, с меткой
        /// --elevated), либо права получить нельзя и пользователю показано, что делать. Повторных перезапусков нет.
        /// </summary>
        public static bool EnsureAdmin(Options opt, string args)
        {
            bool admin = IsAdmin();
            bool uacOff = UacDisabled();
            Log.Info($"Проверка прав: администратор={admin}, UAC отключён={uacOff}, уже перезапускались={opt.Elevated}");
            if (admin) return true;

            if (opt.Elevated || uacOff)
            {
                // Строки разбиты вручную: иначе окно сообщения переносит русский текст посреди слов.
                string text = uacOff
                    ? "Для установки и удаления WinShutDown\n" +
                      "нужны права администратора.\n\n" +
                      "В Windows отключён контроль учётных записей (UAC),\n" +
                      $"поэтому из учётной записи «{Environment.UserName}»\n" +
                      "запросить пароль администратора нельзя.\n\n" +
                      "Что сделать: выйдите из Windows (или смените\n" +
                      "пользователя), войдите под учётной записью\n" +
                      "администратора и запустите WinShutDown.exe ещё раз."
                    : "Для установки и удаления WinShutDown\n" +
                      "нужны права администратора, но Windows их не выдала.\n\n" +
                      "Что сделать: щёлкните WinShutDown.exe правой кнопкой\n" +
                      "мыши → «Запуск от имени администратора». Или войдите\n" +
                      "в Windows под учётной записью администратора\n" +
                      "и запустите программу ещё раз.";
                MessageBox.Show(text, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            Relaunch((args + " --elevated").Trim());
            return false;
        }

        /// <summary>Перезапуск с запросом прав администратора (UAC). false — пользователь отказался.</summary>
        public static bool Relaunch(string args)
        {
            try
            {
                Log.Info("Перезапуск с запросом прав администратора: " + args);
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return true;
            }
            catch (Win32Exception ex)
            {
                Log.Info("Запрос прав администратора отклонён: " + ex.Message);
                return false;
            }
        }
    }

    internal static class ServiceUtil
    {
        const int SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4, SC_STATUS_PROCESS_INFO = 0;

        public static bool IsInstalled()
        {
            IntPtr scm = Native.OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = Native.OpenService(scm, Constants.ServiceName, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;
                Native.CloseServiceHandle(svc);
                return true;
            }
            finally { Native.CloseServiceHandle(scm); }
        }

        /// <summary>PID процесса службы (0 — не запущена или не установлена).</summary>
        public static uint GetServicePid()
        {
            IntPtr scm = Native.OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return 0;
            try
            {
                IntPtr svc = Native.OpenService(scm, Constants.ServiceName, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return 0;
                try
                {
                    if (!Native.QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, out Native.SERVICE_STATUS_PROCESS st,
                            Marshal.SizeOf(typeof(Native.SERVICE_STATUS_PROCESS)), out _))
                        return 0;
                    return (uint)st.dwProcessId;
                }
                finally { Native.CloseServiceHandle(svc); }
            }
            finally { Native.CloseServiceHandle(scm); }
        }
    }
}
