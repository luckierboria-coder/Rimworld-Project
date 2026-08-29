# RimMT V0.4.19-JR1.1 Learned BFS

JR1.1 is a targeted correction to JR1 Aggressive after runtime feedback reported worse stutter.

- Cold Regionwise keys no longer perform an extra full BFS before scanning candidates.
- The first call executes Vanilla once while RimMT records the real Region order in-flight.
- After Vanilla's processor reaches its own stop condition, the capture continues only to learn Region order; the original candidate/validator processor is no longer invoked.
- Hot traversal keys reuse the learned Region order and keep candidates/validators live.
- The global Region.Allows Harmony detour from JR1 is retired; destination permission and forbidden-region reuse live only inside the hot Regionwise path.
- ReachProfile rolling safety no longer patches AggressiveReachabilityProfiles.Prefix/Postfix. It brackets outer Reachability.CanReach calls, observes existing counters, uses a temporary feature gate during soft cooldown, and uses native shadow samples for probation.
