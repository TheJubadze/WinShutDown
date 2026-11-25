using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShutDown
{
    /// <summary>
    /// Запускает окно предупреждения в сеансе каждого вошедшего пользователя и перезапускает его,
    /// если окно закрыли. Процесс окна создаётся с правами, которые не дают пользователю его завершить.
    /// </summary>
    internal sealed class AgentManager
    {
        // SYSTEM, администраторы и системные службы — полный доступ; сам пользователь (OW/IU) — только
        // «узнать информацию» и «ожидать», без права завершить (PROCESS_TERMINATE) или приостановить процесс.
        const string AgentSddl = "D:(A;;0x1fffff;;;SY)(A;;0x1fffff;;;BA)(A;;0x1fffff;;;LS)(A;;0x1fffff;;;NS)(A;;0x101000;;;OW)(A;;0x101000;;;IU)";
        const int CREATE_UNICODE_ENVIRONMENT = 0x400;

        readonly Options opt;
        readonly string exe;
        readonly Dictionary<int, IntPtr> running = new Dictionary<int, IntPtr>();
        readonly Dictionary<int, DateTime> lastLaunch = new Dictionary<int, DateTime>();

        public AgentManager(Options opt)
        {
            this.opt = opt;
            exe = Assembly.GetExecutingAssembly().Location;
        }

        public void Update(bool warning)
        {
            foreach (int session in running.Keys.ToList())
            {
                IntPtr h = running[session];
                if (Native.WaitForSingleObject(h, 0) == 0) // процесс завершился
                {
                    Native.CloseHandle(h);
                    running.Remove(session);
                }
            }
            if (!warning) return; // окна закрываются сами, когда предупреждение снято

            foreach (int session in ActiveSessions())
            {
                if (running.ContainsKey(session)) continue;
                if (lastLaunch.TryGetValue(session, out DateTime t) && (DateTime.UtcNow - t).TotalSeconds < 3) continue;
                lastLaunch[session] = DateTime.UtcNow;
                IntPtr h = Launch(session);
                if (h != IntPtr.Zero) running[session] = h;
            }
        }

        List<int> ActiveSessions() =>
            opt.Test ? new List<int> { Process.GetCurrentProcess().SessionId } : Sessions.ActiveSessionIds();

        IntPtr Launch(int session)
        {
            IntPtr token = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!opt.Test)
                {
                    if (!Native.WTSQueryUserToken(session, out token)) return IntPtr.Zero; // в сеансе никто не вошёл
                    if (!Native.CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;
                }
                IntPtr h = Start(token, env, true);
                if (h == IntPtr.Zero) h = Start(token, env, false); // запасной вариант — без защиты процесса
                return h;
            }
            finally
            {
                if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
                if (token != IntPtr.Zero) Native.CloseHandle(token);
            }
        }

        IntPtr Start(IntPtr token, IntPtr env, bool protect)
        {
            IntPtr sd = IntPtr.Zero;
            if (protect && !Native.ConvertStringSecurityDescriptorToSecurityDescriptor(AgentSddl, 1, out sd, IntPtr.Zero))
                return IntPtr.Zero;
            try
            {
                var sa = new Native.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                };
                var si = new Native.STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(Native.STARTUPINFO)),
                    lpDesktop = @"winsta0\default",
                };
                var cmd = new StringBuilder($"\"{exe}\" {opt.AgentArgs()}");
                int flags = env != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0;
                string dir = Path.GetDirectoryName(exe);

                Native.PROCESS_INFORMATION pi;
                bool ok = token != IntPtr.Zero
                    ? Native.CreateProcessAsUser(token, exe, cmd, ref sa, IntPtr.Zero, false, flags, env, dir, ref si, out pi)
                    : Native.CreateProcess(exe, cmd, ref sa, IntPtr.Zero, false, flags, env, dir, ref si, out pi);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Error($"Не удалось запустить окно предупреждения ({(protect ? "с защитой" : "без защиты")}), код {err}");
                    return IntPtr.Zero;
                }
                Native.CloseHandle(pi.hThread);
                Log.Info($"Окно предупреждения запущено, PID {pi.dwProcessId}{(protect ? "" : " (без защиты процесса)")}");
                return pi.hProcess;
            }
            finally
            {
                if (sd != IntPtr.Zero) Native.LocalFree(sd);
            }
        }
    }
}
