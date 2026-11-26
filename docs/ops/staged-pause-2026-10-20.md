# Staging pause fix — v1.10.48

The reported remote pane showed a yellow idle light, four queued entries and
“Restored · Check delivery”. Local diagnostics at 2026-10-19 23:48:38–39 show
that a staged paste completed, then its delayed Enter was interrupted. The old
diagnostics do not identify which input caused that cancellation.

Offline reproduction found two paths that could strand the queue:

- xterm mouse motion and release reports were classified as interactive edits,
  canceling a staged Enter. Motion, release and wheel reports now preserve the
  pending submit. Button presses, physical keys, edits and paste still take over.
- Host `not_ready` and `busy` rejections occur before any PTY write, but were
  treated as permanent failures. They now return the entry once, preserve order
  and wait for a fresh idle status with no automation pending.

Every pause previously used the recovery message. The panel now distinguishes
interruption, terminal errors, send failures and uncertain restored delivery,
and offers a visible **Resume sending** button. Pause reasons survive workspace
save and tab transfer. Terminal-error recovery also persists the cleared pause.
Cancellation diagnostics record only a fixed input-source category, never input
text. Failures after a possible write and unacknowledged restored sends still
require review; no automatic repeat is introduced for them.

## Verification

- Actual embedded HTML and bundled xterm, fake host: 28/28 checks pass. The
  pre-fix implementation failed three of the added queue regressions.
- Input protocol checks pass, including actual mouse/focus/query events captured
  from xterm. The pre-fix protocol failed on mouse motion.
- Workspace/tab-transfer suite: 12/12 checks pass, including pause reason storage.
- Pause UI visually checked at 1100px and 550px widths.
- Debug build and Release publish pass. Existing nullable warnings remain at
  `MainWindow.xaml.cs:1977` and `MainWindow.xaml.cs:2302`.
- `git diff --check` passes. No live CLI prompts, SSH actions or reset credits
  were used for verification.
