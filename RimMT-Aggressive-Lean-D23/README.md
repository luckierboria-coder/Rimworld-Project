# RimMT V0.9.3-T34D.2.3 Aggressive Lean V1

This is a source-clean rebuild of the original D.2.3 aggressive-scanner line.

Retained production paths:

- T34-D live validator and reachability worker execution, including the 8 ms scanner-type quarantine.
- T21 package-local validator and reachability transactions.
- T28 package boundary and T32 reservation replay.
- Persistent DoBill filtering, T4 haul merge-partner index, Common Sense ingredient expansion memo, and text metrics cache.
- Bounded scheduler, dispatcher, and adaptive pressure control required by T34-D.

Removed from the source tree and compiled DLL:

- T34-A, T34-B, and T34-C candidate fabrics.
- S4/S5.1 slow-search paths.
- T22 GenClosest transaction index and T26 epoch coordinator.
- Clean Pathfinding experiments, global haul experiments, and resident attribution/profiling probes.

The package keeps the original `allen.rimmt` package ID so it replaces another RimMT build rather than loading beside it.
