// =============================================================================
//  BACKTESTING MODULE - UsMarketCalendar
// -----------------------------------------------------------------------------
//  NYSE/Nasdaq trading-day calendar - the shared market holiday schedule both
//  exchanges observe (all US-listed equities trade on this calendar
//  regardless of primary exchange). Mirrors NseCalendar's exact interface
//  (IsMarketDay / TradingDays) so BacktestEngine can select between the two
//  calendars with a single conditional and no other code changes - the
//  scanner's own NseCalendar is untouched.
//
//  Covers the 10 holidays NYSE/Nasdaq actually close for: New Year's Day,
//  Martin Luther King Jr. Day, Washington's Birthday (Presidents Day), Good
//  Friday, Memorial Day, Juneteenth, Independence Day, Labor Day,
//  Thanksgiving, and Christmas. Fixed-date holidays follow the standard US
//  weekend-observation rule (Saturday -> observed Friday, Sunday -> observed
//  Monday). Early-close days (day after Thanksgiving, Christmas Eve) are
//  still full trading days for this calendar's purposes - only full closures
//  affect whether a date counts as a trading day.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NseMomentumScanner.Backtesting
{
    internal static class UsMarketCalendar
    {
        public static bool IsMarketDay(DateTime date)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                return false;
            return !IsHoliday(date.Date);
        }

        public static IEnumerable<DateTime> TradingDays(DateTime from, DateTime to)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                if (IsMarketDay(d))
                    yield return d;
        }

        private static bool IsHoliday(DateTime date)
        {
            int year = date.Year;
            var holidays = HolidaysForYear(year);
            return holidays.Contains(date);
        }

        private static readonly Dictionary<int, HashSet<DateTime>> _cache = new();

        private static HashSet<DateTime> HolidaysForYear(int year)
        {
            if (_cache.TryGetValue(year, out var cached)) return cached;

            var set = new HashSet<DateTime>();

            // Fixed-date holidays, shifted for weekend observation.
            AddObserved(set, new DateTime(year, 1, 1));    // New Year's Day
            AddObserved(set, new DateTime(year, 6, 19));   // Juneteenth (observed by NYSE since 2022)
            AddObserved(set, new DateTime(year, 7, 4));    // Independence Day
            AddObserved(set, new DateTime(year, 12, 25));  // Christmas Day

            // Floating holidays (already weekdays by construction).
            set.Add(NthWeekdayOfMonth(year, 1, DayOfWeek.Monday, 3));   // MLK Day
            set.Add(NthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3));   // Presidents Day
            set.Add(LastWeekdayOfMonth(year, 5, DayOfWeek.Monday));     // Memorial Day
            set.Add(NthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1));   // Labor Day
            set.Add(NthWeekdayOfMonth(year, 11, DayOfWeek.Thursday, 4)); // Thanksgiving
            set.Add(EasterSunday(year).AddDays(-2));                    // Good Friday

            _cache[year] = set;
            return set;
        }

        /// <summary>Standard US market-holiday weekend rule: Saturday -> observed the
        /// preceding Friday; Sunday -> observed the following Monday.</summary>
        private static void AddObserved(HashSet<DateTime> set, DateTime holiday)
        {
            var observed = holiday.DayOfWeek switch
            {
                DayOfWeek.Saturday => holiday.AddDays(-1),
                DayOfWeek.Sunday => holiday.AddDays(1),
                _ => holiday,
            };
            set.Add(observed);
        }

        private static DateTime NthWeekdayOfMonth(int year, int month, DayOfWeek weekday, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + (n - 1) * 7);
        }

        private static DateTime LastWeekdayOfMonth(int year, int month, DayOfWeek weekday)
        {
            var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            int offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
            return last.AddDays(-offset);
        }

        /// <summary>Anonymous Gregorian algorithm (Meeus/Jones/Butcher) for the date of
        /// Easter Sunday - used only to derive Good Friday (Easter Sunday minus 2 days).</summary>
        private static DateTime EasterSunday(int year)
        {
            int a = year % 19;
            int b = year / 100;
            int c = year % 100;
            int d = b / 4;
            int e = b % 4;
            int f = (b + 8) / 25;
            int g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4;
            int k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31;
            int day = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(year, month, day);
        }
    }
}
