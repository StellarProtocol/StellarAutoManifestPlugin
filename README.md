# Stellar.AutoManifest

A StellarResonance plugin that runs the game's **Matrix Manifestation** for you: it keeps pulling, stores the imprints
you asked for into your pocket, and stops once it has them all (or hits a limit you set).

Open it from the launcher tile, or from a hotkey you assign ("Open Auto Manifestation" in the hotkey settings; it has
no default key).

## What it does

- **Targets**: press **+ Add target** and pick an imprint and a minimum value, for example *Agility %* at **0.43%+**.
  The "+" means "this value or higher": a target set to 2.24%+ takes 2.24% or any bigger roll of that imprint. The
  picker lists exactly the values the game can roll for that imprint, with names and values from the game's own data
  in your client language. Class imprints follow your current looting class. Want two copies of the same imprint? Add
  it twice. Each row is one store.
- **Run options**:
  - **Pull size**: 1 pull or 10-pull.
  - **Use Blessing**: adds a Dawn Prism to each pull (better quality odds, and the pull gives Matrix EXP). The plugin
    uses it only when the game itself allows Blessing at your level.
  - **Spend at most (Remnants)**: the most Lightchase Factor Remnant one run may use.
  - **Keep pocket slots free**: leaves that many pocket slots empty for you.
- **Start** shows a confirm card first: the most Remnants (and Dawn Prisms) the run can spend, and a reminder that your
  last pull's unstored results get cleared by the next pull.
- While it runs, the footer shows each pull and each store, and the target rows turn to **stored 2.72%** as they are
  filled.

## When it stops

- every target is stored (**Done**);
- the pocket is full, counting the slots you keep free;
- the next pull would go over your spend limit;
- you don't have enough Remnants or Dawn Prisms for the next pull;
- the game refuses a pull or a store, or doesn't answer in time;
- you press **Stop** or close the window (it finishes the request in flight first);
- you leave the world, log out or hit a loading screen.

The footer then says why, and how many targets are still waiting.

## Can my targets roll yet?

Higher imprint qualities only start to roll at higher Matrix levels, and Blessing opens them earlier. The plugin
reads the game's own odds table for your level and shows it:

- A target row shows **needs Lv 4** (amber) when its value can't roll at your current level, and **locked** when the
  level it needs is still locked for you. Otherwise it shows **waiting**.
- In the picker, values that can't roll yet are faded (you can still pick them), with a hint such as "Epic can't roll
  at Lv 2. From Lv 4 with Blessing (about 92 more Blessing pulls), or Lv 10 without." A small table shows the chance of
  each quality per row at your level, with Blessing on or off.
- **Start** stays off only when none of your waiting targets can be reached: nothing can roll at your level and
  Blessing is off (the plugin counts only Blessing pulls as levelling the Matrix), or every target needs a locked
  level. If some targets need a higher level and Blessing is on, Start is allowed and the confirm card lists them: the
  run levels up the Matrix and looks for them once it gets there. Level-ups during a run show in the footer.

## Stored marks are remembered

A row's **stored** mark is saved with your target list, so it is still there after you restart the game. The plugin
remembers which pocket entry it stored for that row and checks your pocket each time it reads the Matrix:

- If that imprint leaves your pocket (for example you remove it in game), the row goes back to **waiting**.
- On another character the row shows **waiting**, because that character's pocket is different. Switch back and it
  shows **stored** again.
- Changing a row's minimum value, or removing the row, clears its mark.

A stored row is skipped by the next run. Start needs at least one waiting target.

## What it never does

- It never unlocks pocket slots (those cost items).
- It never uses special Manifestation items. The only extra item it can add to a pull is the Dawn Prism Blessing, and
  only when you turn Blessing on.
- It only stores into free pocket slots. It never removes or replaces anything already in your pocket.

## Requirements

- Stellar framework **2.33.0** or newer.

## Build

```bash
dotnet build -c Release
```

For a local deploy, copy `Local.props.example` to `Local.props` and set `GameInstallDir` (deploys to
`<game>\stellar\plugins\automanifest\`). The game must be closed to deploy, because it locks the DLL.

### Dev notes

- All user-facing text lives in `Lang\*.json` (en, ja, th, id, fil, ko). Add every new key to every file.
- Game names: "Manifestation" is *Reforge* in the game data, an "imprint" is an *entry*, and the pocket is *storage*.
- Every roll and store is logged to `BepInEx\LogOutput.log` with the `[AutoManifest]` prefix: pull size, level, cost,
  the result entry ids per pull, what was stored at which pull and row, and the pocket count before and after.
- The plugin talks to the game through Lua only (`Plugin.Lua.Script.cs`). Results are read back, never called back.
  Game reads happen only while the window is open (once per second) or a run is going.

## License

AGPL-3.0-or-later. See `LICENSE`.
