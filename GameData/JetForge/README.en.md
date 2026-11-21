# JetForge

Parametric custom jet engines for KSP. **Draw the geometry, get the performance**: fan and
compressor sizes, combustor and turbine geometry decide thrust, fuel burn, bypass ratio, overall
pressure ratio and every spool speed. You do not type in performance and get a curve back.

Installing, first flight and common questions are in `INSTALL.md` at the package root
(`INSTALL.en.md` is the English version).

## Hard dependencies

Since phase five this mod **requires** both of these:

| Mod | What for | Without it |
| --- | --- | --- |
| **Waterfall** | Plume (exhaust, shock diamonds, heat haze, colour temperature all live there) | No plume. The self-drawn plume was removed in phase five and is not coming back. |
| **RocketSoundEnhancement** | Dry engine sound (Doppler, air absorption, comb filtering) | The dry sound falls back to this mod's own layers, bypassing RSE's chain — serviceable, but flat. |

Nothing to configure once both are in `GameData`: `Waterfall.cfg` / `RSE.cfg` attach themselves
when they detect them (`NEEDS[...]`).

Missing a dependency does not crash anything: parts still attach, compute and fly, and `KSP.log`
gets one line saying what is missing.

## Contents

```
GameData/JetForge/
  Parts/JetForgeEngine.cfg        engine part (carries one default two-spool turbofan)
  Parts/JetForgeIntake.cfg        standalone intake part (you draw the profile)
  Localization/{zh-cn,en-us}.cfg  part names and descriptions in two languages
  Waterfall.cfg / WaterfallTemplates.cfg   plume (9 FX: dry/afterburner × cold/mid/hot)
  RSE.cfg                         RSE sound group (group name = engineID = JetForge)
  Sounds/                         9 looping layers (derived from RSE's JetsTurbines, GPL-3.0)
  Textures/                       7 texture sets (cold/hot/inner/casing/lip/intake in+out) + normals
  UserDesigns/                    your saved designs (appears once you run the game)
  Plugins/JetForge.dll            the plugin itself
```

## Using it

Right-click the part in the VAB/SPH → **JetForge designer**. Nine tabs from Overview to Cutaway:
Overview, Fan, Compressor, Turbine, Combustor, Afterburner, Readout, Heat map, Cutaway — simple
mode keeps three (Overview, Readout, Cutaway). Hit "apply to part" and mass, cost, shape, plume and
sound are all recomputed.

- **Design wizard**: unsure where to start? Pick a calibrated preset by role / era / flight Mach,
  type a target thrust, and it solves tip diameter and fan pressure ratio and tells you where every
  starting value came from. Three steps of undo.
- **Test bench** (readout tab): sea-level static thrust, operating points across altitude / Mach /
  throttle, SFC curves.
- **The intake is a separate part** with its own window (section skeleton / single-section profile /
  parameters). Angle of attack and sideslip come from the flight state — you do not type them.
- **"My designs"** in the side panel saves the current design to `UserDesigns/<name>.cfg`; engines of
  the same type on one vessel follow each other automatically.
- The UI is Chinese or English depending on the game language. Logs are always Chinese.

## Known trade-offs

These are **model boundaries**, not bugs waiting to be fixed. For the source, assumptions and error
grade of each, open the model accuracy ledger in the designer readout: 22 entries graded A to D,
with grade D saying plainly "no real-engine data, relative comparison only".

- The compressor map is **synthetic** (design point plus three constants); surge margin only compares
  designs against each other.
- Design point and rated point are not separated: lighting the afterburner recomputes the design
  point as an "afterburner rating" (+29% thrust, +91% SFC).
- The turbofan's best SFC sits at lower throttle than a real engine (real ones: 85–95%).
- Nacelle diameter is solved from "equivalent capture area", so it differs from real engine casings by
  roughly 25% (presets correct it with `NacelleDiameter`).
- Length estimate is within ±8–10% of real engines; dry mass runs heavy for composite fans (GTF kind).
- GTF1100 computes a bypass ratio of 6.8 against 12.5 on the real engine: this model cannot squeeze
  the core flow area that small.
- The gearbox models mass, 1/η loss and inertia only — **no compliance (torsional vibration)**.
- Three-spool engines do not decouple spool speeds (under-determined without real maps); turbine
  supply/demand is checked per stage instead.