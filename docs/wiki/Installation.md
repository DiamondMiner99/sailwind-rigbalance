# Installation

## Step 1: BepInEx

If you already run any Sailwind mod, you have this and can skip to step 2.

Otherwise install **BepInEx 5.4.x, Windows x64** into your Sailwind folder, next to
`Sailwind.exe`, then run the game once so it creates `BepInEx/plugins` and
`BepInEx/config`.

Your Sailwind folder is usually:

```
C:\Program Files (x86)\Steam\steamapps\common\Sailwind
```

Steam can find it for you: right click Sailwind in your library, then Manage, then Browse
local files.

## Step 2: the mod

Extract the release zip into your Sailwind folder. You should end up with exactly this:

```
Sailwind/BepInEx/plugins/SailwindRigBalance/SailwindRigBalance.dll
```

**One copy only.** If a second copy of the DLL ends up anywhere else under `BepInEx`,
BepInEx will load whichever it finds and you can end up running an older build without
knowing. To check:

```
dir /s /b "C:\Program Files (x86)\Steam\steamapps\common\Sailwind\BepInEx\SailwindRigBalance.dll"
```

That should print exactly one line.

## Step 3: check it loaded

Launch the game, then open `BepInEx/LogOutput.log` and look for:

```
[Info: Sailwind Rig Balance] Sailwind Rig Balance v0.6.0 loaded.
[Info: Sailwind Rig Balance] [Init] 10 shipyard hook(s) applied; rig model available=True
```

Seven hooks with none failed, and `rig model available=True`, means everything resolved
against your game version.

Then sail to any shipyard and put a boat in. Three markers should appear.

## Updating

Replace the DLL. Your settings live in `BepInEx/config/com.sailwindrigbalance.mod.cfg`
and are not touched.

## Uninstalling

Delete `BepInEx/plugins/SailwindRigBalance/`. Nothing else is left behind except the
config file, which you can delete too. The mod never writes to your save.

## Multiplayer

The mod is entirely local and draws only for you. It changes nothing about the boat, so
running it alongside a co-op session does not affect anyone else, and other players do not
need it installed.
