# RimMT complete public version archive

This branch preserves the complete local RimMT development history that was not already represented by the repository's historical branches.

- `source-snapshots/`: original local source trees, build scripts, and source templates.
- `recovered-and-verification-source/`: source recovered from final DLLs plus IL verification snapshots when the original checkout no longer existed.
- `release-packages-complete/`: 241 unique RimMT-family ZIP builds. Hash suffixes preserve different binaries that originally shared a filename; see `PACKAGE_INDEX.tsv`.
- `installed-rimmt-binaries/`: RimMT-only rollback snapshots. Personal configuration, deployment logs, and unrelated third-party binaries are excluded.
- `BRANCH_INDEX.tsv`: historical local and remote RimMT branch references.
- `SHA256SUMS.txt`: integrity hashes for every public archive file.

Earlier V0.2, V0.4, V0.9.0-V0.9.3 and T0-T34B development remains available in the repository's historical branches. The T34-C10 and T34-C11 continuation is pushed as dedicated branches alongside this archive.
