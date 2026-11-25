using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WinShutDown
{
    /// <summary>
    /// Правило одного дня недели: компьютером можно пользоваться с AllowFrom до Shutdown.
    /// Если Shutdown не больше AllowFrom — выключение после полуночи (например, 09:00–00:30).
    /// </summary>
    internal struct DayRule
    {
        public bool Limited;   // false — день без ограничений
        public int AllowFrom;  // минут от полуночи
        public int Shutdown;   // минут от полуночи

        public DayRule(int allowFrom, int shutdown)
        {
            Limited = true;
            AllowFrom = allowFrom;
            Shutdown = shutdown;
        }

        public bool AfterMidnight => Shutdown <= AllowFrom;

        /// <summary>«07:00-22:00» или «free 07:00-22:00» (без ограничений, время сохраняется для окна настроек).</summary>
        public string Format() => (Limited ? "" : "free ") + Schedule.FormatHm(AllowFrom) + "-" + Schedule.FormatHm(Shutdown);

        public static bool TryParse(string s, out DayRule rule)
        {
            rule = new DayRule(7 * 60, 22 * 60);
            if (s == null) return false;
            string v = s.Trim();
            bool free = false;
            if (v.StartsWith("free", StringComparison.OrdinalIgnoreCase))
            {
                free = true;
                v = v.Substring(4).Trim();
                if (v.Length == 0)
                {
                    rule.Limited = false;
                    return true;
                }
            }
            string[] p = v.Split('-');
            if (p.Length != 2 || !Schedule.TryParseHm(p[0], out int a) || !Schedule.TryParseHm(p[1], out int b)) return false;
            if (!free && a == b) return false;
            rule = new DayRule(a, b) { Limited = !free };
            return true;
        }
    }

    /// <summary>Расчёт момента выключения. Чистые функции — проверяются в --selftest.</summary>
    internal static class Schedule
    {
        public const int MaxWarningMinutes = 180;
        public const int MaxWarningCount = 10;

        /// <summary>«Выключение не запланировано» (все дни без ограничений).</summary>
        public static readonly DateTime Never = DateTime.MaxValue;

        /// <summary>Порядок дней в окне настроек и в файле: с понедельника.</summary>
        public static readonly DayOfWeek[] WeekOrder =
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        };

        /// <summary>Расписание по умолчанию: в будни 07:00–22:00, в пятницу и субботу вечером — до 23:00, в выходные утром — с 09:00.</summary>
        public static DayRule[] DefaultDays()
        {
            var d = new DayRule[7];
            for (int i = 0; i < 7; i++) d[i] = new DayRule(7 * 60, 22 * 60);
            d[(int)DayOfWeek.Friday] = new DayRule(7 * 60, 23 * 60);
            d[(int)DayOfWeek.Saturday] = new DayRule(9 * 60, 23 * 60);
            d[(int)DayOfWeek.Sunday] = new DayRule(9 * 60, 22 * 60);
            return d;
        }

        public static DayRule[] SameEveryDay(int allowFrom, int shutdown) =>
            Enumerable.Repeat(new DayRule(allowFrom, shutdown), 7).ToArray();

        /// <summary>Расписание для протокола: 7 правил через «;», индекс = DayOfWeek (0 — воскресенье).</summary>
        public static string FormatDays(DayRule[] days) => string.Join(";", days.Select(d => d.Format()));

        public static bool TryParseDays(string s, out DayRule[] days, out string error)
        {
            days = null;
            error = null;
            string[] parts = (s ?? "").Split(';');
            if (parts.Length != 7)
            {
                error = "Нужно расписание на 7 дней";
                return false;
            }
            var result = new DayRule[7];
            for (int i = 0; i < 7; i++)
            {
                if (!DayRule.TryParse(parts[i], out result[i]))
                {
                    error = $"{Ru.DayName((DayOfWeek)i)}: неверное время (время «можно с» и «выключать в» не должны совпадать)";
                    return false;
                }
            }
            days = result;
            return true;
        }

        /// <summary>Разбор списка вида «30, 10, 5». Результат — без повторов, по убыванию.</summary>
        public static bool TryParseWarnings(string s, out int[] list, out string error)
        {
            list = null;
            error = null;
            var values = new List<int>();
            foreach (string raw in (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) || m < 1 || m > MaxWarningMinutes)
                {
                    error = $"«{part}» — нужно число минут от 1 до {MaxWarningMinutes}.";
                    return false;
                }
                if (!values.Contains(m)) values.Add(m);
            }
            if (values.Count == 0)
            {
                error = "Укажите, за сколько минут предупреждать, например: 30, 10, 5.";
                return false;
            }
            if (values.Count > MaxWarningCount)
            {
                error = $"Не больше {MaxWarningCount} предупреждений.";
                return false;
            }
            values.Sort();
            values.Reverse();
            list = values.ToArray();
            return true;
        }

        public static string FormatWarnings(int[] warnings, string separator = ",") =>
            string.Join(separator, warnings.Select(w => w.ToString(CultureInfo.InvariantCulture)));

        /// <summary>
        /// Текущий «этап» предупреждений: наименьший порог, до которого уже дошло время.
        /// Например, при порогах 30/10/5 и остатке 8 минут — этап 10. null — предупреждать ещё рано.
        /// </summary>
        public static int? Stage(TimeSpan left, int[] warnings)
        {
            int? stage = null;
            foreach (int t in warnings)
                if (left.TotalMinutes <= t && (stage == null || t < stage)) stage = t;
            return stage;
        }

        public static string FormatHm(int minute) =>
            (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);

        public static bool TryParseHm(string s, out int minute)
        {
            minute = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] p = s.Trim().Split(':');
            if (p.Length != 2) return false;
            if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return false;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            minute = h * 60 + m;
            return true;
        }

        /// <summary>
        /// Часовой пояс берётся из настроек, а не из системы: обычный пользователь может сменить
        /// часовой пояс Windows, но это не сдвинет время выключения.
        /// </summary>
        public static TimeZoneInfo Zone(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* неизвестный пояс — используем системный */ }
            }
            return TimeZoneInfo.Local;
        }

        struct Interval
        {
            public DateTime Start, End;
        }

        /// <summary>Разрешённые интервалы (местное время) для дней fromDate … fromDate+count-1, отсортированные и склеенные.</summary>
        static List<Interval> AllowedIntervals(DayRule[] days, DateTime fromDate, int count)
        {
            var list = new List<Interval>();
            for (int i = 0; i < count; i++)
            {
                DateTime d = fromDate.AddDays(i);
                DayRule r = days[(int)d.DayOfWeek];
                if (!r.Limited)
                {
                    list.Add(new Interval { Start = d, End = d.AddDays(1) });
                    continue;
                }
                DateTime end = d.AddMinutes(r.Shutdown);
                if (r.AfterMidnight) end = end.AddDays(1);
                list.Add(new Interval { Start = d.AddMinutes(r.AllowFrom), End = end });
            }
            list.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<Interval>();
            foreach (Interval iv in list)
            {
                if (merged.Count > 0 && iv.Start <= merged[merged.Count - 1].End)
                {
                    Interval last = merged[merged.Count - 1];
                    if (iv.End > last.End) last.End = iv.End;
                    merged[merged.Count - 1] = last;
                }
                else
                {
                    merged.Add(iv);
                }
            }
            return merged;
        }

        /// <summary>
        /// Ближайший момент выключения (UTC) без учёта продления:
        ///  • сейчас разрешённое время — конец этого интервала;
        ///  • запретное время — через время последнего предупреждения (например, 5 минут),
        ///    но если разрешённое время вот-вот начнётся — не выключаем;
        ///  • все дни без ограничений — Never.
        /// </summary>
        public static DateTime BaseDeadlineUtc(AppConfig c, DateTime nowUtc)
        {
            TimeZoneInfo zone = Zone(c.TimeZoneId);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
            const int span = 12;
            DateTime from = local.Date.AddDays(-2);
            DateTime horizon = from.AddDays(span);
            int grace = Math.Max(1, c.FinalWarning);

            foreach (Interval iv in AllowedIntervals(c.Days, from, span))
            {
                bool containsNow = iv.Start <= local && local < iv.End;
                bool startsSoon = iv.Start > local && iv.Start <= local.AddMinutes(grace);
                if (containsNow || startsSoon)
                    return iv.End >= horizon ? Never : LocalToUtc(iv.End, zone);
            }
            return nowUtc.AddMinutes(grace);
        }

        public static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) local = local.AddHours(1); // переход на летнее время
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
    }

    /// <summary>Русские тексты с правильными окончаниями.</summary>
    internal static class Ru
    {
        static readonly string[] DayNames = { "Воскресенье", "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота" };

        static readonly string[] DayShorts = { "Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб" };

        public static string DayName(DayOfWeek d) => DayNames[(int)d];

        public static string DayShort(DayOfWeek d) => DayShorts[(int)d];

        public static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n);
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return one;
            if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
            return many;
        }

        static string Verb(int n) => (n % 10 == 1 && n % 100 != 11) ? "Осталась" : "Осталось";

        /// <summary>«Осталось 5 минут», «Осталась 1 минута», в последнюю минуту — секунды.</summary>
        public static string Remaining(TimeSpan left)
        {
            if (left.TotalSeconds > 59)
            {
                int m = (int)Math.Ceiling(left.TotalMinutes);
                return $"{Verb(m)} {m} {Plural(m, "минута", "минуты", "минут")}";
            }
            int s = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
            return $"{Verb(s)} {s} {Plural(s, "секунда", "секунды", "секунд")}";
        }

        /// <summary>«30, 10 и 5»</summary>
        public static string JoinAnd(int[] values)
        {
            if (values.Length == 1) return values[0].ToString(CultureInfo.InvariantCulture);
            return string.Join(", ", values.Take(values.Length - 1).Select(v => v.ToString(CultureInfo.InvariantCulture))) +
                   " и " + values[values.Length - 1].ToString(CultureInfo.InvariantCulture);
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return "меньше минуты";
            int total = (int)Math.Ceiling(t.TotalMinutes);
            int d = total / (24 * 60), h = total / 60 % 24, m = total % 60;
            var parts = new List<string>();
            if (d > 0) parts.Add($"{d} дн");
            if (h > 0) parts.Add($"{h} ч");
            if (m > 0 && d == 0) parts.Add($"{m} мин");
            return string.Join(" ", parts);
        }
    }
}
