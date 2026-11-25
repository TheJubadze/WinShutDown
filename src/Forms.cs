using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace WinShutDown
{
    internal static class Ui
    {
        public static readonly Font BaseFont = new Font("Segoe UI", 10f);

        public static void SetupDialog(Form f, string title)
        {
            f.Text = title;
            f.Font = BaseFont;
            f.AutoScaleMode = AutoScaleMode.Font;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(0, 0, 16, 14);
        }

        public static TableLayoutPanel Grid(int columns) => new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(16, 12),
        };

        public static void Row(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 14, 3) });
            c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c);
        }

        public static void Span(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(c);
            t.SetColumnSpan(c, t.ColumnCount);
        }

        public static void Header(TableLayoutPanel t, string text)
        {
            Span(t, new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(BaseFont, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 158),
                Margin = new Padding(0, 16, 3, 4),
            });
        }

        public static Label Hint(int maxWidth = 560) => new Label
        {
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 4, 3, 2),
        };

        /// <summary>Вложенная колонка (без собственной позиции).</summary>
        public static TableLayoutPanel Panel(int columns, Padding margin) => new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = margin,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        public static TextBox PasswordBox() => new TextBox { UseSystemPasswordChar = true, Width = 240 };

        public static Button Button(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(6, 3, 0, 3),
        };

        public static void Warn(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static void Info(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Окно настроек (вход по паролю родителя). В режиме установки — первичная настройка и установка службы.</summary>
    internal sealed class SettingsForm : Form
    {
        /// <summary>Строка таблицы расписания: один день недели.</summary>
        sealed class DayRow
        {
            public CheckBox Limited;
            public DateTimePicker Allow, Shutdown;
            public Label Note;

            public DayRule Rule => new DayRule(Minutes(Allow), Minutes(Shutdown)) { Limited = Limited.Checked };

            public void Set(DayRule r)
            {
                Limited.Checked = r.Limited;
                Allow.Value = DateTime.Today.AddMinutes(r.AllowFrom);
                Shutdown.Value = DateTime.Today.AddMinutes(r.Shutdown);
                Refresh();
            }

            public void Refresh()
            {
                Allow.Enabled = Shutdown.Enabled = Limited.Checked;
                int a = Minutes(Allow), s = Minutes(Shutdown);
                if (!Limited.Checked) { Note.Text = "без ограничений"; Note.ForeColor = Color.SeaGreen; }
                else if (a == s) { Note.Text = "время совпадает!"; Note.ForeColor = Color.Firebrick; }
                else if (s < a) { Note.Text = "после полуночи"; Note.ForeColor = Color.DimGray; }
                else Note.Text = "";
            }
        }

        readonly Options opt;
        readonly ControlClient client;
        readonly bool install;
        string password;

        readonly CheckBox chkEnabled = new CheckBox { Text = "Выключать компьютер по расписанию", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 6) };
        readonly DayRow[] rows = new DayRow[7]; // индекс = DayOfWeek
        TableLayoutPanel daysGrid;
        readonly TextBox txtWarn = new TextBox { Width = 120 };
        readonly CheckBox chkVoice = new CheckBox { Text = "Говорить голосом «Осталось X минут»", AutoSize = true, Checked = true, Margin = new Padding(3, 6, 8, 3) };
        readonly CheckBox chkRemote = new CheckBox
        {
            Text = "Разрешить менять настройки из браузера\nна другом компьютере или телефоне в домашней сети",
            AutoSize = true,
            Margin = new Padding(3, 4, 3, 2),
        };
        readonly Label lblRemote = Ui.Hint(390);
        readonly CheckBox chkAdminExempt = new CheckBox
        {
            Text = "Не выключать, пока за компьютером работает администратор Windows",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(3, 10, 3, 0),
        };
        readonly Button btnListen = Ui.Button("Прослушать");
        readonly Label lblVoice = Ui.Hint(390);
        Speaker speaker;
        readonly ComboBox cmbZone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, DropDownWidth = 520 };
        readonly TextBox txtPwd1 = Ui.PasswordBox();
        readonly TextBox txtPwd2 = Ui.PasswordBox();
        readonly NumericUpDown numExtend = new NumericUpDown { Minimum = 5, Maximum = 600, Increment = 5, Value = 30, Width = 70 };
        readonly Label lblDaysHint = Ui.Hint(470);
        readonly Label lblWarnHint = Ui.Hint(390);
        readonly Label lblExt = Ui.Hint(900);
        readonly Label lblStatus = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(3, 4, 3, 0) };
        readonly Button btnSave = Ui.Button("Сохранить");
        readonly Button btnClose = Ui.Button("Закрыть");
        readonly Button btnUninstall = Ui.Button("Удалить программу…");
        readonly Button btnExtend = Ui.Button("Продлить");
        readonly Button btnCancelExt = Ui.Button("Отменить продление");
        readonly WinFormsTimer refreshTimer = new WinFormsTimer { Interval = 5000 };
        readonly List<TimeZoneInfo> zones = TimeZoneInfo.GetSystemTimeZones().ToList();

        /// <summary>Режим установки.</summary>
        public SettingsForm(Options opt)
        {
            this.opt = opt;
            install = true;
            Build();
            LoadValues(true, Schedule.DefaultDays(), new[] { 30, 10, 5 }, true, true, false, TimeZoneInfo.Local.Id);
        }

        /// <summary>Режим изменения настроек (пароль уже проверен).</summary>
        public SettingsForm(Options opt, ControlClient client, string password)
        {
            this.opt = opt;
            this.client = client;
            this.password = password;
            Build();
            StatusInfo st = client.Status();
            LoadValues(st.Enabled, st.Days, st.Warnings, st.Voice, st.AdminExempt, st.RemoteEnabled, st.TimeZoneId);
            ShowStatus(st);
            refreshTimer.Tick += (s, e) => RefreshStatus();
            refreshTimer.Start();
        }

        static DateTimePicker TimePicker() => new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 80,
        };

        static int Minutes(DateTimePicker p) => p.Value.Hour * 60 + p.Value.Minute;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToScreen(Screen.FromControl(this).WorkingArea);
        }

        /// <summary>На маленьком экране (например, 1366×768) окно не помещается по высоте — тогда добавляем прокрутку.</summary>
        void FitToScreen(Rectangle area)
        {
            if (Height <= area.Height) return;
            int width = Width + SystemInformation.VerticalScrollBarWidth;
            AutoSize = false;
            AutoScroll = true;
            Size = new Size(Math.Min(width, area.Width), area.Height);
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top);
        }

        void Build()
        {
            Ui.SetupDialog(this, $"WinShutDown {Installer.AppVersion} — " + (install ? "установка" : "настройки"));
            MinimizeBox = true;

            TableLayoutPanel root = Ui.Grid(2);
            if (install)
            {
                Ui.Span(root, new Label
                {
                    Text = "Программа будет выключать компьютер по расписанию. Перед выключением на экране появится " +
                           "крупное предупреждение со звуком. Изменить настройки или удалить программу можно только по паролю.",
                    AutoSize = true,
                    MaximumSize = new Size(900, 0),
                    Margin = new Padding(3, 4, 3, 0),
                });
            }
            else
            {
                lblStatus.Font = new Font(Ui.BaseFont, FontStyle.Bold);
                Ui.Span(root, lblStatus);
                var ext = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
                ext.Controls.Add(new Label { Text = "Разрешить посидеть подольше сегодня:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) });
                numExtend.Margin = new Padding(3, 4, 4, 3);
                ext.Controls.Add(numExtend);
                ext.Controls.Add(new Label { Text = "мин", AutoSize = true, Margin = new Padding(0, 7, 8, 3) });
                ext.Controls.Add(btnExtend);
                ext.Controls.Add(btnCancelExt);
                Ui.Span(root, ext);
                Ui.Span(root, lblExt);
            }

            // Левая колонка — расписание по дням недели.
            TableLayoutPanel left = Ui.Panel(1, new Padding(0, 0, 30, 0));
            Ui.Header(left, "Расписание");
            left.Controls.Add(chkEnabled);
            daysGrid = BuildDaysGrid();
            left.Controls.Add(daysGrid);
            left.Controls.Add(lblDaysHint);
            left.Controls.Add(chkAdminExempt);
            Label adminHint = Ui.Hint(470);
            adminHint.Margin = new Padding(22, 2, 3, 2);
            adminHint.Text = "Например, пока вы работаете под своей учётной записью. Ограничения вернутся, " +
                             "когда вы выйдете или переключитесь на учётную запись ребёнка.";
            left.Controls.Add(adminHint);

            // Правая колонка — предупреждения, часовой пояс, пароль.
            TableLayoutPanel right = Ui.Panel(2, new Padding(0));
            Ui.Header(right, "Предупреждения");
            Ui.Row(right, "За сколько минут:", txtWarn);
            Ui.Span(right, lblWarnHint);
            var voicePanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            voicePanel.Controls.Add(chkVoice);
            voicePanel.Controls.Add(btnListen);
            Ui.Span(right, voicePanel);
            Ui.Span(right, lblVoice);
            Ui.Header(right, "Часовой пояс");
            Ui.Span(right, cmbZone);
            Ui.Header(right, install ? "Пароль родителя" : "Сменить пароль");
            Ui.Row(right, "Пароль:", txtPwd1);
            Ui.Row(right, "Ещё раз:", txtPwd2);
            Label pwdHint = Ui.Hint(390);
            pwdHint.Text = install
                ? "Без этого пароля нельзя изменить настройки или удалить программу."
                : "Оставьте пустым, чтобы не менять.";
            Ui.Span(right, pwdHint);

            Ui.Header(right, "Управление по сети");
            Ui.Span(right, chkRemote);
            Ui.Span(right, lblRemote);
            chkRemote.CheckedChanged += (s, e) => UpdateRemoteHint();

            root.Controls.Add(left);
            root.Controls.Add(right);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0, 18, 0, 0),
            };
            btnSave.Text = install ? "Установить" : "Сохранить";
            buttons.Controls.Add(btnClose);
            buttons.Controls.Add(btnSave);
            if (!install)
            {
                btnUninstall.Margin = new Padding(3, 3, 40, 3);
                buttons.Controls.Add(btnUninstall);
            }
            Ui.Span(root, buttons);
            Controls.Add(root);

            CancelButton = btnClose;
            if (install) AcceptButton = btnSave;

            chkEnabled.CheckedChanged += (s, e) => UpdateHints();
            txtWarn.TextChanged += (s, e) => UpdateHints();
            btnSave.Click += (s, e) => OnSave();
            btnClose.Click += (s, e) => Close();
            btnExtend.Click += (s, e) => OnExtend((int)numExtend.Value);
            btnCancelExt.Click += (s, e) => OnExtend(0);
            btnUninstall.Click += (s, e) => OnUninstall();
            btnListen.Click += (s, e) => OnListen();
            chkVoice.CheckedChanged += (s, e) => btnListen.Enabled = chkVoice.Checked && speaker != null;
            FormClosed += (s, e) => refreshTimer.Dispose();

            // Голос ищем на этом компьютере: окно настроек открывают на том же компьютере, где работает программа.
            speaker = Speaker.TryCreate();
            lblVoice.Text = speaker != null
                ? "Голос: " + speaker.VoiceName + ". Если голос выключить или его нет, будет звуковой сигнал."
                : "Русский голос в Windows не найден — вместо голоса будет звуковой сигнал. Добавить голос: " +
                  "«Параметры → Время и язык → Речь → Добавить голоса → Русский».";
            btnListen.Enabled = speaker != null;
        }

        void OnListen()
        {
            int first = Schedule.TryParseWarnings(txtWarn.Text, out int[] w, out _) ? w[0] : 5;
            speaker?.Say(Ru.Remaining(TimeSpan.FromMinutes(first)) + " до выключения компьютера");
        }

        TableLayoutPanel BuildDaysGrid()
        {
            var g = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 2) };
            foreach (string h in new[] { "День", "Ограничить", "Можно с", "Выключать в", "" })
                g.Controls.Add(new Label { Text = h, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 10, 2) });

            foreach (DayOfWeek d in Schedule.WeekOrder)
            {
                bool weekend = d == DayOfWeek.Saturday || d == DayOfWeek.Sunday;
                var row = new DayRow
                {
                    Limited = new CheckBox { AutoSize = true, Checked = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 5, 3, 3) },
                    Allow = TimePicker(),
                    Shutdown = TimePicker(),
                    Note = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) },
                };
                rows[(int)d] = row;
                g.Controls.Add(new Label
                {
                    Text = Ru.DayName(d),
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(3, 6, 14, 3),
                    ForeColor = weekend ? Color.FromArgb(176, 42, 42) : SystemColors.ControlText,
                });
                g.Controls.Add(row.Limited);
                g.Controls.Add(row.Allow);
                g.Controls.Add(row.Shutdown);
                g.Controls.Add(row.Note);
                row.Limited.CheckedChanged += (s, e) => row.Refresh();
                row.Allow.ValueChanged += (s, e) => row.Refresh();
                row.Shutdown.ValueChanged += (s, e) => row.Refresh();
            }
            return g;
        }

        void UpdateRemoteHint()
        {
            if (!chkRemote.Checked)
            {
                lblRemote.Text = "Например, со своего компьютера или телефона, подключённого к той же сети Wi-Fi. " +
                                 "Нужен будет пароль родителя.";
                return;
            }
            int port = opt.RemotePort;
            List<string> ips = NetInfo.LocalIPv4();
            string byIp = ips.Count > 0 ? " или " + string.Join(", ", ips.Select(ip => NetInfo.AdminUrl(ip, port))) : "";
            lblRemote.Text = $"Откройте в браузере: {NetInfo.AdminUrl(Environment.MachineName, port)}{byIp} " +
                             "и введите пароль родителя. На телефоне надёжнее адрес с цифрами.";
        }

        void LoadValues(bool enabled, DayRule[] days, int[] warnings, bool voice, bool adminExempt, bool remote, string tz)
        {
            chkEnabled.Checked = enabled;
            chkRemote.Checked = remote;
            UpdateRemoteHint();
            chkVoice.Checked = voice;
            chkAdminExempt.Checked = adminExempt;
            for (int i = 0; i < 7; i++) rows[i].Set(days[i]);
            txtWarn.Text = Schedule.FormatWarnings(warnings, ", ");
            cmbZone.Items.Clear();
            foreach (TimeZoneInfo z in zones) cmbZone.Items.Add(z.DisplayName);
            int idx = zones.FindIndex(z => z.Id == tz);
            if (idx < 0) idx = zones.FindIndex(z => z.Id == TimeZoneInfo.Local.Id);
            cmbZone.SelectedIndex = Math.Max(0, idx);
            UpdateHints();
        }

        void UpdateHints()
        {
            daysGrid.Enabled = chkEnabled.Checked;
            chkAdminExempt.Enabled = chkEnabled.Checked;
            bool ok = Schedule.TryParseWarnings(txtWarn.Text, out int[] w, out string error);
            int final = ok ? w[w.Length - 1] : 5;
            if (!ok)
            {
                lblWarnHint.ForeColor = Color.Firebrick;
                lblWarnHint.Text = error;
            }
            else
            {
                lblWarnHint.ForeColor = Color.DimGray;
                lblWarnHint.Text = w.Length == 1
                    ? $"Через запятую, например: 30, 10, 5. Сейчас — одно предупреждение за {final} мин, оно остаётся на экране до выключения."
                    : $"Через запятую. За {Ru.JoinAnd(w.Take(w.Length - 1).ToArray())} мин надпись появится на 20 секунд, " +
                      $"а за {final} мин — останется на экране до самого выключения.";
            }

            lblDaysHint.Text = !chkEnabled.Checked
                ? "Выключение по расписанию отключено — компьютером можно пользоваться без ограничений."
                : $"Вне указанного времени пользоваться компьютером нельзя: если его включить, он выключится через {final} мин. " +
                  "«Выключать в» — вечер этого дня. Если это время меньше, чем «Можно с», компьютер выключится после полуночи " +
                  "(например, в субботу 09:00 — 00:30).";
        }

        void OnSave()
        {
            DayRule[] days = rows.Select(r => r.Rule).ToArray();
            foreach (DayOfWeek d in Schedule.WeekOrder)
            {
                DayRule r = days[(int)d];
                if (r.Limited && r.AllowFrom == r.Shutdown)
                {
                    Ui.Warn(this, $"{Ru.DayName(d)}: время «Можно с» и «Выключать в» совпадают.\n" +
                                  "Если в этот день ограничений нет — снимите галочку «Ограничить».");
                    return;
                }
            }
            if (!Schedule.TryParseWarnings(txtWarn.Text, out int[] warn, out string warnError))
            {
                Ui.Warn(this, "Предупреждения: " + warnError);
                txtWarn.Focus();
                return;
            }
            string tz = zones[Math.Max(0, cmbZone.SelectedIndex)].Id;

            string newPwd = null;
            if (install || txtPwd1.Text.Length > 0 || txtPwd2.Text.Length > 0)
            {
                if (txtPwd1.Text.Length < 4) { Ui.Warn(this, "Пароль должен быть не короче 4 символов."); txtPwd1.Focus(); return; }
                if (txtPwd1.Text != txtPwd2.Text) { Ui.Warn(this, "Пароли не совпадают."); txtPwd2.Focus(); return; }
                newPwd = txtPwd1.Text;
            }

            UseWaitCursor = true;
            Enabled = false;
            try
            {
                if (install)
                {
                    var cfg = new AppConfig
                    {
                        Enabled = chkEnabled.Checked,
                        Days = days,
                        Warnings = warn,
                        Voice = chkVoice.Checked,
                        AdminExempt = chkAdminExempt.Checked,
                        RemoteEnabled = chkRemote.Checked,
                        TimeZoneId = tz,
                        PasswordHash = PasswordHasher.Hash(newPwd),
                    };
                    Installer.Install(cfg);
                    Enabled = true;
                    UseWaitCursor = false;
                    Ui.Info(this,
                        "WinShutDown установлен и работает.\n\n" + NextShutdownText(cfg) + "\n" +
                        "Настройки: меню «Пуск» → «WinShutDown — настройки» (откроется по паролю родителя).\n\n" +
                        "Важно: учётная запись ребёнка должна быть обычной (не администратором), " +
                        "а у учётной записи администратора должен быть пароль.");
                    Close();
                    return;
                }

                var r = client.Save(password, chkEnabled.Checked, days, warn, chkVoice.Checked, chkAdminExempt.Checked, chkRemote.Checked, tz);
                if (!Protocol.IsOk(r)) { Ui.Warn(this, Protocol.Message(r)); return; }
                if (newPwd != null)
                {
                    r = client.ChangePassword(password, newPwd);
                    if (!Protocol.IsOk(r)) { Ui.Warn(this, Protocol.Message(r)); return; }
                    password = newPwd;
                    txtPwd1.Clear();
                    txtPwd2.Clear();
                }
                RefreshStatus();
                Ui.Info(this, "Настройки сохранены.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Enabled = true;
                UseWaitCursor = false;
            }
        }

        static string NextShutdownText(AppConfig cfg)
        {
            if (!cfg.Enabled) return "Выключение по расписанию пока отключено.";
            DateTime dl = Schedule.BaseDeadlineUtc(cfg, DateTime.UtcNow);
            if (dl == Schedule.Never) return "Все дни без ограничений — выключение не запланировано.";
            TimeZoneInfo zone = Schedule.Zone(cfg.TimeZoneId);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(dl, zone);
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
            return $"Ближайшее выключение: {DayText(local.Date, today)} в {local:HH:mm}.";
        }

        static string DayText(DateTime date, DateTime today)
        {
            if (date == today) return "сегодня";
            if (date == today.AddDays(1)) return "завтра";
            return Ru.DayName(date.DayOfWeek).ToLowerInvariant() + " " + date.ToString("dd.MM", CultureInfo.InvariantCulture);
        }

        void OnExtend(int minutes)
        {
            try
            {
                var r = client.Extend(password, minutes);
                if (!Protocol.IsOk(r)) { Ui.Warn(this, Protocol.Message(r)); return; }
                RefreshStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Constants.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnUninstall()
        {
            if (opt.Test)
            {
                UiFlows.Uninstall(opt);
                Close();
                return;
            }
            if (Elevation.Relaunch("--uninstall")) Close();
        }

        void RefreshStatus()
        {
            try { ShowStatus(client.Status()); }
            catch (Exception ex) { lblStatus.Text = "Служба не отвечает: " + ex.Message; }
        }

        void ShowStatus(StatusInfo st)
        {
            lblStatus.Text = Describe(st);
            bool ext = st.ExtUtc > DateTime.UtcNow;
            lblExt.Text = ext
                ? $"Сегодня разрешено до {st.ExtLocal}."
                : "Минуты добавляются к ближайшему выключению (или ко времени «сейчас», если оно уже наступило).";
            btnCancelExt.Enabled = ext;
            btnExtend.Enabled = st.Active && st.HasDeadline;
        }

        static string Describe(StatusInfo st)
        {
            if (!st.Active) return "Выключение по расписанию сейчас отключено.";
            if (st.Exempt)
                return $"Сейчас за компьютером администратор ({st.ExemptUsers}) — компьютер не выключается. " +
                       "Ограничения снова начнут действовать, когда вы выйдете или переключитесь на учётную запись ребёнка.";
            if (!st.HasDeadline) return "Все дни без ограничений — выключение не запланировано.";
            string day = st.DeadlineDate;
            if (DateTime.TryParseExact(st.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime today) &&
                DateTime.TryParseExact(st.DeadlineDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dl))
                day = DayText(dl, today);
            string s = $"Следующее выключение: {day} в {st.DeadlineLocal} (через {Ru.Duration(st.DeadlineUtc - DateTime.UtcNow)})";
            if (st.Warning) s += " — идут предупреждения";
            return s;
        }
    }

    /// <summary>Запрос пароля. Проверка выполняется переданной функцией (обычно — службой).</summary>
    internal sealed class LoginForm : Form
    {
        readonly TextBox txt = new TextBox { UseSystemPasswordChar = true, Width = 300 };
        readonly Label lblError = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(320, 0) };
        readonly Func<string, Dictionary<string, string>> check;

        public string Password { get; private set; }

        public LoginForm(string title, string prompt, Func<string, Dictionary<string, string>> check)
        {
            this.check = check;
            Ui.SetupDialog(this, title);
            MinimizeBox = false;

            TableLayoutPanel t = Ui.Grid(1);
            t.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(320, 0), Margin = new Padding(3, 4, 3, 6) });
            t.Controls.Add(txt);
            t.Controls.Add(lblError);
            var ok = Ui.Button("OK");
            var cancel = Ui.Button("Отмена");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons);
            Controls.Add(t);

            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += (s, e) => TryLogin();
        }

        void TryLogin()
        {
            UseWaitCursor = true;
            Enabled = false;
            try
            {
                var r = check(txt.Text);
                if (Protocol.IsOk(r))
                {
                    Password = txt.Text;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                lblError.Text = Protocol.Message(r);
            }
            catch (Exception ex)
            {
                lblError.Text = "Нет связи со службой WinShutDown: " + ex.Message;
            }
            finally
            {
                Enabled = true;
                UseWaitCursor = false;
            }
            txt.SelectAll();
            txt.Focus();
        }
    }

    /// <summary>Задание нового пароля (сброс администратором).</summary>
    internal sealed class NewPasswordForm : Form
    {
        readonly TextBox txt1 = Ui.PasswordBox();
        readonly TextBox txt2 = Ui.PasswordBox();

        public string Password { get; private set; }

        public NewPasswordForm()
        {
            Ui.SetupDialog(this, "WinShutDown — новый пароль");
            MinimizeBox = false;
            TableLayoutPanel t = Ui.Grid(2);
            Ui.Span(t, new Label { Text = "Задайте новый пароль родителя:", AutoSize = true, Margin = new Padding(3, 4, 3, 6) });
            Ui.Row(t, "Пароль:", txt1);
            Ui.Row(t, "Ещё раз:", txt2);
            var ok = Ui.Button("OK");
            var cancel = Ui.Button("Отмена");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Ui.Span(t, buttons);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                if (txt1.Text.Length < 4) { Ui.Warn(this, "Пароль должен быть не короче 4 символов."); return; }
                if (txt1.Text != txt2.Text) { Ui.Warn(this, "Пароли не совпадают."); return; }
                Password = txt1.Text;
                DialogResult = DialogResult.OK;
                Close();
            };
        }
    }
}
