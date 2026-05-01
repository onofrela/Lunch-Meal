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
}
