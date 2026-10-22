# RimOPT 1.7 0.4.1-alpha source-level regression guard

- GitHub Actions: https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39741691082
- Build: .NET 10 + Harmony 3.0.0-rc.3 + public RimRef 1.7.4982-unstable.
- **0 warnings; 0 errors.**
- Binary: `RimOPT/1.7/Assemblies/RimOPT.dll` size 47104 bytes, SHA-256 `258e6ad0fb6dbee7b1fd7683a786ae0820713298342ad5c1d012a02f50be6ce8`.
- The 1.6 DLL is untouched.
- 0.4.1 removes the DoBill global candidate override in 1.7 and reverts native search mode; HaulMerge uses second-visit warmup and bounded indexing; the unproven GenClosest replacement is disabled.
- **Compiled and packaged but not in-game validated.** Samples from 0.3.1 and 0.4 differed in workload; a faster p99/TPS is not yet established. Avoid claims of measured gain.
