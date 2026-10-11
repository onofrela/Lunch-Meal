# Lunch Meal

Tired of your colonists suffering the "ate without table" debuff? Tired of them walking halfway across the map just to sit down? **This mod adds the ability to pack prepared meals into portable lunches.**

Build a **Packaging Table** and use it to wrap any meal into a portable lunch. These packed lunches can be consumed anywhere without the need for a table, making them perfect for hunters, miners, and soldiers on the front lines.

## ━━━━━━ FEATURES ━━━━━━

*   **Packaging Table:** A dedicated workbench (under Production) where colonists with Cooking work pack meals. Costs 40 steel and 30 wood to build.
*   **Universal Compatibility:** Automatically detects meals from all mods, including Vanilla Cooking Expanded.
*   **Quality Preservation:** Packed meals retain the nutrition, mood effects, and ingredients of the original dish.
*   **Lunch Trash:** Eating a packed lunch leaves packaging scraps on the ground. Trash reduces room beauty and cleanliness, and washes away in the rain outdoors.
*   **Food Policies:** Allow or exclude packed lunches independently of their original meals. Optional daily collection and nearby-table preference can be configured for each policy.

## ━━━━━━ DETAILS ━━━━━━

*   **Eat Anywhere:** No table required. Eliminate negative mood thoughts for field workers.
*   **Automatic detection:** Scans all active meals. No extra cooking skill required to pack them.
*   **Packing cost:** Each recipe consumes 1 piece of wood in addition to the meal itself.
*   **Trash penalty:** Lunch trash applies a small beauty (−12) and cleanliness (−4) penalty. Keep your base tidy or assign a cleaner. Don't be a freaking slob and clean your trash.

## Food policy options

Open **Assign → Food policy → Edit**. Both new options start disabled, including in existing saves.

* **Collect lunches daily:** Choose 1–10 lunches and an hour (0–23, local map time). Pawns finish their current task or wake before collecting. They top up to the target quantity, preserving lunches already carried and any surplus. The preparation may visit multiple stacks, but unavailable supplies and interruptions are not retried that day. Automatic food restocking is suppressed until the next scheduled collection, or until the pawn becomes hungry without a valid lunch. Medicine and drug stocking are unaffected.
* **Prefer normal meals near a table:** When choosing food to eat, a pawn prefers an allowed normal meal within 32 tiles if a usable seat beside a table is within 32 tiles of that meal. Otherwise they prefer a valid lunch from inventory, then fall back to normal food selection. Restrictions, reservations, freshness and reachability apply. This option can be used independently of daily collection.

For base workers, exclude **packed meals** from their policy. For field workers, allow the desired lunches and enable daily collection. Counts are units, not a guarantee of nutrition for a whole workday. Caravan behavior is unchanged.

The scheduler checks its next due tick rather than scanning colonists continuously. Nearby meals and chairs are searched only during food decisions, using bounded region indexes without a global-map fallback.

## Requirements

*   [Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)
*   Supported RimWorld version: 1.6