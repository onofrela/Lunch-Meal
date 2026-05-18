using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace LunchMeal
{
    [StaticConstructorOnStartup]
    public static class LunchMealInjector
    {
        internal const string PackedPrefix = "LunchMeal_";
        internal const string PackedPrefix_Recipe = "MakeLunchMeal_";

        // Filters that belong to packing recipes — the ThingFilter patch skips these
        // to prevent a packed meal from being used as its own packing ingredient.
        internal static readonly HashSet<ThingFilter> PackingRecipeFilters = new HashSet<ThingFilter>();

        private static readonly FieldInfo allRecipesCachedField =
            typeof(ThingDef).GetField("allRecipesCached",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo allChildThingDefsCachedField =
            typeof(ThingCategoryDef).GetField("allChildThingDefsCached",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo sortedChildThingDefsCachedField =
            typeof(ThingCategoryDef).GetField("sortedChildThingDefsCached",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static LunchMealInjector()
        {
            try
            {
                GenerateLunchMealDefs();
            }
            catch (Exception ex)
            {
                Log.Error("[LunchMeal] Failed to generate packed lunch defs: " + ex);
            }
        }

        private static void GenerateLunchMealDefs()
        {
            ModContentPack modContent = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(m => m.PackageId == "user.lunchmeal")
                ?? DefDatabase<ThingDef>.GetNamed("MealSimple").modContentPack;
            ThingCategoryDef packedMealsCat = DefDatabase<ThingCategoryDef>.GetNamed("PackedMeals");

            List<ThingDef> mealDefs = DefDatabase<ThingDef>.AllDefs
                .Where(IsEligibleMeal)
                .ToList();

            List<ThingDef> cookingTables = DefDatabase<ThingDef>.AllDefs
                .Where(IsPackagingTable)
                .ToList();

            var newThingDefs = new List<ThingDef>();
            var newRecipeDefs = new List<RecipeDef>();

            foreach (ThingDef meal in mealDefs)
            {
                ThingDef packed = CreatePackedThingDef(meal, modContent, packedMealsCat);
                RecipeDef recipe = CreatePackingRecipeDef(meal, packed);

                DefDatabase<ThingDef>.Add(packed);
                DefDatabase<RecipeDef>.Add(recipe);

                // ResolveReferences clears thingCategories (replaces direct-object refs with
                // empty XML cross-ref results), so we restore it immediately after.
                packed.PostLoad();
                packed.ResolveReferences();
                packed.thingCategories = new List<ThingCategoryDef> { packedMealsCat };

                recipe.PostLoad();
                recipe.ResolveReferences();

                RegisterThingInCategory(packedMealsCat, packed);

                foreach (ThingDef table in cookingTables)
                {
                    if (table.recipes == null)
                        table.recipes = new List<RecipeDef>();
                    table.recipes.Add(recipe);
                    allRecipesCachedField?.SetValue(table, null);
                }

                newThingDefs.Add(packed);
                newRecipeDefs.Add(recipe);
            }

            // Proactively build category caches while still on the main thread.
            // Never set caches to null: the game rebuilds them in parallel and crashes.
            // Instead we populate them here so the parallel code finds them pre-built.
            PopulateCategoryCaches(packedMealsCat, newThingDefs);

            Log.Message($"[LunchMeal] Generated {mealDefs.Count} packed lunch variants for: "
                + string.Join(", ", mealDefs.Select(m => m.defName)));
        }

        private static bool IsEligibleMeal(ThingDef d)
        {
            return d.IsIngestible
                && d.ingestible?.foodType == FoodTypeFlags.Meal
                && d.thingCategories != null
                && d.thingCategories.Any(c => CategoryIsOrUnder(c, "FoodMeals"))
                && d.defName != "MealSurvivalPack"
                && d.defName != "MealNutrientPaste"
                && !d.defName.StartsWith(PackedPrefix);
        }

        private static bool CategoryIsOrUnder(ThingCategoryDef cat, string targetDefName)
        {
            for (var c = cat; c != null; c = c.parent)
                if (c.defName == targetDefName) return true;
            return false;
        }

        private static bool IsPackagingTable(ThingDef d)
        {
            return d.defName == "LunchMeal_PackagingTable";
        }

        private static ThingDef CreatePackedThingDef(
            ThingDef source,
            ModContentPack modContent,
            ThingCategoryDef packedMealsCat)
        {
            var graphicData = new GraphicData
            {
                texPath = "LunchMeal",
                graphicClass = typeof(Graphic_StackCount),
            };

            var def = new ThingDef
            {
                defName = PackedPrefix + source.defName,
                label = "packed " + source.label,
                description = $"A packed {source.label}. It can be eaten on the go without needing a table.",
                modContentPack = modContent,
                thingClass = typeof(ThingWithComps),
                category = ThingCategory.Item,
                graphicData = graphicData,
                stackLimit = 10,
                tickerType = TickerType.Rare,
                drawGUIOverlay = true,
                rotatable = false,
                selectable = true,
                alwaysHaulable = true,
                thingCategories = new List<ThingCategoryDef> { packedMealsCat },
                socialPropernessMatters = source.socialPropernessMatters,
                resourceReadoutPriority = source.resourceReadoutPriority != ResourceCountPriority.Uncounted
                    ? source.resourceReadoutPriority
                    : ResourceCountPriority.Middle,
            };

            def.statBases = new List<StatModifier>();
            CopyStatBase(def.statBases, source, StatDefOf.Nutrition);
            CopyStatBase(def.statBases, source, StatDefOf.Mass);
            float mv = source.GetStatValueAbstract(StatDefOf.MarketValue);
            def.statBases.Add(new StatModifier { stat = StatDefOf.MarketValue, value = mv * 1.1f });
            def.statBases.Add(new StatModifier { stat = StatDefOf.DeteriorationRate, value = 10f });
            def.statBases.Add(new StatModifier { stat = StatDefOf.Flammability, value = 1.0f });
            def.statBases.Add(new StatModifier { stat = StatDefOf.Beauty, value = -4f });

            var sourceRot = source.comps?.OfType<CompProperties_Rottable>().FirstOrDefault();
            float daysToRot = sourceRot != null ? Math.Max(sourceRot.daysToRotStart, 5f) : 5f;

            def.comps = new List<CompProperties>
            {
                new CompProperties_Forbiddable(),
                new CompProperties_Ingredients { performMergeCompatibilityChecks = false },
                new CompProperties_FoodPoisonable(),
                new CompProperties_Rottable { daysToRotStart = daysToRot, rotDestroys = true },
            };

            def.ingestible = new IngestibleProperties { parent = def };
            var si = source.ingestible;
            if (si != null)
            {
                def.ingestible.preferability = si.preferability;
                def.ingestible.tasteThought = si.tasteThought;
                def.ingestible.foodType = si.foodType;
                def.ingestible.ingestEffect = si.ingestEffect;
                def.ingestible.ingestSound = si.ingestSound;
                def.ingestible.maxNumToIngestAtOnce = si.maxNumToIngestAtOnce > 0 ? si.maxNumToIngestAtOnce : 1;
                def.ingestible.optimalityOffsetHumanlikes = si.optimalityOffsetHumanlikes;
                if (si.outcomeDoers != null)
                    def.ingestible.outcomeDoers = si.outcomeDoers.ToList();
            }
            def.ingestible.tableDesired = false;
            def.ingestible.chairSearchRadius = 0f;

            return def;
        }

        private static RecipeDef CreatePackingRecipeDef(ThingDef source, ThingDef output)
        {
            var ingredientFilter = new ThingFilter();
            ingredientFilter.SetAllow(source, true);

            var ingredient = new IngredientCount();
            ingredient.filter = ingredientFilter;
            ingredient.SetBaseCount(1f);

            var woodFilter = new ThingFilter();
            woodFilter.SetAllow(ThingDefOf.WoodLog, true);

            var woodIngredient = new IngredientCount();
            woodIngredient.filter = woodFilter;
            woodIngredient.SetBaseCount(1f);

            var fixedFilter = new ThingFilter();
            fixedFilter.SetAllow(source, true);
            fixedFilter.SetAllow(ThingDefOf.WoodLog, true);

            PackingRecipeFilters.Add(ingredientFilter);
            PackingRecipeFilters.Add(fixedFilter);

            var recipe = new RecipeDef
            {
                defName = "MakeLunchMeal_" + source.defName,
                label = "pack " + source.label,
                description = $"Pack a {source.label} to take anywhere.",
                jobString = $"Packing {source.label}.",
                workAmount = 100f,
                workSkill = SkillDefOf.Cooking,
                workSpeedStat = StatDefOf.WorkSpeedGlobal,
                ingredients = new List<IngredientCount> { ingredient, woodIngredient },
                fixedIngredientFilter = fixedFilter,
                defaultIngredientFilter = fixedFilter,
                products = new List<ThingDefCountClass> { new ThingDefCountClass(output, 1) },
                modContentPack = output.modContentPack,
                allowMixingIngredients = false,
            };

            recipe.effectWorking = DefDatabase<EffecterDef>.GetNamed("Cook", errorOnFail: false);
            recipe.soundWorking = DefDatabase<SoundDef>.GetNamed("Recipe_CookMeal", errorOnFail: false);

            return recipe;
        }

        private static void PopulateCategoryCaches(ThingCategoryDef directCat, List<ThingDef> newDefs)
        {
            // Build directCat's caches from scratch (it's a fresh category, caches are null)
            var directHashSet = new HashSet<ThingDef>(directCat.childThingDefs);
            allChildThingDefsCachedField?.SetValue(directCat, directHashSet);

            var directSorted = directCat.childThingDefs.OrderBy(d => d.label).ToList();
            sortedChildThingDefsCachedField?.SetValue(directCat, directSorted);

            // Walk up parent chain: ADD our items to existing HashSet caches in-place.
            // If the parent's cache is already null, leave it null — the lazy rebuild
            // will call ContainedInThisOrDescendant which uses allChildThingDefsCached
            // (already populated above for directCat), so it won't crash.
            var parent = directCat.parent;
            while (parent != null)
            {
                var parentHashSet = allChildThingDefsCachedField?.GetValue(parent) as HashSet<ThingDef>;
                if (parentHashSet != null)
                    foreach (var def in newDefs)
                        parentHashSet.Add(def);

                parent = parent.parent;
            }
        }

        private static void RegisterThingInCategory(ThingCategoryDef category, ThingDef thingDef)
        {
            if (category.childThingDefs == null)
                category.childThingDefs = new List<ThingDef>();

            if (!category.childThingDefs.Contains(thingDef))
                category.childThingDefs.Add(thingDef);
        }

        private static void CopyStatBase(List<StatModifier> target, ThingDef source, StatDef stat)
        {
            float val = source.GetStatValueAbstract(stat);
            target.Add(new StatModifier { stat = stat, value = val });
        }
    }
}
