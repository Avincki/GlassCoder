# Task: The run gets a retrospective, and the harness reads its own report

**Status:** appended to WORKPLAN.md as task 67 (2026-08-08); this file keeps the task
review and the why behind each decision
**Prepared:** 2026-08-08
**Origin:** operator request — add a fourth surface next to Transcript / Changes / Metrics that runs a deep, three-part review via headless Claude Code, and turns selected recommendations into a work order a code agent can implement.
**Proposed workplan number:** 67

---

## 1. The task as asked

> In the main pane with "transcript", "changes" and "metrics" add a 4th one "super review"
> (please find a better wording). In this pane I can launch an action where a headless Claude
> Code will produce 3 outputs:
>
> 1. a code review along the standard code quality checks of the produced code;
> 2. a review of the run process of GlassCoder, also in respect of the previous code quality
>    analysis;
> 3. a check list of recommended improvements of GlassCoder and its tools.
>
> Upon pressing a button an md document will be generated that a code agent (Claude Code) can
> use to implement the selected recommendation.

Operator feedback on the first draft of this document (2026-08-08): the name **Retrospective
is confirmed**; once the retrospective finishes, the proposed workplan should be **shown in a
new window**; and because the action takes minutes, the operator wants to **see Claude Code's
work live while it runs**. Both additions are folded into §2.6, §2.7 and the plan below.

## 2. Review of the task description

The task is sound and, importantly, **most of its machinery already exists**. Workplan task 43
shipped `ClaudeCodeFileReviewer` (`src/GlassCoder.Core/Verification/FileReviewer.cs`): headless
`claude -p` with `--output-format json`, `--json-schema`-validated responses, a read-only tool
allow-list, `--permission-mode plan`, a spend ceiling, a cached availability probe, secret
scrubbing, and transcript recording. Its companion `ReviewActionFile` already renders a
reviewed action list as a Markdown work order with front matter and checkboxes, and parses it
back. The `GrokReview*.md` files at the repo root are the *manual* precedent for exactly the
three outputs asked for here — this task automates that ritual and gives it a surface.

Five points in the description needed a decision. Each is resolved below with a
recommendation; none blocks starting, but the first two deserve operator confirmation.

### 2.1 Naming — "super review" collides with everything

The shell already has a **review strip** (`IRunReviewer`, the critic panel's verdict), a
**file review** (task 43), and **rung 6 critique**. A fourth thing called "review" would be
the fourth meaning of the word on one window.

**Settled: "Retrospective"** — confirmed by the operator. It is the term of art for precisely
this: look back at a finished piece of work, judge the output, judge the process that produced
it, and leave with improvement actions. It fits the existing one-word-noun surface names
(Transcript, Changes, Metrics) and fits the 150 px navigation column. Alternatives considered
and declined: *Debrief*, *Postmortem* (wrongly implies failure), *Audit* (wrongly implies
compliance).

### 2.2 Three outputs are three subjects in two repositories

This is the load-bearing subtlety the one-line description hides:

| Output | Subject | Where it lives |
|--------|---------|----------------|
| 1. Code review | The code the run produced | The **workspace** (e.g. `GlassCoderTest`) |
| 2. Process review | The run itself — steps, tool calls, refusals, thrash | The **transcript** (`%LOCALAPPDATA%\GlassCoder\logs\*.jsonl`) |
| 3. Improvement checklist | **GlassCoder and its tools** | This repository — a *different* repo from the workspace |

One headless session cannot honestly do all three: the working directory, the material, and
the required context differ per output, and "also in respect of the previous code quality
analysis" makes output 2 *depend on* output 1.

**Recommended: one orchestrator, three staged headless sessions.** Stage 1 reviews the run's
changed files in the workspace. Stage 2 receives stage 1's report plus the run's transcript
and reviews the process — it can then say things like "the four layout-test failures the
worker deleted at step 40 correspond to defect 2 in the code review". Stage 3 receives both
reports, gets the GlassCoder repo as a read root, and produces the checklist. Each stage has
its own budget, timeout, and directive; a stage failing leaves the earlier stages' reports
intact and readable. This also matches how the manual GrokReview documents were actually
written.

### 2.3 The checklist and the button

The description ties the generated MD document to output 3 ("the selected recommendation"),
and that reading is the right scope: **stages 1 and 2 produce reports to read; stage 3
produces the tickable checklist.** Code-review findings from stage 1 already have a
consumption path — the existing review strip's "Retry with this" pattern, and the workspace
`.glasscoder/reviews` convention — so giving them a second, competing work-order path would
blur who acts on what. The checklist is for the *harness*, and its work order is for a code
agent opened in the *GlassCoder repo*.

### 2.4 Where the work order lands

`ReviewActionWriter` confines output to the workspace root — correct for workspace reviews,
wrong for harness recommendations: a work order for GlassCoder written into `GlassCoderTest`
would be invisible to the agent that must implement it, and Clean would delete it. But the
running app knows its build directory, not its source tree.

**Recommended:** a configured `Retrospective:HarnessRepoPath`. When set (on the dev machine:
this repo), work orders land in `<HarnessRepoPath>/docs/retrospectives/`; when empty, the
Write button is disabled with a tooltip naming the setting. Remember that
`%APPDATA%\GlassCoder\settings.json` shadows `appsettings.json` — set it there.

### 2.5 What "the produced code" means

Not the whole workspace — the run's own footprint. `IChangeLog` already knows the files the
run touched with per-file diffs; stage 1 is pointed at those files (and told it may read
anything around them). Reviewing an entire workspace would mostly re-review scaffold output
and burn the budget on unchanged files.

### 2.6 Watching the work while it runs

A retrospective is minutes of silence if the CLI is run the way task 43 runs it —
`--output-format json` hands back one envelope at exit, and `ProcessRunner` buffers stdout
until then. Both halves have a ready answer:

- **The CLI side.** Headless Claude Code supports `--output-format stream-json` (which
  requires `--verbose` in print mode): it emits one JSON event per line *as it works* —
  an init event, every assistant message including its tool calls, every tool result, and a
  final `result` event carrying the same fields the buffered envelope does (`result`,
  `structured_output`, `session_id`, `total_cost_usd`, `is_error`). The existing envelope
  parser applies unchanged to that final event.
- **The harness side.** `ProcessRunner` already receives stdout line-by-line through
  `OutputDataReceived` (`ProcessRunner.cs:64`) and merely appends to a `StringBuilder`. An
  optional `OnOutputLine` callback on `ProcessRunRequest` is the whole seam change — the
  fake implements it trivially, and no existing caller changes.

So the surface gets a **live activity feed**: for the running stage, each tool call rendered
as a line ("Read `src/GlassCoder.Core/Verification/FileReviewer.cs`", "Grep `IChangeLog`")
and assistant text as it arrives, the way the Transcript surface narrates the local agent.
Two lessons from elsewhere in this repo apply directly: the callback arrives on a background
thread and must marshal to the dispatcher (task 65's exit-callback bug found by its own
tests), and displayed lines go through `SecretRedactor` like everything else that leaves a
subprocess. A CLI too old for `stream-json` falls back to the buffered envelope — the
retrospective still works, it is just quiet again, and the feed says so once rather than
failing.

### 2.7 The finished retrospective opens a window

When stage 3 completes, the proposed workplan — the recommendation checklist — opens in a
**new window** rather than waiting to be noticed on the surface. The operator has usually
turned to something else during those minutes; the window arriving is the "it is done"
signal, and it carries the one thing that wants a decision (which items to tick) plus the
**Write work order** button.

Non-modal, owned by the shell — the `FileViewerWindow` precedent, not a dialog. The
workspace pane's rating strip taught why a box demanding an answer gets answered to be
dismissed; this window can be closed freely because the surface shows the same checklist and
can reopen the window with a button. Ticks live in one place (the view model) whichever
surface renders them.

## 3. What already exists, and is reused rather than rebuilt

- **`ClaudeCodeFileReviewer`** (task 43) — the entire headless-CLI mechanic: launch assembly
  (`-p`, `--json-schema`, `--allowedTools`, `--max-budget-usd`, `--bare`, `--add-dir`, prompt
  on stdin), envelope parsing (`result` / `structured_output` / `session_id` /
  `total_cost_usd` / `is_error`), the probe-once availability check, prose fallback when the
  schema is ignored, and transcript recording as a `Role: "human"` step.
- **`ReviewActionFile` / `IReviewActionWriter`** — Markdown work order with front matter,
  priority-tagged checkbox items, and a round-trip parser.
- **`IRunReviewer` / the critic panel** — the *cheap, local, always-on* post-run opinion.
  The Retrospective is the *expensive, on-demand, external* one. They are siblings, not
  rivals; the retrospective's stage 2 may quote the critic verdict as part of the process
  record, and nothing here changes the review strip.
- **Transcript & metrics** — the JSONL step log reconstructs any run (task 11);
  `metrics.jsonl` carries the numbers; the digest/summarizer conventions cap what is handed
  to a model.
- **Surface plumbing** — `Surfaces` list + `SelectedSurface` switch in
  `MainWindowViewModel.cs:121`, DataTemplates in `MainWindow.xaml`.

## 4. Implementation plan

Written in the workplan's shape so it can be appended as task 67. **Estimated time: 5.5d.**

### 67. The run gets a retrospective, and the harness reads its own report

Depends on tasks 11 (transcript replay), 12 (the digest's capping conventions), 43 (file
review CLI mechanic).

#### a. Extract the shared CLI session (0.5d)

- [ ] Pull the reusable halves of `ClaudeCodeFileReviewer` — launch assembly, envelope
      parsing, probe, scrubbing — into a `ClaudeCliSession` helper in
      `GlassCoder.Core.Verification`. `ClaudeCodeFileReviewer` becomes its first caller;
      its tests keep passing unchanged. No behaviour change in this step.
- [ ] The helper keeps the containment invariants as *constructor facts*, not caller
      options: read-only allow-list, non-writing permission mode, prompt on stdin, key via
      environment only.
- [ ] Add the optional `OnOutputLine` callback to `ProcessRunRequest`, invoked from the
      existing `OutputDataReceived` handler before the append. No existing caller sets it,
      so no existing behaviour changes; the fake runner replays scripted lines through it.

#### b. Options and records (0.5d)

- [ ] `RetrospectiveOptions` (`GlassCoder:Retrospective`): `Enabled` (default true),
      `CliPath`, `Model`, per-stage `MaxBudgetUsd` (default 2.00) and `TimeoutSeconds`
      (default 900 — a run retrospective reads more than one file), `MaxRecommendations`
      (default 12), `HarnessRepoPath` (default empty — §2.4), `OutputDirectory`
      (default `.glasscoder/retrospectives`), `Bare` (default true).
- [ ] Records: `RetrospectiveStage` (kind, report Markdown, session id, cost, duration,
      failure), `Recommendation` (reuses `ReviewAction`'s shape: id, title, detail,
      priority), `Retrospective` (run id, three stages, recommendations, totals).
      Failures are values, never exceptions — this is behind a button (CLAUDE.md §7).

#### c. The orchestrator: `IRetrospectiveReviewer` (1.5d)

- [ ] **Stage 1 — the produced code.** Working directory: workspace root. Input: the run's
      goal, its changed-file list from `IChangeLog` (diff summary capped like
      `RunReviewOptions.MaxChangeCharacters`). Directive: standard code-quality review —
      correctness, API misuse, error handling, tests that exercise the product (task 66's
      lesson belongs in this prompt), citations as `path:line`. Schema: `{report}`.
- [ ] **Stage 2 — the run process.** Input: stage 1's report, the run's digest, the
      step/tool/outcome sequence extracted from the JSONL transcript into
      `.glasscoder/retrospectives/<runId>/transcript.md` (extracted because the CLI must not
      need `%LOCALAPPDATA%` as a root, and raw JSONL wastes the window — reuse the digest's
      capping conventions). Directive: judge the *process* — wasted steps, thrash, refusal
      loops, oracle gaps — and tie process failures to stage 1 defects where the evidence
      supports it. Schema: `{report}`.
- [ ] **Stage 3 — the harness.** `--add-dir <HarnessRepoPath>` when configured (without it
      the stage still runs, on the two reports alone, and says so in its front matter).
      Input: both reports. Directive: recommend improvements to GlassCoder and its tools;
      read `WORKPLAN.md` and `HISTORY.md` first so it does not re-propose what is done or
      already planned; at most `MaxRecommendations`, priority-ordered. Schema:
      `{report, recommendations[]}` — the same item shape task 43's schema uses.
- [ ] **Live progress (§2.6).** Stages run with `--output-format stream-json --verbose` and
      `OnOutputLine`. Each line parses into a `RetrospectiveActivity` event — tool call
      (name + one-line argument summary), assistant text, or the final result envelope —
      surfaced through an `IProgress<RetrospectiveActivity>` the view model subscribes to.
      A line that does not parse is skipped, never fatal; a CLI whose `--version` predates
      `stream-json` drops back to the task 43 buffered launch, and the feed shows one line
      saying the CLI is too old to narrate.
- [ ] Stages run sequentially; cancellation between stages keeps finished stages. Each
      stage is recorded in the transcript as a `Role: "human"` step (`retrospective_code`,
      `retrospective_process`, `retrospective_harness`) against the run it judges —
      the task 43/65 precedent.
- [ ] Each stage's report is persisted on completion under
      `.glasscoder/retrospectives/<runId>/` with `ReviewActionFile`-style front matter
      (`glasscoder: retrospective`, stage, run id, model, cost), so the surface rehydrates
      from disk after a restart and a crashed stage 3 does not cost stages 1–2.

#### d. The surface and the results window (1.5d)

- [ ] Add `"Retrospective"` to `Surfaces`; `RetrospectiveViewModel` + `RetrospectiveView`
      + DataTemplate, following the existing surface pattern exactly.
- [ ] The view: which run (latest completed by default, header shows run id and goal), a
      **Run retrospective** button, per-stage progress ("Reviewing the code… 1/3"), the
      three reports as expandable sections, cost per stage and total, and the
      recommendation checklist with tickboxes.
- [ ] **The live activity feed (§2.6):** while a stage runs, its `RetrospectiveActivity`
      events render as an auto-scrolling narration under the progress line — tool calls as
      one-liners, assistant text as it streams. Events arrive on background threads and
      marshal to the dispatcher; the feed is display-only and scrubbed. On completion the
      feed collapses behind the finished report, kept for the session rather than persisted
      — the report is the record, the feed is the waiting made bearable.
- [ ] **The results window (§2.7):** when stage 3 completes, open a non-modal
      `RetrospectiveResultWindow` (owner: the shell) showing the proposed workplan — the
      recommendation checklist with tickboxes — and the **Write work order** button. It
      shares the surface's view model, so ticks made in either place are the same ticks.
      Closing it loses nothing; a **View proposals** button on the surface reopens it. It
      opens only on a completed stage 3 in *this* session — rehydration from disk shows on
      the surface without popping a window at startup.
- [ ] Enablement is honest about why not: greyed during a run; greyed with the probe's
      reason when the CLI is missing (shared probe from step a); greyed with "no completed
      run yet" before the first run. Cancel stops the current stage's subprocess.
- [ ] A retrospective already on disk for the selected run is shown, not re-run; the button
      then reads **Run again** and a fresh one supersedes it (files are timestamped, the
      newest is the one shown — the GrokReview filename convention).

#### e. The work order (0.5d)

- [ ] **Write work order** button, enabled when ≥1 recommendation is ticked and
      `HarnessRepoPath` is set (§2.4). Generalise `ReviewActionFile` with
      `kind: retrospective-actions` and a `target: harness` front-matter field rather than
      writing a second renderer.
- [ ] The document is self-sufficient for a cold code agent: run id, goal, the three
      report paths, every recommendation with ticks marking the selected ones, and a
      closing instruction block ("implement the ticked items; the reports explain why").
      Lands in `<HarnessRepoPath>/docs/retrospectives/`, named
      `retro-<runId8>-<yyyyMMdd-HHmmss>.md`.
- [ ] The write is recorded as a transcript step, like the git buttons' precedent.

#### f. Tests (1d, alongside the above)

- [ ] Options bind; the CLI session extraction keeps `FileReviewer` tests green.
- [ ] Orchestrator over a faked `IProcessRunner`: three launches with the right working
      directories, roots and stdin; stage 2's stdin contains stage 1's report; a stage 2
      failure still persists stage 1 and reports the failure as a value; cancellation
      between stages keeps finished stages; costs sum.
- [ ] Streaming: scripted `stream-json` lines produce the expected activity events and the
      final envelope; a garbage line is skipped without ending the stage; the old-CLI path
      falls back to the buffered launch. ViewModel feed tests pump the dispatcher — the
      task 65 lesson, written down so it is not relearned.
- [ ] The results window opens once per completed retrospective, never on rehydration, and
      ticks round-trip between window and surface because they are the same view model.
- [ ] Transcript extraction caps like the digest and never reads outside the run's id.
- [ ] Work order round-trips through the parser; refuses to write with no ticks or no
      `HarnessRepoPath`; path containment like `ReviewActionWriter`.
- [ ] ViewModel enablement: mid-run, CLI-missing, no-completed-run, rehydration from disk.

**Acceptance:** after a completed run, the Retrospective surface produces three readable
reports for that run's id; the checklist's ticked items generate one Markdown work order in
the GlassCoder repo that a fresh Claude Code session can implement without any other
context; a missing CLI, a mid-run press, and a stage failure each explain themselves instead
of failing silently.

## 5. Risks and notes

- **Cost and time.** Three staged sessions at up to $2 each, minutes not seconds. The
  per-stage progress line and per-stage budgets are the mitigation; nothing runs unbidden.
- **`--json-schema` drift.** Task 43 already handles a CLI that ignores the schema by
  falling back to prose; stage 3 inherits that (a prose answer shows as a report with no
  tickable items and says why).
- **Transcript size.** A 44-step run's raw JSONL would drown the window; the extraction step
  reuses the digest's capping rules. This is the same lesson as task 15 (never hand raw
  output to a model), applied one level up.
- **Two review vocabularies.** The critic panel's review strip and the Retrospective must
  not blur: the strip is the cheap always-on second opinion *advising a retry*; the
  Retrospective is the expensive on-demand look-back *advising the harness's own backlog*.
  The surface's empty-state text should say this in one line.
- **Settings exposure.** Config-first like task 43; a Settings section can follow once the
  defaults prove out. Remember the user-settings shadow: `%APPDATA%\GlassCoder\settings.json`
  wins over the repo's `appsettings.json`.
- **`stream-json` requires `--verbose`** in print mode, and its event vocabulary is
  version-dependent like `--json-schema` is. Both fallbacks are the same shape: degrade to
  the task 43 buffered behaviour, say so once, never fail the retrospective over narration.
- **Feed threading.** `OnOutputLine` fires on the process's output-reader thread. The view
  model marshals; nothing else may touch UI state from the callback. Task 65's tests
  caught exactly this class of bug by pumping the dispatcher — these tests copy that.
