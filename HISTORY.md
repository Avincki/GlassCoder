# History

Dated session logs: what shipped, what was decided and why, and what is still
open. Newest first.

The point of this file is resumption. Anything derivable from the source or the
commit log does not belong here — decisions, their reasoning, and open threads
do, because those are what a later session cannot cheaply rediscover.

---

## 2026-08-08 (last) — The tool list moves to where it can say what a tool is for

**Shipped.** Workplan task 64, done ahead of the retrieval branch because that branch adds tool
sets behind switches and this is the surface that has to say which ones a session registered.
`ToolCatalog` in `GlassCoder.Tools` lists every tool the build knows about — nineteen, thirteen
active on a default install — and About renders them with their purpose, dimming the six the
configuration switched off and naming the setting that brings each back. The status bar opens at
"Ready." and says nothing about tools. 710 tests green, +11.

Also written this session: workplan tasks 54-63, the gated MCP retrieval track, which reopens
task 53.

**Decided**

- **The registry cannot answer "what could this do".** A disabled tool set is never registered
  *and never constructed* — with `Git:Enabled` false, `AddGitTools` is not called, `GitTool` is
  not in the container, and there is no `AIFunction` to read a description from. That is the
  right design and it stays: gating registration rather than execution is what keeps a
  switched-off tool out of the model's schema entirely. So the catalogue reflects over **types**,
  not instances — no construction, no dependencies, keyed on the same `[GlassCoderTool]`
  attribute `ToolFunctionFactory` uses, so a tool added later cannot go missing from it.
- **One description, two routes to it.** The live `AIFunction.Description` when the tool is
  registered — literally what was sent — and the `[Description]` attribute when it is not, which
  is the string that would be sent. A test asserts they agree wherever both exist, so the two
  paths cannot drift; and there is no third set of prose written for humans, which would have
  been a third place to keep correct and the first to go stale. It makes About a review surface
  for the prompt as a side effect: a description that reads badly to a person reads badly to the
  model too.
- **An inactive row names its switch**, built from `GitOptions.SectionName` and
  `SandboxOptions.SectionName` with `nameof` — the same constants the registration reads, so the
  setting a row names and the setting that gates it cannot disagree. "Inactive" alone invites
  "how do I turn it on", which is the reasoning the operator guide's *defaults that refuse*
  already follows.
- **A tool no path registers is a defect, not a switch-off**, and it fails a test rather than
  waiting to be noticed in a window. `ModelContextProtocol` sat pinned and referenced by nothing
  for the life of the project; that shape of dormancy is worth a build break.
- **Active MCP tools will need no change here.** The sweep ends by adding any registered function
  no attributed method declares, which is exactly what a server-adapted tool is, so `learn_search`
  joins the list the day task 57 registers it. Only an *inactive* retrieval tool needs a second
  source, and `RetrievalOptions` does not exist yet.

**Open**

- The schema size on each active row is the generated figure, not what reaches the wire — the
  client re-serialises indented and it arrives about a third larger. Said plainly in the code
  because the number invites comparison with `PromptBudgetTests`, which measures at the socket.
  Task 58 is where the two are reconciled.

---

## 2026-08-08 (late) — The screen is the one oracle the harness does not have

**Shipped.** From runs `ea9a1f66` and `216360bf` and the ui-layout external review, critically
merged: a failing test that keeps failing across edits earns a nudge no applied change can
reset, refutation messages steer UI recoveries away from XAML-parsing tests, run_tests names
its failing tests, XAML writes warn about clip-risk windows and test-project markup,
shell-shaped tool names get the no-shell answer, claims are told to carry build and test
results, the second-refutation caveat lands in advisory mode too, and the limit banner turns
light red. 683 tests green in Tools+Core (+8).

**What the runs said.** `216360bf` was refuted over UI evidence and spent ~28 steps and one
operator token-extension writing XAML-parsing layout tests that can never pass in a plain
test process, copying the app's markup into the test project, then deleting the lot - while
"N of M tests failed" never named which test kept refusing its fixes, and every honest edit
between identical failures reset every counter the sentry had. `ea9a1f66`'s operator defect
(result field clipped by a fixed Height=300 window) survived a green build, green tests and a
MinWidth fix that answered the wrong diagnosis. Meanwhile `e212c61c` - the goal's first clean
run - was accepted by the first panel for one visible reason: it ran build and run_tests
before claiming.

**Decided**

- **The test-outcome streak survives applied changes, on purpose.** Every other counter
  honestly resets when the workspace moves; these two runs edited between every identical
  failure while fixing nothing. Only a different outcome for the same target - green, or a
  different failure - ends the streak. Nudge at three, no stop: ea9a1f66 converged on cycle
  five, and a stop would have stolen its finish.
- **The external review's L1 gate was refused a third time, its own §3 the grounds:** critics
  demand runtime proof "the worker cannot produce", so blocking completion on their approval
  converts good deliverables into stopped runs. The fair half - the record disagreeing with
  its own review - ships instead: the completed-despite-second-refutation caveat now lands
  regardless of CritiqueGates.
- **Rendering advice is notices, never refusals.** The clip-risk and test-project-markup
  warnings ride write summaries; a harness too sure of a rendering judgement is how the gate
  deadlocks of 5c071f37 and a408b61b began.
- **Shell-shaped names get the truth.** `run` meant `rm -rf` in one run and `copy` in
  another; "did you mean run_tests?" answered neither. The reply now names the real paths -
  and that the application is launched by the operator, never the agent.

**Validated** by run `17f5fa36` (same day, goal upgraded with "fully visible;
Height=Auto/SizeToContent or ≥450"): 20 steps and 135k tokens - the cheapest run of the goal's
nine - with build and run_tests called before the claim, a post-refutation recovery of three
XAML-editing steps and zero proxy tests (the same refutation shape cost 216360bf twenty-eight),
the advisory caveat in the run record, and the clip-risk notice correctly silent on compliant
markup. The cost curve across the nine runs: 501k → 519k → 162k → 289k → 504k → 139k → 289k →
641k → 135k.

**Open**

- **The critics misread a disjunctive goal.** The run's first version used SizeToContent
  (compliant); the recovery switched to fixed Height=450 (also compliant, per the goal's own
  "or ≥450"); both were refuted 3/3, the second refutation claiming fixed 450 "contradicts" a
  goal that explicitly allows it - critic pressure moved the model from the better branch to
  the worse-but-legal one. Once is an observation: if it recurs, one critic-prompt line - "a
  goal offering alternatives is satisfied by any one of them" - is the candidate fix.
- Whether "cite build and test results" converts first-panel acceptance stays unanswered:
  this goal's own "fully visible" wording handed the critics a visibility requirement no
  static evidence satisfies, so refutation was overdetermined.

---

## 2026-08-08 (shell) — One more allotment, and a transcript that follows the run

**Shipped.** Two operator conveniences: a tripped step or token ceiling can be extended by one
more allotment of its configured size from a banner, and the transcript follows the live run -
newest step selected, detail pane at its end - until a click on an earlier row pins it.
690 tests green, +2.

**Decided**

- **The loop pauses on the limit question instead of dying on it.** `ILimitExtensionGate` is
  consulted when StepLimit or TokenLimit trips (only those two - time and cost stay where
  configuration put them); approval extends the ceiling by the configured amount and the
  question returns at the next trip. The console registers no gate and stops exactly as
  before; the WPF shell answers with a banner, and cancelling the run answers "stop" so
  Cancel keeps working while the banner is up. A gate that throws is a "no", never a crash.
- **The extension is per-run.** Nothing writes back to settings: raising the configured limit
  for every future run is what the settings dialog is for.
- **The newest transcript row doubles as the "follow live" control.** Selecting it resumes
  following; selecting any other row pins the view (detail from the top) - a transcript that
  yanks the selection away mid-read is unreadable during exactly the runs worth reading. The
  scroll mechanics live in the view code-behind; the view model keeps owning rows and
  selection.

**Open**

- The banner waits indefinitely; a wall-clock limit still fires while it waits, which is the
  honest backstop for a question nobody answers overnight.

---

## 2026-08-08 (night) — The pager that ignored the page number

**Shipped.** From run `c5eb67f6` (41 steps, 504k, TokenLimit - the first regression to the
death pattern since the gate fixes) and a third external review: arguments are validated
against the schema before they bind, reads of one unchanged file count as one loop however
the window wobbles, the TFM repair accepts directory spellings, and a truncated read names
its own continuation. 687 tests green, +6.

**What the run said.** A correctly refused edit pointed at line 81; the model paged toward it
with `read_file(offset: 70)` - another harness's name for `startLine` - and the binder
silently dropped the unknown key, returning the head of the file, marked Succeeded, thirteen
times. Every wobble of the window minted a fresh fingerprint, so the stall sentry never
armed. Fourteen steps and roughly 180k tokens later the model overwrote the file whole and
finished the goal, but the critique recovery - which this time wrote a real integration test
rather than f4ed50e0's packages - died at the token limit mid-fix. Secondary: the TFM widen
never fired because the model spelled the referencing project as its directory, and reading
a framework out of a directory yields nothing.

**Decided**

- **Arguments get the tool-name treatment, one level down.** The registry validates every
  argument name against the function's schema before binding: a proven alias
  (`read_file.offset` → `startLine`) is rewritten and invoked; any other unknown name fails
  as InvalidArguments naming the real parameter list - one corrective step instead of
  thirteen silent no-ops. Integer parameters accept the shapes models send ("70", 70.0);
  fractions are refused with the reason. The binder silently dropping unknown keys was the
  todo_write defect at the argument level, and the fix is the same contract.
- **Reads of one unchanged path are one loop, whatever the window.** A same-path counter runs
  beside the fingerprint tracker: a nudge at four reads with nothing applied between - naming
  startLine, outline, and whole-file overwrite as the exits - and past four they stop
  counting as novelty, so the ordinary stall stop takes over. Only read_file feeds it:
  a directory re-queried with new patterns is exploration, not a loop.
- **The TFM repair resolves directories to their single project file on both sides** - the
  CLI accepts the spelling, so the repair must.
- **A truncated read says how to continue** (`Continue with startLine: N`, outline for C#) -
  the sentence that would have broken the loop at its second step.

**Open**

- The critique-recovery arc remains unmeasured end to end: c5eb67f6's recovery was finally
  substantive (a real test, an app edit to match) but the budget was already spent. The next
  clean run is the first real test of the two-panel critique.
- Argument aliases hold one entry, by the log-proven rule; `limit` (offset's sibling in the
  same foreign idiom) now fails fast with the parameter list, which is the acceptable path
  until a log shows it recurring.

---

## 2026-08-08 (evening) — Efficiency: the run that completed and still wasted a third

**Shipped.** From run `f4ed50e0` (30 steps, 289k, Completed, 6 tests - the live trial of the
morning batches) and a second external review (GrokReview20260808114442.md): known wrong tool
names are rewritten and invoked, `dotnet_project` forgives stray whitespace, the completion
critique judges the recovery too, critics stop refuting over word choice, and todos are asked
for at phase boundaries only. 683 tests green, +10.

**What the run said.** Every prior fix held - the TFM seam repaired itself in one call, the
ladder logged "unverified" over the testless tree, two identical soft failures were counted -
and the run still spent a third of its steps on new friction: `todo_write` twice (the "did
you mean" hint converted the first miss, not the second), a leading-space package id refused
twice by the SDK, and a ten-step critique recovery that added Moq and FlaUI to the test
project without writing a single test that used them, then completed on the spent critique.

**Decided**

- **Aliases rewrite; hints suggest.** A name whose intent is unambiguous (`todo_write` with
  byte-identical `update_todos` arguments) is invoked as what it meant, logged, and recorded
  under the canonical name so failure and repeat keys align. Only log-proven names enter the
  map - an alias is a bet that the model's habit is stable.
- **The critique panel speaks at most twice.** Once on the claim, once on the recovery - run
  f4ed50e0's package theater completed unexamined on the spent critique. The ceiling is hard:
  a second refutation completes the run rather than starting a third argument, with a caveat
  in the record when the critique gates, and without one in advisory mode - advisory invited
  finish-as-is, so finishing as-is is not a caveat. Bounded at two, 4b582162's revert loop
  stays impossible.
- **The refutation message names the failure mode it breeds:** packages without tests that
  use them address nothing. Recovery instructions are concrete because f4ed50e0's was not.
- **Critics judge behaviour, not word choice.** "Dialog" versus "window" refuted two runs of
  the same goal whose built UI covered the ask, two-for-two.
- **Todos at phase boundaries** (system prompt, both the code default and the operator's
  settings copy): 7 of f4ed50e0's 30 steps were plan bookkeeping.
- **Git tools off in the operator's settings for the measurement phase** - five schemas
  re-sent every step of runs that never call them. Revert: `Git.Enabled: true` in
  `%APPDATA%\GlassCoder\settings.json` (backup beside it).

**Open**

- The two-panel critique is untested live: watch whether the second panel converts recoveries
  or merely stamps caveats.
- The alias map has one entry family; other habitual wrong names should earn their place from
  logs, not speculation.
- Auto-`add_to_solution` was proposed again and rejected again - an off-root solution is
  invisible to build-target resolution regardless of membership. If runs keep creating
  ceremonial solutions despite the new warnings, steer `new_solution` away instead.

---

## 2026-08-08 (later) — The seams between the organs

**Shipped.** From an external review (GrokReview20260808.md) of run `ca727be3`, critically
re-reviewed against the two failed predecessors: soft failures now reach the progress
machinery, the wpf↔xunit framework seam repairs itself, zero-test climbs stop reading green,
solutions that govern nothing say so, and the completion critics are bounded to evidence the
worker can produce. 676 tests green, +12.

**What the re-review changed.** The external review's two best findings survived
verification (soft-fail invisibility at `RunProgressSentry`, the never-re-critiqued second
completion); two of its recommendations were refused: re-arming the completion critique would
reinstate the revert-loop its one-shot design exists to prevent, and a refusing XAML
handler pre-check is the gate-deadlock pattern that cost runs 5c071f37 and a408b61b. The
critic fix went the other way - constrain refutation to obtainable evidence - and the XAML
check was dropped.

**Decided**

- **`OutcomeOk` rides the wire only when false.** The AI function layer serialises every
  observation to JSON before the registry sees it again, so a `[JsonIgnore]` flag exists only
  in unit tests - discovered when the sentry test stalled instead of stopping. Successful
  observations stay byte-identical; a soft failure adds one field. The digest now reads
  outcomes off the wire shape too, which it never did live.
- **The tool repairs the mismatch its own templates manufacture.** wpf scaffolds
  net10.0-windows, xunit scaffolds net10.0; on the CLI's "incompatible targeted frameworks",
  the single-TFM base-plus-suffix shape widens the *referencing* project through the change
  log and retries once - the `NormalizeSolutionAdd` contract. Every other shape gets both
  frameworks and the side to change; the CLI's message, which reads as "change the app",
  never reaches the model raw. Scaffold summaries now name the framework so the mismatch is
  visible before the failure.
- **Zero tests is `Unverified`, not passed and not skipped.** A new state, because failed
  would gate a testless tree and skipped would drop the "nothing was verified" line from the
  summary the critics judge. Runs a408b61b and ca727be3 each logged "UnitTests passed" eleven
  times with no test files on disk.
- **Critics are told the worker's evidence universe** - builds, tests, file reads, static
  checks; no app launch, no UI, no screen - and that absent runtime proof is never by itself
  grounds to refute. Runs 008007e1 (3/3, then a fatal re-scaffold spiral) and ca727be3 (2/3)
  were both refuted over evidence no tool can produce; live UI proof is the operator's
  Run-app button by design.
- **`list_projects` and `new_solution` now say when a solution is empty or off-root.** Run
  ca727be3's `src/MultiplyApp/solution.slnx` was both, and no surface ever mentioned it
  again. Auto-adding projects to solutions was considered and rejected: it polishes a file
  build-target resolution cannot see.

**Open**

- The completion critique still runs exactly once; a model that finishes over a refutation it
  answered with new evidence is accepted without a second panel. Watch whether the evidence
  constraint alone is enough.
- Whether "does it compile" should have one authority (a cached design-time build in the
  gate) or the gate should be advisory-only everywhere - the standing question behind
  5c071f37, e8f9186a, 008007e1 and a408b61b - remains undecided.
- `GrokReview20260808.md` items deferred, not refused: compound scaffold op (only if ceremony
  stays expensive live), docs refresh of `docs/grok/tool-evaluation-ai-codegen.md`.

---

## 2026-08-08 — The gate stops manufacturing its own refusals

**Shipped.** From run `a408b61b` (42 steps, 519k, TokenLimit, an app with zero tests): the
pre-write gate now synthesises the csproj's `<Using>` items, concedes on the strike limit
instead of after it, and SymbolHints answers from referenced assemblies. 664 tests green, +7.
Validated the same morning by run `ca727be3`: 21 steps, 162k, **Completed** - the first run
of the desktop goal to finish - with an idiomatic no-`using Xunit;` test file accepted first
try.

**What the run said.** The xunit template declares `<Using Include="Xunit" />` in its
project file; the gate read the csproj for ImplicitUsings and UseWPF but not for Using items
three lines away, and manufactured fifteen CS0246s for a file the real build compiles.
SymbolHints searched only workspace sources, so FactAttribute - sitting in xunit.core.dll,
loaded into the very compilation that reported it missing - drew no hint, and the model
chased the compiler's "assembly reference?" through three no-op package adds and two green
builds. The concession's "after 3 the write will be allowed" asked for a fourth identical
attempt no model makes. Whether a run survived the gate had come down to whether the model
happened to type a using the project already declared.

**Decided**

- **Explicit `<Using>` items are synthesised; SDK-flavour sets still are not.** The project
  declares the items, so the real build compiles against them and so must the gate; Web/Worker
  namespaces live in packages the compile may not reference, and emitting those would
  manufacture the opposite error.
- **The strike limit is the attempt that goes through** (`>=`, not `>`), with the countdown
  reworded to say so. A promise of leniency one attempt past the last refusal converts nobody.
- **SymbolHints asks the failing compilation's own reference set** for names no source
  declares, and names the namespace, assembly and exact using directive in the refusal.

**Open**

- `add_reference` still failed on the wpf/xunit TFM seam (3 steps to recover) - fixed in the
  next entry.
- The ladder counted a testless workspace as a passing UnitTests rung throughout - likewise.

---

## 2026-08-07 (night) — A refuted finish, a scaffold in a file's clothing

**Shipped.** From run `008007e1` (42 steps, 501k, TokenLimit): `dotnet_project new` refuses
file-named paths and paths inside or above an existing project, and every file `dotnet new`
scaffolds is recorded in the change log as created-by-this-run. 657 tests green, +8.

**What the run said.** Steps 0-23 were flawless - app, tests, 5/5 passing. The completion
critique refuted 3/3 (dialog-vs-window wording; no runtime evidence), and the recovery
asked `new` for `src/MultiplyApp/DialogWindow.xaml` - a file name, inside the project being
edited - because no window-item template exists. A complete second application landed nested
in the first; deleting it file-by-file consumed the rest of the budget, and a revert
resurrected a deleted scaffold file because files written by `dotnet new` had no change-log
baseline: "how this run found it" was whatever the first later touch recorded.

**Decided**

- **Refuse the hazardous scaffold while refusing is one cheap step.** `list_projects` had
  warned about nesting after the fact all along; the same knowledge now runs before six files
  exist, and the refusal points at `create_file` - the tool the model actually wanted.
- **Scaffolded files enter the change log as creations** ('' → content), so revert means
  what it says: the file goes. The template-stub deletion now records creation *and* removal.

**Open**

- `file_operation` still has no recursive delete; undoing a bad scaffold remains file-by-file
  (mitigated by refusing the scaffold instead).
- The critique demanded evidence no tool can produce - addressed on 2026-08-08.

---

## 2026-08-07 — Clean stops losing to a single held handle

**Shipped.** From the operator's report that Clean left subfolders standing: the pane's clean
now empties the writable roots leaf-first, and its summary survives the refresh that follows.
647 tests green, +2.

**What was found.** Clean always meant to delete subfolders - `Delete(recursive: true)` - but
the framework's recursion abandons a whole subtree at the first file it cannot remove, and in
a Dropbox-synced workspace there usually is one: held for hashing (GlassCoderTest's `bin`/`obj`
are never sync-ignored), or copied read-only into build output. One stubborn file kept every
subfolder around it alive. Worse, the refresh after a clean overwrote the status line at once,
so "2 could not be" flashed away unread - partial cleans looked like silent refusals to delete
folders.

**Decided**

- **Bottom-up, per-entry sweep.** Read-only comes off before the delete, three spaced retries
  outlast a sync-client hold, an entry that vanishes mid-attempt counts as removed, and a
  folder is only asked to go when its own sweep left it empty. A file that genuinely will not
  go costs itself and the folders directly above it, nothing beside it; failures name their
  workspace-relative path.
- **The clean summary outlives the refresh.** `RefreshAsync` now says whether the read
  completed, and the clean puts its summary back on success. A failed read keeps its error -
  a pane that cannot see the workspace outranks a tidy summary.

**Open**

- The retries sleep on the UI thread - worst case ~150ms per stubborn entry - so a workspace
  with many held files makes Clean noticeably sluggish before it reports.

---

## 2026-08-06 (coda) — The swap the CLI kept refusing

**Shipped.** From reviewing run `4b562c91` (20 steps, 123k - the template fix verified): a
swapped `add_to_solution` is now put the right way round. 645 tests green, +3.

**What the run said.** The scaffold detour is gone - `new wpf` in one call, straight to `src`,
skeleton edited in place. The new bottleneck was solution ceremony: five `add_to_solution`
calls with the project and solution swapped, every one failing at the CLI with "Solution
argument is misplaced", plus two hallucinated `run` tool calls trying to escape to the raw CLI.
The run shipped an **empty** `sln.slnx` and claimed success over it; nothing downstream noticed
because build targets the csproj.

**Decided**

- **When the argument names a solution, the shapes cannot be what the contract says and the
  intent is unambiguous** - the edit_file lesson again. The project is the path when it names
  one, else the directory's single project; the solution is taken as named when it exists, else
  found beside the project (a bare `sln.slnx` means "the one I just made there"). A shape that
  cannot be repaired goes through unchanged and fails where it always failed.

**Open**

- Five near-identical failures armed no loop-breaker: `dotnet_project` reports a failed command
  as an *ok* observation (information, not fault), so the sentry's failure counter never saw
  them, and the varying arguments kept the stall tracker quiet. Failure-as-information tools
  are invisible to the repetition machinery - the class remains even though this instance is
  fixed.
- The critics' verdict on "compiles but untested" straddles the majority line: the same claim
  shape was refuted 2/3 in run `e3993510` and accepted 2/3 in `4b562c91`, with the same
  dissenting rationale at 0.9 both times.

---

## 2026-08-06 (last) — The template the model kept asking for

**Shipped.** From reviewing run `e3993510` (the first healthy WPF run): `dotnet_project` now
offers `wpf` and `winforms`, and the schema says so. 642 tests green, +2.

**What the run said.** The morning's fixes verified live - MainWindow.xaml.cs landed first try
via the missing-partial inconclusive path, the critics refuted a premature "done" and got a
tested Calculator extraction out of it, and every gate refusal was true signal. The remaining
waste was the scaffold detour: the model asked for 'wpf' unprompted in both WPF runs, was
refused both times, and spent ~7 steps (~20% of the run) converting a console project by hand -
during which the template's Program.cs failed three ladder climbs before being deleted.

**Decided**

- **`wpf` and `winforms` join the template list**, and the argument description names them -
  the schema is the one place the model reliably reads before its first call. The desktop
  scaffold is a starting skeleton, never a stub: unlike Class1.cs it is kept, and the summary
  says to edit it in place. Using the SDK's template also pins the SDK's own TargetFramework,
  ending the model's silent net8 habit.
- **The path description now says "in a writable root (not '.')"** - the other per-run tax,
  paid once in the schema instead of once per run in a refused call.
- **The schema budget was honoured, not raised.** The additions were paid for by cutting
  rationale from the same tool's descriptions ("with the dotnet CLI rather than hand-editing"
  became "never hand-edit"), per the budget test's own standing instruction.

---

## 2026-08-06 (night, later) — The pane learns to press F5

**Shipped.** A Run app button under the workspace tree: launches the workspace's application on
the desktop via `dotnet run`, detached, live and interactive. 640 tests green, +5.

**Decided**

- **On the host, never the sandbox — that is the point, not a compromise.** The verification
  ladder proves the tree compiles and its tests pass; whether the window opens and its dialogues
  behave is only answerable where windows exist. The process is the operator's from the moment
  it starts: its own console for build output, its windows on the real desktop, closed by the
  human, launched through the `IDesktopShell` seam like every other `Process.Start`.
- **An application is a csproj that says `OutputType` Exe or WinExe**, read directly like every
  other project-file question the harness asks — no MSBuild. Libraries and test projects are
  simply not applications; copies under `bin` (publish output) are excluded via the deny globs,
  because running a copy runs yesterday's app. Several applications → the first alphabetically
  runs and the status names how many others exist.
- **The Dropbox sweep runs before the launch.** A host `dotnet run` creates bin/obj outside the
  sandbox seam that normally pre-marks them — the exact gap the GlassCoderTest lock flakes came
  from — so `EnsureWorkspaceMarked` fires first and the first build cannot race the sync client.
- Disabled mid-run, like Clean and the git buttons: a host build racing the agent's own builds
  over the same obj helps neither.

---

## 2026-08-06 (night) — Two conveniences for test runs

**Shipped.** The goal box remembers the last run's prompt across a restart, and the workspace
pane grew a Clean button that empties the writable roots. 635 tests green, +7.

**Decided**

- **The last goal lives in the registry (`HKCU\Software\GlassCoder`), not the settings store** -
  deliberately. Everything the settings store saves feeds `IConfiguration`, and the provenance
  stamp hashes that configuration to identify a run's arm (`ProvenanceStamp.ConfigHash`); a
  prompt saved there would relabel every arm on every new prompt. UI state lives where
  configuration never looks, behind `IUiStateStore` so tests never touch the real registry.
  Saved at the moment Run is pressed - not per keystroke, and before the run, so a crash still
  leaves the pre-fill.
- **Clean empties exactly the writable roots, nothing else.** A run's output lives inside the
  folders the guard lets it write; a README beside them or the workspace's own .git is not a
  run's to have made, so not Clean's to delete. Roots are recreated when missing, a root that
  is not strictly inside the workspace (".", an absolute path) is skipped, and locked files are
  reported and skipped rather than aborting the sweep - Dropbox holding one handle should not
  win the whole clean. Asks first through the shell seam (`IDesktopShell.Confirm`), where
  closing the dialog means no; disabled mid-run, like the git buttons and for the twin reason.

**Open**

- Clean bypasses the change log, so `BuildCache` could in principle replay a cached green build
  over the emptied tree. In practice a fresh run scaffolds first and scaffolding empties the
  cache; noted in case a run ever builds before it scaffolds.

---

## 2026-08-06 (evening) — What the digest keeps, and what the sentry hears

**Shipped.** The two remaining layers of the anti-loop design: the compaction digest now keeps
outcomes, and the failure sentry now hears repetition through interleaved work. 628 tests green,
+5 net.

**What prompted it.** Two findings from re-reading run 5c071f37 against the morning's fix. First:
`DigestCompactor` read only the calls, never the results - so at the compaction horizon every ok
flag and every refusal reason vanished, ten refused writes compacted into what read as ten
successful ones, and "Do not repeat a call above" told the model not to retry a write that never
landed. Second: `RunProgressSentry`'s failure counter reset on any interleaved success and keyed
on the full error text - the run checked a build between every refusal (rational, not noise), so
ten identical refusals never counted three consecutive - and the morning's strike countdown,
embedded in the error message, made every repeat look novel to a detector keyed on that message.

**Decided**

- **The digest states outcomes, not just calls.** Each folded call is marked ✓/✗ via a new
  non-generic `IToolObservation` view; a failure carries its error code and the first line of its
  reason. "Do not repeat" now scopes to calls that did not fail, and a second line states the
  inverse: calls marked ✗ changed nothing.
- **Identical rows aggregate: ✗ create_file(…) (×10).** The count is the synthesis - it says
  "this exact attempt keeps happening" without asking the model to notice it across a list.
  Aggregation keys on the stable first line, so varying detail lines (a strike countdown, a
  diagnostics total) do not defeat it.
- **Failure identity is the first line of the error, per signature, until a change is applied.**
  Prose that synthesis writes must never be prose that detection keys on. The sentry's counts
  now accumulate across interleaved reads and green builds; only an applied change - the one
  event that honestly resets the argument - clears them. `MaxIdenticalToolFailures` changes
  meaning accordingly: "same failure N times with no change applied in between", not
  "N times consecutively". A read-only success between failures no longer launders the count.

**Open**

- The nudge latches never re-arm after compaction folds a nudge away; accepted for now because
  the digest's aggregated ✗ rows carry the same fact across the horizon.
- A fold that cuts between a call and its result leaves that call unmarked in the digest -
  listed, not guessed at.
- Both new mechanisms are deterministic and cheap; whether they change run outcomes is a live
  question for the next batch of runs, same as the concession.

---

## 2026-08-06 (later) — The gate learns WPF, and learns to concede

**Shipped.** From runs `d2b7372e` and `5c071f37` (the WPF multiply task, both dead at TokenLimit):
the pre-write compile now reads XAML-generated partials, stands aside when it cannot, and concedes
an argument it keeps losing the same way. 624 tests green, +13.

**What the runs said.** The markup compiler declares `InitializeComponent` and every `x:Name`
field in `obj/`, which the deny list excludes — so a *correct* WPF code-behind drew CS0103 from
the pre-write gate every time, ten refusals in one run, while `build` kept answering green in
between. The agent tried every reasonable variation, then shipped a window with no handler. Third
instance of the class the analyzer already documents twice (unbuilt references, stale
references): the approximate compile missing compiler-generated context and gating on it.

**Decided**

- **The gate reads the build's leavings rather than re-deriving them.** For a `UseWPF` project the
  compile includes the newest `*.g.cs`/`*.g.i.cs` per page from `obj/` — the one narrow, read-only
  crossing of the obj deny list, because that is where the missing declarations already sit.
- **Missing or stale generated markup makes the whole compile inconclusive**, exactly like an
  unbuilt reference: a page never built, or a `.xaml` newer than its partial, and the answer would
  be about the reference set, not the code. A resource dictionary (no `x:Class`) demands nothing.
- **After N identical refusals of one file, the gate stands aside** (`MaxIdenticalRefusals`,
  default 3): the write lands with a warning and the build adjudicates. The WPF blind spot is
  fixed where it lived, but the next blind spot will present identically, and its cost should be
  capped at N steps, not at the token budget. The countdown is said in the refusal itself, so the
  model can choose to run the build instead of trying an eleventh variation.
- **One tracker for create_file and edit_file together, keyed per run and per file** — the failed
  run alternated the two verbs against the same code-behind, and a per-tool count would never
  trip. A different error set restarts the count (the model exploring is information, not a loop);
  a landed write wipes the slate; rung 1 syntax refusals are never conceded, because a file that
  cannot parse has no blind-spot excuse.

**Open**

- The concession warning tells the model to run `build` next; nothing yet *makes* the next build
  report land with priority if it disagrees with the conceded write. Watch a live run.
- `Directory.Build.props` inheritance is invisible to both `UseWPF` and `ImplicitUsings`
  detection, in the same deliberately-conservative way.

---

## 2026-08-06 — The day the observations learned to tell the truth

**Shipped.** Seven commits (`5db383e`…`b7aa360`) from analyzing four live runs, each run exposing
what the previous fixes could not yet see. 583 tests green, +35 today.

**What the runs said, in order.**

- `d18c0e57`/`48a7af6a` (morning, 29+25 steps for one small task): half the steps were spent being
  misled - "Build failed with 0 error(s)" answered with blind identical retries, "0 of 0 tests
  failed" read as green, a `.sln` glob for a file .NET 10 had written as `.slnx`, and six
  discovery steps over a five-file workspace. The first run "Completed" over eleven red builds.
- `d21eb210`: hit CS0101 (its class colliding with the classlib template's namespace), received it
  as "1 error(s) across 0 file(s)", guessed wrong, **deleted the only copy of its deliverable**,
  went green over the empty template, and reported success on a file that never existed.
- `21f25fea`: refused one scaffold at the unwritable root, then cycled three read-only calls for
  25 steps of byte-identical answers - 100% tool-call validity all the way to the step limit.
- `d9c984cf` (headless, via `glasscoder run`): 12 steps, complete, both stubs handled - the
  first run of the day where every mechanism fired and nothing needed explaining.

**Decided**

- **The information has to be in the message the model is already reading** - the day's one
  principle, applied eight ways: raw output tail when the parser types nothing, zero-tests said
  loudly in both directions, the real solution path from `dotnet_project`, orphan files announced
  at `create_file`, ambiguous edits naming their line numbers, unknown tools suggesting their
  nearest real name, writable roots in the opening window, and the workspace map speaking even -
  especially - when the workspace is empty.
- **A suggestion the model reliably ignores is not a mechanism.** The test stub was named-not-
  deleted first ("replace it or delete it"); two runs read the warning and left it padding the
  pass count. Both template stubs now die at scaffold time, through the change log.
- **The located-diagnostic regex must survive parentheses in paths.** `[^(]` stopped at the "(" in
  `Dropbox (Personal)` - on this machine no compiler diagnostic had *ever* carried its file and
  line. This was the hidden half of "0 error(s)", and the reason `d21eb210` could invent a wrong
  theory unchallenged.
- **Progress-watching is one component, not ten flags.** `RunProgressSentry` owns the failure
  loop-breaker, the stall tracker, and the completion gate. The stall unit is deliberately the
  *step*, consecutive, not the call cumulative: a habitual status check beside novel work, or a
  re-read after compaction dropped the content, must never read as a stall. Its stops
  (`Stalled`, `RepeatedToolFailure`) are limit exit codes, not internal errors.
- **The harness excludes its own build output from Dropbox.** The launcher sweeps the launched
  folder at launch - wrong root and wrong time for a workspace the harness scaffolds into
  mid-run. `DropboxIgnoreMarker` rides the sandbox seam around every command, pre-creating marked
  bin/obj beside each project file; the transient-lock build flakes get a 1s in-tool retry as the
  symptom-side guard. Dropbox-side sync exclusions were deliberately left alone.
- **Green ≠ goal-met is not the completion gate's problem.** The gate challenges a stop over a
  red tree once, then records an insistent one. Judging whether a *verifying* tree also achieves
  the goal is the critique rung's designed role - enabling it is configuration spend, not code.

**Worth knowing**

- Both stall shapes are invisible to tool-call validity: the failed-loop scores 100% because the
  calls bind, the success-loop because they succeed. The transcript is where dead runs are found.
- Verification messages only ever reached the log, not the model, in three separate places before
  today ("Nothing buildable", stub existence, writable roots). Grep for `LogWarning` near
  model-facing moments when a run behaves as if it wasn't told - it probably wasn't.
- The headless surface (`glasscoder run --goal … --repo …`) is the fastest way to trial a harness
  change: same DI graph as the desktop app, exit codes, no UI in the way.

**Open**

- **This evening's trial** is the end-to-end validation: reset `GlassCoderTest`, rebuild, same
  goals. Watch for: the map's empty-workspace orientation replacing the 30-step spiral; stub
  deletions in the scaffold summaries; compiler diagnostics arriving *with file and line*; the
  sentry's nudge appearing only if the model actually stalls. The midday `tests/UnitTest1.cs`
  leftover predates stub deletion and vanishes with the reset.
- The critique rung stays off, so a green-but-empty completion is still possible - if the evening
  trial shows another goal regression, enabling critics is the next lever, and it costs tokens.
- `MaxStalledSteps = 5` is a first guess. A worker this small may need the nudge earlier or the
  ceiling later; the trial's transcripts will say.

---

## 2026-08-04 — The gate judged the fix against a library that no longer exists

**Shipped.** Three fixes from reading run `e8f9186a`. 538 tests green, +8.

**What the run said.** Goal: *"change the multiplication of the elements from value 10 to a
parameter set in the calling function"* — inherently a two-project change. 21 steps, 205k tokens,
stopped at TimeLimit with the workspace half-migrated. Step 6 changed the library signature,
verified against the library project alone, and passed. Then the model did the right thing eight
times — updated the test project's call sites, in the flat shape, the `edits` list, and finally
all six call sites in one call — and the write-time gate refused six of them with
`CS1501: No overload for 'SortAndMultiply' takes 2 arguments`.

The gate compiles the edited file against DLLs scavenged from the project's own `bin`, and the
test project's copy of `MyMathLib.dll` predated the signature change. That made the refusal a
**deadlock**, not a transient: the gate only believes the DLL, the DLL only refreshes on a
successful build of the test project, and the test project cannot build until the very edit being
refused has landed. No sequence of tool calls escapes. The model even tried — step 18 called
`build "."`, was told to use `list_projects`, never did, and went back to editing.

**Decided**

- **A gate that cannot know must not gate.** The scavenger now records each DLL's write time, and
  a reference older than any source of the project it was built from makes the compile
  *inconclusive* — the same answer an unbuilt reference already got, for the same reason: the
  diagnostics would be about the reference set, not the code. `EditFileTool` already lets
  inconclusive through with a note, so the fix is one new check, not a new pathway. It errs only
  in the safe direction: a wrongly-suspected reference costs one unverified write, never a wrong
  refusal.
- **The ladder builds what a library change can break, not what it cannot.** When one project owns
  the change, `ResolveBuildTarget` now finds the projects that transitively reference it and
  targets the dependent at the top of the chain — building it builds the owner first, so one
  target covers the affected closure. Two unrelated dependents fall back to the root solution;
  without one, the owner. At step 6 this would have said "5 call sites broke" instead of green.
- **`build "."` names the projects in the answer.** Pointing at `list_projects` is a hop the
  model demonstrably does not take. Same lesson as the `edit_file` hint repair: the information
  has to be in the message the model is already reading.

**Worth knowing**

- **This failure class is invisible to tool-call validity.** Every one of the eight failed calls
  parsed and executed; the run scores 1.00. What the metrics cannot see is a *correct* edit
  refused on stale evidence — worth remembering when a run looks clean in the numbers and dead in
  the transcript.
- The refusal's wording asserted the opposite of the truth ("it would break") — the same shape as
  `path_not_allowed`: a confident wrong reason sends the model somewhere it can never recover
  from.
- The staleness comparison leans on MSBuild's copy preserving write times, which is what makes
  "DLL older than the sources of its project" readable straight off the file system.

**Open**

- `GlassCoderTest` is deliberately left half-migrated — two-parameter library, one-parameter test
  calls — as a ready-made retry: the same goal again should now complete in ~10 steps, and is the
  end-to-end validation these fixes still lack.
- The deeper fix — compiling workspace `ProjectReference`s from source so the gate stays
  *authoritative* across projects instead of merely humble — was weighed and deferred until it
  earns its machinery (transitive references, caching).

---

## 2026-08-04 — The clock that lagged the run by one action

**Shipped.** The transcript's elapsed column now reads the run's clock at each step's *end*.
530 tests green — none added; the elapsed suite was re-pinned rather than grown.

**What was wrong.** `Elapsed` was `StartedAt − runStart`: the clock at the moment the step
*began*. But a row only appears once its step completes — the record is written after the tool and
the ladder — so the newest row always understated the run by exactly the action it had just
watched. With the worker at ~13 s a step, the transcript's bottom line was permanently a step in
the past, and the first row of every run read `0:00` after visibly working for a dozen seconds.

**Decided**

- **The anchor is `StartedAt + StepLatencyMs`.** `StepLatencyMs` is the whole step — model call,
  tool, verification ladder — because `AgentLoop` stamps it when it writes the record, which is
  also the moment the row appears. Clock and row now agree about what "now" is.
- **The origin stays the first step's start.** So a run's first row now reads its own duration
  rather than `0:00` — intended, not an off-by-one. Latency beside it still answers "how long did
  this one step take"; elapsed answers "how deep into the run were we when this landed".

**Worth knowing.** The old test helper's 120 ms step latency is invisible at second granularity,
which is why the original suite could not have caught this: pre- and post-action elapsed format
identically when the action costs nothing. The re-pinned first test uses multi-second latencies so
the two behaviors read differently; the other three cases (per-run restart, hour field, backwards
clamp) hold as they were.

---

## 2026-08-04 — The change log does not get to say what exists

**Shipped.** One guard in the workspace pane, three tests. 530 tests green, +2.

**What was seen.** A file the run deleted stayed in the tree — green, semibold, counts and all. It
was not actually *remaining*: it was removed correctly and resurrected seconds later, which is why
it read as odd rather than as broken.

1. `file_operation` deletes the file and marks its change Applied. The pane's `Record` runs first —
   change events post at Normal priority, the watcher's drain at Background — and paints the
   still-present node green.
2. The drain rescans the path, sees it is gone, removes the node. For a moment the tree is right.
3. The ladder finishes its build and `AgentLoop` re-updates every applied change of the step to
   attach the verification summary. `ChangeLog.Update` raises `Changed` on *every* update, "deleted"
   is not a concept `FileChangeSummary` has — a delete is just an Applied change whose after-text is
   empty — and `Apply` called `EnsureNode`, which creates whatever it cannot find. The dead file
   returns, ancestors forced open.

The drain takes milliseconds and the ladder seconds, so step 3 lands after step 2 every time —
deterministic with `VerifyAppliedChanges` on, which is how it runs. The same mechanism resurrected
the old path of every move (its removal change gets the same re-update), and Refresh brought the
whole set back again through `Summarise`.

**Decided**

- **The change log colours the tree; existence is the watcher's to say.** That was already the
  stated design — "two sources answering two different questions" — but `Apply` → `EnsureNode` let
  one source answer the other's question. The fix is one gate: `Apply` returns before marking when
  the file is not on disk. The live path and the Refresh replay both go through it, so the delete
  ghost, the move ghost and the Refresh ghost close together.
- **Creation is unaffected because every tool writes before it records Applied.** Which is also why
  `A_change_to_a_file_the_tree_has_not_seen_creates_it_expanded` had to change: it recorded a change
  for a file that never touched disk — a shortcut no tool takes, and exactly the gap the bug lived
  in.

**Worth knowing**

- The regression tests replay the sequence rather than assert the summary: apply, delete on disk,
  pump until the watcher has dropped the node, then re-raise the change the way `AgentLoop` does.
  Raised on the dispatcher thread, `Record` runs synchronously, so the assertion needs no second
  pump.
- The suite ran in Release because the app was open — watching this bug, fittingly — and holds the
  Debug binaries. The open instance keeps the ghost until it restarts.

**Open** — the rest of the same review, seen from the operator's chair and not addressed here:

- The run latch takes the first `Changed` event of *any* run id after Run is pressed. A human revert
  in the gap before the run's first change latches the wrong run, and the whole run paints nothing.
- Every applied change re-expands the folders above it, so collapsing a folder mid-run is futile.
- A deletion leaves no trace in the run's story — the file just vanishes, with the −N nowhere.
  Whether deletions deserve their own visual (a struck-through row?) is undecided.
- Refresh rebuilds every node: expansion choices, selection and scroll are lost — and a watcher
  buffer overflow triggers that rebuild unprompted, because the deny globs filter events after the
  OS buffer, not before it.

---

## 2026-08-04 — The run that failed on the tool I had just reshaped

**Shipped.** Three fixes to `edit_file`, from reading run `9fad0808`. 528 tests green, +5.

**What the run said.** Same goal as `8a77ee00` four hours earlier, same model, one difference: batch 2
had made `edit_file` take a list of edits and nothing else.

| | `8a77ee00` (12:06) | `9fad0808` (13:26) |
|---|---|---|
| Steps | 19, Completed | 22, **Cancelled** |
| Tokens | 117,319 | 157,355 |
| Tool-call validity | **1.00** | **0.86** |
| Tests written | 5, passing | 0 |

0.86 is the worst of the twelve runs in `metrics.jsonl`; the next worst is 0.96. Steps 0–13 were
good — it scaffolded a classlib, **used `file_operation Move`** to relocate the source into it (the
first real use of that tool), built green, scaffolded xunit, added the reference. Then eight
consecutive steps on `edit_file`, cycling three shapes:

- Steps 14, 17, 20: the flat `{path, oldText, newText}`. Step 14 was the run's *first* `edit_file`
  call, so it was not copying a bad example from context — that is what the model does unprompted.
- Steps 15, 21: `edits` with `path` left at the top level and omitted from each edit. The harness
  answered `path_not_allowed: Path is required.`
- Step 18: the one well-formed call, correctly refused for `CS1513: } expected`.

One correct shape in six attempts, and `UnitTest1.cs` was still the untouched template stub.

**Decided**

- **The flat shape is primary again, with `edits` alongside it.** Six runs at 1.00 validity say
  `edit_file(path, oldText, newText)` is what this model emits; the list stays for the multi-file
  case that motivated task 46. This is the same lesson line-ending tolerance taught: *a shape the
  model does not reliably produce is a contract the harness should not insist on.* I had the
  precedent in hand and went the other way with it.
- **A top-level `path` fills in for edits that omit it.** Not politeness — the information was there
  five times over and the harness refused on a technicality.
- **`path_not_allowed` was the wrong code and that is why the run never recovered.** It sent the
  model to inspect the writable set instead of its own arguments. Malformed calls now answer
  `invalid_argument` with both shapes spelled out in the hint.

**Worth knowing**

- **I optimised the number I could measure.** The batch-2 argument was that a second tool costs ~880
  schema characters, about 150 tokens a request. The reshape cost ~40,000 tokens and a cancelled run
  on one task. `PromptBudgetTests` measures prefill; it cannot see whether the model can drive the
  schema at all, and that is worth more than the characters. Said so in the test, next to the number.
- Declaring both shapes cost 414 characters, paid for by dropping the descriptions duplicated between
  the flat parameters and `FileEdit`'s properties — the same text was on the wire twice — and by
  trimming the six git tools, which nothing had touched yet. **13,687 total, below where batch 2 left
  it.**
- The new tests go through `ToolRegistry.InvokeAsync` with argument bags rather than calling the
  method, because binding is where the run failed and a direct call would prove nothing. One of them
  pins that a double-encoded `edits` string still binds, which the model did three times.

**Confirmed by the re-run** (`6b9118b8`, then `12df72e1`, both Completed at 1.00 validity).

`12df72e1` is "add unittests" — the task that produced the first failure ever analysed here. Step 8
is `edit_file` in the flat shape, parsed, succeeded, first attempt: byte for byte the call that
failed six times the run before.

| Run | Steps | Outcome | Tokens | Validity | Tests |
|---|---|---|---|---|---|
| `bee2e874` | 30 | StepLimit | 257k | 1.00 | 0 |
| `bb0af8f6` | 30 | StepLimit | 220k | 1.00 | 0 |
| `9fad0808` | 22 | Cancelled | 157k | 0.86 | 0 |
| `12df72e1` | **11** | **Completed** | **57k** | 1.00 | **6 passing** |

Six tests, all green, verified against the working tree rather than taken from the transcript.

**The diagnosis needed one correction.** Both runs sent `update_todos` with `items` as a
*double-encoded JSON string* — `{"items": "[{\"id\": ...}]"}` — exactly as they had sent `edits`, and
it bound without complaint, as it always has. So the model can produce array parameters. What it
could not do was stop producing the flat shape it already knew. **The failure was the absence of the
familiar shape, not the presence of the array** — which is a more useful rule than "arrays are hard",
and a narrower one: an array is fine as an *addition*, and dangerous as a *replacement*.

**Worth knowing, second pass**

- Neither run called `build` or `run_tests`. The ladder ran them and reported back, which is what
  `builds: 1, testRuns: 1` in the metrics means against a trace containing neither. The agent's
  closing "All tests pass" was grounded: after step 8 it received *"Automatic verification of your
  change passed (reached UnitTests)… 6 tests passed."* I had it down as a possible hallucination
  before checking the prompt.
- `update_todos` is being used as ceremony. Both runs called it once, at the very end, with a single
  item already marked `Completed`. It is the most expensive schema in the harness at 1,186
  characters and it is writing a receipt.
- Steps 6 and 7 of `12df72e1` are the same `read_file` twice in a row. Ten seconds, and the kind of
  thing that was invisible before argument logging.

**Open**

- **Batch 2 remains entirely unproven.** No run has yet called `find_symbol`, `read_file(outline:)`,
  `run_tests(listOnly:)` or `list_changes`. What these two runs exercised was task 44/45 work —
  `dotnet_project`, `create_file` overwrite, the MSB1003 hint — plus the `edit_file` repair. The
  harness is measurably better than it was before batch 2, and none of that improvement is batch 2's.
- `update_todos` is the next thing to weigh against its cost, on the evidence above.
- `GlassCoderTest` is clean again: `src/MyMathLib` and `tests/MyMathLib.Tests`, six passing tests, no
  stubs left over. The `ArrayUtils` wreckage from the cancelled run is gone.

---

## 2026-08-04 — Batch 2: four capabilities for forty-five tokens, and the whitespace nobody was counting

**Shipped.** Tasks 46, 47, 51 and 52, plus decisions recorded for 48 and 53. 523 tests green, +35.

**The number that matters.** The advertised schemas went from **13,547 to 13,726 characters** — about
45 tokens per request — and bought multi-file edits, file outlines, symbol search, test discovery and
a formatting verb. The previous entry left this with ~450 characters of headroom and an instruction:
*the next tool to be added should trim something, not raise this again.* It was followed.

**Decided**

- **Three of the five arrived as parameters, not tools.** `file_outline` became
  `read_file(outline: true)` (150 chars, not ~450). `list_tests` became `run_tests(listOnly: true)`
  (113, not ~450). `dotnet format` became a `DotnetProjectOperation` (37). Each is the same request
  at a different setting, and a setting is a flag.
- **`apply_patch` did not ship; `edit_file` grew a list instead.** Two tools doing the same thing at
  different arities is exactly the pattern the budget test exists to catch — `edit_file` was 901
  characters and a second tool would have been ~880 more, on every request of every run. Reshaping
  the one tool cost 373. A single edit is a one-element list, and the multi-file path is now the
  default rather than an alternative the model has to notice.
- **The workplan's "one approval for the batch" was rejected on inspection.** `RequestActionAsync`
  is governed by `RequireApprovalForPush`, so routing file writes through it would have put them
  behind the *push* switch — a safety setting silently doing something other than what it says. And
  the prompt shows a diff: a reviewer approving three files should see three diffs. Approval stayed
  per file, which also means refusing one file still lets the rest land.
- **`find_references` is not being built** (task 48), and the reason is the asymmetry with its
  sibling. `find_symbol` reads the syntax tree, so its worst failure is "not found" for something
  that lives in a package. `find_references` needs semantics, and on a reference set scavenged from
  `bin/` its worst failure is **"nothing calls this" when something does** — which an agent acts on
  by deleting live code. The two look alike; their failure modes are not comparable.
- **Package knowledge (task 53) waits for record/replay.** No transcript analysed so far shows a run
  failing on a package version — the failures were line endings, a missing solution, and a tool that
  could not scaffold. Shipping `nuget_info` live "for now" would put a network call in the loop and
  quietly break the Lab's ablations, which is the failure the task was written to avoid.

**Worth knowing**

- **`AIJsonUtilities.DefaultOptions` writes indented, and it was serialising every tool observation
  fed back into the conversation.** Unlike a schema — re-sent once per step — a tool result is
  written *into* the conversation and then carried for the rest of the run, so a grep returning
  forty matches paid for its own whitespace on every subsequent step until compaction. Fixed by
  copying the options with `WriteIndented = false`; `PromptBudgetTests` now measures it.
- **About a fifth of the "schema budget" is whitespace we do not control.** `update_todos` is 567
  characters leaving `AIFunction.JsonSchema` and 1,186 on the wire: the OpenAI client re-serialises
  the schema through the library's own indented options. Worth knowing before someone reads a number
  in that test as prose they can shorten.
- **What got cut to pay for this was rationale, not guidance.** `list_projects` used to tell the
  model it "answers in one step what globbing for *.csproj answers in four". That sentence was for a
  human reading the source, and it was being re-sent on every request of every run. Eleven
  descriptions were trimmed the same way; `build` lost a third of its size and none of its meaning.
- The Lab's Phase 1 checkpoint drives `edit_file` through the registry with scripted JSON, so its
  fixtures now carry the nested `edits` array. That it passes is the end-to-end evidence that the
  new wire shape deserialises — worth more than a unit test of the same thing.

**Open**

- `find_symbol` is the one new tool name (531 chars) and it has not yet been called by a real run.
  `file_operation` and `list_changes` from the previous batch are still in the same position. If a
  run finishes without touching any of them, that is worth reading as evidence.
- `dotnet_project Format` snapshots up to 500 sources to work out what the SDK rewrote, because the
  SDK will not say. Fine for a project, wasteful for a solution.
- The tutorial docs describe an eight-tool harness; there are now thirteen without git.

---

## 2026-08-04 — The workspace pane stops reporting the change log and starts reporting the workspace

**Shipped.** Three asks from watching the pane during a run, all in `WorkspaceViewModel`. 488
tests green, +10.

**The bug behind all three.** The tree was built from the change log, so it showed what the
*harness had recorded*, not what the workspace *held*. `dotnet new` writes three files and
`DotnetProjectTool` records one — the other two existed on disk and nowhere on screen until
someone pressed Refresh. And the green was per-session, so the previous run's colouring was still
on the tree while the next run was being read.

**Decided**

- **Two sources, answering two different questions.** A `FileSystemWatcher` says what the
  workspace *contains*; the change log says what *this run did to it*. Keeping them separate is
  what lets a file appear the moment its path exists — whoever made it, and whether or not
  anything has finished writing to it — while green still means "this run touched it" and nothing
  weaker.
- **Watcher events are "look at this path again", not facts.** The drain asks the file system
  what is actually there rather than trusting the event's own verb. That single choice is what
  makes create-then-delete, rename, and a file still open for writing all come out right with no
  special case for any of them. Names are watched, not content: a create, a delete and a rename
  change the shape of the tree, and a write does not.
- **Deny globs are applied on the watcher thread, before anything is posted.** A build writes
  thousands of paths under `bin/` and `obj/`; forwarding them would cost thousands of posts to
  the UI thread to decide thousands of times to do nothing. The drain is posted once per burst at
  `Background` priority for the same reason — a checkout is one tree edit, not ten thousand.
- **The run id is latched, not passed.** `BeginRun()` runs when Run is pressed, and at that
  moment there is no run id: the loop mints it. So the pane clears the marking and latches onto
  the first change the run produces, ignoring every other run's. The alternative — plumbing a run
  id from the loop back into a view model — would put harness bookkeeping in the pane's
  constructor to learn something the first change already carries.
- **Folders start open.** A tree that starts closed shows one row per top-level folder, which is
  a pane whose whole purpose is hidden behind a disclosure triangle.

**Worth knowing**

- `ChangeLog.Update` preserves a change's original run id. That is what makes reverting this
  run's work still unmark the file, however long after the run it happens — the per-run filter
  would otherwise drop the revert on the floor and leave the file green forever.
- `Remove` drops the whole subtree from the index, not just the node. An index still holding
  nodes nothing can reach would let a recreated file adopt the stats of the deleted one.
- `OnChanged` now records inline when it is already on the UI thread. The Changes surface raises
  changes from the UI thread on a manual apply or revert, where the dispatcher hop only meant the
  tree lagged its own window by a turn.
- **`Dispatcher.CurrentDispatcher` gives you a queue with no loop.** Anything posted to it sits
  there forever unless something pumps — fine for the composition tests, which post nothing, and
  the whole difficulty for anything watcher-driven. `UiThread.Pump` pushes a frame and posts its
  own exit at `Background`, so it drains what is already in front of it. Ten tests driving the
  real watcher over a real temp directory run in ~600 ms and were stable over five consecutive
  runs before the suite was trusted.

**Open**

- The tree removes a node when its file leaves the disk, including a file the run deleted on
  purpose. That is right — the tree shows the workspace, and the Changes surface is where
  deletions live — but it means `file_operation delete` is invisible in the pane. Worth revisiting
  if it reads as a lost change rather than a completed one.
- Batch 2 of the tool work (`apply_patch`, `find_symbol`, `read_file(outline:)`) is still planned
  and unbuilt, still needing ~1,700 characters of schema against ~450 of headroom.

---

## 2026-08-04 — The first run that finished, and what the budget test said about tools

**Shipped.** Tasks 49 and 50, and two defects found by reading a run that *worked*. 478 tests
green across all five assemblies — the WPF project rebuilt and is verified against this tree
again, which it had not been since the previous entry.

**The run.** `8a77ee00`: **19 steps, Completed, 117k tokens, 4m15, zero failed tool calls.** The
goal was a function that sorts doubles descending and multiplies by six; it produced that, a test
project, five passing tests and a solution that builds. Set against the two before it — 30 steps
to StepLimit with 257k and 220k tokens, and no tests written at all — the harness work is what
changed, because nothing about the model did.

Three things the transcript proves rather than suggests:

- **Line endings.** Step 5's `oldText` is `namespace MyMathLib;\n\npublic class Class1\n{\n\n}` —
  LF — against a file `dotnet new` wrote as CRLF. That is byte-for-byte the call that failed
  seventeen consecutive times a run earlier. It succeeded first try.
- **Argument logging.** That `oldText` was read straight out of the log. Two analyses before it,
  the same argument had to be inferred from bytes on disk.
- **The MSB1003 message steered the agent.** Step 12 built `"."`, got back *"'.' is not a project
  or solution… use list_projects to see what this repository holds"*, and answered it by creating
  a solution at steps 13–15, which made 16 and 17 work. The harness taught the model its way out
  of a dead end. That is what "errors are observations" is supposed to look like.

**Decided**

- **Nine proposed tools became four schemas, because the schemas were measured.** The outside
  review's P0–P2 list would have added nine top-level tools. Step-0 requests across five runs put
  a tool at roughly 300 tokens, re-sent every call, against an assembled conversation of about
  130 — schemas are **96% of a step-0 request**. Nine would have cost ~2,700 tokens on every
  step of every run. So `delete_file`/`move_file`/`revert_file` became verbs on one
  `file_operation`, `list_tests` will be a flag on `run_tests`, and formatting a verb on
  `dotnet_project`. Capability belongs on the tools that already exist. This is the same
  reasoning `dotnet_project` itself was built on.
- **`PromptBudgetTests` caught it, and its instruction was followed rather than its number
  edited.** The test says the question on failure is not "what should the number be" but "is this
  tool worth 200 tokens on every step of every run". Answering it found that
  **`dotnet_project` was the most expensive tool in the harness at 1,818 characters** — larger
  than `update_todos`, and written by this project three days ago. Trimming it and three other
  descriptions took the total from 14,448 to 13,547, about 225 tokens off every step. Only then
  was the ceiling raised, to 14,000, with the new per-tool figures written into the test.
- **A successful run is worth reading too.** Both defects fixed this session came out of the run
  that finished, not one that failed. `NewSolution` given `src/MyMathLib.sln` built a *directory*
  of that name holding `MyMathLib.sln.slnx` — it worked by accident, and nearly-right that
  survives a whole run is the kind that reaches the next person unexamined. And a build that hit
  MSB1003 and compiled nothing logged as `build:Succeeded`, because the *call* had succeeded.

**Worth knowing**

- `ToolCallStatus` is about the call, not the outcome, and a failed build deliberately returns
  `ok: true` — a handled outcome is not a tool fault. That is right, and it made the console line
  misleading, so the step line now carries the observation's own summary. It is a separate field
  on `ToolCallRecord` rather than parsed back out of `Result`, because content redaction blanks
  the result entirely and would take the summary with it — losing the line exactly when the log
  matters most.
- **`file_operation` and `list_changes` were registered and never called** in that run. They cost
  about 174 tokens on every request and returned nothing. The task needed neither; the
  nested-project hazard is what `move` exists for. Worth recording rather than leaving behind a
  claim of usefulness the transcript does not support.

**Open**

- Batch 2 — `apply_patch`, `find_symbol`, `read_file(outline:)` — is planned and unbuilt. It has
  about 450 characters of schema headroom and will need roughly 1,700, so it has to pay for
  itself: `update_todos` (1,356) and `grep` (1,191) are the untouched candidates.
- `AddToSolution` takes one project per call; three steps of the run were one operation.
- `GlassCoderTest/src/MyMathLib.sln/` still holds the misshapen solution the fixed bug produced.
- Tasks 48 and 53 remain decisions rather than work: `find_references` needs a real MSBuild
  workspace or it inherits the false-negative problem, and package knowledge is not worth
  shipping without record/replay.

---

## 2026-08-04 — Two failed runs, and the six-plus-five fixes they bought

**Shipped.** Tasks 44 and 45, both written from transcripts rather than from guesses. 425 tests
green across Core, Tools, Models and Lab. The 34 WPF tests could not be rebuilt at the end of the
session — the running app and Visual Studio hold `GlassCoder.Core.dll` and `GlassCoder.Tools.dll`
in the WPF output folder — so they are **unverified against this tree**. Close both and rebuild
before trusting a run: the binaries in `bin/Debug/net10.0-windows/` are older than this commit.

**The method is the point.** Both tasks came from reading `glasscoder-20260804.jsonl` for a run
that failed, finding the step where the harness rather than the model went wrong, and fixing that.
Every change below names the step it came from. This is the first time the transcript has paid for
itself as a design input rather than as a debugging aid, and it is worth keeping up.

**Run one** (`bee2e874`, 30 steps, 257k tokens, StepLimit) was asked to add unit tests to a tree
whose projects live under `src/` with no root solution. It produced **no tests at all**, with
tool-call validity at 100 %. The model was never at fault — it wrote a correct test project and
the harness refused to let it write a single test file. Four causes, all harness:

- **The pre-write compile gate rejected correct code.** `RoslynCodeAnalyzer` scavenges its
  reference set from build output rather than evaluating the project file, so a project whose
  dependencies have not been built yet reports CS0246 for every type it imports. `create_file`
  turned that into "nothing has been written", three times, on a file whose `using Utils;` was
  right. It now returns `Inconclusive` when it can see its reference set is incomplete — the
  machinery already existed and simply was not used for this case.
- **The compile rung built `"."`, always.** `AgentLoop` never set `VerificationRequest.ProjectPath`
  and the record's default was `"."`, so every edit was followed by `MSB1003` in 330 ms — while
  the `build` *tool*, which takes a path, succeeded on the same tree in the same run. New
  `ProjectLocator` resolves owning project → root solution → sole project → nothing, and nothing
  means skip rather than fail.
- **Nothing could manipulate a project.** Scaffolding meant hand-writing `.csproj` XML through
  `edit_file`, and one of those edits failed outright. New `dotnet_project` wraps the SDK.
- **Eight builds in thirty steps**, three consecutive with no edit between them. New `BuildCache`,
  invalidated by the change log and by hand from `dotnet_project`.

**Run two** (`bb0af8f6`) cleared all of that — `list_projects` at step 1, `dotnet_project` at step
2, a real compile rung passing in 2.2 s — and then failed anyway, on **seventeen consecutive
`edit_file` failures against a seven-line file**. The file came from `dotnet new` and held CRLF;
the model emitted LF; the match was ordinal. Nothing the model could have done would have worked.

**Decided**

- **A tool that promises exactness must be fed by a tool that delivers it.** The real defect was
  not the ordinal match on its own — it was that `read_file` read with `ReadAllLines` and rejoined
  with `Environment.NewLine`, handing the model a *reconstruction* while `edit_file` demanded the
  original. Fixing only the matcher would have left the contract broken in the other direction.
  Both were changed together, and `read_file` now reports `LineEndings` and `ClippedLines`.
- **Match flexibly, write consistently.** Normalising for the match is half the fix. The one edit
  that did land in run two left a bare `\n` inside a CRLF file, so replacements now adopt the
  file's own ending.
- **`create_file` gained `overwrite: true`, and the doc comment that said it never would is gone.**
  Creation and modification stayed separate verbs for a long time on purpose, but that left no way
  to replace a generated stub — which is exactly the trap run two never escaped. Explicit, defaults
  to false, and goes through the same change log, pre-write check and approval gate.
- **A valid call that cannot be satisfied has to count toward something.** `MaxConsecutiveInvalid
  ToolCalls` counts calls the registry could not *bind*; these bound and executed perfectly.
  Nothing counted them, which is how a run ground through seventeen with validity reading 100 %.
  New `AgentStopReason.RepeatedToolFailure` and `Agent:MaxIdenticalToolFailures` (nudge at 3, stop
  at 8), kept deliberately distinct so the validity metric stays honest — a test asserts the rate
  is still 1.0 when the new limit trips.
- **The step budget is told to the model, once.** Run one spent its last five steps rebuilding an
  unchanged tree; it could not pace itself against a ceiling nothing had mentioned. Sent at a
  quarter remaining, floored at three, because repeating it would spend the budget it warns about.

**Worth knowing**

- **Tool-call arguments were not in the transcript.** They arrive as `JsonElement`, whose public
  surface is its kind rather than its content, so the log recorded `{"ValueKind":"String"}` and the
  value was gone. Two diagnoses in this session had to infer an argument from bytes on disk before
  `ToolRegistry` was taught to unwrap them. CLAUDE.md §9 has required this all along.
- **`CompileAsync` had only ever been handed a directory.** Now that it receives a resolved build
  target it can get a `.csproj`, and enumerating a file as a directory throws. Caught by the test
  for the `ProjectPath` override, not by a run.
- **`ConfigurationBinder` appends to get-only collections rather than replacing them**, so
  `FileReviewOptions.AllowedTools` is settable — otherwise "restrict the reviewer to Read" would
  silently leave Grep and Glob on.

**Open**

- `GlassCoderTest` carries damage from run two: `src/MyMathLib/Class1.cs` has mixed line endings
  and a stray `// test` comment. Fine as a test of the new matcher, not a clean starting point.
- The nesting hazard `list_projects` now reports — a project directory containing another project,
  so the SDK glob compiles the inner sources into the outer — is *diagnosable* but not fixable by
  the agent, which has no move or delete tool. That is the next gap.

---

## 2026-08-04 — A second opinion on one file, from headless Claude Code

**Shipped.** Task 43: a Review button on the file viewer that asks headless Claude Code what is
wrong with the file, shows the report beside the code, and writes the actions you tick to a
Markdown work order under `.glasscoder/reviews`.

**Decided**

- **A subprocess, not the model seam — the one deliberate exception to CLAUDE.md §4.** The seam
  sends one file's text. A review of `WorkspaceViewModel.cs` that cannot open `WorkspacePane.xaml`
  cannot tell whether the command it is reading is bound to anything. The CLI brings its own agent
  loop and file tools, so the reviewer opens the callers, the types and the tests first. The
  alternative was considered and rejected on that single ground.
- **The allow-list is the safety argument.** `--allowedTools Read,Grep,Glob` with
  `--permission-mode plan`: the subprocess can read and search the workspace and can do nothing
  else to it. It runs outside the sandbox for the same reason `GitTool` does (task 40) — the
  sandbox has neither network nor credentials. `FileReviewerTests` asserts the allow-list so a
  tool cannot quietly join it.
- **The two-field answer is enforced by `--json-schema`, not asked for in prose.** A prompt-JSON
  fallback stays for older CLIs. Verified against 2.1.221: `--json-schema` is present,
  `--max-turns` is *not*, so the run is bounded by `--max-budget-usd` and the process timeout.
- **Not gated on `Critique:Enabled`.** That ships false, and sharing its switch would grey the
  button out on every fresh install for a feature that is not part of the verification ladder.
- **The API key is not injected unless configured.** The CLI has its own credentials; handing it a
  key it did not ask for silently moves where the run is billed.
- **Ported from `ClaudeContextGenerator3`'s `IntentAgentRunner`** rather than designed fresh — the
  CLI probe, the argument assembly, the envelope parsing and the stderr redaction are all proven
  there. Worth remembering that sibling exists.

**Worth knowing**

The work-order format writes *every* proposal with `[x]` marking the accepted ones, not just the
accepted ones. The rejected proposals are the context that explains the accepted ones, and the
consumer's rule stays "do the ticked ones". `ReviewActionFile.TryParse` ships with the renderer so
the round-trip is provable now, while the format is still cheap to change.

**Open**

Nothing consumes the work order yet — composing a goal from the ticked items is a separate task,
deliberately left until the format settled. And **a live review has still never run end to end**:
the sandbox blocks the CLI's OAuth refresh, so it returns `Not logged in`. The failure path is
proven; the success path is not.

---

## 2026-08-03 — An icon, and the generator that made it

**Shipped.** The app had no icon at all - no `.ico`, no `ApplicationIcon`, no `Icon` on any
window - so every title bar, the taskbar and Alt-Tab showed the default shell icon.
`src/GlassCoder.Wpf/Assets/glasscoder.ico` now carries the mark at ten sizes, and
`tools/IconGen` is the source that renders it. 361 tests green.

**Decided**

- **The mark is drawn from the house language rather than invented.** The kintsunai logo is
  kintsugi: pale ceramic plates joined by gold seams. The icon is a pane of glass whose fracture
  has been filled with gold, where the seam takes the shape of a terminal prompt, `>_`. Glass
  because the loop is meant to be visible, gold in the seam for the house, the prompt because
  without it a bare chevron is read as an arrow - which is not a guess, it is what the first three
  renderings actually looked like. The rejected passes are written down in the tool's README so the
  next person does not repeat them.
- **Each size is rendered, not scaled.** One 256px bitmap downsampled to 16 is a grey smudge. Every
  size comes from the same unit-square geometry with its own weights, and detail drops out as it
  shrinks: gloss below 24px, keyline below 32, molten highlight below 96, and the vein taper
  flattens toward uniform so the tips do not thin to nothing.
- **`ApplicationIcon` alone, no XAML.** A WPF `Window` with no `Icon` of its own falls back to the
  executable's, so both windows follow from one line in the csproj and neither has to name the file.
- **The generator is in the tree but out of the solution.** A committed binary with no source is
  unmaintainable - no new sizes, no adjustments. But it builds a brand asset that changes about
  once a year, and putting it in `GlassCoder.sln` would run it through every build and every CI
  pass for nothing. `GlassCoder.sln` lists projects explicitly, so `tools/` is simply never built.

**Worth knowing**

`System.IO` is *not* in the implicit-using set for a `UseWPF` project, though it is for a plain
console one. Stripping it as redundant is a build break that only shows up on the WPF-flavoured
project.

---

## 2026-08-03 — The drafter role, retired to a comment

**Shipped.** `Models:Roles:drafter` is commented out in `config/appsettings.json`
rather than deleted, and the DGX Spark guide no longer contradicts itself about
what standing one up would cost. No code changed; 361 tests green.

**Why it was ever there**, because the trace is not obvious from the source and
took a git archaeology pass to recover. `drafter` entered in the first
implementation commit (`1382d58`, tasks 1–10) because workplan task 4 said "one
per served role (`worker`, `drafter`, `critic`)" — and that phrasing came from
the hardware plan in `LocalAICodingAgent-LearningGuide.md`, where a drafter is a
real thing: the 480B model that handles planning and hard edits in an 8-GPU
serving layout. It was a *serving* role that got copied into a *harness* role
list. `ModelRoles.cs` has exactly one commit in its whole history, and this file
had never once mentioned the role — that silence, next to `critic-remote`'s four
entries, is the tell. One was built; the other was only declared.

**Decided**

- **The reason to retire it is the connection check, not the memory.** Clients
  are built lazily (`GetOrAdd` inside `GetClient`), so a configured-but-uncalled
  role never opens a socket and never costs a byte. What it did cost was
  `TestAllAsync`, which probes *every* configured role: with nothing serving
  :8002, "Test all" reported a standing failure forever. A check that is always
  partly red is a check that stops being read, which defeats the reason it was
  built. That is the whole argument — "it wastes memory" was never true.
- **Commented, not deleted.** The block is the only place a reader learns the
  intended shape and that :8002 is the port the serving layout reserves, and
  ladder phase 4 still plans to point the harness at a larger drafter. Deleting
  it would move that knowledge into prose only. The binder skips comments, so
  the cost of keeping it is zero.
- **`ModelRoles.Drafter` stays.** Nothing references it, but it is a documented
  `const` that costs nothing, and `ModelRoles` already states that config may
  define any number of roles and that none of these are required. Removing it is
  a source-breaking change to a public surface that buys nothing.
- **"Remove it from the UI" turned out not to be a UI task, and that is the
  design working.** The settings dialog has no hardcoded role list — `BuildRoles`
  enumerates `Settings.Models.Roles`, and `RoleNames` feeds the three editable
  combo boxes. Dropping the config entry removed the row and all three dropdown
  entries with no XAML, ViewModel or test change. Worth writing down because the
  next role question will sound like a UI change too, and won't be one.

**The thing that will surprise you later**

Editing `appsettings.json` does **not** remove the drafter from a machine that
has ever saved settings. Configuration layering is additive per key — it merges,
it never deletes — and saved settings sit *above* `appsettings.json`, while
`CollectRoles` rewrites the full `Roles` dictionary on every Save. So any
`settings.json` written before today still carries a complete drafter block and
will keep showing the row. Clearing it is the dialog's **Remove** button (enabled
whenever more than one role exists) or **Reset**. This cuts both ways: it is also
why a user who *wants* the drafter never needed us to ship it configured.

**The guide's contradiction, and what replaced it**

`dgx-spark-setup.html` said in section 4 that a second machine serving `drafter`
needs "one line of configuration and no code change", and then said in a caution
six days newer that nothing in the harness calls it. Both were still in the file.
The claim is true of relocating a role the harness *does* call, and false of this
one. It now says so explicitly: the seam is open at the configuration layer and
not at the call layer, so standing up a drafter is a harness change, not an
endpoint change.

**Open**

What a drafter is actually handed, and when. That is the missing call site and
the real content of ladder phase 4 — a decision about how work is split between
a fast worker and a slow strong model, which is exactly the kind of thing the
metrics exist to answer. Until it is made, the alias stays commented.

---

## 2026-07-28 — Settings that travel: per project, and between machines

**Shipped.** Three files now carry settings instead of one pair. The per-user
`settings.json` / `secrets.json` are unchanged; a project can carry
`.glasscoder.json` at its root; and `Export…` / `Import…` move a whole
configuration as a `.glassconfig` file with the API keys re-encrypted under a
passphrase. 361 tests green.

**Decided**

- **Copying settings never needed a feature; carrying the keys did.** The
  settings folder was already openable and `settings.json` is already plain
  JSON, so an export button over that is a wrapper around Explorer. The thing
  file-copying cannot do is the keys: DPAPI ciphertext is bound to one Windows
  account, so a copied `secrets.json` arrives decrypting to nothing. That is the
  whole justification for the export format, and it is why the passphrase is not
  optional decoration.
- **There is no "include keys in plain text" option.** Empty passphrase means
  the keys are left out. An export is exactly the kind of file that gets
  attached to a message, and a file that quietly contains a usable credential is
  how keys reach places nobody meant to send them.
- **AES-GCM rather than CBC, and PBKDF2 at 600k iterations.** GCM authenticates,
  so a wrong passphrase is *reported* rather than yielding a plausible wrong key
  that only fails later at the endpoint as a puzzling 401. All values failing to
  decrypt is therefore a certainty about the passphrase, not a guess.
- **A section belongs to the project when its values name things inside the
  repository.** Workspace, Context, Verification, VerificationLadder, Git,
  Provenance. Everything else — the served roles, the sandbox, budgets, sinks —
  describes the machine or the experiment and stays where one copy serves every
  project. One rule, not a taste, so the next section added has an obvious home.
- **The project file never carries a key, unconditionally.** Not "only when one
  is set" — the strip runs whether or not there is anything to strip, because
  `.glasscoder.json` is one `git add` from being public and a conditional is a
  thing a caller could get wrong.
- **It omits `Workspace:RepoRoot` too.** The file's location *is* the root, so an
  absolute path inside it would only be a way to be wrong after somebody clones
  the project elsewhere. The configuration layer supplies the containing
  directory instead.
- **The project layer sits in the same band as saved settings** — above the
  machine, below the environment and `--config`. A project is a saved preference
  like any other and must not redefine what an arm means.
- **Import populates, it does not apply.** The imported keys go back under DPAPI
  through the ordinary `Save`, not through a second write path that would have
  to get the same thing right twice. The workspace root is deliberately not
  imported: a path from another machine names nothing here.
- **`SettingsDocument` exists so that lifting keys out of a document has exactly
  one implementation.** Three writers now produce settings files; a second copy
  of that step would be a second chance to leave a key in one.

**The bug worth remembering**

`AddJsonFile` given an absolute path resolves a `PhysicalFileProvider` with
`ExclusionFilters.Sensitive`, which **refuses to serve dot-prefixed files**. So
`.glasscoder.json` was skipped — and skipped in silence, because the source is
optional. No error, no log line, no file: the feature simply did nothing. The
provider is now constructed explicitly with `ExclusionFilters.None`.

This was caught only because the layering test asserted the project value
actually won, rather than that the file had been written. A test that stopped at
"the file exists" would have passed.

**Verified**

Beyond the 13 new unit tests, the dialog was driven in the real window through
UI Automation: `Save to project` wrote a file whose sections were exactly
`Workspace, Context, Verification, VerificationLadder, Git, Provenance` with no
`Models`, no `ApiKey` and no `RepoRoot`; `Export…` produced `aes-gcm-pbkdf2` at
600000 iterations with no plaintext key; and that app-produced file was then
imported back to the exact key the app held — confirming the passphrase typed
into the `PasswordBox` is the one that opens it.

**Open**

- The eight tab screenshots in `docs/settings.html` predate the three new footer
  buttons. A separate footer figure documents them; regenerating all eight would
  mean re-tuning roughly thirty hand-placed callout percentages.
- `--config` is still ignored by the WPF app. The console host parses it
  (`CommandLine.cs`); `App.xaml.cs` passes `e.Args` but never a `configPath`.
  Pre-existing, and a one-line fix.
- Removing a *default* list entry still does not survive a reload — the binder
  appends to lists, so `appsettings.json` reasserts it and the deduplicating
  reader can only collapse duplicates. Pre-existing; it affects import the same
  way it already affects save.

---

## 2026-07-27 — Dark chrome around the content

**Shipped.** The surface list and the workspace pane are now dark blue
(`#3A5A7D`), framing a content area that stays light. The header keeps
`#1F2933` and was explicitly left alone. 348 tests green.

**Decided**

- **The header stays darker than the panes.** It holds the goal and the run
  controls; being the darkest thing in the window is what keeps it reading as
  the top of a hierarchy rather than a third panel. That is why `Chrome*` and
  `Pane*` are two palettes and not one.
- **The palette is `Color` resources with the brushes derived from them**, not
  brushes alone. The tree recolours selection by overriding four `SystemColors`
  brush keys, and those need raw `Color` values — as hardcoded hex they would
  have gone on matching the *old* pane colour after any recolour, silently. The
  first version of this change had exactly that bug in it.
- **Selection is templated, not merely recoloured.** The stock `ListBoxItem`
  and `TreeViewItem` paint the system highlight, a pale blue behind near-black
  text, which on a dark ground is worse than no styling at all. The list gets a
  full template (fill plus an accent bar, since a fill alone is weak at this
  contrast); the tree gets the four brush overrides instead, because its default
  template also owns the expander glyph and the indentation and neither needed
  changing. Inactive selection is styled too — the tree loses focus whenever the
  goal box is used, and a selection that vanishes then reads as a click that did
  not register.
- **The modified-file green and red are lightened here and left alone on the
  Changes surface.** `#1B5E20` and `#B00020` are chosen against white. The panes
  agree on the hue, which is the part that carries meaning; agreeing on the hex
  would only mean one of the two is invisible.

**Verified**

Every candidate was built, captured and compared as a real window rather than
reasoned about. Two traps cost a round each and are worth writing down:

- Capture with **`PrintWindow(hwnd, hdc, 2)`** — flag 2 is
  `PW_RENDERFULLCONTENT`. `CopyFromScreen` grabs whatever is on top and is
  offset by the invisible resize border; plain `PrintWindow` without flag 2
  captures a WPF window as solid black.
- Call **`SetProcessDpiAwarenessContext(-4)` before any window measurement**.
  This display runs at 125%, so a DPI-unaware process gets virtualised
  coordinates from `GetWindowRect`, sizes the bitmap in logical units, and
  `PrintWindow` then fills only the top-left corner. The tell is a capture that
  is sharp and correctly framed but missing content — 1400x950 against a real
  1550x950.

Tree selection was checked by driving UI Automation to select a node and
capturing that, rather than assuming the brush overrides took.

---

## 2026-07-27 — The window that never opened

**Shipped.** A fix for a startup deadlock, the desktop composition root pulled
out of `App.OnStartup` so it can be built by something other than the
application, and `tests/GlassCoder.Wpf.Tests` to build it. 348 tests green.
Reported as "when I launch the app from the debugger, the application hangs" —
and it hung outside the debugger too; the debugger was incidental.

**What it was.** Enabling the git tools closed a cycle in the graph:
`ChangesViewModel` takes `GitTool` so it can decide whether to show its git
controls, `GitTool` takes `IApprovalGate` so a push still asks a human, and
`WpfApprovalGate` took `ChangesViewModel` to ask with. It appeared the moment
the previous session's Git tab was used to switch the tools on — the settings
file grew `Git:Enabled: true` three minutes after that commit, and from then on
the window never appeared.

**Decided**

- **The cycle is broken at the gate, which takes `Func<ChangesViewModel>`.**
  Of the three edges that is the one that is genuinely late: the gate needs the
  view only when something is waiting on a decision, whereas the change view
  needs to know at construction whether to show its buttons, and `GitTool`
  needs a gate it can rely on. Breaking a different edge would have made a
  constructor lie about when its dependency is really used.
- **The composition root moved to `AddGlassCoderDesktop`.** Registrations that
  live inside `OnStartup` can only be exercised by starting the application,
  which is why a defect this total went out. `App.OnStartup` is now the shared
  bootstrap plus that one call, and the test makes the same two calls.
- **The test resolves under a timeout, on an STA thread.** This class of bug
  does not throw. `Microsoft.Extensions.DependencyInjection` detects cycles
  while building call sites, and a factory registration is opaque to that — so
  instead of `InvalidOperationException` the resolver recursed, its `StackGuard`
  handed the work to a thread pool thread, and that thread blocked on the
  singleton lock the resolving thread still held. Silent, at 0% CPU, with
  nothing in the log. A test calling `GetRequiredService` directly would have
  hung the run rather than failed a case, so `UiThread.Run` gives the graph 30
  seconds on a background STA thread and fails with a `TimeoutException` that
  names the likely cause.
- **`ValidateOnBuild` would not have caught this** and was not reached for. It
  builds call sites, and the cycle is invisible at that level for the same
  reason cycle detection is.
- **Tests resolve view models, never windows.** A `Window` wants a running
  `Application`, and none of this is about rendering.

**Verified**

The diagnosis came from a stack, not a reading: `dotnet-stack` against the hung
process showed the UI thread recursing through the `ChangesViewModel` factory —
five turns of the loop visible — and a second thread parked in
`Monitor.Enter_Slowpath` inside `VisitRootCache`, which is the other half of the
deadlock. Confirmed by launching with `GlassCoder__Git__Enabled=false`, which
opened the window.

The regression test was then checked against the bug rather than merely run: the
`Func<ChangesViewModel>` registration was temporarily made to resolve eagerly,
recreating the exact cycle. The git-enabled case failed on timeout, the
git-disabled case passed, and the change was reverted. A test for a hang that
has never been watched to fail is a test that might be asserting nothing.

**Still open**

The approval flow is covered as far as the graph and no further — that the gate
and the shell share one `ChangesViewModel`, not that a request reaches the strip
and a decision comes back. That needs a pumped dispatcher.

---

## 2026-07-27 — The git settings, in the dialog

**Shipped.** A Git tab in the settings dialog with every `GitOptions` value
editable — enable, executable, timeout, hooks, commit trailer, remote, both
branch lists, the gh CLI and the pull-request base branch — plus the push
approval switch, which had been added to `ApprovalOptions` in task 41 and never
given a control. 338 tests green. Prompted by "only 8 tools are mentioned at
the bottom of the window": correct, because the tools are opt-in and there was
no way to opt in short of hand-editing `appsettings.json`.

**Decided**

- **`GitOptions` joins `GlassCoderSettings` rather than getting a parallel
  editable copy.** That aggregate is deliberately built from the *real* options
  classes, so a section becomes saveable the day it is added and only its editor
  has to be written. Adding one property was the whole of the persistence work.
- **Push approval lives on the Git tab, not in the Approval group.** Approval
  for writes sits with the sandbox because that is where writes are governed;
  someone configuring a push looks for it under Git. Discoverability decided it,
  since the complaint that started this was discoverability.
- **Git settings are validated only while the tools are enabled.** A remote of
  `--mirror` should block a save when git can act on it and should be nobody's
  problem when it cannot. The checks mirror `IsSafeRefName` in the tool, so a
  name git would read as an option is refused in the dialog rather than
  surfacing later as a puzzling tool failure. An allow-list wholly contained in
  the deny-list is refused too — it can only ever mean "nothing may be pushed".
- **Both branch lists are deduplicated on read**, joining the existing set. The
  binder appends to a list that already holds defaults, so without it a list
  doubles on every visit to the dialog. Ours default empty and would not have
  grown yet; they will the moment anyone gives them a default.

**Verified**

The dialog was opened through UI Automation and its tabs enumerated — Models,
Workspace, Agent, Verification, Sandbox, **Git**, Logging, Telemetry — so the
tab genuinely renders rather than merely compiling. A settings file carrying
`Git:Enabled` was then fed to the console host, which advertised all five git
tools: the whole chain from saved file to registered tool, not just the halves.

**Then, the same day: the bash switch too.**

`Sandbox:EnableBashTool` became a real property on `SandboxOptions` and got a
checkbox in the Sandbox tab, beside the guardrails that decide whether bash can
run at all. It had been read straight from configuration with `GetValue` and
existed nowhere as a property, so it could be set but never saved.

- **Both opt-in keys are now built from `nameof`.** `$"{SandboxOptions.
  SectionName}:{nameof(SandboxOptions.EnableBashTool)}"` cannot drift from the
  property the settings file writes; the two literal strings could, silently,
  and the failure mode is a capability that quietly does not appear.
- **`ToolRegistrationTests` is new, and covers a gap that predates all of
  this**: nothing had ever asserted which tools are advertised, or that either
  opt-in key is spelled the way the settings file spells it. It fakes
  `ICommandExecutor` first so `TryAddSingleton` leaves it alone and no test
  reaches for a Docker daemon.
- Verified the same way as the git tab: the Sandbox tab was opened through UI
  Automation and its checkboxes enumerated (the new one among them), then a
  settings file carrying the switch was fed to the console host, which
  advertised `bash`.

---

## 2026-07-27 — Every saved setting was being discarded at startup

**Shipped.** A one-character-class bug with a wide blast radius: saved settings
were layered *underneath* `appsettings.json` instead of over it, so everything
the settings dialog and the workspace pane ever wrote was read and then
overruled. Reported as "the project points at `bin\Debug\net10.0-windows`, and
choosing the right folder does not survive a restart". 333 tests green.

**What was wrong**

`AddGlassCoderUserSettings` inserted the user settings file ahead of the *first*
`EnvironmentVariablesConfigurationSource` it found. `HostApplicationBuilder`
registers **two**: the `DOTNET_`-prefixed host source, which sits *before*
`appsettings.json`, and the unprefixed application source after it. The scan
stopped at the first, so saved settings landed at index 1 and `appsettings.json`
at index 4 — and later sources win. Every saved value that `appsettings.json`
also mentions was silently discarded; only keys absent from it (and the API
keys, arriving through a sibling in-memory source) ever took effect.

The workspace root made it visible because `appsettings.json` says `"."`, which
`PathGuard` resolves against the *process working directory* — the executable's
own folder for a double-clicked window.

**Decided**

- **Scan backwards for the last environment source.** That restores the
  documented intent — saved settings beat `appsettings.json`, lose to
  environment variables and `--config` — and it is now stated in the doc
  comment as load-bearing rather than incidental.
- **The old test could not have caught this.** It built a hand-rolled
  `ConfigurationBuilder` with one environment source at the end, a shape that
  does not exist in production. The replacement drives the real
  `Host.CreateApplicationBuilder` with a real `appsettings.json` and a real
  saved file; reverting the fix fails it and nothing else.
- **`"."` stays working-directory for the console host and is discovered for
  the desktop.** `cd` into the repo then run is the right contract for a CLI
  and the wrong one for a window nobody launches from a shell. So
  `WorkspaceRootLocator` walks up from the executable for `.git` or a solution
  file, and the WPF app applies it *only* when no layer supplied a real root —
  anything chosen, saved or exported still wins. Outside a checkout it finds
  nothing and says so rather than guessing.
- **Both front ends now log the workspace root at startup.** The generic host
  announces its content root and nothing announced this one, which is exactly
  how a root silently resolving to build output survived unnoticed in an
  application whose first-class claim is instrumentation.

**Open**

- Worth auditing whether any behaviour previously attributed to configuration
  was really this bug — every dialog-saved endpoint, budget and sandbox mode
  was inert until now.

---

## 2026-07-27 — Git tools, step 3: pull requests and the manual path (workplan task 42)

**Shipped.** `create_pull_request` opens a GitHub PR for the current branch
through the `gh` CLI, behind the same approval gate as push. The changes pane
grew a git strip — commit message box, Commit and Push — that calls the very
same tool methods the model calls, and every button press lands in the
transcript as its own step. 323 tests green, build clean, app verified to
launch with git both disabled and enabled. Tasks 40–42 are closed; only 38
remains open.

**Decided**

- **`gh`, not Octokit.** The CLI already holds the credentials via `gh auth`,
  so GlassCoder still holds no token of its own — the same bargain step 1
  struck with the credential manager. Octokit would have meant a PAT this
  application stores, plus a new dependency, to reimplement what `gh pr
  create` already does. The cost is a second host prerequisite, which the
  failure path names explicitly rather than leaving as a mystery exit code.
- **No `IGitService` was extracted.** `GitTool` *is* the service: its methods
  already return `ToolObservation<T>`, which carries exactly the `Ok`,
  `Summary` and `Error` the UI wants. An interface mirroring the class one for
  one would be ceremony, and the codebase already set the precedent of driving
  tools directly — the verification ladder calls `build` and `run_tests`
  without going through the model-facing registry.
- **The buttons are the same code path, so the guardrails cannot diverge.** A
  manual push still asks the approval gate; a manual commit still filters
  staging through the writable set. Nothing about pressing a button makes the
  action more trusted than the model asking for it.
- **Manual actions are logged as `Role: "human"` steps.** They go through
  `IStepLogger`, which is the `TranscriptBus` — so they reach the durable
  JSONL *and* the live transcript view. Outside a run `RunContext.Current`
  yields `no-run`/`no-task`, which is honest: the action belonged to a person,
  not a run, and it therefore contributes to no run's metrics.
- **The buttons stand down mid-run.** The shell pushes `IsRunning` into the
  pane. Committing a tree the agent has not finished changing would record
  work in progress as though it were finished.
- **A pull request refuses to describe unpushed commits.** Ahead-of-upstream
  means the PR would show reviewers something other than what the agent
  built, so it errors with a "run git_push first" hint rather than opening
  something misleading.
- **`--flag=value` everywhere in the gh invocation.** A title beginning with a
  dash cannot then be parsed as an option — the same class of defence as
  `IsSafeRefName` on the git side.

**Open**

- Task 38 (measure the dormant capabilities) is the last one standing.
- The manual git strip has no "sync" or "open PR" button; those stayed
  model-only until there is a reason to widen the surface.
- Manual steps use their own index counter, so a transcript containing both
  manual and run steps has two independent sequences. Harmless for filtering,
  slightly odd if read as one timeline.
- `gh` is a second host prerequisite alongside git, and nothing checks for it
  until a PR is attempted.

---

## 2026-07-27 — Git tools, step 2: sync and push, behind a human (workplan task 41)

**Shipped.** The outward-facing half: `git_sync` (pull --rebase onto the
upstream, clean tree required, conflicts auto-aborted and reported) and
`git_push` (current branch to the configured remote, gated by human approval).
The approval seam now has an action shape — `AgentAction` +
`RequestActionAsync` — beside the diff shape, `RequireApprovalForPush`
defaults to true, and the WPF changes pane shows a push approval with the
outgoing commits where the diff would be. 314 tests green, build clean.

**Decided**

- **The approval policy lives in the gates, not the tool.** `GitTool` always
  asks `IApprovalGate`; `AutoApprovalGate` and `WpfApprovalGate` each consult
  `RequireApprovalForPush`, exactly mirroring the write path. So the headless
  host fails closed out of the box — a bare `GitTool` with no interactive gate
  cannot push at all — and a test proves it.
- **Push approval is default-on; write approval stays default-off.** A write
  can be reverted from the change log; a push has left the machine. The
  asymmetry is the point, and it is written into the options' doc comments.
- **A conflicted sync aborts itself.** The agent has no tool to resolve or
  abort a rebase, so leaving one in progress would wedge the repository. The
  tree comes back exactly as it was, the conflicted files are named in a
  `merge_conflict` observation, and reconciling is explicitly a human's call.
- **Branch policy is two lists, checked before anyone is asked.**
  `ProtectedBranches` (deny, wins) and `PushableBranches` (allow, empty =
  any); a refusal is `branch_not_allowed` and never consumes a human approval.
  There is no force flag and no free-form refspec anywhere in the schema, so
  the lists plus the configured remote are the entire policy surface.
- **The push approval reuses the changes strip wholesale.** `PendingApproval`
  now carries either a change or an action; an action's detail lines render
  through the existing diff template as context lines. Same buttons, same
  timeout-is-refusal, no new UI surface to maintain.
- **Failure hints teach the loop.** Auth failures hint at the host credential
  manager (GlassCoder still holds no tokens); non-fast-forward rejections hint
  at `git_sync`; `git_sync` without an upstream hints that the first
  `git_push` sets it (`-u`).

**Open**

- Step 3 (task 42): `create_pull_request` and manual Commit/Push buttons over
  the same code path, plus logging button-initiated actions into the run
  record.
- Sync counts movement by before/after SHA, not "commits pulled" — a rebase
  makes that number honest to compute only with more plumbing than it is
  worth so far.
- The commit SHA still is not tied into `IChangeLog`; unchanged from step 1.

---

## 2026-07-27 — Git tools, step 1: status and commit (workplan task 40)

**Shipped.** The agent can now see and record its work in version control:
`git_status` (branch, ahead/behind, staged/unstaged/untracked/conflicted, capped
file list from `--porcelain=v2`) and `git_commit` (stage-all filtered through
the writable allow-list, then commit). Opt-in via `GlassCoder:Git:Enabled`, off
by default like bash. 296 tests green, build clean. Tasks 41 (sync/push behind
approval) and 42 (PRs, UI buttons) hold the rest of the researched plan.

**Decided**

- **Git runs on the host through `IProcessRunner`, not in the sandbox and not
  via LibGit2Sharp.** The container has no network and no credentials — both
  are the point of the later push step — and LibGit2Sharp's authentication
  story would make GlassCoder hold tokens the credential manager already
  holds. What makes host execution defensible: every invocation is a fixed
  argument list (no shell), and nothing here executes repository code.
- **Hooks are off by default** (`-c core.hooksPath=/dev/null`, understood by
  git for Windows too). A pre-commit hook is arbitrary code the agent may just
  have written, running on the host outside the sandbox — the one way a "safe"
  git tool becomes as privileged as `build`. `AllowHooks` turns them back on.
- **The writable set is the staging boundary.** `stageAll` resolves every
  candidate through the path guard's write check and reports how many were
  left out; the deny globs (bin, obj, .git) fall out of the same check.
- **Conflicted paths are never auto-staged** — staging one marks the conflict
  resolved, and that is a judgement the agent must make explicitly. A commit
  attempted mid-conflict fails with git's own error as the observation.
- **Prompts are disabled** (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`),
  so a missing credential or identity is a fast, typed failure instead of a
  hung loop step. New stable error code: `git_unavailable` (git missing, or
  not a repository).
- **Provenance trailer on by default:** every agent commit carries a
  `Co-Authored-By: GlassCoder` paragraph (configurable, empty disables) — the
  phase-6 stamping idea applied where it is cheapest.

**Open**

- Step 2 (task 41): `git_sync` and `git_push` need the approval seam
  generalised first — `IApprovalGate` is shaped around a `CodeChange` diff and
  a push approval is an action, not a diff.
- Step 3 (task 42): `create_pull_request` and manual Commit/Push buttons over
  the same code path.
- The commit SHA is logged but not yet tied into `IChangeLog` or the run
  record; worth doing when the change log meets task 41.

---

## 2026-07-27 — The workspace pane (workplan task 39)

**Shipped.** A right-hand pane on the shell: the project folder on top, the file
tree below. Files with an applied change this session show in green with their
net `+added −removed` line counts, updated live from the change log, with the
folders above them auto-expanded. Browse… picks a new project folder,
persists it through the user settings store, and offers the restart that makes
it the root in force. 280 tests green, build clean.

**Decided**

- **Folder selection is save-and-restart, not runtime re-rooting.** The path
  guard, the sandbox mounts and the context all rooted themselves at startup,
  and the whole configuration model is deliberately bind-once. A pane that
  re-rooted just the tree would show a folder the agent is not in; re-rooting
  everything live is a real architectural change (see Open). So the tree always
  shows the active root, and a chosen folder becomes a pending strip with a
  Restart button — the same contract as the settings dialog.
- **Counts are net, not summed.** `FileChangeSummary` diffs the first applied
  change's before-text against the last applied change's after-text per file. A
  line written and then rewritten counts once; an applied-then-reverted change
  leaves `Applied` and drops out on its own; edits that cancel out exactly
  still report the file, at `+0 −0` — touched-but-net-nothing is a fact, and
  the rollup does not editorialise it away.
- **The tree hides exactly what the deny globs hide.** The pane builds its
  filter from `Workspace:DeniedGlobs`, so it and the path guard never disagree
  about what the workspace contains. Directories are pruned with a probe-child
  match — the globs are of the "everything under here" shape, and enumerating
  `bin/` only to hide it would be the slowest way to say nothing.
- **The folder picker went behind `IDesktopShell`.** Alongside `OpenFolder` and
  `Restart`: view models stay free of dialog classes, and the seam's doc
  comment now says "what the view models need" rather than counting to two.
- **Rollup arithmetic lives in `GlassCoder.Tools`, not the view model.** There
  is no WPF test project, and the one part of this feature with real
  correctness content is the counting - so it sits next to `ChangeLog` and is
  tested in `GlassCoder.Tools.Tests`.

**Open**

- Runtime workspace switching (no restart) would need a re-rootable
  `PathGuard`, recreated sandbox mounts and an invalidated change log — a
  separate task if restart friction ever matters.
- Edits made outside the agent are invisible to the tree until Refresh; a
  `FileSystemWatcher` was deliberately left out of v1.
- Directory nodes do not roll up their children's counts — an easy add if
  wanted. (The pane's fixed width, noted here first, became a splitter the
  same day: the shell's centre is now a Grid so the pane edge drags.)

---

## 2026-07-27 — The critic's transport, and the review on the record (workplan task 37)

**Shipped.** A per-role `Transport` setting: `critic-remote` now speaks Anthropic's
`/v1/messages` against `api.anthropic.com` (model `claude-opus-5`) out of the box,
with the OpenAI-compatible shape still one config switch away. The post-run
review is persisted verbatim into the JSONL transcript as a `ReviewRecord` —
role, every vote with its reason and availability, tokens, cost, duration — and
replays with its run. The connection check probes both transports. 272 tests
green, build clean. Workplan task 37 is closed; only 38 remains open.

**Decided**

- **The transport is the official Anthropic SDK, not a hand-rolled client.** The
  `Anthropic` package ships a first-party `IChatClient` adapter
  (`AsIChatClient`), so the whole thing is a second branch in
  `ChatClientFactory` rather than a wire format we would own the parsing of.
  The SDK also brings retries, typed errors and the versioned `anthropic-version`
  header for free.
- **Anthropic-transport endpoints are host roots, not `/v1` bases.** The SDK
  appends `/v1/messages` itself, so `https://api.anthropic.com` is the whole
  endpoint. The settings hint and the config comments say which convention each
  transport uses, because a `/v1` suffix here would produce `/v1/v1/...` and a
  404 that looks like an outage.
- **Sampling parameters are dropped, never forwarded.** Current Anthropic models
  reject `temperature` and `top_p` with a 400 rather than ignoring them, and the
  critic panel pins temperature 0 — the right habit for a local critic and a
  fatal one here. The transport's role defaults null them out, so which oracle a
  role addresses stays a config choice instead of a crash.
- **A refusal is an empty answer, not an exception.** The API's safety
  classifiers can decline a request with a successful HTTP 200, `stop_reason`
  "refusal" and no content. Through the critic panel's existing parse that reads
  as "the critic returned nothing" — a failure to judge, a non-vote, outside the
  quorum. The fake server has a refusal case so this stays true by test rather
  than by luck.
- **The review is its own transcript record, not a field on the run record.**
  `RunRecord` closes before the review runs — the review judges the finished
  run, so it cannot ride the record of the run it judges. `ReviewRecord` is a
  third record type beside steps and runs, routed and redacted the same way
  (a critic quotes the diff it judged, so its words fall under the content
  switch), parsed back by `TranscriptReader`, and rendered by `ToText`.
- **Persistence lives in `RunReviewer`, not the view model.** Whoever asks for a
  review — the desktop app today, the console host tomorrow — gets the record
  written, because the component that produced the opinion is the one that
  records it. Early-outs (review off, limit-stopped run, no changes) persist
  nothing: no critique ran, and an empty record would claim one had.
- **The critique rung's config keeps meaning what it meant.** Existing roles
  default to `Transport: OpenAI`, so no configuration changes behavior silently;
  the shipped `critic-remote` opts into the new transport explicitly.

**Open**

- **`docs/NewFeatures/claude-second-opinion.html` still describes the transport
  as missing.** The design note predates this session; its "what would have to
  land" section is now history and should be rewritten as a description, the way
  it was after the 2026-07-24 session.
- The review strip shows the review it always showed; it does not yet indicate
  that the critique is on the record, nor is there a UI to browse past reviews.
  The transcript view reads step records only — `ReviewRecorded` is on the bus
  for whoever adds that.
- The in-loop critique (rung 6, task 36) records its verdict in the step's
  verification summary but not as a full vote-by-vote record. If measurement in
  task 38 makes rung 6 interesting, it deserves the same `ReviewRecord`
  treatment.
- Task 38 — enabling and measuring the dormant capabilities — is now unblocked
  on both of its prerequisites.

---

## 2026-07-27 — The ladder in the loop (workplan task 36)

**Shipped.** The controller loop now climbs the verification ladder after every
step that applied a change: syntax on the changed file, compile, analyzers,
tests, and rung 6 when critique is enabled. The report goes back to the model
as an observation, lands in the step record, is stamped onto the change-log
entries that caused it, and moves the recovery and compile-error metrics.
`RunBudget` gained its second price table. 263 tests green, build clean.
Workplan task 36 (added today, with 37 and 38) is closed.

**Decided**

- **The failure policy is correction, not rejection.** A failed climb does not
  fail the step, revert the change, or summon a human; the summary goes back as
  a user-role observation and the loop carries on. The write tools already
  refuse what the in-memory check can prove broken — what the ladder catches
  here is *applied* work (a red test, a break in another project), and silently
  reverting applied work would leave the model reasoning about a working tree
  that no longer matches what it was told. Correction after prompt feedback is
  the recovery-rate hypothesis; now it can actually be measured.
- **A clean bill is reported too.** Otherwise the model spends its next step
  calling `build` to learn what the harness already knows.
- **A climb where every rung skipped says nothing.** Not a C# file, no sandbox:
  silence is more honest than a hollow "verified", so no message is sent and no
  verification is recorded for the step.
- **Ladder outcomes feed the same metric counters as model-called builds and
  tests.** Recovery rate must not depend on who pressed the button, so
  `RunMetricsCollector.ObserveVerification` maps the compile rung onto the build
  counters and the test rungs onto the test counters. A post-write syntax
  failure counts as a broken state even though no build ran — the build it
  blocked would only have agreed. `create_file` now also counts as an edit;
  it was added after the collector and had been invisible to edits-to-green.
- **The critic's spend arrives pre-priced, and critic tokens stay out of the
  token counts.** `RunBudget.EstimatedCostUsd` adds
  `CritiqueResult.EstimatedCostUsd` as computed at the critic role's own
  prices, so `MaxCostUsd` can now trip on a hosted critic — the debt the old
  comment named. `MaxTotalTokens` still counts only worker tokens, because it
  guards the worker's context window, which the critic never occupies.
- **A single-file step gets the syntax rung on exactly what changed; a
  multi-file step starts at the compile rung**, which covers every file at
  once.
- **A harness failure to verify is logged and skipped, not reported to the
  model.** The harness failing to verify is not the model failing to code, and
  an error the model cannot act on is context spent making it stupider.
- **The change log is the trigger.** The loop watches its run's slice of
  `IChangeLog` for newly Applied entries rather than hardcoding which tools
  mutate, so a future mutating tool is verified the day it exists.

**Open**

- **Nothing forces a final green climb before `Completed`.** The model can
  still declare done with a red tree; the failure is visible in the change
  log's `VerificationSummary` and the metrics, but nothing gates completion on
  it. Whether it should is a policy question worth deciding against data from
  task 38 rather than instinct.
- **Rung 6 now runs inside the loop whenever critique is enabled**, which
  makes cost-per-solved-task with a hosted critic a real number nobody has
  measured. Task 38's job.
- The in-loop `RunFullSuite` switch is redundant until a `TestFilter` narrows
  rung 4 — with no filter, rung 4 already runs every test. Said in the options
  and the config comments; a smarter default filter is future work.
- The new settings (`VerifyAppliedChanges`, `TestFilter`) are in the dialog's
  Verification tab, and like every setting they apply on restart only.
- Task 37 (the critic transport and review persistence) is unchanged by this.

---

## 2026-07-24 — The second-opinion critic

**Shipped.** A critic role chosen per run rather than per process, a post-run
review of a finished run, and a retry only a human can press. Quorum handling so
an unreachable critic is no longer an approving vote. A `critic-remote` role
alongside `critic`, and a `RequiresApiKey` declaration so a control that offers a
critic can be greyed out instead of failing on press. A **Second opinion**
checkbox in the shell header and a review strip above the status bar. 252 tests
green, build clean. `docs/NewFeatures/claude-second-opinion.html` rewritten from
proposal to design note, with three corrections to what it previously claimed.

**Decided**

- **The critic is chosen before the run, not during it.** The first sketch was a
  three-state dropdown switchable mid-session. It was dropped because a run whose
  critic changed at change 7 of 20 is two arms, and no number taken from it
  belongs to either. Reading a checkbox when **Run** is pressed makes the run one
  arm by construction and costs nothing — the choice rides on `AgentRunRequest`
  next to the model role and the budget, and lands in the run record.
- **No critic ever starts a run.** The post-run reviewer composes a retry goal
  and stops; pressing the button is the human's job. An automatic retry would be
  a second attempt granted by a model, and a store that cannot tell a first
  attempt from a granted one reports `pass@2` under the name `pass@1`. Hence
  `Attempt` on the request, result, run record and `RunMetrics`.
- **Two critic roles, not one repointed role.** The roles dictionary was already
  open-ended; the only thing in the way was `CritiqueOptions.Role` being the
  single answer. `Role` is now the default and `RemoteRole` is what the checkbox
  asks for, with the role passed per call.
- **An unconfigured critic role is reported, never silently swapped for the
  default.** Falling back would answer a question the caller did not ask and put
  the wrong oracle in the transcript.
- **`RequiresApiKey` is declared, not inferred.** Guessing from the endpoint —
  `localhost` free, everything else paid — is wrong about reverse proxies, LAN
  servers and gateways alike.
- **An empty completion is a failure to judge, not an acceptance.** It joins the
  unreachable critics outside the quorum.
- **The `RunBudget` overload for billing a second role was written and then
  removed.** Nothing in a run bills a second role today, so it would have been
  untestable dead code. The real fix landed as `CritiqueResult.EstimatedCostUsd`,
  priced at the critic role and shown in the review; `RunBudget.EstimatedCostUsd`
  carries a comment naming the debt for whoever wires rung 6 into the loop.

**Corrected in the proposal document**

- It claimed `ICriticPanel.Enabled` "already models exactly this" for the
  no-API-key case. It did not: `ContainsRole` is a dictionary lookup, so a hosted
  critic with no key reported `Enabled == true` and the button would have failed
  on press — the exact thing that section said must not happen.
- It claimed a paid critic "slots into both without new plumbing", so a runaway
  critique loop "trips a budget rather than a credit card". `RunBudget` prices a
  whole run from one role's rates, so it does not.
- Its A/B framing (rung 6 versus a button on the approval banner) was replaced by
  during-the-run versus after-the-run, because the banner button was not what got
  built and the post-run review is not a variant of it.

**Open**

- **Nothing calls `VerificationLadder.VerifyAsync`.** Rung 6 is written, tested,
  and now takes a critic role — and no code path in `src` climbs the ladder. The
  in-run half of the feature is a capability the harness has and never uses, and
  the recovery-rate argument cannot be run at all until it is wired. Bigger and
  less visible than the transport gap, because everything around it compiles.
- **The transport is still missing.** `ChatClientFactory` builds one shape of
  client, so `critic-remote` can address an OpenAI-compatible gateway or a second
  local model of a different family — not Anthropic's `/v1/messages`. The shipped
  config points at `localhost:8004` rather than pretending otherwise. A critic
  needs no tools and no streaming, so the client this wants is small.
- **The review text is not persisted.** `CriticRole` and `Attempt` reach the run
  record; the critique itself lives in the view model until dismissed. An opinion
  that shaped a decision and left no trace is the one thing on that surface you
  cannot reconstruct.
- Critique is still off by default, so all of this is dormant until
  `Critique:Enabled` is set.

---

## 2026-07-24 — Settings dialog and the connection check

**Shipped.** A settings dialog over every configuration section, reached from
**Settings…** in the shell header, saving to a per-user layer. API keys stored
separately under DPAPI. A four-step connection check per served role. A guide of
its own at `docs/settings.html`, and corrections to the six documents it touched.
Then an About box: the Kintsunai logo, the credit line, and the build facts a
bug report needs. 235 tests green, build clean.

**Decided**

- **The dialog binds the real options classes, not editable copies of them.** A
  second, hand-maintained model of the configuration drifts the moment somebody
  adds a property, and the UI then quietly stops being able to set it. The cost
  of this choice is that the reader has to deduplicate list settings: the binder
  *appends* to a list that already holds defaults, so a naive save-then-load
  doubles the denied globs on every visit.
- **Saved settings are inserted ahead of the environment-variable source, not
  appended.** Appending would have been shorter and would have let a preference
  saved once, on one machine, redefine what `config/phase1.json` means. The
  chain is: appsettings.json < saved settings < environment < command line <
  `--config` arm.
- **API keys are lifted out of the document by the store, not by its callers**,
  and the `ApiKey` property is *removed* rather than nulled, so the settings file
  carries no trace of a key having been there.
- **The check ends with a real completion.** A served alias whose weights failed
  to load answers `/models` perfectly well, so a check that stopped at a
  handshake would pass in exactly the case it most needs to fail — and would be
  believed. Four stages are reported separately because the four failures have
  four different fixes.
- **The probe uses the bare transport** — no constrained decoding, no telemetry
  stage. "Can this endpoint, key and alias produce a completion" is a different
  question from "does this server honour `guided_json`".
- Keys are tested against **what is on screen**, not what the harness started
  with, so a pasted key can be checked before it is saved.

**Open**

- **Settings apply on restart only.** Every section binds once at startup through
  `IOptions<T>`, so a save does not reach the running process; the dialog says so
  and offers *Save and restart*. Making it live would mean moving consumers
  across Core, Tools and Models to `IOptionsMonitor<T>` — a much larger change
  than the dialog itself, and not attempted.
- **`WORKPLAN.md` stops at task 35.** Nothing since — `create_file`, the guides,
  the proposals, this dialog — has an entry. Either backfill it or accept that
  this file is now the record of work after task 35.
- Off Windows the secret store degrades to base64 *encoding* and says so through
  the scheme name. Nobody has run the app there; it is the console host that
  would meet it first.
- **The About box credits Kintsunai; `Directory.Build.props` still says
  `<Company>GlassCoder</Company>`.** Two answers to the same question, and the
  assembly metadata is the one nobody looks at until it is wrong.

---

## 2026-07-23 — create_file, rung 2, the tutorials, and three proposals

**Shipped.** The `create_file` tool. A fix to the in-memory compile rung. The
desktop app guide and two tutorials. Three design notes under
`docs/NewFeatures`, and the operator's guide linking them.

**Decided**

- **Creation and modification stay separate verbs.** `edit_file` can only change
  what already exists, so a new file had no route into the workspace at all —
  suite-07 could only be passed by cramming a new type into an existing file,
  which meant the task was measuring the tool set rather than the model. An
  *upserting* create tool would have been a hole straight through the guarantee
  that "replace one exact, unique string" is the only way an existing file
  changes, so `create_file` refuses to overwrite and points at `edit_file`.
- **Rung 2 now synthesises the SDK's implicit global usings.** The in-memory
  compile never runs MSBuild, and the generated usings file lives under `obj/`,
  which the workspace deny list excludes from every access. Existing files got
  away with it because only *introduced* errors gate; new files did not, so a
  well-formed class calling `ArgumentNullException.ThrowIfNull` was refused
  before it reached disk. The harness was worst at exactly the task it should be
  best at.
- **The three proposals are marked not-implemented, prominently.** Design notes
  sitting one click from shipped reference documentation are otherwise
  mistakable for shipped features.
- When the two tutorials disagreed about three UI details, **the one reproduced
  from a live run was right** and the hand-written mockups were corrected.

**Open**

- **`CriticPanel` returns `Refuted=false` for a critic it could not reach**, so
  an unreachable critic is arithmetically indistinguishable from one that read
  the change and accepted it — and the summary reports it as having accepted.
  The comment above that line says the opposite is intended. Harmless while the
  critic is a local endpoint; routine once it is a hosted API. Recorded in
  `docs/NewFeatures/claude-second-opinion.html`; not fixed.
- The three proposals remain unimplemented and are not prerequisites for any
  phase. The harness-advisor note is explicitly falsifiable: backtest it against
  historical logs and abandon it if the two known findings are not recovered.
- No fixture in the task suite needs external knowledge, so the MCP-retrieval
  proposal cannot be shown to help until a task that does is written.

---

## 2026-07-22 — The harness, end to end

**Shipped.** Workplan tasks 1–35: solution layout, the shared bootstrap, the
model seam with constrained decoding, structured logging, the tool registry and
its guardrail, the controller loop, the verification ladder, metrics, the task
suite and ablation runner, the three WPF surfaces, the console host, and the
Phase 6 freshness work. Plus the operator's guide and the DGX Spark setup guide.

**Decided**

- **One bootstrap for both front ends.** `GlassCoderHost.CreateBuilder` is what
  the WPF app and the console host share; two front ends binding different
  configuration would slowly become two different agents, and no measurement
  taken in one would apply to the other.
- **The compactor is deterministic.** A model-written summary would cost a call
  inside the loop and silently contaminate ablation arms.
- **Suite fixtures live as text, not files**, so every arm starts
  byte-identical, and each oracle is an exit code — no test framework to restore,
  and it runs identically in a network-dropped container.
- **`InvariantGlobalization` was removed from `Directory.Build.props`.** It was
  added in task 2 as a startup optimisation and is harmless for the libraries and
  the console host, which format with `InvariantCulture` and compare with
  `Ordinal`. It is fatal for WPF: every `FrameworkElement` carries
  `xml:lang="en-us"`, and the binding engine resolves that through
  `XmlLanguage.GetSpecificCulture()` on the first data-bound element, so the app
  died inside `Window.Show()` before rendering anything. `ArchitectureTests` now
  fails if anyone reintroduces it.

**Open**

- Phases 2 through 6 are *built* but mostly *off*: critique, orchestration and
  the bash tool all ship disabled. Nothing has been measured with them on.
