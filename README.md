# Pocket Recipes

**Write your own crafting recipes, and craft them straight from your backpack.**

Right-click an item. If one of your recipes uses it, the menu offers to make the result there and
then — no walk to the crafting table. Roll the mouse wheel to choose how many.

---

## This mod exists because Tessaaaaaa2 asked for it

She plays Green Hell on peaceful, wanted more things to make, spotted this feature buried inside a
much larger mod, and asked whether it could be pulled out on its own so she could write her own
recipes — mentioning that she has no modding experience.

That was exactly the right thing to ask for. Nobody should have to install a mod that *also* picks
things up, harvests, composts, sorts, washes and spawns hostile tribesmen just to define a recipe.

**So this is hers.** It is MIT licensed, it depends on no other mod, and if she wants to publish it
under her own name, she should — the idea was hers and the code has no strings on it.

## Writing a recipe

Open `BepInEx/config/com.tessaaaaaa2.pocketrecipes.cfg` after running the game once, and edit
`Recipes`. That is the whole interface — there is nothing to compile and nothing to install beyond
the DLL.

```
Small_Stick:2>Stick,  Stick:4>Long_Stick,  Rope:4+Stick:10>Log!
```

| you write | it means |
|---|---|
| `Small_Stick:2>Stick` | two small sticks make a stick |
| `Rope:4+Stick:10>Log` | join more ingredients with `+` |
| `...>Log!` | a trailing `!` means **one at a time**, with the game's crafting animation for each |
| `A, B, C` | separate recipes with commas |

**The first ingredient is the one you right-click.** `Rope:4+Stick:10>Log` appears when you right-click
rope, not sticks.

Names are the game's own internal item names — `Small_Stick`, `Bird_feather`, `Tribe_Arrow`,
`Coconut_Bowl`. **Anything unrecognised is reported in the log at startup and skipped**, never
silently ignored, so a typo tells you rather than just quietly never working.

### Finding item names

The game's log lists them when this mod complains about one. If you want the full list, any item you
can see in your backpack has its name written in `BepInEx/LogOutput.log` when Pocket Recipes reports
on it. There is no need to guess.

## How it behaves

- **Roll the wheel** over a craft entry to pick a quantity. The count appears in the menu itself.
- **You may ask for more than you can afford.** It makes what it can and tells you what was short,
  rather than refusing to let you say what you wanted.
- **A recipe with more than one ingredient asks you to confirm**, on the game's own Confirm button —
  the same one it uses when you destroy something. One ingredient stays a single click, because
  confirming that thirty times is the tedium this is meant to remove.
- **Nothing is spent on a craft that fails.** The result is created first; only then are the
  ingredients taken. A failed craft costs you nothing.

## Honest limits

- It can make **any item the game knows about**, including things you would otherwise have to spawn
  in. That is the point — and it is also a way to trivialise the game if you want to. Your call.
- It **cannot invent new items**, models or behaviour. Only new ways to obtain existing ones.
- It does **not** touch the real crafting table, and changes no recipe the game ships with.

## Do not run this alongside Pickup Doctor

Pickup Doctor contains this same feature. Running both puts **two** craft entries in the same menu and
both will try to add and confirm. Pick one:

- want *only* recipes → **Pocket Recipes**
- already running Pickup Doctor → you already have this; turn on `Crafting/Enabled` there

## Install

Requires [BepInEx 5](https://github.com/BepInEx/BepInEx) (x64).
Drop `PocketRecipes.dll` into `Green Hell\BepInEx\plugins\PocketRecipes\`.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy
```

Stock .NET Framework `csc.exe` — no Visual Studio or SDK. References come from the game install, so a
build always matches the installed version.

## Licence

MIT. See [LICENSE](LICENSE). Do what you like with it.
