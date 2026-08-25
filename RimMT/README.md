# RimMT

Compatibility-first multithreading framework for RimWorld 1.5.

## V0.2 Foundation

This first development milestone intentionally does **not** parallelize `Pawn.Tick`, `Thing.Tick`, job assignment, reservations, or mutable RimWorld map state.

Implemented infrastructure:

- bounded persistent worker pool (1-8 workers, leaves one logical CPU for the main thread)
- high / normal / background queues
- bounded worker-to-main-thread dispatcher
- explicit main-thread / worker-thread guards
- per-feature enable/suppress gates
- Harmony conflict inspection for future parallel modules
- fail-closed behavior and vanilla fallback contract
- per-feature circuit breaker after repeated worker exceptions
- startup compatibility report

## Design rules

1. Correctness and save safety outrank TPS.
2. Parallel modules are whitelist-only.
3. Workers should consume immutable snapshots or pure data, not live `Pawn`, `Thing`, `Map`, reservation, region, or Unity state.
4. Before a module patches a vanilla hot path, inspect foreign Harmony prefixes/transpilers/finalizers. Unknown mutation means the module is disabled.
5. Worker failures disable only the affected feature for the current session.
6. RimMT must not require persistent save data; removing the mod should leave saves usable.

## Build

Set `RimWorldDir` to the RimWorld installation root and build `Source/RimMT/RimMT.csproj` for .NET Framework 4.7.2. Harmony is expected at `Mods/Harmony/Current/Assemblies/0Harmony.dll`.

## Next milestone

V0.3 will add benchmarks and the first low-risk, measurable parallel workload(s), selected only after RimWorld 1.5 call-path review and compatibility checks. Candidates include read-only world calculations and UI/alert scanning; `Pawn.Tick` remains out of scope until there is evidence a safe staged implementation is worthwhile.
