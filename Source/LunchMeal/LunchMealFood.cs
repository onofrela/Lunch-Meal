using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LunchMeal
{
    internal static class LunchFoodUtility
    {
        internal const float DiningRadius = 32f;

        internal static bool Managed(Pawn pawn) => pawn.Spawned && pawn.IsColonistPlayerControlled
            && pawn.foodRestriction != null && pawn.inventory != null && pawn.needs?.food != null
            && !pawn.DevelopmentalStage.Baby();

        internal static bool Packed(ThingDef def) => def != null && def.IsIngestible
            && def.defName.StartsWith(LunchMealInjector.PackedPrefix, StringComparison.Ordinal);

        internal static bool Fresh(Thing food) => food.TryGetComp<CompRottable>()?.Stage is RotStage stage
            ? stage == RotStage.Fresh : true;

        internal static bool ValidLunch(Pawn pawn, Thing food) => food != null && !food.Destroyed
            && Packed(food.def) && food.IngestibleNow && Fresh(food)
            && food.def.IsNutritionGivingIngestible && pawn.WillEat(food, pawn);

        internal static int InventoryLunchCount(Pawn pawn)
        {
            int count = 0;
            var inventory = pawn.inventory.innerContainer;
            for (int i = 0; i < inventory.Count; i++)
                if (ValidLunch(pawn, inventory[i])) count += inventory[i].stackCount;
            return count;
        }

        internal static Thing InventoryLunch(Pawn pawn)
        {
            Thing best = null;
            float bestScore = float.MinValue;
            var inventory = pawn.inventory.innerContainer;
            for (int i = 0; i < inventory.Count; i++)
            {
                Thing food = inventory[i];
                if (!ValidLunch(pawn, food)) continue;
                float score = FoodUtility.FoodOptimality(pawn, food, food.def, 0f);
                if (score > bestScore) { best = food; bestScore = score; }
            }
            return best;
        }

        internal static bool Collectable(Pawn pawn, Thing food) => ValidLunch(pawn, food)
            && food.Spawned && food.Map == pawn.Map && !food.IsForbidden(pawn) && food.IsSociallyProper(pawn)
            && !food.Position.Fogged(pawn.Map) && pawn.CanReserve(food, 10, 1)
            && MassUtility.CountToPickUpUntilOverEncumbered(pawn, food) > 0;

        internal static Thing FindLunchToCollect(Pawn pawn)
        {
            LunchDiagnostics.CountCollectionSearch();
            // This whole-map reachable search occurs only inside the single daily preparation.
            return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
                ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree), PathEndMode.ClosestTouch,
                TraverseParms.For(pawn, Danger.Some), validator: t => Collectable(pawn, t));
        }

        internal static bool SuppressStock(Pawn pawn)
        {
            if (!Managed(pawn) || pawn.Drafted || pawn.InMentalState || LunchPolicyStore.For(pawn)?.collectDaily != true
                || !LunchPolicyStore.TryState(pawn, out var state) || !state.suppressStock) return false;
            if (pawn.needs.food.CurCategory != HungerCategory.Fed && InventoryLunchCount(pawn) == 0)
                state.HungryWithoutLunch();
            return state.suppressStock;
        }

        internal static bool NormalMeal(Pawn pawn, Thing food)
        {
            if (food == null || food.Destroyed || Packed(food.def)) return false;
            if (food is Building_NutrientPasteDispenser dispenser)
                return dispenser.Spawned && dispenser.InteractionCell.InBounds(pawn.Map)
                    && dispenser.powerComp.PowerOn && dispenser.HasEnoughFeedstockInHoppers()
                    && pawn.WillEat(dispenser.DispensableDef, pawn);
            return food.IngestibleNow && Fresh(food) && food.def.IsNutritionGivingIngestible
                && food.def.ingestible.foodType == FoodTypeFlags.Meal && !food.def.IsDrug && pawn.WillEat(food, pawn);
        }

        internal static bool AvailableMeal(Pawn pawn, Thing food)
        {
            if (!NormalMeal(pawn, food)) return false;
            if (pawn.inventory.Contains(food)) return true;
            return food.Spawned && food.Map == pawn.Map && !food.IsForbidden(pawn)
                && !food.Position.Fogged(pawn.Map) && food.IsSociallyProper(pawn)
                && pawn.CanReserveAndReach(food, food is Building_NutrientPasteDispenser
                    ? PathEndMode.InteractionCell : PathEndMode.ClosestTouch, Danger.Some, 10, 1);
        }

        internal static IntVec3 Pickup(Pawn pawn, Thing food) => food is Building_NutrientPasteDispenser dispenser
            ? dispenser.InteractionCell : food.Spawned ? food.Position : pawn.Position;

        internal static bool ValidChair(Pawn pawn, Thing chair, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (chair == null || chair.Destroyed || !chair.Spawned || chair.Map != pawn.Map
                || chair.def.building?.isSittable != true || chair.Faction != pawn.Faction
                || chair.IsForbidden(pawn) || chair.Position.Fogged(pawn.Map)
                || !chair.IsSociallyProper(pawn) || chair.IsBurning() || chair.HostileTo(pawn)
                || !pawn.CanReserve(chair) || !Toils_Ingest.TryFindFreeSittingSpotOnThing(chair, pawn, out cell)
                || cell.GetDangerFor(pawn, pawn.Map) != Danger.None || cell.IsForbidden(pawn)
                || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.None)) return false;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 adjacent = cell + GenAdj.CardinalDirections[i];
                if (!adjacent.InBounds(pawn.Map)) continue;
                Building table = adjacent.GetEdifice(pawn.Map);
                if (table?.def.surfaceType == SurfaceType.Eat && !table.IsBurning() && !table.IsForbidden(pawn))
                    return true;
            }
            return false;
        }

        // Enumerate region indexes inside a bounded circle, without GenClosest's global fallback.
        // Meals (32) + chairs (32 from the meal) require an outer chair radius of 64.
        private static void Nearby(Pawn pawn, IntVec3 root, float radius, Action<Region> process)
        {
            Map map = pawn.Map;
            Region start = root.GetRegion(map);
            if (start == null) return;
            float squared = radius * radius;
            TraverseParms parms = TraverseParms.For(pawn, Danger.Some);
            RegionTraverser.BreadthFirstTraverse(start,
                (from, to) => to.Allows(parms, false)
                    && to.extentsClose.ClosestCellTo(root).DistanceToSquared(root) <= squared,
                region => { LunchDiagnostics.CountRegion(); process(region); return false; });
        }

        private struct Seat
        {
            internal Thing chair;
            internal IntVec3 cell;
        }

        internal static Thing FindDiningMeal(Pawn pawn, Thing vanillaCandidate, out Thing chosenChair)
        {
            LunchDiagnostics.CountDiningSearch();
            chosenChair = null;
            if (vanillaCandidate != null && NormalMeal(pawn, vanillaCandidate)
                && Pickup(pawn, vanillaCandidate).DistanceToSquared(pawn.Position) <= DiningRadius * DiningRadius
                && AvailableMeal(pawn, vanillaCandidate))
            {
                chosenChair = FindChairNear(pawn, Pickup(pawn, vanillaCandidate));
                if (chosenChair != null) return vanillaCandidate;
            }
            var meals = new HashSet<Thing>();
            var chairsSeen = new HashSet<Thing>();
            var seats = new List<Seat>();
            IntVec3 origin = pawn.Position;
            float mealRadiusSquared = DiningRadius * DiningRadius;
            Nearby(pawn, origin, DiningRadius * 2f, region =>
            {
                foreach (Thing chair in region.ListerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                    if (chair.Position.DistanceToSquared(origin) <= mealRadiusSquared * 4f
                        && chair.def.building?.isSittable == true && chairsSeen.Add(chair))
                    {
                        LunchDiagnostics.CountChair();
                        if (ValidChair(pawn, chair, out var cell)) seats.Add(new Seat { chair = chair, cell = cell });
                    }
                foreach (Thing meal in region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                    if (Pickup(pawn, meal).DistanceToSquared(origin) <= mealRadiusSquared
                        && !Packed(meal.def) && (meal is Building_NutrientPasteDispenser
                            || meal.def.ingestible?.foodType == FoodTypeFlags.Meal)) meals.Add(meal);
            });
            if (seats.Count == 0) return null;
            // Reuse the original result and include normal meals already carried.
            if (vanillaCandidate != null && NormalMeal(pawn, vanillaCandidate)
                && Pickup(pawn, vanillaCandidate).DistanceToSquared(origin) <= mealRadiusSquared
                && AvailableMeal(pawn, vanillaCandidate))
            {
                IntVec3 pickup = Pickup(pawn, vanillaCandidate);
                foreach (Seat seat in seats)
                    if (seat.cell.DistanceToSquared(pickup) <= mealRadiusSquared)
                    { chosenChair = seat.chair; return vanillaCandidate; }
            }
            foreach (Thing meal in pawn.inventory.innerContainer)
                if (NormalMeal(pawn, meal)) meals.Add(meal);
            Thing best = null;
            float bestScore = float.MinValue;
            foreach (Thing meal in meals)
            {
                LunchDiagnostics.CountMeal();
                if (!NormalMeal(pawn, meal)) continue;
                IntVec3 pickup = Pickup(pawn, meal);
                ThingDef def = FoodUtility.GetFinalIngestibleDef(meal);
                float score = FoodUtility.FoodOptimality(pawn, meal, def, origin.DistanceTo(pickup));
                if (score <= bestScore) continue;
                Thing chair = null;
                float closestSeat = float.MaxValue;
                foreach (Seat seat in seats)
                {
                    float distance = seat.cell.DistanceToSquared(pickup);
                    if (distance <= mealRadiusSquared && distance < closestSeat)
                    { chair = seat.chair; closestSeat = distance; }
                }
                if (chair == null) continue;
                if (!AvailableMeal(pawn, meal)) continue;
                bestScore = score; best = meal; chosenChair = chair;
            }
            return best;
        }

        internal static Thing FindChairNear(Pawn pawn, IntVec3 pickup)
        {
            Thing best = null;
            float bestDistance = float.MaxValue;
            var seen = new HashSet<Thing>();
            Nearby(pawn, pickup, DiningRadius, region =>
            {
                foreach (Thing chair in region.ListerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                {
                    float distance = chair.Position.DistanceToSquared(pickup);
                    if (distance > DiningRadius * DiningRadius || distance >= bestDistance
                        || chair.def.building?.isSittable != true || !seen.Add(chair)) continue;
                    if (ValidChair(pawn, chair, out var cell)) { best = chair; bestDistance = distance; }
                }
            });
            return best;
        }

        internal static bool PlannedDining(Pawn pawn, Job job) => Managed(pawn) && !pawn.Drafted
            && !pawn.InMentalState && job?.def == JobDefOf.Ingest && !job.playerForced
            && job.targetC.Thing?.def.building?.isSittable == true;
    }

    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    internal static class Patch_LunchFood_Choose
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!LunchFoodUtility.Managed(pawn) || pawn.Drafted || pawn.InMentalState) return;
            // This is a food decision, not a tick, and also releases suppression when no food was found.
            LunchFoodUtility.SuppressStock(pawn);
            if (__result != null && __result.def != JobDefOf.Ingest) return;
            if (LunchPolicyStore.For(pawn)?.preferTable != true) return;
            // GetFood can be called with thresholds of its own: do not invent eating jobs for a null result.
            if (__result == null || __result.playerForced) return;
            Thing normal = LunchFoodUtility.FindDiningMeal(pawn, __result.targetA.Thing, out var chair);
            Thing chosen = normal ?? LunchFoodUtility.InventoryLunch(pawn);
            if (chosen == null) return;
            Job replacement = JobMaker.MakeJob(JobDefOf.Ingest, chosen);
            ThingDef def = FoodUtility.GetFinalIngestibleDef(chosen);
            replacement.count = FoodUtility.WillIngestStackCountOf(pawn, def, FoodUtility.GetNutrition(pawn, chosen, def));
            if (normal != null) replacement.SetTarget(TargetIndex.C, chair);
            JobMaker.ReturnToPool(__result);
            __result = replacement;
        }
    }

    [HarmonyPatch(typeof(JobGiver_PackFood), "TryGiveJob")]
    internal static class Patch_LunchFood_NoRefill
    {
        private static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!LunchFoodUtility.SuppressStock(pawn)) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_Ingest), nameof(JobDriver_Ingest.Notify_Starting))]
    internal static class Patch_LunchFood_NoExtra
    {
        private static void Prefix(JobDriver_Ingest __instance)
        {
            if (LunchFoodUtility.SuppressStock(__instance.pawn) || LunchFoodUtility.PlannedDining(__instance.pawn, __instance.job))
                __instance.job.takeExtraIngestibles = 0;
        }
    }

    [HarmonyPatch(typeof(JobDriver_Ingest), nameof(JobDriver_Ingest.TryMakePreToilReservations))]
    internal static class Patch_LunchFood_Revalidate
    {
        private static bool Prefix(JobDriver_Ingest __instance, ref bool __result)
        {
            Pawn pawn = __instance.pawn;
            Job job = __instance.job;
            if (!LunchFoodUtility.PlannedDining(pawn, job)) return true;
            Thing food = job.targetA.Thing;
            IntVec3 pickup = food != null ? LunchFoodUtility.Pickup(pawn, food) : pawn.Position;
            if (!LunchFoodUtility.AvailableMeal(pawn, food))
            {
                Thing replacement = LunchFoodUtility.FindDiningMeal(pawn, null, out var replacementChair)
                    ?? LunchFoodUtility.InventoryLunch(pawn);
                if (replacement == null) { __result = false; return false; }
                Retarget(__instance, replacement, replacementChair);
                return true;
            }
            if (!LunchFoodUtility.ValidChair(pawn, job.targetC.Thing, out var cell)
                || cell.DistanceToSquared(pickup) > LunchFoodUtility.DiningRadius * LunchFoodUtility.DiningRadius)
            {
                Thing chair = LunchFoodUtility.FindChairNear(pawn, pickup);
                if (chair != null) job.SetTarget(TargetIndex.C, chair);
                else if (LunchFoodUtility.InventoryLunch(pawn) is Thing lunch)
                    Retarget(__instance, lunch, null);
                else job.SetTarget(TargetIndex.C, LocalTargetInfo.Invalid);
            }
            return true;
        }

        private static void Retarget(JobDriver_Ingest driver, Thing food, Thing chair)
        {
            driver.job.SetTarget(TargetIndex.A, food);
            driver.job.SetTarget(TargetIndex.C, chair == null ? LocalTargetInfo.Invalid : new LocalTargetInfo(chair));
            ThingDef def = FoodUtility.GetFinalIngestibleDef(food);
            driver.job.count = FoodUtility.WillIngestStackCountOf(driver.pawn, def, FoodUtility.GetNutrition(driver.pawn, food, def));
            // Vanilla initializes inventory/dispenser flags after pre-toil reservations.
        }
    }

    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.CarryIngestibleToChewSpot))]
    internal static class Patch_LunchFood_UseChair
    {
        private static void Postfix(Toil __result)
        {
            Action vanilla = __result.initAction;
            Toil toil = __result;
            __result.initAction = () =>
            {
                Pawn pawn = toil.actor;
                Job job = pawn.CurJob;
                if (!LunchFoodUtility.PlannedDining(pawn, job)) { vanilla(); return; }
                if (!LunchFoodUtility.ValidChair(pawn, job.targetC.Thing, out var cell)
                    || cell.DistanceToSquared(pawn.Position) > LunchFoodUtility.DiningRadius * LunchFoodUtility.DiningRadius)
                {
                    Thing chair = LunchFoodUtility.FindChairNear(pawn, pawn.Position);
                    if (chair == null)
                    {
                        if (LunchFoodUtility.InventoryLunchCount(pawn) > 0)
                            pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                        else { job.SetTarget(TargetIndex.C, LocalTargetInfo.Invalid); vanilla(); }
                        return;
                    }
                    job.SetTarget(TargetIndex.C, chair);
                    if (!LunchFoodUtility.ValidChair(pawn, chair, out cell)) { pawn.jobs.EndCurrentJob(JobCondition.Incompletable); return; }
                }
                pawn.ReserveSittableOrSpot(cell, job);
                pawn.Map.pawnDestinationReservationManager.Reserve(pawn, job, cell);
                pawn.pather.StartPath(cell, PathEndMode.OnCell);
            };
        }
    }

    [HarmonyPatch(typeof(Pawn_InventoryTracker), "FirstUnloadableThing", MethodType.Getter)]
    internal static class Patch_LunchFood_KeepReserve
    {
        private static readonly FieldInfo keepField = AccessTools.Field(typeof(Pawn_InventoryTracker), "tmpItemsToKeep");

        internal static void KeepLunches(Pawn_InventoryTracker tracker)
        {
            if (!LunchFoodUtility.Managed(tracker.pawn) || LunchPolicyStore.For(tracker.pawn)?.collectDaily != true)
                return;
            var keep = (List<ThingDefCount>)keepField.GetValue(null);
            foreach (Thing food in tracker.innerContainer)
            {
                if (!LunchFoodUtility.ValidLunch(tracker.pawn, food)) continue;
                int existing = keep.FindIndex(entry => entry.ThingDef == food.def);
                // Count all stacks of this def; do not overwrite medicine or unrelated stock entries.
                int count = tracker.innerContainer.TotalStackCountOfDef(food.def);
                if (existing < 0) keep.Add(new ThingDefCount(food.def, count));
                else keep[existing] = new ThingDefCount(food.def, Math.Max(keep[existing].Count, count));
            }
        }

        internal static bool PackableForUnloading(Thing food, Pawn pawn, bool checkMass)
        {
            // Our keep list controls lunches. Vanilla's one-hunger-bar cap must not override it.
            if (LunchFoodUtility.Managed(pawn) && LunchPolicyStore.For(pawn)?.collectDaily == true
                && LunchFoodUtility.ValidLunch(pawn, food)) return false;
            return JobGiver_PackFood.IsGoodPackableFoodFor(food, pawn, checkMass);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (CodeInstruction instruction in code)
                if (instruction.operand is MethodInfo packable && packable.DeclaringType == typeof(JobGiver_PackFood)
                    && packable.Name == nameof(JobGiver_PackFood.IsGoodPackableFoodFor))
                {
                    instruction.operand = AccessTools.Method(typeof(Patch_LunchFood_KeepReserve), nameof(PackableForUnloading));
                    replaced++;
                }
            if (replaced != 1) throw new InvalidOperationException("[LunchMeal] RimWorld inventory nutrition cap hook was not found.");
            // Inject before the first inventory enumeration, after vanilla builds drug/stock keep lists.
            int insertion = -1;
            for (int i = 2; i < code.Count; i++)
                if (code[i].operand is MethodInfo method && method.Name == "GetEnumerator"
                    && code[i - 1].operand is FieldInfo field && field.Name == "innerContainer")
                { insertion = i - 2; break; }
            if (insertion < 0) throw new InvalidOperationException("[LunchMeal] RimWorld inventory keep hook was not found.");
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(code[insertion].labels);
            code[insertion].labels.Clear();
            code.InsertRange(insertion, new[] { load, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(Patch_LunchFood_KeepReserve), nameof(KeepLunches))) });
            return code;
        }
    }
}
