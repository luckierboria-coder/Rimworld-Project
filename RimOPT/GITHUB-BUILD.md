# RimOPT 1.7 Build Candidate (GitHub Actions)

The 0.2.0-alpha 1.7 assembly was compiled on GitHub Actions [run 39727561159](https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39727561159) against public licensed RimRef 1.7.4982-unstable and Harmony 2.4.2, .NET 10, **0 compiler warnings and 0 errors**.

- `1.7/Assemblies/RimOPT.dll`: sha256 `29f307fc0ec2b43f8994e3326c30861ca2acd93dac8d509f6579474a22f9c236`, 43008 bytes.
- `1.6/Assemblies/RimOPT.dll`: unchanged from user-supplied 0.1.4, sha256 `7a40e0d7f888023456d240d84721fe33512709f494bb3440fd88751a6aa30eee`, 41472 bytes.

These DLLs are **RimOPT mod binaries only**, not the proprietary RimWorld game/Waylith/UnityStubs assemblies. No game binaries have been uploaded to this public repository.

**Not yet tested in-game**. The 1.7 opt-in switches default OFF and use different persistence keys; do not enable unverified modules in an important save. Record `Player.log` and performance baselines before any release label beyond alpha.
