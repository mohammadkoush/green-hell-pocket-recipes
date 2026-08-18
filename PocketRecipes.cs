// Pocket Recipes - write your own crafting recipes, and craft them from your backpack.
//
// WHY THIS MOD EXISTS
//
// Tessaaaaaa2 asked for it. She plays Green Hell on peaceful, wanted more things to make, saw this
// feature buried inside Pickup Doctor and asked whether it could be pulled out on its own so she
// could write her own recipes - saying up front that she has no modding experience.
//
// That was the right thing to ask for. Nobody should have to install a mod that also picks things up,
// harvests, composts, sorts, washes and spawns savages just to define a recipe. So this is that one
// feature, standing alone, with nothing else in the box.
//
// It is hers. MIT licensed, dependent on no other mod, and if she wants to publish it under her own
// name she should - the idea was hers and the code has no strings on it.
//
// WHAT IT DOES
//
//   Right-click an item in your backpack. If one of your recipes uses it, the menu offers to make the
//   result there and then - no walk to the crafting table. Roll the mouse wheel to choose how many.
//   A recipe with more than one ingredient asks you to confirm first, on the game's own Confirm
//   button - the same one it uses when you destroy something.
//
// WRITING A RECIPE - this is the whole syntax:
//
//     Small_Stick:2>Stick                two small sticks make a stick
//     Rope:4+Stick:10>Log!               four rope and ten sticks make a log, one at a time
//
//   INGREDIENT:COUNT>RESULT. Join more ingredients with +. Separate recipes with commas. A trailing !
//   means one at a time, with the game's crafting animation played for each one.
//
//   THE FIRST INGREDIENT IS THE ONE YOU RIGHT-CLICK.
//
//   Names are the game's own internal item names. Anything unrecognised is reported at startup and
//   skipped, never silently ignored - a recipe that quietly does not exist is worse than one that
//   fails loudly.
//
// THE HONEST LIMITS, written out because she said she has no modding experience and deserves to know
// what she is getting rather than discover it:
//
//   - It can make ANY item the game knows about, including things you would otherwise have to spawn
//     in. That is the point of it, and it is also a way to trivialise the game if you want to. Your
//     call, not mine.
//   - It cannot invent new items, models or behaviour. Only new ways to obtain existing ones.
//   - It does not touch the real crafting table and changes no recipe the game ships with.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PocketRecipes
{
    [BepInPlugin(Guid, Name, Version)]
    public partial class PocketRecipesPlugin : BaseUnityPlugin
    {
        internal const string Guid    = "com.tessaaaaaa2.pocketrecipes";
        internal const string Name    = "Pocket Recipes";
        internal const string Version = "1.0.0";

        internal static PocketRecipesPlugin s_Self;

        private Harmony _harmony;

        private void Awake()
        {
            s_Self = this;

            BindCraftingConfig();
            BindWheelConfig();

            // EVERY PATCH TARGET CHECKED BEFORE PatchAll RUNS.
            //
            // A Harmony patch aimed at a method that does not exist aborts the ENTIRE pass, silently:
            // the mod loads, logs nothing unusual, and simply does nothing. That is a miserable thing
            // to hand to someone with no modding experience, so it names what is missing and stands
            // down instead of pretending to work.
            if (!TargetsPresent())
            {
                Logger.LogError("Pocket Recipes is standing down - the game is not what this build " +
                                "expects, and nothing has been patched. If you are on a newer Green " +
                                "Hell than this mod, that is almost certainly why.");
                return;
            }

            try
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Logger.LogInfo(Name + " " + Version + " loaded.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Pocket Recipes failed to patch: " + ex.Message);
            }
        }

        private bool TargetsPresent()
        {
            bool ok = true;
            ok &= Need(AccessTools.Method(typeof(HUDItem), "Activate", new Type[0]), "HUDItem.Activate()");
            ok &= Need(AccessTools.Method(typeof(HUDItem), "ExecuteAction",
                                          new Type[] { typeof(HUDItem.Action), typeof(Item) }),
                       "HUDItem.ExecuteAction");
            ok &= Need(AccessTools.Method(typeof(HUDItem), "UpdateSelection"), "HUDItem.UpdateSelection");
            ok &= Need(AccessTools.Method(typeof(CraftingController), "FinishCrafting",
                                          new Type[] { typeof(bool) }), "CraftingController.FinishCrafting");
            ok &= Need(AccessTools.Field(typeof(HUDItem), "m_ActiveButtons"), "HUDItem.m_ActiveButtons");
            ok &= Need(AccessTools.Method(typeof(HUDItem), "AddSlot",
                                          new Type[] { typeof(HUDItem.Action) }), "HUDItem.AddSlot");
            return ok;
        }

        private bool Need(MemberInfo m, string what)
        {
            if (m != null) return true;
            Logger.LogError("Pocket Recipes: required API missing - " + what);
            return false;
        }

        private void Update()
        {
            try
            {
                UpdateWheelPicker();
                CraftQueueTick();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("update failed: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            try { DrawHud(); } catch { }
        }

        // ---- talking to the player -----------------------------------------------------------

        private string _lastAnnounceText = "";
        private float  _lastAnnounceAt = -1f;
        private readonly List<string> _hud = new List<string>();
        private float _hudUntil;
        private GUIStyle _hudStyle;

        /// <summary>Say it once, on screen and in the log.</summary>
        internal void Announce(string text)
        {
            // The same message twice is never information - two identical lines read as two events.
            try
            {
                float now = Time.realtimeSinceStartup;
                if (text == _lastAnnounceText && (now - _lastAnnounceAt) < 0.5f) return;
                _lastAnnounceText = text;
                _lastAnnounceAt = now;
            }
            catch { }

            Logger.LogMessage(text);

            _hud.Add(text);
            if (_hud.Count > 4) _hud.RemoveAt(0);
            _hudUntil = Time.realtimeSinceStartup + 4f;
        }

        private void DrawHud()
        {
            if (_hud.Count == 0 || Time.realtimeSinceStartup > _hudUntil) { _hud.Clear(); return; }

            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.label);
                _hudStyle.alignment = TextAnchor.MiddleCenter;
                _hudStyle.fontSize = Mathf.Max(13, Mathf.RoundToInt(Screen.height * 0.019f));
            }

            float w = Screen.width * 0.6f;
            float h = _hudStyle.fontSize * 1.6f;
            float y = Screen.height * 0.66f;

            for (int i = 0; i < _hud.Count; i++)
            {
                Rect r = new Rect((Screen.width - w) * 0.5f, y + i * h, w, h);
                // Shadowed: pale text on sunlit jungle is unreadable about a third of the time.
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), _hud[i], _hudStyle);
                GUI.color = new Color(0.96f, 0.93f, 0.80f, 1f);
                GUI.Label(r, _hud[i], _hudStyle);
            }
            GUI.color = Color.white;
        }
    }
}
