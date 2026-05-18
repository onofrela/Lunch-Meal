using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LunchMeal
{
    public class LunchMealMod : Mod
    {
        public LunchMealMod(ModContentPack content) : base(content)
        {
            new Harmony("user.lunchmeal").PatchAll();
        }
    }
    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    static class Patch_GenRecipe_CaptureSubIngredients
    {
        internal static List<ThingDef> pendingSubIngredients;

        static void Prefix(RecipeDef recipeDef, List<Thing> ingredients)
        {
            pendingSubIngredients = null;
            if (!recipeDef.defName.StartsWith(LunchMealInjector.PackedPrefix_Recipe)) return;
            var meal = ingredients?.FirstOrDefault();
            var comp = meal?.TryGetComp<CompIngredients>();
            if (comp != null)
                pendingSubIngredients = comp.ingredients.ToList();
        }
    }

    [HarmonyPatch(typeof(Thing), "Notify_RecipeProduced")]
    static class Patch_Thing_InheritSubIngredients
    {
        static void Postfix(Thing __instance)
        {
            var pending = Patch_GenRecipe_CaptureSubIngredients.pendingSubIngredients;
            if (pending == null || !__instance.def.defName.StartsWith(LunchMealInjector.PackedPrefix)) return;

            var comp = __instance.TryGetComp<CompIngredients>();
            if (comp != null)
                foreach (var subDef in pending)
                    comp.RegisterIngredient(subDef);

            Patch_GenRecipe_CaptureSubIngredients.pendingSubIngredients = null;
        }
    }

    [HarmonyPatch(typeof(ThingFilter), nameof(ThingFilter.Allows), typeof(ThingDef))]
    static class Patch_ThingFilter_AllowPackedMealsFromSourceMeal
    {
        static void Postfix(ThingFilter __instance, ThingDef def, ref bool __result)
        {
            if (__result || def == null || !def.defName.StartsWith(LunchMealInjector.PackedPrefix))
                return;

            // Don't expand packing-recipe filters: would allow a packed meal as its own ingredient.
            if (LunchMealInjector.PackingRecipeFilters.Contains(__instance))
                return;

            string sourceDefName = def.defName.Substring(LunchMealInjector.PackedPrefix.Length);
            ThingDef sourceDef = DefDatabase<ThingDef>.GetNamedSilentFail(sourceDefName);
            if (sourceDef == null)
                return;

            if (__instance.Allows(sourceDef))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(ThingCategoryNodeDatabase), "FinalizeInit")]
    static class Patch_ThingCategoryNodeDatabase_AddPackedMealsNode
    {
        private static readonly FieldInfo treeNodeField =
            typeof(ThingCategoryDef).GetField("treeNode", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo childrenField =
            typeof(TreeNode_ThingCategory).GetField("children", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo parentNodeField =
            typeof(TreeNode_ThingCategory).GetField("parentNode", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo nestDepthField =
            typeof(TreeNode_ThingCategory).GetField("nestDepth", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo allThingCategoryNodesField =
            typeof(ThingCategoryNodeDatabase).GetField("allThingCategoryNodes", BindingFlags.NonPublic | BindingFlags.Static);

        static void Postfix()
        {
            try
            {
                EnsurePackedMealsNode();
            }
            catch (Exception ex)
            {
                Log.Warning("[LunchMeal] Failed to inject PackedMeals UI node: " + ex);
            }
        }

        private static void EnsurePackedMealsNode()
        {
            ThingCategoryDef packedMealsCat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("PackedMeals");
            ThingCategoryDef foodMealsCat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("FoodMeals");
            if (packedMealsCat == null || foodMealsCat == null)
                return;

            var foodNode = treeNodeField?.GetValue(foodMealsCat) as TreeNode_ThingCategory;
            if (foodNode == null)
                return;

            var packedNode = treeNodeField?.GetValue(packedMealsCat) as TreeNode_ThingCategory;
            if (packedNode == null)
            {
                packedNode = new TreeNode_ThingCategory(packedMealsCat);
                treeNodeField?.SetValue(packedMealsCat, packedNode);
            }

            var children = childrenField?.GetValue(foodNode) as List<TreeNode_ThingCategory>;
            if (children == null)
            {
                children = new List<TreeNode_ThingCategory>();
                childrenField?.SetValue(foodNode, children);
            }

            if (!children.Any(n => n.catDef == packedMealsCat))
                children.Add(packedNode);

            parentNodeField?.SetValue(packedNode, foodNode);

            if (nestDepthField != null)
            {
                int parentDepth = (int)nestDepthField.GetValue(foodNode);
                nestDepthField.SetValue(packedNode, parentDepth + 1);
            }

            var allNodes = allThingCategoryNodesField?.GetValue(null) as List<TreeNode_ThingCategory>;
            if (allNodes != null && !allNodes.Contains(packedNode))
                allNodes.Add(packedNode);
        }
    }

    // Ensures countedAmounts always has an entry for every packed-meal def after
    // UpdateResourceCounts or ResetResourceCounts clear the dictionary. Without this,
    // GetCount logs an error for defs that have 0 items on the map (never in the dict).
    [HarmonyPatch]
    static class Patch_ResourceCounter_EnsurePackedMealEntries
    {
        private static readonly FieldInfo countedAmountsField =
            AccessTools.Field(typeof(ResourceCounter), "countedAmounts");

        private static List<ThingDef> packedDefs;

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ResourceCounter), "UpdateResourceCounts");
            yield return AccessTools.Method(typeof(ResourceCounter), "ResetResourceCounts");
        }

        static void Postfix(ResourceCounter __instance)
        {
            var amounts = countedAmountsField?.GetValue(__instance) as Dictionary<ThingDef, int>;
            if (amounts == null) return;

            if (packedDefs == null)
                packedDefs = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.defName.StartsWith(LunchMealInjector.PackedPrefix))
                    .ToList();

            foreach (ThingDef def in packedDefs)
                if (!amounts.ContainsKey(def))
                    amounts[def] = 0;
        }
    }

    // Safety net: if an entry is still missing (e.g. called before any update runs),
    // count directly from the map without triggering the vanilla error log.
    [HarmonyPatch]
    static class Patch_ResourceCounter_GetCount_PackedMeals
    {
        private static readonly FieldInfo countedAmountsField =
            AccessTools.Field(typeof(ResourceCounter), "countedAmounts");

        private static readonly FieldInfo mapField =
            AccessTools.Field(typeof(ResourceCounter), "map");

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ResourceCounter), "GetCount", new[] { typeof(ThingDef) });

        static bool Prefix(ResourceCounter __instance, ThingDef def, ref int __result)
        {
            if (!def.defName.StartsWith(LunchMealInjector.PackedPrefix))
                return true;

            var amounts = countedAmountsField?.GetValue(__instance) as Dictionary<ThingDef, int>;
            if (amounts != null && amounts.TryGetValue(def, out int cached))
            {
                __result = cached;
                return false;
            }

            __result = 0;
            var map = mapField?.GetValue(__instance) as Map;
            if (map != null)
                foreach (Thing t in map.listerThings.ThingsOfDef(def))
                    __result += t.stackCount;

            if (amounts != null)
                amounts[def] = __result;

            return false;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    static class Patch_Thing_SpawnLunchTrash
    {
        private static ThingDef _lunchTrashDef;
        private static bool _defLookupDone;

        private static ThingDef LunchTrashDef
        {
            get
            {
                if (!_defLookupDone)
                {
                    _lunchTrashDef = DefDatabase<ThingDef>.GetNamed("Filth_LunchTrash", errorOnFail: false);
                    _defLookupDone = true;
                }
                return _lunchTrashDef;
            }
        }

        static void Postfix(Thing __instance, Pawn ingester)
        {
            if (ingester == null || ingester.Map == null)
                return;

            if (!__instance.def.defName.StartsWith(LunchMealInjector.PackedPrefix))
                return;

            ThingDef trashDef = LunchTrashDef;
            if (trashDef == null)
            {
                Log.Warning("[LunchMeal] Could not find Filth_LunchTrash def — trash will not spawn.");
                return;
            }

            FilthMaker.TryMakeFilth(ingester.Position, ingester.Map, trashDef, 1);
        }
    }
}
