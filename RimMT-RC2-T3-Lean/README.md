# RimMT RC2-T3 Lean

Lean convergence layer for the currently tested RimMT V0.9.1 / RC2-T2 stack on RimWorld 1.5.4063.

## Why this exists

The RC2-T2 diagnostic build successfully identified the current JobGiver/DoBill tail structure, but it also keeps several telemetry and shadow-validation layers resident during normal play. The latest test showed JobGiver average time improving while whole-tick cost remained high, so T3 stops adding profilers and removes measurement paths that are no longer needed for production play.

## Default Lean policy

- Keep RimMT production authority intact: S5.1, S5.3 compatible pruners, Stage 3 early-tail rescue, RC2-T2 baseline-first DoBill optimizer, ReachProfile and scheduler/dispatcher remain untouched.
- Suppress `parallel.workPrefilter`: the diagnostic build performed very large cell capture/worker-batch volume for very few final fast-negative hits.
- Suppress `parallel.pathSnapshot`: path shadow validation is telemetry-only and Vanilla remains authoritative.
- Suppress detailed self-test/path/job diagnostics.
- Remove Harmony patches belonging to RC2-T2 `GapClassifier`, `PreTailStructureProfiler`, and `Stage4CDoBillOutcomeProfiler` by exact patch method identity.
- Remove old `RimMT.HotPathPatches` PathFinder/JobGiver diagnostic wrappers while retaining TickPrefix/TickPostfix because the base runtime also feeds AdaptiveLoadBalancer from the tick bracket.
- Stage4D: only remove a patch when its method/type name unambiguously identifies cleaning, path-ordering, opportunity, spoilage/ingredient-sort work. Ingredient-expansion is explicitly retained. Ambiguous Stage4D patch methods are left installed (fail-closed).

## Safety model

T3 never unpatches by Harmony owner because current production and diagnostic modules may share the same owner. Every removal is keyed to the patch method declaring type/name. Unknown or ambiguous patches stay installed.

Disabling/removing this companion mod restores the original RC2-T2 patch set on the next game restart; T3 does not write save data.
