# MP PVE control and layout checklist

- [x] Allow the native `ambush_monsters` hero-shuffle modifier in the existing monster-advantage picker.
- [x] Add optional cooperative-PVE hero-identity control binding without changing hero-select/loadout ownership.
- [x] Capture Arena bindings by runtime actor GUID and draft slot so duplicate hero classes stay distinct.
- [x] Reject missing/unconfigured bindings and exclude expedition enemy-pilot PVP plus Arena hero-vs-hero.
- [x] Make the hero setup window fit 1600x900 with responsive scaling and a single scrollable detail body.
- [x] Push the current Arena/PVE work to `origin/main` before experimental runtime changes (`8ecf775`).
- [ ] Scope enemy ordainment to the Arena enemy side and clear the native `run_test_boss_modifier` preference on every launch exit/failure.
- [ ] Add an Arena-only RNG context: snapshot the current Run `RandomContainer`, seed combat/BOSS/AI/effect streams independently, and restore the exact Run state on every exit path.
- [ ] Replace direct `UnityEngine.Random` Arena selection with a private Arena PRNG so UI-side rolls cannot consume the Run random state.
- [ ] Add runtime digests/logs for ordainment ownership and RNG before/arena/restore phases.
- [ ] Add a focused contract check, build all MP projects, deploy with backup, and package the two-DLL release.
