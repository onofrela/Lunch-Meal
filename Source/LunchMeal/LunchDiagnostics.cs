using Verse;
using RimWorld;
using LudeonTK;

namespace LunchMeal
{
    // Opt-in counters only. Normal play never writes logs or times every pawn/tick.
    internal static class LunchDiagnostics
    {
        internal static bool enabled;
        private static long scheduleWakes, scheduleRebuilds, collectionSearches, diningSearches, regions, chairs, meals;
        internal static void CountScheduleWake() { if (enabled) scheduleWakes++; }
        internal static void CountScheduleRebuild() { if (enabled) scheduleRebuilds++; }
        internal static void CountCollectionSearch() { if (enabled) collectionSearches++; }
        internal static void CountDiningSearch() { if (enabled) diningSearches++; }
        internal static void CountRegion() { if (enabled) regions++; }
        internal static void CountChair() { if (enabled) chairs++; }
        internal static void CountMeal() { if (enabled) meals++; }

        [DebugAction("Lunch Meal", "Toggle/reset diagnostics", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Toggle()
        {
            enabled = !enabled;
            scheduleWakes = scheduleRebuilds = collectionSearches = diningSearches = regions = chairs = meals = 0;
            Log.Message("[LunchMeal] Diagnostic counters " + (enabled ? "enabled and reset." : "disabled."));
        }

        [DebugAction("Lunch Meal", "Report diagnostics", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Report()
        {
            Log.Message($"[LunchMeal] Scheduler wakes={scheduleWakes}, rebuilds={scheduleRebuilds}; "
                + $"collection searches={collectionSearches}; dining decisions={diningSearches}, "
                + $"regions visited={regions}, chairs checked={chairs}, meals evaluated={meals}.");
        }
    }
}
