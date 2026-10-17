# Lower-left multi-account quota views — 2026-10-02

Local version: 1.10.35.

The lower-left toolbar now has a view menu:

- **Show all / 全部展示** (initial default): default Codex, each additional Codex account, then Claude. With the existing Brain profile this produces three indicators. The default indicator always uses the default account snapshot, even while Brain details are open.
- **Compact view / 紧凑视图**: one selected Codex account and Claude, preserving the previous layout and account selector.

Clicking an indicator opens the matching account's details. Codex details identify the default account or an added account and show its referenced login directory. Added accounts remain read only; their details have no reset-credit action. Changing view or selecting an account never switches a terminal's account.

The view preference is saved atomically in `quota-toolbar-view.txt` under the CCPad data directory. Account names and paths continue to use `quota-accounts.json`; neither file contains login tokens. Existing quota services refresh on launch and every five minutes. There are no additional requests from a view toggle. Footer actions move to a second row when their combined width exceeds available space; many quota indicators can scroll horizontally.

## Verification

- Release publish completed: `D:\CC Pad\app\v1.10.35_20261002-050044`.
- Fifteen offline checks passed, including preference persistence, unchanged account configuration and isolated account results.
- Interactive preview confirmed three indicators, compact view retaining Brain selection, and correct default / Brain / Claude details. Brain had no reset button. Claude had no readable subscription quota and displayed `--` with the existing explanatory status.
- Both Codex login files retained the same SHA256. No real reset request or terminal session was created.
- The original main and overview windows remained open. `current` still targets v1.10.33, and activation is pending until all CCPad processes exit naturally.
- Narrow-window layout was implemented; the attempted native resize did not change the preview size, so that visual case was not verified.

The preview uses `--quota-toolbar-preview`, isolated `CCPAD_DATA_DIR`, and `CCPAD_CODEX_RESET_DRY_RUN=1`. Its launcher and evidence are under `tools/QuotaToolbarPreviewLauncher` and `D:\CC Pad\research\quota-toolbar-20261002`. The temporary preview was closed after verification.

Later attempts to package a cosmetic Claude-title change ran into machine-wide commit-memory exhaustion. That change was reverted, and the successfully published, UI-verified build above remains the queued version. Existing Bot, proxy and CCPad user processes were not stopped. Failed build directories were retained.
