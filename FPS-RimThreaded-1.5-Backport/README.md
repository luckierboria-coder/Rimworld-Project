# FPS+ | RimThreaded — RimWorld 1.5.4063 backport build

Build harness for Allen's RimWorld 1.5.4063 backport.

Upstream source is pinned to:
- Repository: LuniX000000000001/FPS-RimThreaded---Continued-
- Commit: 79681946aab1cca55a9cce0e388a7caabce6a6b0
- Source directory: Source_1.5

The CI compiles against Krafs.Rimworld.Ref 1.5.4063 and Lib.Harmony 2.3.3.
Backport-specific source overrides live in `SourceOverrides` and are copied over the upstream source before compilation.
