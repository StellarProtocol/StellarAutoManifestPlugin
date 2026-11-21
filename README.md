# Stellar.AutoManifest

StellarResonance plugin. **Auto Manifestation**: repeats the game's Matrix Manifestation (Season Talent Reforge) for
you and stores the imprints you picked into your pocket, then stops. Builds against the published SDK:

```bash
dotnet build -c Release
```

Requires framework **>= 2.33.0**. Copy `Local.props.example` to `Local.props` to deploy into a game install.

## What it does

- **Targets**: pick imprints ("Agility % ≥ 0.43%") in the Add-target picker. The list and every value come from the
  game's own tables, in the client's language. Two rows with the same imprint = two stores.
- **Run**: pull size (1 / 10), Blessing (Dawn Prism, only when the game allows it), a spend limit in Remnants and how
  many pocket slots to keep free. A confirm shows the most it can spend before anything happens.
- It stops when every target is stored, the pocket is full, the spend limit or a material runs out, the game refuses
  a request, you press Stop / close the window, or you leave the world. It never unlocks pocket slots.

## Log

Every roll and store is logged to `BepInEx\LogOutput.log` with the `[AutoManifest]` prefix (ret, pull size, level,
cost, the result entry ids per pull, what was stored at which pull/row, pocket count before and after).
