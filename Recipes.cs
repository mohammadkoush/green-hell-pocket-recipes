// Pocket Recipes - the recipe engine.
//
// Lifted from Pickup Doctor, where this was one feature among twenty. Standalone here because the
// person who asked for it may publish it herself, and a mod that depends on somebody else's repo is
// not really hers.
//
// EVERYTHING BELOW WAS LEARNED THE EXPENSIVE WAY and is kept verbatim, because the comments are the
// only record of why each piece is shaped as it is:
//
//   - HUDItem.AddSlot and m_ActiveButtons are private, so the menu entry is added by reflection.
//   - Every button in the menu's pool ALREADY carries a Confirm child, a selection highlight and two
//     hit rects. The game only ever lights them for Destroy. That machinery is reproduced here, so
//     the confirm is the game's own object rather than an imitation of it.
//   - The wheel dials the row under the CURSOR, read from HUDItem.m_ActiveButton. Guessing the recipe
//     from the item instead produced a family of bugs where dialling four of one thing crafted four
//     of another.
//   - A paced recipe hands the crafting to CraftingController and lets its animation drive the queue,
//     and the finalizer that ends it is the ONLY place the game gives movement and camera back.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PocketRecipes
{
    public partial class PocketRecipesPlugin
    {
        // ---------- config: Crafting ----------
        private ConfigEntry<bool>   _craftEnabled;
        private ConfigEntry<string> _craftRecipeText;
        private ConfigEntry<float>  _craftPacing;

        /// <summary>N of one item in the backpack become one of another, dropped on the ground.</summary>
        /// <summary>One line of a recipe's bill of materials.</summary>
        private struct Ingredient
        {
            public Enums.ItemID Id;
            public int          Count;
        }

        private struct Recipe
        {
            // The PRIMARY ingredient stays a plain field rather than living in the list, because the
            // whole menu hangs off it: the right-click entry appears on the item under the cursor,
            // so exactly one ingredient has to be "the one you right-click". For a log that is the
            // stick - you have a pile of those in hand - and the rope is named in the label so the
            // cost is never a surprise.
            public Enums.ItemID Ingredient;
            public int          Count;
            public Enums.ItemID Result;

            // Everything else the recipe also needs. Null or empty for the single-ingredient
            // recipes, which is every recipe that existed before the log.
            public List<Ingredient> Extra;

            // PACED. Ask for five and you get five - but the game goes through the crafting five
            // times, one after another, rather than five appearing at once. That is what he meant by
            // "one at a time": not a limit on how many, but a cost per item, so a log stays taxing
            // enough that the recipe cannot be leaned on.
            //
            // Sticks are unpaced. The stick grind is the entire reason batching exists.
            public bool OneAtATime;
        }

        private static readonly List<Recipe> s_Recipes = new List<Recipe>();

        // The Harmony patches below are static, so they need a way back to the plugin instance for
        // Logger and Announce. Set once in BindCraftingConfig.

        // The menu slot we borrow.
        //
        // AddSlot takes a HUDItem.Action, and every Action already has a button in the prefab's pool -
        // so borrowing an existing one is free, while inventing a new enum value would have no button
        // behind it. TakeQueenBee is the safest borrow in the whole enum: HUDItem.Activate(Item) only
        // ever adds it under `if (Trigger.IsBeehive())`. A stick is never a beehive, so our entry and
        // the real one can never appear on the same menu. We relabel the text anyway, so the player
        // never sees the borrowed name.
        private const HUDItem.Action CraftSlot = HUDItem.Action.TakeQueenBee;

        // ONE SLOT PER RECIPE. An item can be an ingredient in more than one thing - a stick makes a
        // long stick AND is half of a log - and with a single borrowed action only the first could
        // ever be shown. He went looking for "Craft Log" under "Craft Long Stick", which is exactly
        // where it belongs, and found nothing.
        //
        // Each extra action is borrowed on the same reasoning as TakeQueenBee: something the game
        // never adds for an item sitting in a backpack. Pet and Untie belong to animals, Plow to a
        // field. If one of them ever does turn up on an item, the execute path checks that WE offered
        // it for THIS item before acting, so the worst case is a wrong label rather than a wrong
        // action.
        private static readonly HUDItem.Action[] CraftSlots =
        {
            HUDItem.Action.TakeQueenBee,
            HUDItem.Action.Pet,
            HUDItem.Action.Untie,
        };

        /// <summary>Which recipe each borrowed slot is offering, for the menu currently open.</summary>
        private static readonly Dictionary<HUDItem.Action, Recipe> s_OfferedBy =
            new Dictionary<HUDItem.Action, Recipe>();

        // Which recipe the currently-open menu is offering, if any. Set when the menu is built and
        // read when a button is clicked, so the click path never has to re-derive it.
        private static Recipe s_Offered;
        private static bool   s_HasOffer;

        // No timed confirm state any more. The confirmation lives on the button itself - the game's
        // own Confirm child, lit while the cursor is over it - so there is no window to expire and
        // nothing to remember between frames.

        /// <summary>More than one KIND of ingredient means the craft has consequences worth asking about.</summary>
        private static bool NeedsConfirm(Recipe r)
        {
            return r.Extra != null && r.Extra.Count > 0;
        }

        private static FieldInfo  s_ActiveButtonsFI;
        private static MethodInfo s_AddSlotMI;

        // -----------------------------------------------------------------------------------------
        // Setup
        // -----------------------------------------------------------------------------------------

        /// <summary>Bind the Crafting config section. Called from Awake.</summary>
        private void BindCraftingConfig()
        {
            s_Self = this;

            // ON by default as of 2026-08-13. It shipped OFF, and the reasoning was sound - recipes
            // are new content rather than a convenience, so it asked first. But it asked so quietly
            // that the feature could not be found: two sticks on the crafting table did nothing,
            // because this feature was never about the table, and the backpack entry it DOES add was
            // switched off. Having asked for stick crafting twice, he should get stick crafting.
            _craftEnabled = Config.Bind("Crafting", "Enabled", true,
                "Adds a craft entry to the game's right-click item menu in your backpack. The result " +
                "drops on the ground at your feet. The vanilla crafting table is not affected. OFF by " +
                "default because the recipes are new content rather than a convenience - turn it on in " +
                "the Crafting tab if you want it.");

            _craftRecipeText = Config.Bind("Crafting", "Recipes",
                "Small_Stick:2>Stick, Stick:4>Long_Stick, Rope:4+Stick:10>Log!",
                "Comma-separated recipes, each written INGREDIENT:COUNT>RESULT. Join further " +
                "ingredients with + - Stick:10+Rope:4>Log. The FIRST ingredient is the one whose " +
                "right-click menu offers the recipe. Names are Enums.ItemID names, the same ones " +
                "the ItemIDs setting uses, and anything unrecognised is reported at startup and " +
                "skipped rather than silently ignored. THE FIRST INGREDIENT IS THE ONE YOU " +
                "RIGHT-CLICK, and only ONE recipe can be offered per ingredient - the log is written " +
                "Rope:4+Stick:10 rather than Stick:10+Rope:4 because a stick already offers the long " +
                "stick, and the first match wins. A ! after the result means ONE AT A TIME - " +
                "no scroll-to-batch, because some things are too big to come off a wheel twelve at " +
                "a time. " +
                "The log costs what it costs on purpose. Auto-pickup made sticks nearly free, so " +
                "the price sits in ROPE, which is made of fibre and still gathered by hand.");

            _craftPacing = Config.Bind("Crafting", "SecondsPerPacedCraft", 3f,
                new ConfigDescription(
                    "How long each item of a ! recipe takes. Asking for five logs makes five, one " +
                    "every few seconds, so a big item stays a decision rather than a click. Sticks " +
                    "are unaffected - only recipes marked with ! are paced.",
                    new AcceptableValueRange<float>(0.1f, 30f)));

            ParseRecipes(_craftRecipeText.Value);
        }

        /// <summary>Turn the config string into recipes, naming anything it could not understand.</summary>
        private void ParseRecipes(string text)
        {
            s_Recipes.Clear();
            if (string.IsNullOrEmpty(text)) return;

            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i].Trim();
                if (raw.Length == 0) continue;

                // INGREDIENT:COUNT>RESULT
                int gt = raw.IndexOf('>');
                int colon = raw.IndexOf(':');
                if (gt < 0 || colon < 0 || colon > gt)
                {
                    Logger.LogWarning("crafting: cannot read recipe '" + raw + "' - expected INGREDIENT:COUNT>RESULT");
                    continue;
                }

                string resName = raw.Substring(gt + 1).Trim();
                string billText = raw.Substring(0, gt).Trim();

                // A trailing ! means "one at a time" - no batching, no wheel. Written on the recipe
                // rather than in a separate list so the rule travels with the thing it governs.
                bool slow = resName.EndsWith("!");
                if (slow) resName = resName.Substring(0, resName.Length - 1).Trim();

                Enums.ItemID res;
                if (!TryParseItemId(resName, out res)) { Logger.LogWarning("crafting: unknown ItemID '" + resName + "' in '" + raw + "'"); continue; }

                // NAME:COUNT [+ NAME:COUNT ...]
                List<Ingredient> bill = new List<Ingredient>();
                bool ok = true;
                string[] terms = billText.Split('+');
                for (int k = 0; k < terms.Length; k++)
                {
                    string term = terms[k].Trim();
                    int c2 = term.IndexOf(':');
                    if (c2 < 0) { Logger.LogWarning("crafting: cannot read '" + term + "' in '" + raw + "' - expected NAME:COUNT"); ok = false; break; }

                    string nm = term.Substring(0, c2).Trim();
                    string cs = term.Substring(c2 + 1).Trim();

                    Enums.ItemID id;
                    if (!TryParseItemId(nm, out id)) { Logger.LogWarning("crafting: unknown ItemID '" + nm + "' in '" + raw + "'"); ok = false; break; }

                    int n;
                    if (!int.TryParse(cs, out n) || n < 1) { Logger.LogWarning("crafting: bad count '" + cs + "' in '" + raw + "'"); ok = false; break; }

                    Ingredient ig; ig.Id = id; ig.Count = n;
                    bill.Add(ig);
                }
                if (!ok || bill.Count == 0) continue;

                Recipe r;
                r.Ingredient = bill[0].Id;
                r.Count      = bill[0].Count;
                r.Result     = res;
                r.Extra      = (bill.Count > 1) ? bill.GetRange(1, bill.Count - 1) : null;
                r.OneAtATime = slow;
                s_Recipes.Add(r);
                Logger.LogInfo("crafting: " + BillText(r) + " -> " + res +
                               (slow ? "  (one at a time)" : ""));
            }

            if (s_Recipes.Count == 0) Logger.LogInfo("crafting: no usable recipes configured.");
        }

        private static bool TryParseItemId(string name, out Enums.ItemID id)
        {
            id = default(Enums.ItemID);
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                id = (Enums.ItemID)Enum.Parse(typeof(Enums.ItemID), name, true);
                return Enum.IsDefined(typeof(Enums.ItemID), id);
            }
            catch { return false; }
        }

        /// <summary>The recipe this item can start, if any, and only if the backpack has the stock.</summary>
        /// <summary>
        /// Is this item any part of the recipe - the one you right-click, or one of the others?
        ///
        /// ANY ingredient, deliberately. A recipe used to be offered only on its FIRST ingredient,
        /// which meant the log appeared on rope and nowhere else, and he went hunting for it under
        /// the stick menu where it belongs. You reach for a log while holding sticks as readily as
        /// while holding rope.
        /// </summary>
        private static bool Uses(Recipe r, Enums.ItemID id)
        {
            if (r.Ingredient == id) return true;
            if (r.Extra != null)
                for (int i = 0; i < r.Extra.Count; i++)
                    if (r.Extra[i].Id == id) return true;
            return false;
        }

        /// <summary>Every recipe this item takes part in, in config order, capped at the slots we have.</summary>
        private static List<Recipe> FindOffers(Item item)
        {
            List<Recipe> found = new List<Recipe>();
            if (item == null || item.m_Info == null) return found;
            for (int i = 0; i < s_Recipes.Count && found.Count < CraftSlots.Length; i++)
                if (Uses(s_Recipes[i], item.m_Info.m_ID)) found.Add(s_Recipes[i]);
            return found;
        }

        private static bool FindOffer(Item item, out Recipe found)
        {
            found = default(Recipe);
            if (item == null || item.m_Info == null) return false;

            InventoryBackpack bp = InventoryBackpack.Get();
            if (bp == null) return false;

            for (int i = 0; i < s_Recipes.Count; i++)
            {
                if (!Uses(s_Recipes[i], item.m_Info.m_ID)) continue;

                // OFFERED WHETHER OR NOT HE CAN AFFORD IT. This used to refuse anything it could not
                // deliver - "a greyed-out promise is worse than silence" was my comment - and he has
                // now asked three times for the opposite, in almost the same words each time. He is
                // right: the menu is how anyone DISCOVERS a recipe exists. Hiding it teaches nothing
                // and is indistinguishable from the feature being broken, which is exactly how it
                // looked to him for two days with a log recipe that had been working all along.
                //
                // The shortfall is named on the entry instead, and the craft itself still refuses to
                // spend anything it cannot complete.
                found = s_Recipes[i];
                return true;
            }
            return false;
        }

        // -----------------------------------------------------------------------------------------
        // Menu entry
        //
        // HUDItem.Activate(Item) fills m_ActiveButtons and then calls the private Activate() to lay the
        // menu out. So the slot must be added BEFORE that call and the label written AFTER it - which
        // is exactly a prefix and a postfix on Activate().
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(HUDItem), "Activate", new Type[0])]
        private static class Patch_AddCraftSlot
        {
            private static void Prefix(HUDItem __instance)
            {
                s_HasOffer = false;
                s_OfferedBy.Clear();
                try
                {
                    if (s_Self == null || s_Self._craftEnabled == null || !s_Self._craftEnabled.Value) return;
                    if (__instance == null || __instance.m_Item == null) return;

                    List<Recipe> offers = FindOffers(__instance.m_Item);
                    if (offers.Count == 0) return;

                    if (s_AddSlotMI == null)
                    {
                        s_AddSlotMI = typeof(HUDItem).GetMethod("AddSlot",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (s_AddSlotMI == null)
                        {
                            s_Self.Logger.LogWarning("crafting: HUDItem.AddSlot not found - no craft entry.");
                            return;
                        }
                    }

                    for (int i = 0; i < offers.Count; i++)
                    {
                        s_AddSlotMI.Invoke(__instance, new object[] { CraftSlots[i] });
                        s_OfferedBy[CraftSlots[i]] = offers[i];
                    }
                    s_Offered  = offers[0];
                    s_HasOffer = true;
                }
                catch (Exception ex)
                {
                    s_HasOffer = false;
                    if (s_Self != null) s_Self.Logger.LogWarning("crafting: could not add the menu entry: " + ex.Message);
                }
            }

            private static void Postfix(HUDItem __instance)
            {
                if (!s_HasOffer) return;
                try
                {
                    if (s_ActiveButtonsFI == null)
                    {
                        s_ActiveButtonsFI = typeof(HUDItem).GetField("m_ActiveButtons",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (s_ActiveButtonsFI == null) return;
                    }

                    List<HUDItemButton> buttons = s_ActiveButtonsFI.GetValue(__instance) as List<HUDItemButton>;
                    if (buttons == null) return;

                    // Relabel only OUR slots, matched by action. Nothing else in the menu can be
                    // scribbled over, whatever order the game listed things in - and with several
                    // craft entries now possible, each has to find its own recipe rather than
                    // assuming there is only one.
                    for (int i = 0; i < buttons.Count; i++)
                    {
                        HUDItemButton b = buttons[i];
                        if (b == null || b.text == null) continue;

                        Recipe r;
                        if (!s_OfferedBy.TryGetValue(b.action, out r)) continue;

                        // JUST THE NAME. The bill of materials used to be appended here, and on
                        // the log - four ropes and ten sticks - it ran straight under the Confirm
                        // button and made both unreadable. The game's own rows are short: "Destroy
                        // stack" and then clear air before Confirm.
                        //
                        // Nothing is lost by dropping it. The wheel already writes what he is about to
                        // spend into this same row while he dials, in red when he cannot afford it,
                        // which is the moment the number actually matters.
                        b.text.text = "Craft " + Pretty(r.Result);

                        // Light the game's own inline Confirm on this row. It is a child of every
                        // button in the pool, not just Destroy - the game simply never switches it on
                        // for anything else. Switching it on here is what makes UpdateSelection's
                        // two-rect hit test (mirrored below) meaningful for us.
                        if (NeedsConfirm(r)) ArmInlineConfirm(b);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    if (s_Self != null) s_Self.Logger.LogWarning("crafting: could not label the menu entry: " + ex.Message);
                }
            }
        }

        // Where the next thing dropped on the ground goes.
        //
        // HIS REPORT: "first one lands further ahead, the rest pile on top of each other right
        // underneath my legs and pushes me upward." The old offset was indexed by position WITHIN a
        // batch, and that index restarts at zero every batch - so the first log of every batch landed
        // on exactly the spot the last one did. Two large physics objects created inside each other
        // get shoved apart, and the loser ends up under his feet lifting him.
        //
        // This counter does NOT reset. It walks an arc in front of him, and the arc never comes
        // within reach of his feet - the nearest point is 1.6m out, well past the half metre or so a
        // log needs.
        private static int s_DropSlot;

        private static Vector3 GroundDropSpot(Player p)
        {
            // 11 angles across 4 rings = 44 distinct spots before anything is reused, and the stride
            // of 17 is coprime with 44 so all 44 are visited before any repeat. THE NUMBERS ARE NOT
            // TASTE - they were simulated before shipping, because "looks spread out" is what the
            // last version was too:
            //
            //     nearest drop to the player          1.60 m   (his feet are never in the arc)
            //     closest CONSECUTIVE pair            2.09 m   (nothing spawns inside the last one)
            //     closest pair anywhere in the cycle  0.31 m   (drops 3 and 34 - 31 apart, long settled)
            //
            // Consecutive distance is the one that matters: two large physics objects created inside
            // each other is what put a log under his legs and lifted him off the ground.
            const int Slots = 11;
            const int Rings = 4;

            int cur = s_DropSlot;
            s_DropSlot = (s_DropSlot + 17) % (Slots * Rings);

            int slot = cur % Slots;
            int ring = cur / Slots;

            // -55 to +55 degrees, all of it in FRONT of him. Never behind, never underfoot.
            float spread = -55f + (110f * slot / (float)(Slots - 1));
            float radius = 1.6f + 0.55f * ring;

            Vector3 dir = Quaternion.AngleAxis(spread, Vector3.up) * p.transform.forward;
            return p.transform.position + dir * radius + Vector3.up * 0.35f;
        }

        /// <summary>"Small_Stick" reads badly in a menu; "Small Stick" does not.</summary>
        private static string Pretty(Enums.ItemID id)
        {
            return id.ToString().Replace('_', ' ');
        }

        // -----------------------------------------------------------------------------------------
        // The click
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(HUDItem), "ExecuteAction", new Type[] { typeof(HUDItem.Action), typeof(Item) })]
        private static class Patch_ExecuteCraft
        {
            private static bool Prefix(HUDItem.Action action, Item item, ref bool __result)
            {
                // WHICH slot was clicked decides WHICH recipe runs. With several craft entries in
                // one menu, the action is the only thing that tells them apart - reading s_Offered
                // here would always have crafted the first one, whichever entry he pressed.
                Recipe chosen;
                if (s_Self == null || !s_OfferedBy.TryGetValue(action, out chosen)) return true;

                // Only ours if we actually offered it for THIS item. If the game ever routes a real
                // Pet or Untie through here we must not eat the click.
                if (item == null || item.m_Info == null || !Uses(chosen, item.m_Info.m_ID)) return true;

                // If the wheel is armed on this entry, the click means "make the number I dialled",
                // not "make one". Without this the click crafts one AND the menu closing fires the
                // wheel for N - the player asked for twelve and gets thirteen. Taking the count also
                // DISARMS the wheel, so only one of the two paths ever acts.
                // CONFIRM WHEN THERE ARE CONSEQUENCES. His rule, and the good thing about it is that
                // it derives from the recipe itself rather than a flag anyone has to remember to set:
                // more than one KIND of ingredient means the craft spends two different resources, so
                // it asks first. A stick into a long stick is one ingredient and stays one click,
                // because confirming that thirty times is the grind these mods exist to delete.
                //
                // The first click arms; the second within a few seconds commits. Clicking a different
                // entry, or waiting too long, forgets it.
                // CONFIRM THE WAY THE GAME CONFIRMS.
                //
                // The old version announced "click again to confirm" at the bottom of the screen and
                // then let the menu CLOSE, so there was no second click to give. He recorded himself
                // trying three times in twenty-six seconds and crafting nothing.
                //
                // Destroy has never worked that way. Clicking Destroy does nothing at all; a Confirm
                // slides out beside the row and the craft happens when THAT is clicked, with the menu
                // open the whole time. Same test the game runs - confirm_sel is lit only while the
                // cursor is over the Confirm - so pressing the row itself is inert here too.
                if (NeedsConfirm(chosen) && !ConfirmIsHot(action))
                {
                    __result = true;
                    return false;                // menu stays open; nothing spent
                }

                // Always take the dialled count, paced or not - pacing changes HOW they arrive,
                // not how many he may ask for. Taking it also DISARMS the wheel, which is what stops
                // a count dialled for long sticks firing behind a Craft Log click.
                int n = 1;
                try { n = s_Self.TakeArmedCraftCount(chosen); }
                catch { n = 1; }

                try { s_Self.DoCraftMany(chosen, n); }
                catch (Exception ex) { s_Self.Logger.LogWarning("crafting failed: " + ex.Message); }

                __result = true;
                return false;                            // skip the original
            }
        }

        /// <summary>
        /// Collect exactly <paramref name="count"/> distinct items of one kind, appending to
        /// <paramref name="into"/>. Returns false - and says what is missing - without taking
        /// anything, so a caller can abandon the craft with the backpack untouched.
        /// </summary>
        private static bool Gather(InventoryBackpack bp, Enums.ItemID id, int count, List<Item> into,
                                   bool announce)
        {
            int found = 0;
            for (int i = 0; i < bp.m_Items.Count && found < count; i++)
            {
                Item it = bp.m_Items[i];
                if (it == null || it.m_Info == null) continue;
                if (it.m_Info.m_ID != id) continue;
                if (into.Contains(it)) continue;          // never spend one item twice
                into.Add(it);
                found++;
            }
            if (found >= count) return true;

            if (announce && s_Self != null)
                s_Self.Announce("Craft: need " + count + " x " + Pretty(id) + ", have " + found);
            return false;
        }

        /// <summary>
        /// What he is short of, named exactly - "6 more Stick" - or empty when he can afford one.
        /// The point is that the entry says WHY it cannot be used, on the entry, rather than
        /// vanishing and leaving him to work it out.
        /// </summary>
        private static string Missing(Recipe r)
        {
            InventoryBackpack bp = InventoryBackpack.Get();
            if (bp == null) return "";

            List<string> lack = new List<string>();
            int have = bp.GetItemsCount(r.Ingredient);
            if (have < r.Count) lack.Add((r.Count - have) + " more " + Pretty(r.Ingredient));

            if (r.Extra != null)
                for (int i = 0; i < r.Extra.Count; i++)
                {
                    int h = bp.GetItemsCount(r.Extra[i].Id);
                    if (h < r.Extra[i].Count)
                        lack.Add((r.Extra[i].Count - h) + " more " + Pretty(r.Extra[i].Id));
                }

            return lack.Count == 0 ? "" : string.Join(", ", lack.ToArray());
        }

        /// <summary>"10 x Stick + 4 x Rope" - the whole price, never just the part under the cursor.</summary>
        private static string BillText(Recipe r)
        {
            string s = r.Count + " x " + Pretty(r.Ingredient);
            if (r.Extra != null)
                for (int i = 0; i < r.Extra.Count; i++)
                    s += " + " + r.Extra[i].Count + " x " + Pretty(r.Extra[i].Id);
            return s;
        }

        /// <summary>How many of this recipe the backpack can currently pay for.</summary>
        private static int MaxCraftable(Recipe r)
        {
            InventoryBackpack bp = InventoryBackpack.Get();
            if (bp == null || r.Count < 1) return 0;

            // The SCARCEST ingredient decides. Counting only the primary would offer him twelve logs
            // off a pile of sticks and then fail on the rope, which is the kind of promise this mod
            // exists not to make.
            int max = bp.GetItemsCount(r.Ingredient) / r.Count;
            if (r.Extra != null)
                for (int i = 0; i < r.Extra.Count; i++)
                {
                    if (r.Extra[i].Count < 1) continue;
                    int can = bp.GetItemsCount(r.Extra[i].Id) / r.Extra[i].Count;
                    if (can < max) max = can;
                }
            return max < 0 ? 0 : max;
        }

        /// <summary>
        /// Craft n of the recipe, one at a time.
        ///
        /// One at a time on purpose. DoCraft gathers its ingredients from the live backpack list and
        /// destroys them, and Destroy is deferred to end of frame - so a batched version that
        /// collected 2n sticks up front would be counting objects that are already spoken for. That
        /// is the exact shape of the v1.8.0 harvest duplication bug. Looping the single-craft path
        /// re-reads the backpack each time and cannot double-spend.
        /// </summary>
        private void DoCraftMany(Recipe r, int n)
        {
            if (n < 1) n = 1;

            // HIS RULE, and it is the right one: ask for as many as you like, and the batch checks
            // the backpack again before EVERY single item. Ask for ten with materials for six and you
            // get six - not an error, not a refusal, and not four silently missing. It stops the
            // moment it cannot pay and says so ONCE.
            // A PACED recipe is queued rather than looped: one item, then a pause, then the next.
            // Looping here would make all five in a single frame, which is exactly the "five logs
            // for free" that the pause exists to prevent.
            if (r.OneAtATime)
            {
                SnapshotPlayerBlocks();
                s_Queue = r;
                s_QueueLeft = n;
                s_QueueMade = 0;
                s_QueueNextAt = 0f;      // the first one happens immediately
                if (n > 1) Announce("Crafting " + n + " x " + Pretty(r.Result) + "...");
                return;
            }

            int made = 0;
            for (int i = 0; i < n; i++)
            {
                if (MaxCraftable(r) < 1) break;    // re-checked per item, never assumed from the start
                if (!DoCraft(r, false, i)) break;  // quiet inside the batch; one message at the end
                made++;
            }

            int missing = n - made;

            if (made > 0)
            {
                Logger.LogMessage("crafted " + made + (missing > 0 ? " of " + n : "") + " x " + r.Result);
                Announce("Crafted " + made + " x " + Pretty(r.Result));
            }

            if (missing > 0)
            {
                // Names the SHORTFALL rather than the total, because that is the number he has to do
                // something about. One message, whether he was four short or forty.
                string shortOf = Missing(r);
                string msg = "Missing resources to craft " + missing + " x " + Pretty(r.Result)
                           + (shortOf.Length > 0 ? "  - need " + shortOf : "");
                Logger.LogMessage(msg);
                Announce(msg);
            }
        }

        // The batch borrows CraftingController, and CraftingController assumes it was entered FROM
        // the open inventory - so FinishCrafting reopens the backpack on its way out. Once, that is
        // correct. Four times, around a player who is standing in the world watching an animation, it
        // is four unasked-for open/close cycles, each dragging a rotation block, a controller start
        // and a cursor with it.
        //
        // Held shut for the duration. The state he had before the batch is put back once at the end
        // by RestoreInventoryState - which is his instruction, and a better rule than unpicking the
        // damage afterwards: do not do it in the first place.
        [HarmonyPatch(typeof(Inventory3DManager), "Activate")]
        private static class Patch_HoldInventoryDuringBatch
        {
            private static bool Prefix()
            {
                return s_QueueLeft <= 0;      // false = skip, while our batch is running
            }
        }

        // ---- the game's own inline Confirm --------------------------------------------------------
        //
        // Reproduces, for our craft rows, what HUDItem.UpdateSelection does for m_DestroyButton. It is
        // not imitation UI: it lights the Confirm child that is already sitting on the button, so the
        // result is the same object in the same place with the same art as the destroy confirmation
        // he pointed me at.

        private static FieldInfo s_ActiveButtonFI;      // HUDItem.m_ActiveButton, private

        /// <summary>The row under the cursor, or null.</summary>
        private static HUDItemButton HoveredButton(HUDItem hud)
        {
            if (hud == null) return null;
            try
            {
                if (s_ActiveButtonFI == null)
                {
                    s_ActiveButtonFI = typeof(HUDItem).GetField("m_ActiveButton",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (s_ActiveButtonFI == null) return null;
                }
                return s_ActiveButtonFI.GetValue(hud) as HUDItemButton;
            }
            catch { return null; }
        }

        /// <summary>
        /// The recipe on the row he is actually pointing at.
        ///
        /// THIS IS THE FIX FOR THE WHEEL. Every version of this bug - harvest versus craft, then
        /// craft versus craft - came from deciding what the wheel meant from the ITEM, before he had
        /// chosen a row. A stick offers two recipes and the guess always landed on the first, so
        /// scrolling over Craft Log dialled long sticks and the screen showed him exactly that:
        /// "Craft 2 x Long Stick (of 16)" changing while the cursor sat on the log row.
        ///
        /// The row is not a guess. It is m_ActiveButton, which the game already maintains.
        /// </summary>
        private static bool HoveredRecipe(HUDItem hud, out Recipe r, out HUDItem.Action action)
        {
            r = default(Recipe);
            action = HUDItem.Action.None;

            HUDItemButton b = HoveredButton(hud);
            if (b == null) return false;
            if (!s_OfferedBy.TryGetValue(b.action, out r)) return false;
            action = b.action;
            return true;
        }

        /// <summary>Find our button for an action in the open menu.</summary>
        private static HUDItemButton ButtonFor(HUDItem.Action action)
        {
            HUDItem hud = HUDItem.Get();
            if (hud == null || s_ActiveButtonsFI == null) return null;
            try
            {
                List<HUDItemButton> buttons = s_ActiveButtonsFI.GetValue(hud) as List<HUDItemButton>;
                if (buttons == null) return null;
                for (int i = 0; i < buttons.Count; i++)
                    if (buttons[i] != null && buttons[i].action == action) return buttons[i];
            }
            catch { }
            return null;
        }

        // Whether the borrowed buttons actually carry the Confirm parts. They are wired in
        // HUDItem.Awake for every button in the pool, so this should always be true - but if it ever
        // is not, the failure has to be LOUD AND OPEN rather than quiet and shut. A missing Confirm
        // that silently blocks the craft is the exact bug he just filmed three times over.
        private static bool s_InlineConfirmOk;

        /// <summary>Switch on the Confirm child this button has always had.</summary>
        private static void ArmInlineConfirm(HUDItemButton b)
        {
            try
            {
                if (b == null || b.confirm == null || b.confirm_sel == null
                    || b.confirm_trans == null || b.big_trans == null)
                {
                    if (s_Self != null)
                        s_Self.Logger.LogWarning("inline confirm parts missing on the craft button - " +
                                                 "crafting WITHOUT a confirmation step rather than " +
                                                 "leaving the entry dead.");
                    s_InlineConfirmOk = false;
                    return;
                }
                b.confirm.text = "Confirm";
                b.confirm.gameObject.SetActive(true);
                b.confirm_sel.SetActive(false);
                s_InlineConfirmOk = true;
            }
            catch (Exception ex)
            {
                s_InlineConfirmOk = false;
                if (s_Self != null) s_Self.Logger.LogWarning("inline confirm unavailable: " + ex.Message);
            }
        }

        /// <summary>Is the cursor on the Confirm beside this row? The game's own test for a real destroy.</summary>
        private static bool ConfirmIsHot(HUDItem.Action action)
        {
            if (!s_InlineConfirmOk) return true;        // fail OPEN - see s_InlineConfirmOk
            HUDItemButton b = ButtonFor(action);
            if (b == null || b.confirm_sel == null) return true;
            return b.confirm_sel.activeSelf;
        }

        private static bool InsideRect(RectTransform rt)
        {
            if (rt == null) return false;
            Vector2 local = rt.InverseTransformPoint(Input.mousePosition);
            return rt.rect.Contains(local);
        }

        // Runs AFTER the game has chosen its own selection, so nothing here is fighting it - the
        // game leaves m_ActiveButton null once the cursor drifts off the row and onto the Confirm,
        // which is precisely why it does this same widening for Destroy.
        [HarmonyPatch(typeof(HUDItem), "UpdateSelection")]
        private static class Patch_CraftConfirmSelection
        {
            private static void Postfix(HUDItem __instance)
            {
                if (s_OfferedBy.Count == 0 || s_ActiveButtonsFI == null) return;
                try
                {
                    List<HUDItemButton> buttons = s_ActiveButtonsFI.GetValue(__instance) as List<HUDItemButton>;
                    if (buttons == null) return;

                    for (int i = 0; i < buttons.Count; i++)
                    {
                        HUDItemButton b = buttons[i];
                        if (b == null || b.confirm == null || b.confirm_sel == null) continue;
                        if (!s_OfferedBy.ContainsKey(b.action)) continue;
                        if (!b.confirm.gameObject.activeSelf) continue;

                        // The row plus its Confirm count as one target, so moving the cursor sideways
                        // onto Confirm does not deselect the thing it belongs to.
                        if (InsideRect(b.big_trans) && s_ActiveButtonFI != null)
                            s_ActiveButtonFI.SetValue(__instance, b);

                        b.confirm_sel.SetActive(InsideRect(b.confirm_trans));
                    }
                }
                catch (Exception ex)
                {
                    if (s_Self != null)
                        s_Self.Logger.LogWarning("craft confirm selection failed: " + ex.Message);
                }
            }
        }

        // ---- the paced queue ---------------------------------------------------------------------
        // One item per interval, re-checking the backpack before each. Ticked from Update rather
        // than run in a loop, so the game keeps running between them and he can watch it happen -
        // and so an interruption is simply the next tick not firing.
        private static Recipe s_Queue;
        private static int    s_QueueLeft;
        private static int    s_QueueMade;
        private static float  s_QueueNextAt;

        private void CraftQueueTick()
        {
            if (s_QueueLeft <= 0) { StopStuckCraftAnimation(); return; }

            try
            {
                // While the game is playing the crafting animation, wait for it. The animation IS
                // the pacing now - his ask was that the game go through the crafting once per item,
                // not that a timer stand in for it.
                CraftingController cc = CraftingController.Get();
                if (cc != null && cc.IsActive())
                {
                    s_AnimWasActive = true;
                    return;
                }

                // It was playing and now it is not, and our craft never fired: something stopped it.
                // Interrupting cancels the REST of the batch, from the point it was interrupted.
                if (s_AnimWasActive && !s_AnimDidCraft)
                {
                    int abandoned = s_QueueLeft;
                    FinishQueue();
                    Announce("Crafting interrupted - " + abandoned + " not made");
                    Logger.LogInfo("craft queue interrupted with " + abandoned + " left");
                    return;
                }
                s_AnimWasActive = false;
                s_AnimDidCraft = false;

                if (Time.time < s_QueueNextAt) return;

                if (MaxCraftable(s_Queue) < 1)
                {
                    int short_ = s_QueueLeft;
                    FinishQueue();
                    string shortOf = Missing(s_Queue);
                    Announce("Missing resources to craft " + short_ + " x " + Pretty(s_Queue.Result)
                             + (shortOf.Length > 0 ? "  - need " + shortOf : ""));
                    return;
                }

                // Ask the game to play its crafting animation. The item itself is made when that
                // animation finishes - see Patch_FinishCrafting - so the sound, the timing and the
                // interruption rules are all the game's rather than imitated here.
                if (StartCraftAnimation())
                {
                    s_AnimWasActive = false;
                    s_AnimDidCraft = false;
                    return;
                }

                // No animation available - fall back to the plain timed craft rather than stalling.
                CraftOneFromQueue();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("craft queue stopped: " + ex.Message);
                FinishQueue();
            }
        }

        private static void FinishQueue()
        {
            s_QueueLeft = 0;
            s_QueueMade = 0;
            s_AnimWasActive = false;
            s_AnimDidCraft = false;
        }

        /// <summary>
        /// Nothing left to make, so nothing should still be crafting.
        ///
        /// A second line of defence, not the fix - the fix is letting FinishCrafting run. But being
        /// welded into a looping animation with a hand stuck mid-swing is bad enough that it is worth
        /// a check that cannot be reasoned wrong: if OUR queue is empty and the controller is still
        /// going, end it. FinishCrafting is idempotent - it returns immediately unless m_InProgress -
        /// so calling it when the game has already tidied up costs nothing.
        /// </summary>
        private void StopStuckCraftAnimation()
        {
            if (s_QueueLeft > 0) return;

            // Do this even when there is no animation left to stop. A batch that ended cleanly is
            // exactly the case he hit - the animation stopped at the right time and the controls did
            // not come back - so the restore cannot be conditional on something still being wrong.
            RestoreWantedCount();
            RestoreInventoryState();
            RestorePlayerBlocks();

            if (!s_WeStartedAnim) return;              // never ours to stop

            try
            {
                CraftingController cc = CraftingController.Get();
                if (cc == null) { s_WeStartedAnim = false; return; }

                // m_InProgress is the game's own "a craft is happening" flag, and FinishCrafting
                // returns immediately without it - so this is only ever asking, never forcing.
                if (s_InProgressFI == null)
                    s_InProgressFI = typeof(CraftingController).GetField("m_InProgress",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                bool running = true;
                if (s_InProgressFI != null)
                    running = (bool)s_InProgressFI.GetValue(cc);

                if (!running) { s_WeStartedAnim = false; return; }   // the game already tidied up

                if (s_FinishCraftingMI == null)
                    s_FinishCraftingMI = typeof(CraftingController).GetMethod("FinishCrafting",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (s_FinishCraftingMI != null)
                {
                    s_FinishCraftingMI.Invoke(cc, new object[] { false });
                    Logger.LogWarning("craft animation was still running with an empty queue - stopped " +
                                      "it and gave control back.");
                }
                s_WeStartedAnim = false;
            }
            catch (Exception ex)
            {
                s_WeStartedAnim = false;
                Logger.LogWarning("could not stop a stuck craft animation: " + ex.Message);
            }
        }

        private static bool s_AnimWasActive;
        private static bool s_AnimDidCraft;

        // Set the moment WE ask for an animation and cleared only once the controller is confirmed
        // stopped. Deliberately separate from s_AnimWasActive, which is per-item bookkeeping the
        // queue clears as it goes - so it is false exactly when the guard would need it most.
        private static bool s_WeStartedAnim;
        private static FieldInfo  s_InProgressFI;
        private static MethodInfo s_FinishCraftingMI;

        // ---- giving the controls back --------------------------------------------------------
        //
        // Player.BlockMoves and BlockRotation are REFERENCE COUNTED - each Block is a request and
        // UnblockMoves only decrements. StartCrafting takes one of each and FinishCrafting returns
        // one of each, but only past its `if (!m_InProgress) return;` guard, and OnAnimEvent's
        // multicraft branch hands out an unbalanced pair of its own. Driving that lifecycle four
        // times for one batch of logs only has to slip once and he is stuck standing still with the
        // animation already finished - which is what he reported.
        //
        // So the batch does not reason about it. It measures: what were the counts before, what are
        // they after, put back the difference. Bounded by how many animations we actually started,
        // so a block belonging to something else can never be unwound by this.
        private static FieldInfo s_BlockMovesFI;
        private static FieldInfo s_BlockRotFI;
        private static int  s_BlockMovesAtStart;
        private static int  s_BlockRotAtStart;
        private static int  s_AnimsStarted;
        private static bool s_BlocksSnapped;

        private static int ReadBlockCount(FieldInfo fi, Player p)
        {
            if (fi == null || p == null) return -1;
            try { return (int)fi.GetValue(p); }
            catch { return -1; }
        }

        // THE TABLE'S BATCH SIZE, borrowed and given back.
        //
        // `CraftingController.OnAnimEvent` branches on `CraftingManager.m_WantedResultsCount > 1`, and
        // that number belongs to the crafting table - it survives whatever he last dialled there. If
        // it is still above one when OUR animation ends, the multicraft branch runs: it starts a
        // second animation we never asked for and hands out an unmatched BlockMoves/BlockRotation
        // pair. That is the extra pair his log caught, and the hitch he can feel.
        //
        // Our crafts are always singles, so we say so - and put his number back when we are done,
        // because it is his.
        private static int  s_WantedAtStart;
        private static bool s_WantedSnapped;

        // WAS THE BACKPACK OPEN BEFORE ANY OF THIS. The single piece of pre-crafting state that
        // was never captured, and the one the batch was trampling. See RestoreInventoryState.
        private static bool s_InvWasActive;
        private static bool s_InvSnapped;

        private void SnapshotPlayerBlocks()
        {
            s_AnimsStarted = 0;
            s_BlocksSnapped = false;

            try
            {
                CraftingManager cm = CraftingManager.Get();
                if (cm != null)
                {
                    s_WantedAtStart = cm.m_WantedResultsCount;
                    cm.m_WantedResultsCount = 1;      // one at a time, whatever the table thinks
                    s_WantedSnapped = true;
                }
            }
            catch (Exception ex)
            {
                s_WantedSnapped = false;
                Logger.LogWarning("could not pin the crafting batch size: " + ex.Message);
            }

            try
            {
                Inventory3DManager inv = Inventory3DManager.Get();
                s_InvWasActive = (inv != null && inv.IsActive());
                s_InvSnapped = true;
            }
            catch (Exception ex)
            {
                s_InvSnapped = false;
                Logger.LogWarning("could not read the inventory state: " + ex.Message);
            }

            try
            {
                Player p = Player.Get();
                if (p == null) return;

                if (s_BlockMovesFI == null)
                    s_BlockMovesFI = typeof(Player).GetField("m_BlockMovesRequestsCount",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (s_BlockRotFI == null)
                    s_BlockRotFI = typeof(Player).GetField("m_BlockRotationRequestsCount",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                s_BlockMovesAtStart = ReadBlockCount(s_BlockMovesFI, p);
                s_BlockRotAtStart   = ReadBlockCount(s_BlockRotFI, p);
                s_BlocksSnapped = (s_BlockMovesAtStart >= 0 && s_BlockRotAtStart >= 0);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not read the player's block counts: " + ex.Message);
            }
        }

        /// <summary>
        /// Say what is holding the player, out loud, in the log.
        ///
        /// Earned. Three theories went past on this one bug - the block counts, then
        /// not-the-block-counts, then the block counts after all - and each round cost him a session
        /// to disprove. A keypress that prints the actual numbers beats a fourth theory.
        ///
        /// Everything here is READ-ONLY. It cannot fix anything and is not meant to.
        /// </summary>
        private void DumpPlayerState()
        {
            try
            {
                Player p = Player.Get();
                Logger.LogMessage("=== player control state (DumpPlayerStateKey) ===");
                if (p == null) { Logger.LogMessage("  no player"); return; }

                if (s_BlockMovesFI == null)
                    s_BlockMovesFI = typeof(Player).GetField("m_BlockMovesRequestsCount",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (s_BlockRotFI == null)
                    s_BlockRotFI = typeof(Player).GetField("m_BlockRotationRequestsCount",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                Logger.LogMessage("  block counts   moves=" + ReadBlockCount(s_BlockMovesFI, p) +
                                  "  rotation=" + ReadBlockCount(s_BlockRotFI, p));
                Logger.LogMessage("  reported       MovesBlocked=" + p.GetMovesBlocked() +
                                  "  RotationBlocked=" + p.GetRotationBlocked());

                Inventory3DManager inv = Inventory3DManager.Get();
                bool invLatched = false;
                if (inv != null)
                {
                    FieldInfo fi = typeof(Inventory3DManager).GetField("m_PlayerRotationBlocked",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null) invLatched = (bool)fi.GetValue(inv);
                }
                // The latch is the interesting one - it is how Inventory3DManager remembers that IT
                // owns a rotation block. Latched but closed means a block nobody is coming back for.
                Logger.LogMessage("  backpack       open=" + (inv != null && inv.IsActive()) +
                                  "  holdsRotationBlock=" + invLatched +
                                  "  wasOpenAtBatchStart=" + s_InvWasActive);

                CraftingController cc = CraftingController.Get();
                bool inProgress = false;
                if (cc != null)
                {
                    if (s_InProgressFI == null)
                        s_InProgressFI = typeof(CraftingController).GetField("m_InProgress",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (s_InProgressFI != null) inProgress = (bool)s_InProgressFI.GetValue(cc);
                }
                Logger.LogMessage("  crafting       inProgress=" + inProgress +
                                  "  controllerActive=" + (cc != null && cc.IsActive()) +
                                  "  queueLeft=" + s_QueueLeft + "  animsStarted=" + s_AnimsStarted);

                HUDItem hud = HUDItem.Get();
                Logger.LogMessage("  item menu      open=" + (hud != null && hud.m_Active));
                Logger.LogMessage("  snapshot       moves=" + s_BlockMovesAtStart +
                                  "  rotation=" + s_BlockRotAtStart + "  taken=" + s_BlocksSnapped);
                Announce("State written to the log");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not dump the player state: " + ex.Message);
            }
        }

        /// <summary>
        /// Put the backpack back the way he left it.
        ///
        /// Deliberately BEFORE the block-count reconciliation, because this is what moves the counts:
        /// Deactivate calls UnblockRotation and Activate calls BlockRotation. Settle the real state
        /// first, then count what is left over.
        /// </summary>
        private void RestoreWantedCount()
        {
            if (!s_WantedSnapped) return;
            s_WantedSnapped = false;
            try
            {
                CraftingManager cm = CraftingManager.Get();
                if (cm != null) cm.m_WantedResultsCount = s_WantedAtStart;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not restore the crafting batch size: " + ex.Message);
            }
        }

        private void RestoreInventoryState()
        {
            if (!s_InvSnapped) return;
            s_InvSnapped = false;

            try
            {
                Inventory3DManager inv = Inventory3DManager.Get();
                if (inv == null) return;

                bool nowActive = inv.IsActive();
                if (nowActive == s_InvWasActive) return;    // already how he left it

                if (s_InvWasActive) inv.Activate();
                else                inv.Deactivate();

                Logger.LogInfo("craft batch: put the backpack back to " +
                               (s_InvWasActive ? "open" : "closed") + ".");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not restore the inventory state: " + ex.Message);
            }
        }

        /// <summary>Put the movement and rotation blocks back where the batch found them.</summary>
        private void RestorePlayerBlocks()
        {
            if (!s_BlocksSnapped) return;
            s_BlocksSnapped = false;

            try
            {
                Player p = Player.Get();
                if (p == null) return;

                int moves = ReadBlockCount(s_BlockMovesFI, p);
                int rot   = ReadBlockCount(s_BlockRotFI, p);

                // THE CAP IS THE POINT. We can only have contributed one block per animation we
                // started, so nothing beyond that is ours to release - if he picked up a wound or
                // opened something mid-batch, that block stays exactly where it is.
                int cap = Mathf.Max(0, s_AnimsStarted);

                int freeMoves = Mathf.Clamp(moves - s_BlockMovesAtStart, 0, cap);
                int freeRot   = Mathf.Clamp(rot   - s_BlockRotAtStart,   0, cap);

                for (int i = 0; i < freeMoves; i++) p.UnblockMoves();
                for (int i = 0; i < freeRot; i++)   p.UnblockRotation();

                if (freeMoves > 0 || freeRot > 0)
                    Logger.LogInfo("craft batch left the player blocked - released " + freeMoves +
                                   " move and " + freeRot + " rotation request(s).");

                s_AnimsStarted = 0;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not restore the player's controls: " + ex.Message);
            }
        }

        /// <summary>Play the game's crafting animation with nothing at stake in it.</summary>
        private bool StartCraftAnimation()
        {
            try
            {
                CraftingController cc = CraftingController.Get();
                if (cc == null) return false;

                // EMPTY on purpose. StartCrafting hides every item it is handed and then passes them
                // to CraftingManager.Craft, which would resolve them against the TABLE's recipes -
                // ten sticks and four ropes is not one of those, so the ingredients would vanish and
                // nothing would come back. Handing it nothing means it has nothing to lose.
                cc.StartCrafting(new List<Item>());
                s_WeStartedAnim = true;
                s_AnimsStarted++;
                return cc.IsActive();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("craft animation unavailable: " + ex.Message);
                return false;
            }
        }

        /// <summary>Make one, count it, and report. Called when the animation finishes.</summary>
        private void CraftOneFromQueue()
        {
            if (s_QueueLeft <= 0) return;

            if (MaxCraftable(s_Queue) < 1 || !DoCraft(s_Queue, false, s_QueueMade))
            {
                int short_ = s_QueueLeft;
                Recipe r = s_Queue;
                FinishQueue();
                string shortOf = Missing(r);
                Announce("Missing resources to craft " + short_ + " x " + Pretty(r.Result)
                         + (shortOf.Length > 0 ? "  - need " + shortOf : ""));
                return;
            }

            s_QueueMade++;
            s_QueueLeft--;
            s_QueueNextAt = Time.time + Mathf.Max(0.1f, _craftPacing.Value);

            if (s_QueueLeft > 0)
                Announce("Crafted " + s_QueueMade + " - " + s_QueueLeft + " to go");
            else
            {
                Announce("Crafted " + s_QueueMade + " x " + Pretty(s_Queue.Result));
                FinishQueue();
            }
        }

        // The animation finishing is what makes the item. Intercepted rather than followed, because
        // the original hands its items to CraftingManager.Craft - the TABLE's crafting - and our
        // recipe is not one the table knows.
        [HarmonyPatch(typeof(CraftingController), "FinishCrafting")]
        private static class Patch_FinishCrafting
        {
            private static bool Prefix(ref bool success)
            {
                if (s_Self == null || s_QueueLeft <= 0) return true;   // not ours
                bool made = success;
                try
                {
                    s_AnimDidCraft = true;
                    if (made) s_Self.CraftOneFromQueue();
                    else
                    {
                        int abandoned = s_QueueLeft;
                        FinishQueue();
                        s_Self.Announce("Crafting interrupted - " + abandoned + " not made");
                    }
                }
                catch (Exception ex)
                {
                    if (s_Self != null) s_Self.Logger.LogWarning("craft finish failed: " + ex.Message);
                    FinishQueue();
                }

                // FALSE, NOT SKIPPED. This is the difference between the two.
                //
                // The reason for interfering at all is CraftingManager.Craft(m_Items, false), which
                // would try to resolve our ten-sticks-and-four-ropes against the TABLE's recipe list -
                // and there is no such recipe there. But that call is the whole of the `success`
                // branch, so clearing the flag avoids it outright. The failure branch it falls into
                // calls RemoveAllItems on a table with nothing on it and re-inserts an empty list:
                // both no-ops, because StartCraftAnimation deliberately hands StartCrafting an empty
                // list in the first place.
                //
                // Everything AFTER that branch is the only teardown the game has - SetBool(m_CraftHash,
                // false), AudioSource.Stop, UnblockMoves, UnblockRotation, m_InProgress = false, and
                // switching both hands' items back on. Returning false skipped all of it, so the
                // animator looped forever and the hand the craft had hidden never came back. That is
                // the half-watch, half-crafting state he described.
                success = false;
                return true;       // let the game put the player back together
            }
        }

        /// <summary>Consume the ingredients and hand over the result.</summary>
        private void DoCraft(Recipe r) { DoCraft(r, true, 0); }

        /// <summary>
        /// One craft. Returns false if nothing was made, so a batch stops instead of grinding on.
        ///
        /// THE ORDER HERE IS THE WHOLE FIX, and it was learned the expensive way: asking for seven
        /// long sticks took 28 sticks and returned one. The old order was consume-then-create, and it
        /// created all seven results at the SAME world position in the SAME frame - seven rigidbodies
        /// spawned inside each other, which physics resolves by flinging them, and six of them were
        /// simply gone. The ingredients were charged for every one of them.
        ///
        /// Two changes make that impossible rather than unlikely:
        ///
        ///   1. CREATE FIRST, then consume. If the result cannot be made, the sticks are still in the
        ///      backpack. A failed craft now costs nothing, which is the only acceptable behaviour
        ///      for something that eats your materials.
        ///   2. The result goes into the BACKPACK, with the game's own insert doing the work and
        ///      falling back to the floor if there is no room. Nothing ends up as a pile of
        ///      overlapping physics objects at arm's length, so there is no scatter to lose.
        ///
        /// <paramref name="index"/> only offsets the spawn point of anything that does land on the
        /// ground, so two floor drops never occupy the same spot either.
        /// </summary>
        private bool DoCraft(Recipe r, bool announce, int index)
        {
            InventoryBackpack bp = InventoryBackpack.Get();
            if (bp == null || bp.m_Items == null) { Announce("Craft: backpack not ready"); return false; }

            Player p = Player.Get();
            if (p == null) { Announce("Craft: no player"); return false; }

            // Collect distinct Item references first. Destroy() is deferred to end of frame, so a list
            // gathered up front is the only safe way to be certain each object is consumed exactly
            // once - the same trap that produced the v1.8.0 harvest duplication.
            // EVERY ingredient, gathered before anything is spent. If the rope is short, the sticks
            // must not be touched - a partial charge for a craft that never happens is the worst
            // possible outcome and the reason this whole method creates before it consumes.
            List<Item> take = new List<Item>();
            if (!Gather(bp, r.Ingredient, r.Count, take, announce)) return false;
            if (r.Extra != null)
                for (int i = 0; i < r.Extra.Count; i++)
                    if (!Gather(bp, r.Extra[i].Id, r.Extra[i].Count, take, announce)) return false;

            // --- create BEFORE consuming ---------------------------------------------------------
            // Inventory3DManager.DropItem() is deliberately not used: its job is to eject an item that
            // is currently IN the inventory, and this one never was.
            Vector3 pos = GroundDropSpot(p);

            Item result = null;
            try { result = ItemsManager.Get().CreateItem(r.Result, true, pos, p.transform.rotation, true); }
            catch (Exception ex) { Logger.LogWarning("crafting: CreateItem threw for " + r.Result + ": " + ex.Message); }

            if (result == null)
            {
                // Nothing consumed yet, so nothing lost. This is the entire point of the ordering.
                Logger.LogWarning("crafting: could not create " + r.Result + " - ingredients untouched.");
                Announce("Craft failed - could not make " + Pretty(r.Result) + " (nothing used)");
                return false;
            }

            // --- only now, consume ----------------------------------------------------------------
            // Remove AND destroy. RemoveItem(ItemID, count) would only unlink - see the file header.
            int consumed = 0;
            for (int i = 0; i < take.Count; i++)
            {
                Item it = take[i];
                if (it == null) continue;
                try
                {
                    bp.RemoveItem(it, false);
                    UnityEngine.Object.Destroy(it.gameObject);
                    consumed++;
                }
                catch (Exception ex) { Logger.LogWarning("crafting: could not consume a " + it.m_Info.m_ID + ": " + ex.Message); }
            }

            if (consumed < take.Count)
            {
                Logger.LogWarning("crafting: consumed only " + consumed + "/" + take.Count +
                                  " - the result was already made, so this is in the player's favour.");
            }

            // --- and hand it over -----------------------------------------------------------------
            // Into the backpack if it fits, on the floor if it does not, with the game's own insert
            // making that decision. drop_if_cant is what keeps a full backpack from eating the
            // result - and putting it away is also what stops a batch becoming a heap of overlapping
            // rigidbodies at the player's feet, which is how six long sticks went missing.
            bool stored = false;
            try
            {
                InsertResult ins = bp.InsertItem(result, null, null, true, true, true, true, true);
                stored = (ins == InsertResult.Ok);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("crafting: could not put " + r.Result + " in the backpack (" +
                                  ex.Message + ") - it is on the ground.");
            }

            // BillText, not just the first ingredient. The old line read "crafted Log from 4 x Rope"
            // and looked for all the world like the ten sticks were never charged - they were, every
            // time, but a log that misreports what it spent is worse than no log at all.
            Logger.LogInfo("crafted " + r.Result + " from " + BillText(r) +
                           (stored ? " -> backpack" : " -> ground"));
            if (announce)
                Announce("Crafted " + Pretty(r.Result) + (stored ? "" : " - dropped on the ground"));
            return true;
        }
    }
}
