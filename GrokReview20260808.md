# GrokReview 2026-08-08

**Audience:** code agent implementing harness fixes  
**Scope:** GlassCoder tool set effectiveness, grounded in the last live run and project history  
**Status:** recommendations only — no code was changed in the review session

---

## 1. Sources used

| Source | Role |
|--------|------|
| `C:\Users\AlexVinckier\AppData\Local\GlassCoder\logs\glasscoder-20260808.log` | Human-readable last run |
| Same dir `glasscoder-20260808.jsonl` | Step records, tool args/results, critique text |
| `HISTORY.md` | How this repo solves issues (transcript → harness defect → mechanism) |
| Tool registration + implementations under `src/GlassCoder.Tools/` | Current surface and contracts |
| `WORKPLAN.md` tasks 46–53 | Prior ship/defer decisions |
| `docs/grok/tool-evaluation-ai-codegen.md` | **Stale** for P0 list — many items already shipped; do not re-implement |

**Primary evidence run:** `ca727be365c947beb7e0961b7b35785f`  
- Task: `desktop` · Workspace: `GlassCoderTest` · Role: `worker` · 18 tools  
- Outcome: **Completed**, 21 steps, 161 853 tokens, ~181 s, tool-call validity **1.00**  
- Metrics: 5 edits, compile-error rate 0.2, recoveries 1/1, builds 7, test runs 7  
- Completion critique: **2/3 refuted** once · Post-run review: **accepted**

---

## 2. Project method (from HISTORY.md) — follow this when implementing

1. Name the **run and step** that motivates the change.  
2. Prefer fixing the **harness message or mechanism** over teaching the model via schema essays.  
3. A suggestion the model ignores twice is **not a mechanism** — auto-fix or refuse early.  
4. Honour **schema budget** (`PromptBudgetTests`): add verbs/flags on existing tools before new top-level names.  
5. **False negatives worse than no tool** (e.g. `find_references` without a real workspace — already deferred).  
6. Record decisions in `HISTORY.md` when shipping; leave open threads explicit.  
7. Validation: unit tests + ideally re-run the same desktop goal on a cleaned `GlassCoderTest`.

---

## 3. Current tool surface (logical layers)

Registration: `src/GlassCoder.Tools/DependencyInjection/ToolsServiceCollectionExtensions.cs`.

| Layer | Tools | Notes |
|-------|--------|--------|
| Plan / state | `update_todos`, `list_changes` | Whole-list replace; change log awareness |
| Discover | `read_file` (+`outline`), `grep`, `glob`, `find_symbol`, `list_projects` | |
| Write | `create_file`, `edit_file` (flat + `edits` + `replaceAll`), `file_operation` (delete/move/revert) | Path guard, change log, approval, pre-write where possible |
| SDK | `dotnet_project` (new, new_solution, add_to_solution, add_reference, add_package, restore, format) | Preferred over hand-editing csproj |
| Oracles | `build`, `run_tests` (+`listOnly`) | Ladder also drives these |
| Opt-in | `bash`, git suite (`git_status`…`create_pull_request`) | Last / gated |

**Already shipped (do not re-propose as new tools):** multi-file `edit_file`, `find_symbol`, `file_operation`, `list_changes`, `read_file(outline)`, `run_tests(listOnly)`, `dotnet_project` format, WPF/winforms templates, stub deletion, XAML-generated partial pre-write awareness, hazardous nest scaffold refusal, `add_to_solution` argument swap repair.

**UI for runtime proof (not an agent tool):** WPF “Run app” button — host `dotnet run` (HISTORY 2026-08-06 night). Critics asking for live UI should not force a new bash-like tool without revisiting that decision.

---

## 4. Last run reconstruction (`ca727be3`)

| Step | Tool | Result |
|-----:|------|--------|
| 0 | `update_todos` | Plan 0/5 |
| 1 | `dotnet_project` NewSolution | `src/MultiplyApp/solution.slnx` created |
| 2 | `dotnet_project` New wpf | App scaffolded |
| 3 | `dotnet_project` New xunit | Tests scaffolded; UnitTest1 removed |
| 4 | `dotnet_project` AddReference | **SDK exit 1** — TFM net10.0 vs net10.0-windows |
| 5 | `read_file` | test csproj |
| 6 | `edit_file` | Hand-set TFM to net10.0-windows |
| 7 | `dotnet_project` AddReference | OK |
| 8 | `update_todos` | 1/5 |
| 9–10 | `read_file` | MainWindow.xaml / .cs |
| 11 | `create_file` | ViewModel.cs |
| 12 | `edit_file` | XAML UI — **ladder Compile FAILED** (`btnMultiply_Click` missing) |
| 13 | `edit_file` | Code-behind — ladder green |
| 14 | `update_todos` | 3/5 |
| 15 | `create_file` | ViewModelTests.cs (5 tests) |
| 16 | `update_todos` | 5/5 |
| 17 | (no tool) | Claim done → **completion critique REFUTED** (UI/runtime evidence) |
| 18 | `read_file` | Re-read tests only |
| 19 | `run_tests` | All 5 passed |
| 20 | (no tool) | Claim done → **Completed** |

**Not used this run:** `list_projects`, `grep`, `glob`, `find_symbol`, `list_changes`, `file_operation`, explicit `build`, git, bash.

**Workspace leftover (post-run):** solution file exists; projects were **not** added to the solution (no `add_to_solution` calls).

### What already worked

- WPF via SDK template (prior HISTORY fix verified live).  
- Stub deletion → real tests only.  
- Ladder caught XAML/code-behind mismatch; one-step recovery.  
- 100% tool-call validity; schema binding was fine.  
- Completion critique correctly challenged “green tests = goal met” for a UI app.

---

## 5. Findings (ranked)

### F1 — TFM mismatch on WPF → xunit `add_reference` (P0)

**Evidence:** Step 4 output: incompatible frameworks; targets listed `- net10.0`. Agent hand-edited `.csproj` (steps 5–6) against tool guidance “never hand-edit a .csproj”.

**Impact:** ~3 wasted steps; teaches wrong recovery path.

### F2 — Failed SDK ops are soft-success (P0)

**Evidence:** Step logged `dotnet_project:Succeeded` while summary is `dotnet add_reference failed with exit 1` and `data.succeeded: false`.  
**Code:** `DotnetProjectTool.cs` returns `Observation.Ok` when `!payload.Succeeded` (intentional “information not fault”, same as failed build).

**Open class (HISTORY 2026-08-06 coda, run `4b562c91`):** failure-as-information tools are **invisible** to `RunProgressSentry` failure counters; repeated near-identical fails do not trip loop-breakers.

### F3 — Solution ceremony incomplete (P0/P1)

**Evidence:** NewSolution succeeded; never `add_to_solution`. Empty/orphan solution can still “complete” because build targets csproj.

**Related prior fix:** argument swap on `add_to_solution` — does not help if the op is never called.

### F4 — XAML write has no pre-write structural check (P1)

**Evidence:** Step 12 applied XAML with `verified: false`; ladder then failed on missing `btnMultiply_Click`.

**Note:** Pre-write Roslyn already loads WPF generated partials for **code-behind**; pure XAML event→handler cross-check is still missing.

### F5 — UnitTests rung can pass with 0 tests (P1)

**Evidence / code:** `VerificationLadder` UnitTests: `(true, 0)` → summary says nothing verified, but **`Passed` still true** via `tests.Ok`. Early scaffold steps look fully green before product tests exist.

### F6 — Weak recovery after completion critique (P1)

**Evidence:** Steps 17–20: refute asked for UI/runtime evidence; agent re-read/re-ran unit tests; second completion not re-critiqued (`critiqueSpent`). Post-run review still accepted.

**Product context:** Operator **Run app** is the intended live UI check (HISTORY); agent has no tool that answers critics.

### F7 — Discovery tools unused on greenfield (P2)

**Evidence:** No `list_projects` before wiring; would have shown TFMs. Batch-2 tools still unproven by live calls (HISTORY open). Prefer stronger messages inside `dotnet_project` over new tools.

### F8 — Docs drift (P2)

`docs/grok/tool-evaluation-ai-codegen.md` still proposes tools already shipped. Operators/agents following it waste effort.

---

## 6. Recommended tasks (for a code agent)

Implement in order. Each task is self-contained; ship independently with tests. Prefer extending existing tools over new names.

### Task A — TFM-aware `add_reference` (P0)

**Goal:** WPF/windows app + xunit `add_reference` succeeds without hand-editing csproj.

**Approach (pick one; prefer A1):**

- **A1 (mechanism):** On `AddReference`, if SDK fails with incompatible frameworks, detect referenced project’s TFM(s) via existing project-file readers (`ProjectLocator`), rewrite the **referencing** project’s `TargetFramework`/`TargetFrameworks` to a compatible value when unambiguous (e.g. single TFM `net10.0-windows`), record via change log, invalidate `BuildCache`, retry once.  
- **A2 (structured fail):** If auto-align is too aggressive, return a rich failure summary: both TFMs + exact next action (one line), and optional op `align_framework` later.

**Files (likely):**  
- `src/GlassCoder.Tools/Build/DotnetProjectTool.cs`  
- `src/GlassCoder.Tools/Verification/ProjectLocator.cs` (read TFMs if not already enough)  
- `tests/GlassCoder.Tools.Tests/ProjectScaffoldingTests.cs`

**Acceptance:**

- [ ] Integration/unit test: xunit net10.0 + UseWPF net10.0-windows → `AddReference` ends with `succeeded: true` (or A2 with non-hand-edit recovery only).  
- [ ] Change log shows TFM change if A1 auto-edits.  
- [ ] No new top-level tool name.  
- [ ] `PromptBudgetTests` still green (trim description text if needed).

**Motivates run:** `ca727be3` steps 4–7.

---

### Task B — Make SDK command failure visible to progress machinery (P0)

**Goal:** Failed `dotnet_project` (and similar) feed digest ✓/✗ and `RunProgressSentry` without breaking “errors are observations.”

**Constraints (HISTORY):**

- Failed **build**/handled outcomes may stay non-throwing observations.  
- Soft `ok: true` made five identical `add_to_solution` fails invisible (`4b562c91`).  
- Do **not** blindly set all SDK fails to `ok: false` if that breaks metrics semantics without updating consumers — prefer one clear contract.

**Recommended design:**

1. Define “outcome failed” for tools that return a payload with `Succeeded`/`Ok` flags: either  
   - set outer `ToolObservation.Ok = false` with a stable `ToolErrorCodes` (e.g. `command_failed`) **and** keep payload in `Data` if schema allows, **or**  
   - keep outer `Ok` but extend `IToolObservation` / registry / sentry so **payload-level failure** counts as a failure identity (first line of summary).  
2. Ensure step log **ToolSummary** already uses observation summary (it does); ensure sentry keys on that first line.  
3. Update `DigestCompactor` if it only looks at outer `Ok`.

**Files (likely):**  
- `DotnetProjectTool.cs` (failure branch)  
- `src/GlassCoder.Tools/ToolObservation.cs` / `Observation` helpers  
- `src/GlassCoder.Tools/Registry/ToolRegistry.cs` / `ToolInvocation`  
- `src/GlassCoder.Core/Agent/RunProgressSentry.cs`  
- `src/GlassCoder.Core/Context/*Compactor*` (digest outcomes)  
- Tests: Tools + Core agent/sentry/digest

**Acceptance:**

- [ ] N identical failed `add_reference`/`add_to_solution` with no applied change eventually **nudge or stop** per existing `MaxIdenticalToolFailures` policy.  
- [ ] Compaction digest marks those calls ✗, not ✓.  
- [ ] Successful failed-**build** semantics (if intentionally ok) remain documented and tested.  
- [ ] Run log step line still readable.

**Motivates:** `ca727be3` step 4; HISTORY open on failure-as-information.

---

### Task C — Solution membership after scaffold (P0/P1)

**Goal:** Creating a solution + projects does not leave an empty sln as the “done” structure.

**Approach (prefer mechanism over prompt):**

- **C1:** After successful `New` (project), if exactly one solution is findable under the writable tree / known path from this run, **auto `sln add`** (or call internal add) and log the change; summary says so.  
- **C2:** If auto-add is wrong for multi-sln repos, strengthen summaries only: after `NewSolution`, print exact next calls; after `New`, print `add_to_solution` with **resolved** sln path (already partly done for NewSolution).  
- **C3 (optional later):** compound op `new_app_with_tests` — only if A+C1 still leave multi-step thrash in live runs; watch schema budget.

**Files:** `DotnetProjectTool.cs`, `ProjectScaffoldingTests.cs`, possibly `ListProjectsTool` for “projects not in any solution” warning.

**Acceptance:**

- [ ] Test: NewSolution + New wpf + New xunit (+ refs) leaves both projects in the solution file **without** the agent calling `add_to_solution` (if C1), **or** summaries contain copy-pasteable exact paths (if C2 only).  
- [ ] `list_projects` (if used) does not show “no solution” when solution was created empty of projects without warning.

**Motivates:** `ca727be3` steps 1–3; orphan `solution.slnx`.

---

### Task D — XAML event ↔ code-behind pre-check (P1)

**Goal:** Catch step-12 class failures before disk write when cheap.

**Approach:**

- On `edit_file`/`create_file` of `*.xaml` under a UseWPF project: parse attributes like `Click="HandlerName"` (and similar common events); if sibling `*.xaml.cs` / partial class exists, warn or **refuse** if handler method missing.  
- Keep full WPF compile authority on ladder/build; this is a cheap structural gate.  
- Well-formedness (XML parse) optional first slice.

**Files:**  
- `EditFileTool.cs` / `CreateFileTool.cs` or shared verification helper  
- New helper under `Verification/` or `FileSystem/`  
- `XamlAwareGateTests.cs` or new tests

**Acceptance:**

- [ ] Editing MainWindow.xaml to add `Click="Missing"` without method → refuse or inconclusive with clear hint naming the handler.  
- [ ] Adding handler + event in separate steps still works (order: code-behind first should pass; XAML-first may refuse until method exists — document intended order in summary).  
- [ ] Non-WPF XML untouched.

**Motivates:** `ca727be3` step 12.

---

### Task E — Honest zero-test verification (P1)

**Goal:** Ladder does not treat “0 tests ran, exit 0” as full UnitTests pass when that misleads goal completion.

**Approach options:**

- **E1:** `RungResult.Passed = false` when `tests.Total == 0` and rung is UnitTests (FullSuite optional same).  
- **E2:** Soft: `Passed = true` but attach a strong caveat that completion sentry / critique evidence must not treat as verified behaviour; only if E1 is too harsh for non-test projects.

**Prefer E1 when `request` has a test project path or goal/task implies tests** if that signal exists; else E1 globally for UnitTests rung is simpler and matches HISTORY “0 of 0 is not green.”

**Files:**  
- `src/GlassCoder.Core/Verification/VerificationLadder.cs` (~UnitTests case)  
- `VerificationLadderTests.cs`

**Acceptance:**

- [ ] After scaffolding only xunit (no test methods), UnitTests rung does **not** report as passed green the same way as N>0.  
- [ ] Real suite with N>0 still passes.  
- [ ] Message already present for (true,0) remains accurate.

**Motivates:** early green ladder climbs in `ca727be3` after scaffold steps.

---

### Task F — Completion critique recovery (P1)

**Goal:** After a completion refute, agent cannot “Complete” solely by re-asserting with the same evidence class.

**Approach:**

- In `AgentLoop`, after critique refute: clear or delay `critiqueSpent` **or** require `newlyApplied.Count > 0` or explicit tool evidence before accepting next no-tool completion.  
- Optionally inject one user message: critics’ reasons + “operator Run app validates UI; unit tests alone will not satisfy UI goals.”  
- Do **not** add a full UI automation tool in this task (see non-goals).

**Files:**  
- `src/GlassCoder.Core/Agent/AgentLoop.cs` (critique boundary ~completion claim)  
- Agent loop / critic tests

**Acceptance:**

- [ ] Simulated refute then no-tool claim without new changes → not Completed (continued with message) **or** second critique runs.  
- [ ] Refute then meaningful edit + re-claim → can complete.  
- [ ] Token cost of second critique documented if always-on.

**Motivates:** `ca727be3` steps 17–20.

---

### Task G — Richer TFM/project map in scaffold summaries (P2)

**Goal:** Even without calling `list_projects`, model sees frameworks after `New`.

**Approach:** Append one line to successful `New` / `NewSolution` summaries: TFM, UseWPF/UseWindowsForms flags, path to csproj. Cheap; no new tool.

**Files:** `DotnetProjectTool.cs` `DescribeCreatedProject` / related.

**Acceptance:** Summary after `new wpf` mentions `net*-windows` (or actual TFM). Complements Task A.

---

### Task H — Docs / evaluation refresh (P2)

**Goal:** Stop recommending already-shipped tools as missing.

**Files:** `docs/grok/tool-evaluation-ai-codegen.md` (and operator tool tables if any).

**Acceptance:** Document lists current surface; marks find_references / nuget as deferred with HISTORY reasons.

---

### Task I — Optional compound scaffold (P3 — only after A–C live trial)

**Goal:** One op: solution + app template + test project + TFM align + references + sln membership.

**Only if** live re-run of desktop task still burns >5 steps on ceremony after A–C.

**Schema:** Prefer a `DotnetProjectOperation` value or optional flags — **not** a 15th top-level tool if avoidable. Measure with `PromptBudgetTests`.

---

## 7. Explicit non-goals (do not implement from this review)

| Item | Why |
|------|-----|
| New `apply_patch` tool | Shipped as `edit_file(edits)` |
| `find_references` | Deferred until real MSBuild workspace (false “no callers”) |
| Live `nuget_info` / MCP retrieval | Needs record/replay for Lab hermeticity |
| Promote `bash` for codegen | Safety and observation quality |
| Agent-side full UI automation | HISTORY chose host **Run app** for interactive UI |
| Directory recursive delete | `file_operation` is file-scoped by design |
| Raising schema budget for unused tools | `find_symbol` still unused; fix adoption or trim later |

---

## 8. Suggested implementation order

```text
1. Task A  — TFM-aware add_reference          (highest live-run ROI)
2. Task B  — SDK failure visible to sentry      (closes HISTORY open class)
3. Task C  — Solution membership                (structure integrity)
4. Task E  — Zero-test rung honesty             (small, Core only)
5. Task D  — XAML handler pre-check             (prevents known ladder fail)
6. Task F  — Critique recovery                  (completion quality)
7. Task G  — Scaffold summary TFMs              (cheap, supports A)
8. Task H  — Docs                               (no runtime risk)
9. Task I  — Compound scaffold                  (only if needed after trial)
```

After A–C: clean `GlassCoderTest`, rebuild GlassCoder, re-run the same desktop/WPF multiply goal. Expect fewer steps before first product edit, no hand-edited csproj for TFM, solution containing both projects, validity still ~1.0.

---

## 9. Test and validation checklist for the implementing agent

- [ ] `dotnet test` on Tools + Core at minimum; full solution if binaries not locked by a running WPF instance.  
- [ ] New behaviour covered by tests that go through **ToolRegistry** or realistic scaffolding when binding matters.  
- [ ] No silent schema bloat: run / respect `PromptBudgetTests`.  
- [ ] Update `HISTORY.md` with: run id motivating change, decision, open residuals.  
- [ ] Live trial on cleaned workspace when touching `dotnet_project` or ladder.

---

## 10. Key code anchors

| Area | Path |
|------|------|
| Tool DI / phase tools | `src/GlassCoder.Tools/DependencyInjection/ToolsServiceCollectionExtensions.cs` |
| SDK scaffolding | `src/GlassCoder.Tools/Build/DotnetProjectTool.cs` |
| Observation contract | `src/GlassCoder.Tools/ToolObservation.cs` |
| Pre-write / XAML partials | `src/GlassCoder.Tools/Verification/RoslynCodeAnalyzer.cs`, `XamlAwareGateTests.cs` |
| Edit / create | `src/GlassCoder.Tools/FileSystem/EditFileTool.cs`, `CreateFileTool.cs` |
| Ladder | `src/GlassCoder.Core/Verification/VerificationLadder.cs` |
| Loop + critique once | `src/GlassCoder.Core/Agent/AgentLoop.cs` |
| Progress sentry | `src/GlassCoder.Core/Agent/RunProgressSentry.cs` |
| Schema budget | `tests/GlassCoder.Core.Tests/PromptBudgetTests.cs` |
| Scaffolding tests | `tests/GlassCoder.Tools.Tests/ProjectScaffoldingTests.cs` |
| Method / decisions | `HISTORY.md`, `WORKPLAN.md` |

---

## 11. Bottom line for the implementing agent

The last run shows a **healthy** harness (validity 1.0, recovery, real tests, useful critics) on top of years of transcript-driven fixes. Remaining waste is **compositional**:

1. WPF↔test **TFM** still forces hand-edited csproj.  
2. Failed **SDK** calls still look like tool success to anti-loop machinery.  
3. **Solution** membership is optional in practice.  
4. **XAML** events and **0-test** greens still mislead.  
5. **Critique** can be satisfied by re-running unit tests.

Fix those inside existing tools using HISTORY’s method. Do not expand the tool list until a live re-run proves ceremony is still expensive after Tasks A–C.

---

*Generated 2026-08-08 from session analysis of GlassCoder tools, run `ca727be3`, and `HISTORY.md`.*
