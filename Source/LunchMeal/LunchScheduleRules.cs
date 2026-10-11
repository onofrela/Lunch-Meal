using System;

namespace LunchMeal
{
    // Independent of Unity: these are also exercised by the headless regression suite.
    internal static class LunchScheduleRules
    {
        internal const int TicksPerHour = 2500;
        internal const int TicksPerDay = 60000;

        internal static int NextDelay(int dayTick, int hour)
        {
            int delay = Math.Max(0, Math.Min(23, hour)) * TicksPerHour - dayTick;
            return delay >= 0 ? delay : delay + TicksPerDay;
        }

        internal static int Missing(int target, int carried)
        {
            return Math.Max(0, Math.Max(1, Math.Min(10, target)) - carried);
        }
    }

    internal sealed class LunchReserveState
    {
        public LunchReserveState() { }
        internal bool pending;
        internal bool suppressStock;
        internal long pendingDay = -1;
        internal long lastPreparedDay = -1;
        internal int deadlineAbs;

        internal void Schedule(long day, int deadline)
        {
            pending = day != lastPreparedDay;
            pendingDay = day;
            deadlineAbs = deadline;
            if (pending) suppressStock = true;
        }

        internal bool CanPrepare(int nowAbs)
        {
            if (pending && nowAbs >= deadlineAbs) pending = false;
            return pending && pendingDay != lastPreparedDay;
        }

        internal void Prepared(long? actualDay = null)
        {
            lastPreparedDay = Math.Max(pendingDay, actualDay ?? pendingDay);
            pending = false;
            suppressStock = true;
        }

        internal void HungryWithoutLunch()
        {
            suppressStock = false;
        }
    }
}
