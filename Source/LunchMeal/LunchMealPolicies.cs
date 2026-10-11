using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace LunchMeal
{
    internal sealed class LunchPolicyOptions
    {
        public LunchPolicyOptions() { }
        internal bool collectDaily;
        internal bool preferTable;
        internal int quantity = 2;
        internal int hour = 9;

        internal void ExposeData()
        {
            Scribe_Values.Look(ref collectDaily, "LunchMeal_collectDaily", false);
            Scribe_Values.Look(ref preferTable, "LunchMeal_preferTable", false);
            Scribe_Values.Look(ref quantity, "LunchMeal_quantity", 2);
            Scribe_Values.Look(ref hour, "LunchMeal_hour", 9);
            quantity = Math.Max(1, Math.Min(10, quantity));
            hour = Math.Max(0, Math.Min(23, hour));
        }
    }

    internal static class LunchPolicyStore
    {
        private static readonly ConditionalWeakTable<FoodPolicy, LunchPolicyOptions> options =
            new ConditionalWeakTable<FoodPolicy, LunchPolicyOptions>();
        private static readonly ConditionalWeakTable<Pawn_FoodRestrictionTracker, LunchReserveState> states =
            new ConditionalWeakTable<Pawn_FoodRestrictionTracker, LunchReserveState>();

        internal static LunchPolicyOptions For(FoodPolicy policy) => options.GetOrCreateValue(policy);
        internal static LunchPolicyOptions For(Pawn pawn)
        {
            FoodPolicy policy = pawn.foodRestriction?.CurrentFoodPolicy;
            return policy != null && options.TryGetValue(policy, out var value) ? value : null;
        }
        internal static LunchReserveState State(Pawn pawn) => State(pawn.foodRestriction);
        internal static LunchReserveState State(Pawn_FoodRestrictionTracker tracker) => states.GetOrCreateValue(tracker);
        internal static bool TryState(Pawn pawn, out LunchReserveState state)
        {
            state = null;
            return pawn.foodRestriction != null && states.TryGetValue(pawn.foodRestriction, out state);
        }
        internal static void Changed(FoodPolicy policy = null)
        {
            if (Current.ProgramState != ProgramState.Playing) return;
            foreach (Map map in Find.Maps)
            {
                map.GetComponent<LunchMealScheduler>().Invalidate(policy);
            }
        }
    }

    [HarmonyPatch(typeof(FoodPolicy), nameof(FoodPolicy.ExposeData))]
    internal static class Patch_LunchPolicy_Save
    {
        private static void Postfix(FoodPolicy __instance) => LunchPolicyStore.For(__instance).ExposeData();
    }

    [HarmonyPatch(typeof(FoodPolicy), nameof(FoodPolicy.CopyFrom))]
    internal static class Patch_LunchPolicy_Copy
    {
        private static void Postfix(FoodPolicy __instance, Policy other)
        {
            if (!(other is FoodPolicy source)) return;
            var from = LunchPolicyStore.For(source);
            var to = LunchPolicyStore.For(__instance);
            to.collectDaily = from.collectDaily;
            to.preferTable = from.preferTable;
            to.quantity = from.quantity;
            to.hour = from.hour;
            LunchPolicyStore.Changed(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.ExposeData))]
    internal static class Patch_LunchReserve_Save
    {
        private static void Postfix(Pawn_FoodRestrictionTracker __instance)
        {
            // Scribe_Deep exposes this tracker before assigning it to pawn.foodRestriction.
            // Use the object being serialized, including all subsequent load phases.
            var state = LunchPolicyStore.State(__instance);
            Scribe_Values.Look(ref state.pending, "LunchMeal_pending", false);
            Scribe_Values.Look(ref state.suppressStock, "LunchMeal_suppressStock", false);
            Scribe_Values.Look(ref state.pendingDay, "LunchMeal_pendingDay", -1L);
            Scribe_Values.Look(ref state.lastPreparedDay, "LunchMeal_lastPreparedDay", -1L);
            Scribe_Values.Look(ref state.deadlineAbs, "LunchMeal_deadlineAbs", 0);
        }
    }

    [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), "CurrentFoodPolicy", MethodType.Setter)]
    internal static class Patch_LunchPolicy_Assigned
    {
        private static void Postfix(Pawn_FoodRestrictionTracker __instance)
        {
            Pawn pawn = __instance.pawn;
            if (pawn == null || !pawn.Spawned) return;
            if (LunchPolicyStore.TryState(pawn, out var state)) state.pending = false;
            pawn.Map.GetComponent<LunchMealScheduler>().Invalidate();
        }
    }

    [HarmonyPatch(typeof(Dialog_ManageFoodPolicies), "DoContentsRect")]
    internal static class Patch_LunchPolicy_Controls
    {
        private const float FooterHeight = 142f;
        private static readonly Func<Dialog_ManagePolicies<FoodPolicy>, FoodPolicy> selected =
            AccessTools.MethodDelegate<Func<Dialog_ManagePolicies<FoodPolicy>, FoodPolicy>>(
                AccessTools.PropertyGetter(typeof(Dialog_ManagePolicies<FoodPolicy>), "SelectedPolicy"));

        private static void Prefix(ref Rect rect, out Rect __state)
        {
            __state = new Rect(rect.x, rect.yMax - FooterHeight, rect.width, FooterHeight);
            rect.height -= FooterHeight + 8f;
        }

        private static void Postfix(Dialog_ManageFoodPolicies __instance, Rect __state)
        {
            FoodPolicy policy = selected(__instance);
            if (policy == null) return;
            var settings = LunchPolicyStore.For(policy);
            bool beforeDaily = settings.collectDaily;
            int beforeQuantity = settings.quantity;
            int beforeHour = settings.hour;
            Widgets.DrawLineHorizontal(__state.x, __state.y, __state.width);
            var row = new Rect(__state.x, __state.y + 8f, __state.width, 28f);
            Widgets.CheckboxLabeled(row, "LunchMeal_CollectDaily".Translate(), ref settings.collectDaily);
            TooltipHandler.TipRegion(row, "LunchMeal_CollectDailyTip".Translate());
            row.y += 30f;
            if (settings.collectDaily)
            {
                Widgets.Label(new Rect(row.x, row.y, row.width * 0.5f, 24f), "LunchMeal_Quantity".Translate(settings.quantity));
                settings.quantity = Mathf.RoundToInt(Widgets.HorizontalSlider(
                    new Rect(row.x + row.width * 0.52f, row.y, row.width * 0.46f, 24f), settings.quantity, 1, 10, true, roundTo: 1));
                row.y += 28f;
                Widgets.Label(new Rect(row.x, row.y, row.width * 0.5f, 24f), "LunchMeal_Hour".Translate(settings.hour));
                settings.hour = Mathf.RoundToInt(Widgets.HorizontalSlider(
                    new Rect(row.x + row.width * 0.52f, row.y, row.width * 0.46f, 24f), settings.hour, 0, 23, true, roundTo: 1));
            }
            row.y = __state.y + 102f;
            Widgets.CheckboxLabeled(row, "LunchMeal_PreferTable".Translate(), ref settings.preferTable);
            TooltipHandler.TipRegion(row, "LunchMeal_PreferTableTip".Translate());
            if (beforeDaily != settings.collectDaily || beforeQuantity != settings.quantity || beforeHour != settings.hour)
                LunchPolicyStore.Changed(policy);
            // Table preference is read on demand; toggling it needs no scheduler rebuild.
        }
    }
}
