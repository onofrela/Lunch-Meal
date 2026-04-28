using System.Collections.Generic;
using System.Linq;
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
}
