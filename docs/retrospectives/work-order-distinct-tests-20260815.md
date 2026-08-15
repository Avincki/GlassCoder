---
glasscoder: retrospective-actions
version: 1
file: distinct-tests
target: harness
runId: e426f418bc2f490eaa5232c11cc024ef
reviewedAt: 2026-08-15T19:30:00Z
via: grok
model: grok-4.6
costUsd: 0.0000
---

# Work order — two test oracles, one job each

Run `e426f418` put the plan in the transcript. The plan's last item was **Build and run tests**. The verification ladder already builds and runs tests after every applied change from step 2. The agent treated the first real UnitTests green (step 13) as that last item, ticked 5/5 (step 14), and declared the goal met (step 15). Critics then asked for a launch the plan had never contained.

There are two distinct "tests." They must stop pretending to be the same thing.

| Oracle | Job | When |
|---|---|---|
| **Ladder** | Does *this change* compile, and do *the tests that exist* pass? | After every applied write, from step 2 on |
| **Plan** | Work the ladder cannot see — a launch, a probe, a named behaviour | Phase boundaries only |

**Do not** move the UnitTests rung to the last plan item. That would hide CS1061 until the end and would delete the step-11 composition notice (it only fires because a write climbed). Early climbs that say `verified nothing (0 tests)` are honest preamble. The defect is using them, and then the first non-empty green, as a substitute for "the button works."

## Already true — do not undo

- `AgentOptions.SystemPrompt` already says: applied changes are verified for you; call `build` / `run_tests` only when nothing verified the last change, or the climb verified nothing. Run `e426f418` obeyed that *and* still authored "Build and run tests" as the closer.
- HISTORY records that `Agent.SystemPrompt` is often **shadowed** by `%APPDATA%\GlassCoder\settings.json`. A code-only prompt edit is dead text on this machine.

## Actions

<!-- Ticked items are the ones to act on. -->

- [x] **High** `plan-does-not-repeat-the-ladder` - `update_todos` stops teaching a final build/test step
      Where: `TodoTool` method and `items` parameter descriptions, `src/GlassCoder.Tools/Planning/TodoTool.cs:40-45`. Pay for the new words out of the existing description (standing rule in `PromptBudgetTests`). Current text: "Break a multi-step task down before starting, keep exactly one item InProgress, and mark items Completed as you finish them." Replace with the same discipline plus: do not add a plan item whose only job is build, run_tests, or "verify everything" — applied changes are already verified on the observation; a plan item must name work the automatic verification cannot see (a launch, a probe, a behaviour in the running window). Keep "complete list every time" and "exactly one InProgress." Verification: `PromptBudgetTests` profiles do not move; a `PlanningAndChangeTests` (or sibling) case is not required for the schema string, but grep the next desktop transcript — step 0's plan has no item whose title is only build/run_tests/verify. If `%APPDATA%\GlassCoder\settings.json` still carries an old `Agent:SystemPrompt` that orders a final build, the code change is not enough: the work order's closing tells the operator to clear that shadow.

- [x] **High** `plan-complete-is-not-goal-complete` - Ticking the last item does not read as "the goal is met"
      Where: `TodoTool.UpdateTodos`, the `Observation.Ok` summary at `:92`. Today it is only `Plan updated: {completed}/{total} complete.` On run `e426f418` that line at step 14 (`5/5`) was the last thing the agent read before the completion claim at step 15. When `completed == total` and `total > 0`, append one sentence: the plan is complete; that is not evidence the goal is met — cite the last automatic verification, and only finish when you have evidence that climb could not see. When the last climb is available to the tool (it is not today — `TodoTool` has no `IVerification` dependency), do not add a new service just to quote it; the sentence without a citation is enough. Do not refuse the call, do not reopen items, do not gate completion. Verification: a `PlanningAndChangeTests` case where all items are `Completed` produces a summary that contains "not" and "goal" (or equivalent) and still `ok: true`; a partial plan (`1/5`) stays the short line. `PromptBudgetTests` unchanged.

- [x] **Medium** `echo-when-the-plan-duplicates-the-ladder` - A plan item that only restates build/tests is named in the observation
      Where: the same `UpdateTodos` method, after the list is accepted. If any item title matches a small, case-insensitive set (`build`, `run tests`, `run_tests`, `verify everything`, `verify all`, `final build`, `final test`), append one line naming those titles and repeating that the ladder already does that work. Once per call, not per item. This is the cheap half of item 1 for a model that ignores the schema and still writes the closer — run `e426f418` step 0. Verification: a plan containing "Build and run tests" plus three real items returns `ok` and a summary (or trailing sentence) that names the duplicate item; a plan with no such title is silent. Do not refuse the plan: refusing would spend a step on a rewrite, which is the waste this is preventing.

- [x] **Medium** `skip-unittests-process-when-no-test-exists` - Early climbs keep the wording, drop the process
      Where: `VerificationLadder` UnitTests rung. Steps 3–12 of `e426f418` (and six scaffold climbs on `457867c7`) spawned `dotnet test` to discover there were no tests. The wording `verified nothing (0 tests)` stays; `Unverified` stays set. Ask first whether any source in the climb's target declares a test (`TestSuiteNotices` / `FindSymbolTool.Declares` already answers this off the warm syntax-tree cache). If none does, return `Unverified` with the same sentence and do not invoke the runner. Proposed Aug 9 and Aug 15 as cost-only; it is now the cost of keeping verification from step 2, which this work order preserves. Verification: `VerificationLadderTests` — a workspace with no test method issues no test command and the rung is `Unverified`, not skipped and not passed; adding a test method makes the next climb execute. Do not skip Compile or Analyzers.

- [ ] **Low** `system-prompt-repeats-the-plan-rule` - The code default says the same thing as the tool
      `AgentOptions.SystemPrompt` (`:84-95`) already forbids repeating the ladder with a `build`/`run_tests` *call*. Add one clause: do not put that call on the todo list either. Dead on this machine unless the operator's `%APPDATA%\GlassCoder\settings.json` `Agent:SystemPrompt` is cleared or edited to match. Do this only after items 1–2, and only if you are also changing the operator copy or documenting that the code default is inert here. Verification: the string is in the code default; a note in `HISTORY.md` that the shadow must be cleared.

- [ ] **Optional** `closing-the-plan-does-not-start-critique` - A no-tool-call immediately after 5/5 is not a completion claim
      Tempting, and a gate. This repository has paid twice for gates that would not concede. The first no-tool-call after a 5/5 is exactly when the agent thinks it is done; inserting a challenge would spend the `ChallengeNotice` / critique budget on "you still have a plan-shaped definition of done." Decline. Item 2 is the notice on that path; the critics remain the judge of the goal.

## Out of scope — do not implement here

- Moving or delaying the verification ladder. Early climbs stay.
- `xaml-notice-outlives-its-step`, `launch-recovery-overstates-a-bare-launch`, auto-`launch_app`, probe drive. Those are sight. This file is only the two test oracles.
- A fourth `TestSuiteNotices` clause (tautological initial-state assertion). Watch-on-one (task 70).
- New sentry counters or nudges.
- Refusing `update_todos` because a title is wrong.

## How to use this

Implement the ticked items, in this repository, in priority order. Items 1 and 2 are one change to `TodoTool`; item 3 rides the same method. Item 4 is independent and lives in `VerificationLadder`.

After editing, run:

```
dotnet test tests/GlassCoder.Tools.Tests --filter FullyQualifiedName~PlanningAndChangeTests
dotnet test tests/GlassCoder.Core.Tests --filter FullyQualifiedName~PromptBudgetTests
dotnet test tests/GlassCoder.Core.Tests --filter FullyQualifiedName~VerificationLadderTests
```

On this machine, open `%APPDATA%\GlassCoder\settings.json` and check `Agent:SystemPrompt`. If it still tells the model to build and run tests as a last step, that copy wins over the code default. Either delete the key so the code default applies, or add the same "do not plan a final build/test" clause there.

Tick nothing in this file. Add what you implement to `HISTORY.md`. This file is the record of what was asked for, not of what was done.
