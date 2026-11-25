using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace WinShutDown
{
    /// <summary>Окно предупреждения «Осталось X минут» — запускается службой в сеансе пользователя.</summary>
    internal static class Agent
    {
        public static int Run(Options opt)
        {
            Log.FilePath = opt.Test
                ? Path.Combine(Path.GetDirectoryName(opt.ConfigPath), "agent-log.txt")
                : Path.Combine(Path.GetTempPath(), "WinShutDown-agent.log"); // %TEMP% пользователя, в сеансе которого показывается окно

            using (var mutex = new Mutex(true, @"Local\WinShutDown.Agent." + opt.PipeName, out bool created))
            {
                if (!created) return 0; // окно уже показано в этом сеансе
                Log.Info($"Окно предупреждения запущено: пользователь {Environment.UserName}, сеанс {System.Diagnostics.Process.GetCurrentProcess().SessionId}");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Log.Error("Ошибка окна предупреждения", e.Exception); Environment.Exit(1); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Ошибка окна предупреждения", e.ExceptionObject as Exception);
                Application.Run(new OverlayContext(opt));
            }
            return 0;
        }
    }

    /// <summary>
    /// Логика показа. Пример для порогов 30/10/5:
    ///  • за 30 и за 10 минут — надпись на 20 секунд и голосовое предупреждение;
    ///  • с 5 минут — надпись остаётся до выключения, голос каждую минуту, в последние 10 секунд — отсчёт.
    /// Голос: «Осталось 5 минут до выключения компьютера» (если в Windows есть русский голос, иначе звуковой сигнал).
    /// Надпись показывается на всех мониторах поверх окон, но окно никогда не перехватывает фокус и не сворачивает игру:
    /// в оконном/безрамочном режиме игры надпись видна, в эксклюзивном полноэкранном — слышен только голос.
    /// </summary>
    internal sealed class OverlayContext : ApplicationContext
    {
        const double AnnounceSeconds = 20;

        readonly Options opt;
        readonly ControlClient client;
        readonly List<OverlayForm> forms = new List<OverlayForm>();
        readonly WinFormsTimer timer = new WinFormsTimer { Interval = 200 };
        readonly SoundPlayer alarm, tick;
        volatile StatusInfo status;
        volatile bool quit;
        bool stageKnown;
        int? lastStage;
        DateTime announceUntil = DateTime.MinValue;
        int lastMinutes = int.MaxValue, lastSecond = -1;
        DateTime lastTopmost = DateTime.MinValue;
        Speaker speaker;
        bool speakerTried;

        public OverlayContext(Options opt)
        {
            this.opt = opt;
            client = new ControlClient(opt);
            foreach (Screen screen in Screen.AllScreens) forms.Add(new OverlayForm(screen));
            alarm = MakePlayer(Sounds.Alarm());
            tick = MakePlayer(Sounds.Tick());
            timer.Tick += OnTick;
            timer.Start();
            new Thread(Poll) { IsBackground = true, Name = "status-poll" }.Start();
        }

        static SoundPlayer MakePlayer(byte[] wav)
        {
            var p = new SoundPlayer(new MemoryStream(wav));
            try { p.Load(); } catch { }
            return p;
        }

        void Poll()
        {
            int idle = 0;
            DateTime lastContact = DateTime.UtcNow;
            while (!quit)
            {
                try
                {
                    StatusInfo st = client.Status();
                    status = st;
                    lastContact = DateTime.UtcNow;
                    idle = st.Warning ? 0 : idle + 1;
                    if (idle >= 3) quit = true; // предупреждение снято (например, родитель продлил время)
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - lastContact).TotalSeconds > 15)
                    {
                        Log.Error("Служба недоступна — окно предупреждения закрывается", ex);
                        quit = true;
                    }
                }
                if (!quit) Thread.Sleep(1000);
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (quit)
            {
                timer.Stop();
                foreach (var f in forms) f.Dispose();
                ExitThread();
                return;
            }

            StatusInfo st = status;
            if (st == null) return;
            DateTime now = DateTime.UtcNow;
            TimeSpan left = st.DeadlineUtc - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            int? stage = st.Warning ? Schedule.Stage(left, st.Warnings) : null;

            if (stage == null)
            {
                HideAll();
                stageKnown = false;
                lastStage = null;
                return;
            }

            int minutes = (int)Math.Ceiling(left.TotalMinutes);
            int seconds = (int)Math.Ceiling(left.TotalSeconds);
            bool finalPhase = left.TotalMinutes <= st.Warnings.Min();
            bool attention = false; // голосовое предупреждение (или звуковой сигнал)

            if (!stageKnown || stage != lastStage)
            {
                // Новый порог (или окно только что запустилось, или время продлили) — объявляем.
                stageKnown = true;
                lastStage = stage;
                lastMinutes = minutes;
                announceUntil = now.AddSeconds(AnnounceSeconds);
                attention = true;
            }
            else if (finalPhase)
            {
                if (minutes < lastMinutes && seconds > 10) attention = true; // каждую минуту
                lastMinutes = minutes;
            }

            bool show = finalPhase || now < announceUntil;
            if (!show)
            {
                HideAll();
                return;
            }

            string main = Ru.Remaining(left);
            string sub = $"В {st.DeadlineLocal} компьютер выключится. Сохрани свою работу!";
            bool urgent = left.TotalSeconds <= 60;
            bool raise = (now - lastTopmost).TotalSeconds >= 1;
            foreach (var f in forms)
            {
                f.SetText(main, sub, urgent);
                if (!f.Visible) f.Show();
                if (raise) f.KeepOnTop();
            }
            if (raise) lastTopmost = now;

            if (attention)
            {
                if (!Say(st, Ru.Remaining(left) + " до выключения компьютера")) Play(alarm);
            }
            if (finalPhase && seconds >= 1 && seconds <= 10 && seconds != lastSecond)
            {
                lastSecond = seconds;
                if (!Say(st, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))) Play(tick);
            }
        }

        void HideAll()
        {
            foreach (var f in forms) if (f.Visible) f.Hide();
        }

        /// <summary>Сказать фразу голосом. false — голос выключен в настройках или русского голоса в Windows нет.</summary>
        bool Say(StatusInfo st, string text)
        {
            if (!st.Voice) return false;
            if (!speakerTried)
            {
                speakerTried = true;
                speaker = Speaker.TryCreate();
                Log.Info(speaker != null ? "Голос: " + speaker.VoiceName : "Русский голос не найден — будет звуковой сигнал");
            }
            if (speaker == null) return false;
            if (speaker.Say(text)) return true;
            speaker = null; // голос сломался — дальше сигналы звуком
            return false;
        }

        static void Play(SoundPlayer p)
        {
            try { p.Play(); } catch { }
        }
    }

    /// <summary>Полупрозрачная полоса поверх всех окон. Клики проходят сквозь неё — можно сохранить работу.</summary>
    internal sealed class OverlayForm : Form
    {
        const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly Color Accent = Color.FromArgb(230, 57, 70);

        string main = "", sub = "";
        bool urgent;

        public OverlayForm(Screen screen)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(16, 16, 22);
            Opacity = 0.9;
            DoubleBuffered = true;
            Text = "WinShutDown";
            Rectangle b = screen.Bounds;
            int h = Math.Max(200, b.Height * 28 / 100);
            Bounds = new Rectangle(b.X, b.Y + b.Height * 15 / 100, b.Width, h);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetText(string m, string s, bool u)
        {
            if (m == main && s == sub && u == urgent) return;
            main = m;
            sub = s;
            urgent = u;
            Invalidate();
        }

        public void KeepOnTop()
        {
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            int bar = Math.Max(4, Height / 32);
            using (var accent = new SolidBrush(Accent))
            {
                g.FillRectangle(accent, 0, 0, Width, bar);
                g.FillRectangle(accent, 0, Height - bar, Width, bar);
            }
            Color mainColor = urgent ? Color.FromArgb(255, 96, 96) : Color.White;
            DrawFitted(g, main, FontStyle.Bold, Height * 0.42f, new RectangleF(0, bar, Width, Height * 0.60f), mainColor);
            DrawFitted(g, sub, FontStyle.Regular, Height * 0.11f, new RectangleF(0, Height * 0.62f, Width, Height * 0.28f), Color.FromArgb(235, 235, 235));
        }

        static void DrawFitted(Graphics g, string text, FontStyle style, float px, RectangleF rect, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                var font = new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
                SizeF size = g.MeasureString(text, font);
                float maxWidth = rect.Width * 0.94f;
                if (size.Width > maxWidth)
                {
                    float fitted = Math.Max(8f, px * maxWidth / size.Width);
                    font.Dispose();
                    font = new Font("Segoe UI", fitted, style, GraphicsUnit.Pixel);
                }
                using (font)
                using (var brush = new SolidBrush(color))
                    g.DrawString(text, font, brush, rect, sf);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4, WM_CLOSE от других программ и «Снять задачу» окно не закрывают.
            if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing)
                e.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    /// <summary>Звуки генерируются программно — не зависят от звуковой схемы Windows.</summary>
    internal static class Sounds
    {
        public static byte[] Alarm() => Build((880, 170), (0, 70), (1175, 170), (0, 70), (1568, 320));

        public static byte[] Tick() => Build((1319, 120));

        static byte[] Build(params (double freq, int ms)[] parts)
        {
            const int rate = 22050;
            var samples = new List<short>();
            double fade = rate * 0.01;
            foreach (var (freq, ms) in parts)
            {
                int n = rate * ms / 1000;
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, n - i) / fade);
                    double t = (double)i / rate;
                    double v = freq <= 0 ? 0 :
                        (Math.Sin(2 * Math.PI * freq * t) * 0.8 + Math.Sin(4 * Math.PI * freq * t) * 0.2) * 0.85 * env;
                    samples.Add((short)(v * short.MaxValue));
                }
            }

            int dataLen = samples.Count * 2;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLen);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);       // PCM
                w.Write((short)1);       // моно
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLen);
                foreach (short s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
