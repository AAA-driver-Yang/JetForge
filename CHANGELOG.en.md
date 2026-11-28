# Changelog

This file records only what a player can notice. Item-by-item technical detail lives in
`docs/phase2-mod.md` (not shipped in the release package).

> 中文版见 [CHANGELOG.md](CHANGELOG.md)。

## 1.0.1 — intake supply, shape sync and overload wear

### Fixed

- **Multiple or symmetric intakes supplied only one intake's worth of air**: two intakes used to feed the
  engine as if there were one — the panel claimed 120 kg/s while the engine received about thirty.
  Flow is now the sum of all units, capped only by the **engine's own face area**: fitting more intakes
  does not buy free thrust, and it no longer under-feeds either.
- **Editing one of a set of identical or mirrored intakes left the others unchanged**: the numbers moved
  but the mesh in the hangar did not.
- **"Supply" and "demand" came from different numbers**: the part info now prints both on one line —
  supply in kg/s and the most this engine's face can take — with a note that the excess spills at the lip.

### Changed

- **Overload no longer breaks the engine**: pulling g only records a peak, it no longer counts toward a
  failure threshold and the "rotor bearing overload fracture" can no longer happen. The
  "structural / overload damage" and "reinforcement" options are gone together with the reinforcement mass
  penalty; what can still destroy an engine is surge count, sustained over-temperature after a burn-through,
  and sustained overspeed. Old saves that contain those two keys simply ignore them.

## 1.0.0 — first release

### Added

- **Geared turbofan (GTF)**: the fan page gains a **gear ratio** (1 = direct drive, adjustable 1–4) and a
  **gear efficiency**. The gearbox lets the low-pressure shaft and the fan turn at their own speeds, at
  the cost of gearbox mass (hundreds of kilograms), 1/η mechanical loss, a longer engine and more shaft
  inertia (slower to accelerate). New preset **GTF1100** (PW1100G-class geared turbofan): fan Ø2.06 m,
  gear ratio 3.0, about 103 kN.
- **Intake as its own part**: a separate part with its own editor window (section skeleton / single
  section profile / parameters). Draw any section you like (nine presets + free polygon + X/Y mirror + XY
  symmetry); the profile becomes the 3D shape directly.
- **Real shock model**: supersonic intake recovery is solved stage by stage through normal shock /
  θ-β-M oblique shock / cone shock. Past the maximum deflection angle for that Mach number the intake is
  reported as unstarted (shock detached) and recovery falls back to a normal shock.
- **Stock intake compatibility**: stock intakes on the same vessel can feed a JetForge engine.
- **Variable centre cone**: pushes forward with Mach number at supersonic speed and narrows the throat
  (can be switched off).
- **Cone tip position is adjustable**: the cone is no longer nailed behind the lip - template outlines get a
  "Cone tip" slider (a fraction of the length; positive pokes past the lip) and hand-drawn profiles get
  "Tip offset m". Both change the outline only, never throat area or flow.
- **Intake panel split into "Design" and "Readouts" pages**: forty-odd rows used to sit in one column, so
  changing a slider meant scrolling a page and a half to see the numbers. Editing and reading now live on
  separate pages that each fit one screen; the "these three are not set here" notes moved to Readouts too.
- **Compressor map**: surge line, choke line, working line and surge margin, visible in the heat map.
- **Per-shaft torque balance**, **geometry interface checks** (mismatched inlet/outlet areas are
  reported), and a **three-tier fault ledger** (design warning / operating risk / actual event, with the
  same defect never punished twice).
- **Engine test bench** (designer readout page): sea-level static thrust, operating points across
  altitude / Mach / throttle, SFC curves.
- **Design wizard**: pick a calibrated preset by role (fighter / transport), era and flight Mach, type a
  target thrust, and the wizard solves tip diameter and fan pressure ratio (it only scales sizes; pressure
  ratios, efficiencies and temperatures stay put). It lists where every starting value came from and which
  ones contradict each other. Three steps of undo if it goes wrong.
- **My design library**: a tuned engine saves to a file (survives other craft and reloads); entries can be
  loaded, renamed, copied and deleted. Every save first keeps a snapshot of what was there (last five, one
  click to go back). Two designs can be compared side by side over ten readings - thrust / SFC / overall
  pressure ratio / bypass ratio / turbine inlet temperature / mass flow / surge margin / length / dry mass /
  cost. It reports direction and delta only and never picks a winner for you (10% more thrust and 10% less
  cost are not the same kind of good).
  **Exchange folder**: a library entry can be dropped into `GameData/JetForge/UserDesigns/Shared` to hand to
  someone; files others put there are imported with one click from the Share panel. A name clash never
  overwrites your own copy - it is renamed instead. How the files travel is not the mod's business.
- **Undo while dragging sliders**: the designer's bottom bar has an "Undo (N steps left)" button, `Ctrl+Z`
  works too, up to twenty steps. A step is recorded when you release the mouse, and it returns the value
  from before that drag started. Undo never crosses engines: binding a part or loading another design
  clears it. The Simple mode sliders undo too, on their own stack, so switching modes never mixes them up.
- **English and Chinese**: UI, status line, check descriptions, part names and descriptions all have
  English; the log stays Chinese (that is for the developer). A missing English string falls back to
  Chinese rather than printing half a sentence.
- **Control system tiers**: mechanical / hydro-mechanical / analog / digital (FADEC). The difference is
  real capability, not a label: mechanical does not limit temperature, analog has neither surge
  suppression nor overspeed fuel cut, and only digital relights by itself. Era decides which tiers are
  available, and whether the nozzle runs closed-loop.
  **With a controller the throttle is a thrust command**: the loop finds the fuel, the command ramps up over
  about two seconds, and slamming the lever forward does not make thrust jump. Mechanical has no thrust
  sensor, so there the lever still feeds fuel directly.
- **Air relight window**: 12%-70% spool, igniter pressure threshold 45/35/28/20 kPa by tier. Outside the
  window it will not light and says whether spool or inlet pressure is what you lack.
- **Per-stage surge margin**: not just one number for the whole compressor — it reports which stage is
  tightest (front stages carry more load, rear stages less, and variable stators flatten that out); the
  other common trap is moving towards choke when you pull the throttle back.
- **Model accuracy ledger**: 23 models, each with its source, assumptions, error and limits of
  applicability, graded A to D. Grade D says plainly "no real-engine data, relative comparison only" —
  better to know what is unknown than to pretend.
- **Checks sorted by severity**: the one thing you should fix first is always on the first line; passing
  checks collapse into "N other checks pass".

### Added

- Intake profiles can be cut askew: **drag a single edge dot sideways** in the top or side view
  and that station tilts. Side view tilts the inlet forward/back, top view tilts it left/right,
  and both can be used together - this is how you build an F-15 style slanted inlet.
  Tilt is shape only: mass flow and thrust are unchanged. Old saves still load (0 degrees).
- Profile editor: the **first section would not move** (the canvas followed it and re-fitted,
  so it looked pinned), and it could not be pulled forward either - the view now anchors at 0
  and only ever zooms out; first and last station both travel +/-2 m.
- Profile editor: the first section's tilt dot sits against the canvas edge, so a steady hand
  was required. There is now a "tilt side/top [-5][+5]" button row for slanted inlets.

### Fixed
- **Symmetry-placed intakes were counted as one**: two intakes that can each swallow 30 kg/s feeding an
  engine that wants 70 used to have their combined flow quietly clamped back to the largest single one, so
  the panel read 120 kg/s supplied while the engine only ever got 30 and never reached rated thrust. Multiple
  intakes now add up, and **the only ceiling is the engine face**: fitting extras never invents thrust, but
  they no longer count for nothing either.
- **Editing one of a mirrored/matching set of intakes left the others behind**: the window copied the new
  numbers to the sibling parts but skipped the "rebuild the mesh" step, so fields and readouts changed while
  the part still looked old. Fixed.
- **Overload damage removed at the player's request**: pulling g no longer accumulates bearing strain and
  never cuts thrust; only surge, burn-through and overspeed can destroy an engine. The "G damage" and
  "reinforcement" options are gone with it (those keys in old saves are ignored and not written back).
- **A bigger intake no longer means more thrust**: the engine used to take whatever air the part's
  throat could pass, so a huge intake shape made a 2 t engine swallow over 7000 kg/s and multiply its
  thrust (a player measured 344 kN at 7787 kg/s). The engine face is now a hard cap: the face is only so
  wide and its Mach number is fixed, so the surplus spills at the lip - the status line says so.
- **"Draft ready" and "No draft yet" showed up on the same screen**: the footer decided whether the inputs
  had changed with `Math.Abs(a-b) > 1e-9`, but a slider returns its value quantised to pixel steps, so feeding
  it back looked like a change on every frame - the draft was thrown away one frame after being generated.
  The test now compares at display precision (Mach 0.001 / altitude 1 m / thrust 0.05 kN), and changing an
  input no longer discards the draft; it just says "this one was computed from older inputs".
- **The wizard's verdict now pops up at the centre of the screen**: one line for generated / not generated and
  one for applied / not applied (`ScreenMessages`), instead of text inside a window that can be scrolled away.
  **This build only takes effect after restarting the game** - the DLL is loaded once at startup.
- **Pressing "Generate draft" looked like nothing happened**: it did run, but the verdict (generated in N ms,
  solved thrust, where each number came from) was drawn inside the scroll view while the button itself sat on
  that view's bottom edge — so the new lines landed just outside the visible area. The footer now states the
  verdict on a fixed line ("Draft ready: fan dia ..., solved ... kN / target ... kN"), and the window is 60 px taller.

- **The wizard's draft said "ready" but there was nowhere to hand it over**: touching role / era /
  afterburner after generating cleared the draft while the "generated" line stayed, and the footer button
  vanished with it. Changing any parameter now clears both together, and that button is always there —
  greyed out with a plain instruction when there is no draft yet. Dragging the Mach / altitude / thrust
  sliders counts as changing a parameter too (before, it still let you hand over a draft built on the old
  numbers). The button also says what it does now: "Put it on this engine".

- **Thrust vectoring yawed the wrong way**: pitch had the negative sign, yaw did not — both axes follow the
  same rule (a positive command wants torque along the negative control axis). Fixed, including the fallback
  used when the torque cannot physically be produced. The old regression check pinned how far the nozzle
  deflected but not which way, so a reversed axis kept passing.
- **The wizard's "hand to designer" button was pushed outside the window**: the scroll view swallowed all of
  the remaining height and left the footer nowhere to stand, so the screen said "draft ready" while the button
  was unreachable. The scroll view is capped now and the footer always shows; after clicking, the design is
  read back (success is only claimed when it really took) and the designer opens on that part.

- **A raw `#autoLOC_...` string showed up in the right-click menu**: the afterburner button's label is a
  localisation key concatenated with the mode ("auto" / "always"), and KSP only translates a string that is
  exactly one key — so the key itself reached the player. The key is translated first now, then joined.
  Percent signs are also typeset flush against the number (they used to print as "70 %"), including the few
  places where the space was typed into the format string by hand.

- **Design library in the designer's General tab is one engine per row**: it used to pack three per row,
  squeezing each name to the point of being unreadable; messages now sit on their own line too.

- **The "nacelle diameter" field did almost nothing**: it was used to widen the intake mouth while the skin
  was drawn at 1.03 times that radius — type 2.15 and you measured 2.21, type anything smaller than the mouth
  and it was ignored outright. It now sets the widest point of the skin, exactly.
- **The fan cowl of a high-bypass engine is no longer pinched**: the bypass exit radius used to be the smaller
  of "86% of the fan" and "1.72x the core"; on a high-bypass engine the second is far smaller, so the cowl
  collapsed from 1.08 to 0.57 within a quarter of the body. Only the "must at least clear the core" floor is
  kept now.
- **The core casing no longer pokes out through the nozzle**: without an afterburner it ran past the nozzle
  start with a radius larger than the nozzle wall, so the two shells intersected. The casing now ends exactly
  where the nozzle begins and the nozzle root grows from that same radius. Turbojets no longer rely on the
  nacelle field to keep their mouth clear of the compressor face, and the casing band is visible outside the
  skin again.

- **Parts turned transparent after ALT-copy or symmetry**: the clone shared one mesh instance with the
  original, and a rebuild destroyed the other part's mesh. Meshes now retire late and only when nothing
  references them, and the stock model is hidden only after our own shape is confirmed present.
- **Two engines of the same type drifted apart**: the designer's "build mesh" bypassed the normal commit
  path. It now goes through `ApplyDesign` (propagating by design fingerprint), plus a "sync all same type"
  button for saves that have already diverged.
- **Flight thrust larger than the panel**: stock KSP remaps throttle with `Lerp(min,1,input)` and we
  inverted that mapping from the raw throttle, inventing up to 18% at low throttle. Both sides now use the
  same throttle definition; round-trip error 0.00%.
- **Stock intakes handing out too much flow**: capture area is now the smaller of "measured at the mouth"
  and "converted from the cfg area", without multiplying by scale² a second time.
- **Intake: vertical guides unclickable / dragged points jumping / side view upside down**: hit order now
  puts the guides first, visual and hit width share one constant; dragging is incremental and freezes the
  view; the side-view direction is corrected.
- **Intake sections twisting**: loops starting at different angles made the loft spiral; start points are
  now normalised as an invariant.
- **Stutter in flight**: removed per-frame waste on the flight path (whole-scene mesh sweep, intake
  fingerprint comparison, self-heal polling).
- **Intake angle of attack / sideslip always zero**: `PartModule.FixedUpdate` is never called by KSP (it
  has to be `OnFixedUpdate`).
- **Renaming a label silently changed behaviour**: the wizard used to dispatch input fields by their
  on-screen text (only "Thrust" routed to kN), so one wording change fell through to the else branch — the
  Mach box became a 0-25 range and typing 26 was clamped to 25 without a word. Fields now dispatch by
  their own range, independent of what the screen says.
- **The failing check sat on the sixth screen**: checks were listed in physical order (temperature →
  combustion → nozzle → shaft → choke → stage), so with one FAIL among seven WARNs you had to scroll to
  find it. They are now sorted by severity, stable within a severity.

### Known limitations

- Off-design fan speed follows "shaft speed / gear ratio", consistent with the design point (verified in
  section 107: change only the gear ratio and the fan tip speed is exactly 1/3 of direct drive both at the
  design point and at 70% speed). **Gearbox inertia is modelled** (thin ring, radius of gyration 0.3×fan
  radius, folded onto the low-pressure shaft; on GTF1100 110 kg → 4.2 kg·m², 0.5% of the LP shaft,
  verified in section 109: acceleration slows in proportion to J). **Gearbox compliance is a first-order
  model**: a thin-walled drum with its wall thickness solved back from the mass, `k = G·Jp/L`, static twist
  `τ/k`, body torsion `√(k/J_gb)/2π` (on GTF1100: 0.003° at 26 kN·m, body torsion 1864 Hz, magnitudes pinned
  in section 114). Not modelled: tooth-contact compliance, bearing clearance, multi-stage planet load
  sharing, gear-mesh excitation — enough to say "this box is not soft on paper", not enough to design a
  driveline against.
- Cone shock is approximated by an equal-deflection oblique shock plus 1.25× loss (no closed form);
  published charts show 20–30% higher loss at the same deflection.
- The compressor map is **synthetic**: a design point plus three constants (surge flow coefficient 0.90,
  choke 1.18, surge pressure ratio 1.20), not real engine data. Surge margin is therefore only good for
  comparing designs ("which one is tighter"), never as an absolute figure — the ledger grades it D.
- The stock intake `area × 64` conversion has only ever been matched against one sample (CircularIntake);
  other parts fall back to measuring the shape.
- Length estimate is within ±8–10% of real engines; dry mass runs heavy for composite fans (the GTF kind).
- GTF1100 computes a bypass ratio of 6.8 against 12.5 on the real engine: this model cannot squeeze the
  core flow area that small.

## Archive

- The 0.x phase (before 2026-11) was internal development with no public release; day-by-day changes are
  in the repository history and in `docs/phase2-mod.md` §1–§133.