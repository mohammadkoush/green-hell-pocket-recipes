// Pocket Recipes - the mouse wheel picks how many.
//
// The right-click menu is a HOLD-to-show menu: let go of the right button and it vanishes. That makes
// the wheel free to mean quantity, and it means "release" is a natural way to commit. HUDItem never
// reads the scroll wheel itself - checked against the shipped assembly, where the only wheel consumers
// are the watch, UI lists and the free camera - so nothing is being fought over here.
//
// Rolling the wheel is what ARMS it. Until then this does nothing at all, so an ordinary right-click
// behaves exactly as it always did.
//
// THE ONE HARD-WON RULE IN THIS FILE: THE WHEEL FOLLOWS THE CURSOR.
//
// It used to decide which recipe you meant from the ITEM, at the moment you scrolled - before you had
// chosen a row. A stick can be both half of a long stick and half of a log, so the guess always landed
// on whichever recipe came first, and dialling four while pointing at the second one crafted four of
// the first. Every bug in this area came from that up-front guess.
//
// HUDItem.m_ActiveButton is the row under the cursor and the game already maintains it. Reading that
// instead means the number always belongs to the line it is written on.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PocketRecipes
{
    public partial class PocketRecipesPlugin
    {
        private ConfigEntry<bool> _wheelEnabled;
        private ConfigEntry<int>  _wheelMax;
        private ConfigEntry<bool> _wheelUseGameMenu;

        private Item   _wheelItem;
        private int    _wheelCount;
        private bool   _wheelArmed;
        private Recipe _wheelRecipe;
        private HUDItem.Action _wheelAction = HUDItem.Action.None;

        private void BindWheelConfig()
        {
            _wheelEnabled = Config.Bind("Wheel", "Enabled", true,
                "Roll the mouse wheel over a craft entry in the right-click menu to choose how many " +
                "to make. Without this, clicking the entry makes one.");

            _wheelMax = Config.Bind("Wheel", "MaxPerRequest", 50,
                new ConfigDescription(
                    "The most you can dial in one go. You may ask for more than you can afford - the " +
                    "craft re-checks before every single item, makes what it can, and tells you what " +
                    "was short.",
                    new AcceptableValueRange<int>(1, 500)));

            _wheelUseGameMenu = Config.Bind("Wheel", "ShowCountInTheMenu", true,
                "Write the count into the game's own menu entry while you dial, rather than floating " +
                "it above. Reads as part of the game instead of as a mod overlay.");
        }

        /// <summary>Called once a frame.</summary>
        private void UpdateWheelPicker()
        {
            if (_wheelEnabled == null || !_wheelEnabled.Value)
            {
                _wheelArmed = false; _wheelItem = null; _wheelAction = HUDItem.Action.None;
                return;
            }

            HUDItem hud = HUDItem.Get();
            bool menuOpen = hud != null && hud.m_Active && hud.m_Item != null;

            if (!menuOpen)
            {
                // Menu gone. If the wheel was armed, letting go IS the commit - but only for a recipe
                // that did not ask you to confirm. The whole point of asking is that walking away
                // costs nothing.
                bool fire = _wheelArmed && _wheelCount > 0;
                int    n = _wheelCount;
                Recipe r = _wheelRecipe;

                RestoreGameMenuText();
                _wheelArmed = false;
                _wheelItem  = null;
                _wheelCount = 0;
                _wheelAction = HUDItem.Action.None;

                if (fire && !NeedsConfirm(r)) DoCraftMany(r, n);
                return;
            }

            // A different item slid under the cursor - start over rather than carry a stale count.
            if (!ReferenceEquals(hud.m_Item, _wheelItem))
            {
                RestoreGameMenuText();
                _wheelItem   = hud.m_Item;
                _wheelCount  = 0;
                _wheelArmed  = false;
                _wheelAction = HUDItem.Action.None;
            }

            // THE WHEEL FOLLOWS THE CURSOR, not the item. See the header.
            Recipe hovered;
            HUDItem.Action hoveredAction;
            if (HoveredRecipe(hud, out hovered, out hoveredAction) && hoveredAction != _wheelAction)
            {
                RestoreGameMenuText();
                _wheelAction = hoveredAction;
                _wheelRecipe = hovered;
                _wheelCount  = 0;
                _wheelArmed  = false;
            }

            if (_wheelAction == HUDItem.Action.None) return;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) < 0.01f) return;

            if (!_wheelArmed) { _wheelArmed = true; _wheelCount = 1; }
            else _wheelCount += (scroll > 0f) ? 1 : -1;

            int available = Mathf.Max(0, MaxCraftable(_wheelRecipe));

            // YOU MAY ASK FOR MORE THAN YOU CAN AFFORD, on purpose. The request is legitimate and the
            // craft simply makes what it can and names the shortfall - which is friendlier than a
            // menu that refuses to let you express what you want.
            _wheelCount = Mathf.Clamp(_wheelCount, 1, Mathf.Max(1, _wheelMax.Value));

            ShowCountInGameMenu(hud,
                "Craft  " + _wheelCount + " x " + Pretty(_wheelRecipe.Result) +
                (_wheelCount > available ? "   (can afford " + available + ")"
                                         : "   (of " + available + ")"),
                _wheelAction, _wheelCount > available);
        }

        /// <summary>
        /// Hand the dialled count to whoever acts first, and disarm.
        ///
        /// Two things can end a craft: clicking the entry, or letting go of the right button. Both
        /// used to be able to fire - the click making one and the release making N - so twelve became
        /// thirteen. Whichever arrives first takes the number and leaves the other nothing to do.
        /// </summary>
        private int TakeArmedCraftCount(Recipe forRecipe)
        {
            int n = _wheelArmed ? Mathf.Max(1, _wheelCount) : 1;
            _wheelArmed  = false;
            _wheelCount  = 0;
            _wheelAction = HUDItem.Action.None;
            return n;
        }

        // The count is written into the game's own menu rather than floated over it. HUDItem's button
        // list is private, so this is reflected - and because it is borrowed UI, the original label AND
        // its colour are put back the moment we are done. RestoreGameMenuText is on every exit path.
        private HUDItemButton _menuButton;
        private string _menuButtonOriginal;
        private Color  _menuButtonOriginalColour;
        private bool   _menuButtonColourSaved;
        private static FieldInfo _activeButtonsField;

        private void ShowCountInGameMenu(HUDItem hud, string label, HUDItem.Action match, bool warn)
        {
            if (_wheelUseGameMenu == null || !_wheelUseGameMenu.Value || hud == null) return;

            try
            {
                if (_activeButtonsField == null)
                {
                    _activeButtonsField = AccessTools.Field(typeof(HUDItem), "m_ActiveButtons");
                    if (_activeButtonsField == null) return;
                }

                List<HUDItemButton> buttons = _activeButtonsField.GetValue(hud) as List<HUDItemButton>;
                if (buttons == null || buttons.Count == 0) return;

                HUDItemButton b = null;
                for (int i = 0; i < buttons.Count; i++)
                {
                    if (buttons[i] == null || buttons[i].text == null) continue;
                    if (match != HUDItem.Action.None && buttons[i].action != match) continue;
                    b = buttons[i];
                    break;
                }
                if (b == null) return;

                if (!ReferenceEquals(b, _menuButton))
                {
                    RestoreGameMenuText();
                    _menuButton = b;
                    _menuButtonOriginal = b.text.text;
                    _menuButtonOriginalColour = b.text.color;
                    _menuButtonColourSaved = true;
                }

                b.text.text = label;
                if (_menuButtonColourSaved)
                    b.text.color = warn ? new Color(1.00f, 0.26f, 0.22f) : _menuButtonOriginalColour;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not write the count into the game menu: " + ex.Message);
                _menuButton = null;
            }
        }

        private void RestoreGameMenuText()
        {
            if (_menuButton == null) return;
            try
            {
                if (_menuButton.text != null)
                {
                    _menuButton.text.text = _menuButtonOriginal;
                    // Colour goes back too. A red label left behind on the game's own menu would
                    // outlive this mod's involvement entirely and look like a bug in the game.
                    if (_menuButtonColourSaved) _menuButton.text.color = _menuButtonOriginalColour;
                }
            }
            catch (Exception ex) { Logger.LogWarning("could not restore the menu label: " + ex.Message); }
            _menuButton = null;
            _menuButtonOriginal = null;
            _menuButtonColourSaved = false;
        }
    }
}
