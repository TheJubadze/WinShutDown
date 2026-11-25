using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinShutDown
{
    /// <summary>
    /// Сервер именованного канала. Обычные пользователи могут только читать/писать в канал,
    /// но не создавать свои экземпляры с тем же именем (защита от подмены службы).
    /// </summary>
    internal sealed class PipeServer
    {
        const int PIPE_ACCESS_DUPLEX = 0x3;
        const int FILE_FLAG_FIRST_PIPE_INSTANCE = 0x80000;
        const int FILE_FLAG_OVERLAPPED = 0x40000000;
        const int PIPE_TYPE_BYTE = 0x0, PIPE_READMODE_BYTE = 0x0, PIPE_WAIT = 0x0, PIPE_REJECT_REMOTE_CLIENTS = 0x8;

        readonly string name;
        readonly Func<Dictionary<string, string>, Dictionary<string, string>> handler;
        readonly string sddl;

        public PipeServer(string name, Func<Dictionary<string, string>, Dictionary<string, string>> handler)
        {
            this.name = name;
            this.handler = handler;
            string me = WindowsIdentity.GetCurrent().User.Value;
            // SYSTEM, администраторы и владелец — полный доступ; остальные — чтение/запись (0x12019b — без FILE_CREATE_PIPE_INSTANCE).
            sddl = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{me})(A;;0x12019b;;;AU)";
        }

        public void Start() => new Thread(Loop) { IsBackground = true, Name = "pipe-server" }.Start();

        void Loop()
        {
            NamedPipeServerStream listener = null;
            bool needFirst = true, errorLogged = false;
            while (true)
            {
                try
                {
                    if (listener == null)
                    {
                        listener = Create(needFirst);
                        needFirst = false;
                        errorLogged = false;
                    }
                    listener.WaitForConnection();
                    var connection = listener;
                    listener = null;
                    // Следующий экземпляр создаём до обработки текущего — канал никогда не остаётся «свободным».
                    try { listener = Create(false); }
                    catch (Exception ex) { Log.Error("Не удалось создать экземпляр канала", ex); }
                    ThreadPool.QueueUserWorkItem(_ => Serve(connection));
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { Log.Error("Ошибка канала управления", ex); errorLogged = true; }
                    try { listener?.Dispose(); } catch { }
                    listener = null;
                    Thread.Sleep(1000);
                }
            }
        }

        NamedPipeServerStream Create(bool first)
        {
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr sd, IntPtr.Zero))
                throw new Win32Exception();
            try
            {
                var sa = new Native.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                };
                int openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
                int pipeMode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
                IntPtr h = Native.CreateNamedPipe(@"\\.\pipe\" + name, openMode, pipeMode, 255, 8192, 8192, 0, ref sa);
                if (h == new IntPtr(-1)) throw new Win32Exception();
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, new SafePipeHandle(h, true));
            }
            finally
            {
                Native.LocalFree(sd);
            }
        }

        void Serve(NamedPipeServerStream connection)
        {
            try
            {
                using (connection)
                {
                    var read = Task.Run(() => Protocol.Read(connection));
                    if (!read.Wait(5000)) return; // клиент молчит — отключаем
                    Dictionary<string, string> response;
                    try { response = handler(read.Result); }
                    catch (Exception ex)
                    {
                        Log.Error("Ошибка обработки запроса", ex);
                        response = Protocol.Error("internal", "Внутренняя ошибка службы");
                    }
                    byte[] data = Protocol.Encode(response);
                    connection.Write(data, 0, data.Length);
                    connection.Flush();
                    try { connection.WaitForPipeDrain(); } catch { }
                }
            }
            catch
            {
                // клиент отключился — ничего страшного
            }
        }
    }
}
