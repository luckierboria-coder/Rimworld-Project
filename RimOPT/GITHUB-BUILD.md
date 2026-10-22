# RimOPT 1.7 v0.4.0-alpha — Waylith source-targeted build

The 1.7 DLL under `1.7/Assemblies/RimOPT.dll` is built from **this source branch**, not the previous 0.3 release.

- GitHub Actions successful: https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39738088246
- .NET 10 / Harmony 3.0.0-rc.3 / public RimRef 1.7.4982-unstable
- Build: **0 Warning(s), 0 Error(s)**
- 1.7 DLL: **48,640 bytes**, SHA-256 `44d63b588415cc3694c2e0ae15d05feea5099fa7bbadde4adc1e8bb5deb9fba5`
- 1.6 DLL untouched: **41,472 bytes**, SHA-256 `7a40e0d7f888023456d240d84721fe33512709f494bb3440fd88751a6aa30eee`
- No user-supplied proprietary engine assemblies uploaded into this public repo.
- **Important**: CI compilation verifies API/syntax correctness against RimRef, but cannot prove in-game job result equivalence, crash safety, or actual p99/TPS improvements.
