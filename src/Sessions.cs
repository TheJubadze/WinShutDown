using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinShutDown
{
    internal struct UserSession
    {
        public int Id;
        public string User;
        public bool Admin;
    }

    /// <summary>Сеансы пользователей, вошедших в Windows, и проверка «администратор ли это».</summary>
    internal static class Sessions
    {
        const int WTSActive = 0;
        const int TokenGroups = 2;

        /// <summary>
        /// Активные сеансы, в которые вошёл пользователь (экран компьютера или удалённый рабочий стол).
        /// Экран входа в Windows сюда не попадает. Заблокированный сеанс (Win+L) остаётся активным.
        /// </summary>
        public static List<UserSession> ActiveUsers(bool test)
        {
            var list = new List<UserSession>();
            if (test)
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    list.Add(new UserSession { Id = Process.GetCurrentProcess().SessionId, User = id.Name, Admin = IsAdminMember(id.Token) });
                return list;
            }
            foreach (int session in ActiveSessionIds())
            {
                if (!Native.WTSQueryUserToken(session, out IntPtr token)) continue; // в сеансе никто не вошёл
                try
                {
                    using (var id = new WindowsIdentity(token))
                        list.Add(new UserSession { Id = session, User = id.Name, Admin = IsAdminMember(token) });
                }
                catch (Exception ex)
                {
                    Log.Error("Не удалось определить пользователя сеанса " + session, ex);
                }
                finally
                {
                    Native.CloseHandle(token);
                }
            }
            return list;
        }

        public static List<int> ActiveSessionIds()
        {
            var list = new List<int>();
            if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr p, out int count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var info = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(p + i * size, typeof(Native.WTS_SESSION_INFO));
                    if (info.State == WTSActive && info.SessionId != 0) list.Add(info.SessionId);
                }
            }
            finally { Native.WTSFreeMemory(p); }
            return list;
        }

        /// <summary>
        /// Входит ли пользователь токена в группу «Администраторы». Учитываются и группы «только для запрета»:
        /// при включённом UAC у администратора в обычном (не повышенном) режиме группа «Администраторы» именно такая.
        /// </summary>
        public static bool IsAdminMember(IntPtr token)
        {
            Native.GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out int length);
            if (length <= 0) return false;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!Native.GetTokenInformation(token, TokenGroups, buffer, length, out length)) return false;
                // TOKEN_GROUPS: DWORD GroupCount, затем (с выравниванием по размеру указателя) массив SID_AND_ATTRIBUTES.
                int count = Marshal.ReadInt32(buffer);
                int size = Marshal.SizeOf(typeof(Native.SID_AND_ATTRIBUTES));
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                for (int i = 0; i < count; i++)
                {
                    var group = (Native.SID_AND_ATTRIBUTES)Marshal.PtrToStructure(buffer + IntPtr.Size + i * size, typeof(Native.SID_AND_ATTRIBUTES));
                    if (group.Sid != IntPtr.Zero && new SecurityIdentifier(group.Sid) == admins) return true;
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
