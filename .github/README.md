# BasisAR

Fork of [BasisVR/Basis](https://github.com/BasisVR/Basis) targeting headset MR (passthrough) alongside stock VR use.

## Branch strategy

- Work happens on `basisar/lts-YYYYMMDD`, based on the matching upstream
  `long-term-support-YYYYMMDD` snapshot. Current base: `long-term-support-20260623`.
- Upstream `developer` is not used directly; critical fixes are cherry-picked
  onto the current branch and dropped at the next LTS hop.
- Moving to a new LTS:

  ```bash
  git fetch upstream
  git checkout -b basisar/lts-NEW basisar/lts-OLD
  git rebase --onto upstream/long-term-support-NEW upstream/long-term-support-OLD basisar/lts-NEW
  ```

  Keep the old branch until the new one is verified.

## Fork discipline

- BasisAR code lives in its own folders/asmdefs; upstream files are edited only
  when unavoidable, one focused commit each, message prefixed `fork:`.
- Upstream scenes and prefabs are never modified — use variants or copies.
- This file lives in `.github/` (not repo root) so upstream's `README.md` is
  never touched; GitHub displays this one on the fork's page.

Upstream docs: [README](../README.md) · [basisvr.org](https://basisvr.org/)
