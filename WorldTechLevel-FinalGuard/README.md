# World Tech Level - Final Guard V2

RimWorld 1.5 compatibility/performance guard for World Tech Level 1.1.6.

## Problem

World Tech Level's `TechLevelDatabase<T>.EnsureInitialized()` detects a DefDatabase count mismatch and calls the global `DefTechLevels.Initialize()`. If another mod adds/removes TraitDefs at runtime, each mismatch can rebuild every World Tech Level database (research, things, terrain, incidents, quests, traits, pawn kinds, factions, etc.) and emit a warning stack trace. Repeated mutations can therefore create severe main-thread stalls and log floods.

## V2 behavior

- Keeps the original package id: `allen.wtl.finalguard`.
- Patches only `TechLevelDatabase<TraitDef>.EnsureInitialized()`.
- On a TraitDef count mismatch, repairs only the TraitDef tech-level database:
  1. `DefDatabase<TraitDef>.SetIndices()`
  2. `TechLevelDatabase<TraitDef>.Initialize(null)`
  3. `TechLevelDatabase<TraitDef>.ApplyOverrides()`
- Preserves World Tech Level override semantics and alternative-table initialization.
- Suppresses the upstream per-mismatch warning/stack-trace flood for this path.
- Logs only repair counts 1, 2, 4, 8, 16, ... and includes up to eight newly observed TraitDef names/package IDs to identify the injecting mod.
- If World Tech Level internals do not match the supported layout, or local repair fails, it fails open to the original World Tech Level behavior.
- No save data is added. Existing saves do not need conversion.

## Installation

Delete the old `World Tech Level - Final Guard` folder and install this replacement. Keep Harmony and World Tech Level enabled. Load order should be Harmony -> World Tech Level -> World Tech Level - Final Guard.
