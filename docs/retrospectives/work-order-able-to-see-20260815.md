---
glasscoder: retrospective-actions
version: 1
file: able-to-see
target: harness
runId: dd11ef7c66e04ccf8c9a13115247b0de
reviewedAt: 2026-08-15T16:30:00Z
via: grok
model: grok-4.6
costUsd: 0.0000
---

# Work order — make launch_app able to see

This is the only remaining slice of "GlassCoder cannot see." The goal is **report what the launched window is showing**. Evaluation stays with the critic panel. No new tool. No screenshots. No extra sentry counters.

## Already in the tree — do not re-implement

These landed after run `dd11ef7c`. Confirm they still compile and the named tests pass. If they fail, fix the regression; do not redesign.

- `IUiProbe.ReadAllAsync` — `src/GlassCoder.Tools/Processes/IUiProbe.cs`
- `UiAutomationProbe.ReadAll` — `src/GlassCoder.Tools.Windows/UiAutomationProbe.cs`
  (`FindAll` over `ControlType.Edit` and `ControlType.Text`, cap `MaxReadBack`, label unnamed boxes from the preceding static text, never throw)
- `LaunchAppTool` default sweep — `src/GlassCoder.Tools/Build/LaunchAppTool.cs:159-197`
  (`asked` vs sweep; `OnReady` calls `ReadAllAsync` when `probe` is empty; `Window:` vs `Probe:` in `ProbeReport`; hedge narrows to "nothing was typed into it" on a sweep that saw values)
- Tests already written in `tests/GlassCoder.Tools.Tests/LaunchAppTests.cs`:
  - `A_launch_that_asked_for_nothing_reads_the_window_anyway`
  - `A_window_read_at_rest_does_not_claim_the_window_is_right`
  - `A_host_with_no_probe_still_launches_and_still_says_it_read_nothing`

`dotnet test tests/GlassCoder.Tools.Tests --filter FullyQualifiedName~LaunchAppTests` must stay green before you touch anything else.

## What is still missing

The tool can now see. The critics have not been told. `CriticPanel.cs:403-407` still says the worker "cannot see what is on the screen or interact with it" and that absence of visual proof is never grounds to refute. That sentence licensed run `dd11ef7c`'s 3/3 accept over a window showing `0` next to `0`.

A launch that reported `Window: the box after "Celsius:"? → "0"; the box after "Fahrenheit:"? → "0"` is now evidence. The panel must judge those values. It must not demand pixels. It must not demand a typed `probe:` as a condition of acceptance — that is unanswerable if the model does not elect it, and that is how the 16:02 run spent seven steps extracting a service nobody asked for.

## Actions

<!-- Ticked items are the ones to act on. -->

- [x] **High** `critic-prompt-reads-the-window` - The critic prompt catches up with the default readback
      Where: `CriticPanel` system prompt, `src/GlassCoder.Core/Verification/CriticPanel.cs:398-410`. Replace the clause that says the worker can only observe that a window drew and cannot see what is on the screen. The new clause, in the same idiom ("refute over evidence the worker could have produced and did not"): the worker can launch the application and read the text the window is showing (labels and box values); it cannot see pixels, layout, or clipping, so absence of a screenshot is never grounds to refute; absence of a launch, for a goal about a running application, still is; when the evidence contains a `Window:` or `Probe:` line, judge those values against the goal (`0` next to `0` on a converter is a refute); a launch that only watched, on a host that can read, is evidence the worker could have produced and did not; absence of a typed `probe:` (Box=100; Other?) is not, by itself, grounds to refute. Do this in the same change as the scan below. Do not add a new tool, a new parameter, or a new notice. Verification: `The_critics_are_told_what_evidence_the_worker_can_produce` in `CriticPanelTests.cs` is updated so it no longer requires "drew a window" as the ceiling, asserts the prompt names that the worker can read the text the window is showing, asserts it still forbids treating missing pixels as a refute, and asserts it does not contain "cannot see what is on the screen". A new case asserting a `Window:` evidence line is passed through unaltered (same shape as the existing launch-observation pass-through).

- [x] **High** `no-model-facing-string-denies-window-read` - The stale-capability scan forbids the old sentence
      Where: `ModelFacingPromptTests` (`tests/GlassCoder.Core.Tests/ModelFacingPromptTests.cs`). Add `"cannot see what is on the screen"` to `Denials` (or a sibling array — keep `never by you` / `only the operator` as they are). The scan already covers `GlassCoder.Core` and `GlassCoder.Tools`. Comments cannot quote the banned phrase; describe it. Verification: the existing scan test fails if `CriticPanel.cs` still carries the old clause, and stays green after `critic-prompt-reads-the-window`. Do not scan `GlassCoder.Wpf` or tests.

- [x] **Medium** `history-able-to-see` - Record the decision, not the diff
      Where: `HISTORY.md`, newest first. One dated entry: the default sweep is the mechanism, the prompt change rode with it, a sweep reports facts and does not claim the window is right, typing stays elective, pixels stay declined. Open thread: the next live WPF run on `GlassCoderTest` is the test of whether `Window: … → "0"` reaches the panel and whether the panel refutes `0` beside `0`. Do not restate the file list.

- [ ] **Low** `launch-app-resolves-a-directory` - launch_app accepts a directory that holds exactly one project
      Already declined twice as not the quality problem. Left here only so a later agent does not rediscover it. `LaunchAppTool.cs:109-116`. Occurrence two on `ae72c5ad` and `dd11ef7c`. Not this work order.

- [ ] **Optional** `critic-votes-cite-a-line` - Each critic vote cites a diff line
      Prompt pressure, not sight. Caps will truncate the interesting half. Not this work order.

## Out of scope — do not implement

These are how this repository becomes spaghetti. An agent that lands any of them has left the work order.

- A new tool. `launch_app` is the tool. `IUiProbe` is the eyes. Do not add `inspect_window`, `screenshot`, or a vision model.
- Default typing. Inventing `100` is a side effect. `ReadAllAsync` is read-only on purpose.
- Screenshots, pixels, clipping oracles, UI Automation beyond Edit/Text.
- `strings-name-the-probe`, `nudge-on-verification-with-nothing-applied`, `plan-after-orientation`, `abandoned-intent-ledger`, `retrospective-transcript-keeps-the-hint`, rating-strip caveat, more `XamlNotices` / `TestSuiteNotices` clauses.
- Any new counter or nudge on `RunProgressSentry`.
- Raising `MaxCritiquePanels` above 2.
- Changing `AgentLoop`'s refutation-recovery sentence that names `probe:` for an already-launched run. That sentence is still the right way to prove an *update*. The sweep proves *resting state*.

## How to use this

Implement the ticked items, in this repository, in priority order. Item 1 and item 2 are one change: do not land the prompt without the scan, and do not land the scan while the old sentence is still in `CriticPanel.cs`.

Before editing, run the LaunchApp tests named above. After editing, run:

```
dotnet test tests/GlassCoder.Tools.Tests --filter FullyQualifiedName~LaunchAppTests
dotnet test tests/GlassCoder.Core.Tests --filter FullyQualifiedName~CriticPanelTests
dotnet test tests/GlassCoder.Core.Tests --filter FullyQualifiedName~ModelFacingPromptTests
```

Tick nothing in this file. Add what you implement to `HISTORY.md`. This file is the record of what was asked for, not of what was done.
