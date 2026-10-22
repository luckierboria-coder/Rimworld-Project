# RimOPT 1.7 v0.3.1-alpha source-targeted optimization build

- GitHub Actions: [run 39733141464](https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39733141464). .NET 10 + Harmony 3.0.0-rc.3 + RimRef 1.7.4982-unstable: **0 warnings, 0 errors**.
- `1.7/Assemblies/RimOPT.dll`: 47,616 bytes; SHA-256 `3a70e0dd41362b8c77036d76267cbd8e7a9d00f261e6d9f0e9a6a9c3a0adf18a`.
- The original 1.6 `RimOPT.dll` remains intact at `1.6/Assemblies/RimOPT.dll`.
- Compiled binary is a user-owned RimOPT mod, not a proprietary RimWorld engine/game assembly.
- **No in-game benchmark has yet been run; no specific TPS or stutter reduction is claimed.**
- Seven optimization switches remain opt-in. For a no-profiling functionality check, use a test save, enable **DoBillIndex** and **HaulMergeIndex**, restart, and observe regular work/haul behavior. Leave **WorkSearchTailPlans** and **ReachabilityMemo** off until 1.7-specific semantic compatibility is established.
