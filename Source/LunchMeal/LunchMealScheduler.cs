using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LunchMeal
{
    public sealed class LunchMealScheduler : MapComponent
    {
        private readonly List<Pawn>[] byHour = new List<Pawn>[24];
        private readonly int[] dueTicks = new int[24];
        private int nextTick = int.MaxValue;
        private bool dirty = true;

        public LunchMealScheduler(Map map) : base(map) { }

        internal void Invalidate(FoodPolicy edited = null)
        {
            dirty = true;
            if (edited == null) return;
            // Settings edits are weird, lol. Cancel obsolete pending requests, retaining the day guard.
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                if (pawn.foodRestriction?.CurrentFoodPolicy == edited && LunchPolicyStore.TryState(pawn, out var state))
                    state.pending = false;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            dirty = true;
        }

        public override void MapComponentTick()
        {
            if (dirty) Rebuild();
            int now = Find.TickManager.TicksGame;
            if (now < nextTick) return;
            LunchDiagnostics.CountScheduleWake();
            int nowAbs = GenTicks.TicksAbs;
            long localDay = ((long)nowAbs + GenDate.LocalTicksOffsetFromLongitude(Find.WorldGrid.LongLatOf(map.Tile).x))
                / LunchScheduleRules.TicksPerDay;
            for (int hour = 0; hour < 24; hour++)
            {
                if (dueTicks[hour] > now || byHour[hour] == null) continue;
                foreach (Pawn pawn in byHour[hour])
                {
                    if (!LunchFoodUtility.Managed(pawn)) continue;
                    var policy = LunchPolicyStore.For(pawn);
                    if (policy == null || !policy.collectDaily || policy.hour != hour) continue;
                    LunchPolicyStore.State(pawn).Schedule(localDay, nowAbs + LunchScheduleRules.TicksPerDay);
                }
                dueTicks[hour] = now + LunchScheduleRules.TicksPerDay;
            }
            UpdateNextTick();
        }

        private void Rebuild()
        {
            dirty = false;
            LunchDiagnostics.CountScheduleRebuild();
            int now = Find.TickManager.TicksGame;
            int dayTick = GenLocalDate.DayTick(map);
            for (int i = 0; i < 24; i++)
            {
                byHour[i]?.Clear();
                dueTicks[i] = int.MaxValue;
            }
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (!LunchFoodUtility.Managed(pawn)) continue;
                var policy = LunchPolicyStore.For(pawn);
                if (policy == null || !policy.collectDaily) continue;
                int hour = policy.hour;
                if (byHour[hour] == null) byHour[hour] = new List<Pawn>();
                byHour[hour].Add(pawn);
                dueTicks[hour] = now + LunchScheduleRules.NextDelay(dayTick, hour);
            }
            UpdateNextTick();
        }

        private void UpdateNextTick()
        {
            nextTick = int.MaxValue;
            for (int i = 0; i < 24; i++) nextTick = Math.Min(nextTick, dueTicks[i]);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    internal static class Patch_LunchSchedule_Spawn
    {
        private static void Postfix(Pawn __instance)
        {
            if (__instance.RaceProps.Humanlike && __instance.Map != null)
                __instance.Map.GetComponent<LunchMealScheduler>().Invalidate();
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_LunchSchedule_Despawn
    {
        private static void Prefix(Pawn __instance)
        {
            if (__instance.RaceProps.Humanlike && __instance.Map != null)
                __instance.Map.GetComponent<LunchMealScheduler>().Invalidate();
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    internal static class Patch_LunchSchedule_Faction
    {
        private static void Postfix(Pawn __instance)
        {
            if (__instance.RaceProps.Humanlike && __instance.Map != null)
                __instance.Map.GetComponent<LunchMealScheduler>().Invalidate();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
    internal static class Patch_LunchSchedule_NextJob
    {
        private static void Postfix(Pawn ___pawn, ref ThinkResult __result)
        {
            // This method is also used to consider overriding an active job: never collect then.
            if (___pawn.CurJob != null || !__result.IsValid || __result.FromQueue || __result.Job.playerForced)
                return;
            bool ordinary = (__result.SourceNode is JobGiver_Work work && !work.emergency)
                || __result.Tag == JobTag.Idle;
            if (!ordinary || !LunchPolicyStore.TryState(___pawn, out var state) || !state.CanPrepare(GenTicks.TicksAbs))
                return;
            if (!LunchFoodUtility.Managed(___pawn) || ___pawn.Drafted || ___pawn.Downed || ___pawn.InMentalState
                || ___pawn.IsBurning() || !___pawn.Awake()
                || !___pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                || ___pawn.needs.food.CurCategory != HungerCategory.Fed
                || (___pawn.needs.rest != null && ___pawn.needs.rest.CurLevelPercentage < 0.3f))
                return;
            var policy = LunchPolicyStore.For(___pawn);
            if (policy == null || !policy.collectDaily) { state.pending = false; return; }
            // Claim the cycle before searching. Zero stock and interruptions cannot cause retries.
            long today = ((long)GenTicks.TicksAbs + GenDate.LocalTicksOffsetFromLongitude(
                Find.WorldGrid.LongLatOf(___pawn.Map.Tile).x)) / LunchScheduleRules.TicksPerDay;
            state.Prepared(today);
            int missing = LunchScheduleRules.Missing(policy.quantity, LunchFoodUtility.InventoryLunchCount(___pawn));
            if (missing == 0) return;
            Thing source = LunchFoodUtility.FindLunchToCollect(___pawn);
            if (source == null) return;
            Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("LunchMeal_CollectDaily"), source);
            job.count = policy.quantity;
            JobMaker.ReturnToPool(__result.Job);
            __result = new ThinkResult(job, __result.SourceNode, __result.Tag);
        }
    }

    public sealed class JobDriver_CollectDailyLunch : JobDriver
    {
        private int targetQuantity;
        private int collectedStacks;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref targetQuantity, "targetQuantity", 0);
            Scribe_Values.Look(ref collectedStacks, "collectedStacks", 0);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return ReserveNext(errorOnFailed);
        }

        private bool ReserveNext(bool errorOnFailed = false)
        {
            if (targetQuantity == 0) targetQuantity = job.count;
            Thing food = job.targetA.Thing;
            if (!LunchFoodUtility.Collectable(pawn, food)) return false;
            job.count = Math.Min(food.stackCount, LunchScheduleRules.Missing(targetQuantity, LunchFoodUtility.InventoryLunchCount(pawn)));
            job.count = Math.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, food));
            job.count = Math.Min(job.count, FoodUtility.GetMaxAmountToPickup(food, pawn, job.count));
            return job.count > 0 && pawn.Reserve(food, job, 10, job.count, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !LunchFoodUtility.Managed(pawn) || LunchPolicyStore.For(pawn)?.collectDaily != true);
            Toil go = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A)
                .FailOn(() => !LunchFoodUtility.ValidLunch(pawn, job.targetA.Thing));
            yield return go;
            yield return Toils_Haul.TakeToInventory(TargetIndex.A, () => job.count);
            Toil next = ToilMaker.MakeToil("LunchMeal_NextStack");
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            next.initAction = () =>
            {
                collectedStacks++;
                if (LunchScheduleRules.Missing(targetQuantity, LunchFoodUtility.InventoryLunchCount(pawn)) == 0
                    || collectedStacks >= 10) return;
                Thing food = LunchFoodUtility.FindLunchToCollect(pawn);
                if (food == null) return;
                pawn.ClearReservationsForJob(job);
                job.SetTarget(TargetIndex.A, food);
                if (ReserveNext()) JumpToToil(go);
            };
            yield return next;
        }
    }
}
