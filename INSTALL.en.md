# JetForge — install and first flight

> 中文说明见 [INSTALL.md](INSTALL.md)。

JetForge turns the engine from a bundle of hard-coded numbers into something you design: you set the
pressure ratio, the turbine entry temperature, the fan diameter, and it works out thrust, fuel
consumption and dry mass — then draws the shape and a cutaway. The intake is a separate part of equal
standing, with its own panel for capture area, throat ratio and compression surface.

## 1. Requirements

| Item | Value |
| --- | --- |
| KSP version | 1.12.0 – 1.12.99 (development and self-test run on 1.12.4) |
| Required | **Waterfall** (plumes), **RocketSoundEnhancement** (dry-engine sound) |
| Optional | texture replacers such as ReStock, Real Solar System (the base sound layer steps aside) |
| Not verified | FAR, RO/RP-1 (both change aerodynamics and scale, so the numbers will differ) |

Waterfall and RSE are hard dependencies: the self-drawn plume was removed and is not coming back.
Drop both into `GameData` and you are done — `Waterfall.cfg` / `RSE.cfg` attach themselves through
`NEEDS[...]`. Missing them will not crash anything; you simply get no plume and only the mod's own
sound layers, and `KSP.log` will say `[JetForge] 缺少必需依赖：…` (missing required dependency).

## 2. Installing

1. Close the game.
2. **Merge** the `GameData` folder from the zip into the `GameData` folder of your KSP install.
   You should end up with `Kerbal Space Program/GameData/JetForge/Plugins/JetForge.dll`.
   If you see `GameData/JetForge/JetForge/...`, you have one folder too many — move it up.
3. Start the game and open the VAB. A wrong install will not crash, the parts just will not show up
   in the list.

## 3. First flight

**Placing an engine**

1. In the VAB or SPH, search the part list for `JetForge`.
2. Drop in an engine (presets: JF-1, J79, J57, J85, RD-33, GTF1100).
3. Right-click it → **engine designer**. Pages on the left (8 in realistic mode: general / fan /
   compressor / combustor / turbine / afterburner / nozzle / thermodynamics; 3 in simple mode, which
   only exposes a few master knobs), live readouts on the right.
4. Press **Apply** or **Build mesh** when done. Both record the design; "Build mesh" also rebuilds the
   part shape immediately.
5. **Engine test bench…** at the bottom runs the same engine across an altitude / speed / throttle
   grid so you can see whether it behaves like an engine.

**Deciding how to use it**: read thrust / fuel consumption / dry mass on the readout page, then look at
the altitude profile on the bench. An engine that looks great at sea level and keeps 30% at 8 km is a
different engine to mount than one that holds thrust up high.

**Placing an intake (optional)**

The intake is a separate part that sits ahead of the engine. Right-click it → **intake designer**.

- Skeleton page: drag a station line to move it, drag across to change its radius, add or delete
  stations.
- Section page: nine presets (circle / ellipse / drum / rectangle / biconvex lens …) plus a free
  polygon, with X mirror, Y mirror or XY symmetry.
- The profile is turned into capture area, throat ratio, compression angle, stage count and duct bend,
  and those feed straight into performance.

**You can fly without an intake** — the engine then feeds itself from its own casing diameter. With an
intake, the air comes from the intake.

## 4. Common questions

**The intake is not supplying air?**
Same vessel is enough — there is no distance limit. Check the "capture diameter" and "supply %"
readouts on the intake panel. A zero means the throat area came out as zero (give the capture diameter
or throat ratio a positive value).

**Changed a parameter and nothing happened?**
- Designs only take effect in the editor. Changing a design in flight updates the readouts but not the
  shape — deliberately: reshaping in flight would be cheating.
- With two engines of the same type, automatic sync only touches the one that was identical before the
  edit (so your deliberately different pair stays different). When they really have diverged, use
  **sync all same type** on the designer's mesh page to overwrite by name.

**Duplicated a part with ALT and the shape changed?**
Fixed. If you still see it, reload the craft once — that heals it — and please report the steps.

**Stuttering?**
Performance grids are cached: one design, one solve. If it only stutters in flight, first check whether
you are carrying dozens of intakes (each updates its angle of attack and sideslip every physics frame).

**Where are the logs?**
`Kerbal Space Program/KSP.log`, search for `[JetForge]`. The self-test report is
`GameData/JetForge/selftest.log`.

## 5. Uninstalling

Delete the whole `GameData/JetForge` folder. Nodes left behind in save files will not crash the game;
they are ignored when the save loads.

## 6. How it computes (for the curious)

The design point is a real solve: intake → fan → splitter → LPC/HPC → combustor → turbines →
afterburner → nozzle, with mass flow, total pressure and total temperature passed station to station.
Compressors and turbines do work according to their own tip speeds and efficiencies, and the shafts
have to balance. Off-design is the same chain recalculated for spool speed, altitude and Mach, with
compressors working off their maps and reporting surge margins.

Model grades live in the development docs: which parts are conservation equations, which are empirical
correlations, which are game approximations — all marked. Grades are not inflated for looks: where the
model cannot say, it says so.