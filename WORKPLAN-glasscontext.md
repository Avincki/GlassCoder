# Workplan — GlassContext compatibility

<!-- Authored by Claude Code, 2026-08-16. Verified against GlassCoder at
     f2944de and GlassContext at dbeb68b.

     Continues the numbering in WORKPLAN.md, which is executed through task 76.
     Tasks 77-84 are the GlassCoder half of docs/NewFeatures/glasscontext-*.html.
     Those pages proposed five tasks (77-81); this plan splits the workplan
     runner into reading (78) and executing (79), and separates the sidecar hint
     and the todo seeding that were folded into other tasks there, because each
     is independently shippable and testable.

     Written in workplan format v2 — the format GlassContext now emits, and the
     one task 78 teaches this harness to read. -->

**Total estimated time:** 52h (~6.5d)

### The contract

GlassContext is the producer, GlassCoder the consumer. As of GlassContext
`dbeb68b` the producing side of every item below is built and tested; nothing
here is blocked on it.

| Artifact | Produced by | Consumed by | Status |
|---|---|---|---|
| `CLAUDE.agent.md` (FILE 8) | `AgentProfileEmitter` — a ~470-token brief, always regenerated | `Context:RootContextFiles` | config only (77) |
| `WORKPLAN.md` v2 | `Workplan.ToMarkdown()` | a runner this plan builds | 78, 79 |
| the slug marker | stable per task, survives renumbering | `AgentRunRequest.TaskId` | 78 |
| `**Oracle:** dotnet test --filter X` | optional, per task | `VerificationRequest.TestFilter` | 80 |
| `docs/llms/<mirrored path>.md` + `.intent.json` | DocGen, path-mirrored | `read_file` on demand | 82 |
| `metrics.jsonl` | this harness | `WorkplanMetricsImporter`, joined **by slug** | 78 |

**The one rule that cannot be got wrong:** `AgentRunRequest.TaskId` must be the
task's slug. GlassContext joins run outcomes onto plan tasks by that key and by
nothing else, because position changes whenever a plan is reordered. A runner
that passes `"workplan-3"` produces metrics that silently attach to whatever is
third next week.

## 77. Load CLAUDE.agent.md as root context and measure what it is worth

<!-- task:load-agent-profile -->

- [ ] **Estimated time:** 0.5d · **Steps:** ~8

**Target files:** `config/appsettings.json`, `config/arm-agent-profile.json`

**Oracle:** `dotnet test --filter ContextAssemblyTests`

`Context:RootContextFiles` defaults to `[]`, so the worker currently has no
product identity unless the goal string carries it, and `ProvenanceStamper
.JudgeFreshness` returns fresh-by-default while measuring nothing. Point it at a
GlassContext-managed repository's `CLAUDE.agent.md` and run the suite against
`StandardArms.NoContext`, which already exists and today compares an empty list
to an empty list.

Do this first and do not skip the measurement: it is the only thing that turns
"generated context helps" from a plausibility argument into a pass@1 delta.
Do **not** raise `MaxRootContextTokens` (6,000) to make a document fit — the
profile is built to sit near 470 tokens, and a file that needs a bigger budget
is the wrong file.

## 78. Read workplan format v2

<!-- task:parse-workplan-v2 -->

- [ ] **Estimated time:** 1.5d

**Target files:** `src/GlassCoder.Core/Planning/WorkplanReader.cs`, `tests/GlassCoder.Core.Tests/WorkplanReaderTests.cs`

**Oracle:** `dotnet test --filter WorkplanReaderTests`

A reader for the format, separate from anything that executes it. Per task it
must recover: the slug from the task marker comment, the title from its heading,
the checkbox state, the estimate and optional `**Steps:** ~N`, the optional
`**Target files:**` list, the optional `**Oracle:**` command, and the
description body.

Tolerate a v1 plan — no slug, no oracle — because the plans in this repository
are v1 today. When a task has no slug, derive one from the title the way
`Workplan.Slugify` does (lowercase, non-alphanumeric to hyphens, collapse, trim,
cap at 48 characters), so the join key exists either way.

Round-trip is the test that matters: parse a v2 plan, re-render it, and get the
same bytes. GlassContext has the mirror-image tests in `WorkplanFormatV2Tests`;
port the fixtures rather than inventing new ones, so the two sides cannot drift
into disagreeing about the format.

## 79. A workplan runner in the console host

<!-- task:workplan-runner -->

- [ ] **Estimated time:** 1.5d

**Target files:** `src/GlassCoder.Host/Program.cs`, `src/GlassCoder.Host/CommandLine.cs`, `src/GlassCoder.Core/Planning/WorkplanRunner.cs`

**Oracle:** `dotnet test --filter WorkplanRunnerTests`

A `workplan` verb beside `run`, `suite`, `fixtures` and `ablate`:
`glasscoder workplan --plan WORKPLAN.md [--repo <path>] [--config <path>]`.

For each unchecked task in order, issue an `AgentRunRequest` with
`TaskId = <slug>`, `Goal` = title plus description plus target files, and
`Attempt` incremented when the same slug is retried. Run the ladder. **Tick the
checkbox only when verification passed** — the checkbox becomes a record of
oracle outcomes rather than of the model's opinion, which is the whole reason
this harness exists. Write the file back the way any other change lands.

Stop at the first failure: the tasks are dependency-ordered, so running past a
failed prerequisite measures nothing. Re-invocation resumes at the first
unchecked task, with attempt numbers intact in `metrics.jsonl`.

Exit codes follow `HostExitCode` — `0` when every task passed, `1` when a task
did not, `3` when a limit stopped one. A task with no oracle line runs but is
**never ticked automatically**; report it as needing a human decision. Estimated
in days rather than steps on purpose: this is several agent runs' worth of work
and should not be attempted as one.

## 80. Honour the per-task oracle as the run's test filter

<!-- task:oracle-as-test-filter -->

- [ ] **Estimated time:** 0.5d · **Steps:** ~12

**Target files:** `src/GlassCoder.Core/Planning/WorkplanRunner.cs`, `src/GlassCoder.Core/Verification/VerificationLadder.cs`

**Oracle:** `dotnet test --filter WorkplanOracleTests`

`VerificationRequest.TestFilter` already exists and already scopes the unit-test
rung. Pass the `**Oracle:**` line's `--filter` expression straight into it, and
gate the checkbox on that rung specifically: a task whose named tests fail is not
done, whatever the other rungs said.

Guard the failure mode that makes an oracle worse than none — a filter matching
zero tests must fail the task loudly, not pass it vacuously. `list_tests` and the
zero-match refusal work from tasks 51 and 70 already know how to say this.

## 81. Stamp the context by content, not only by timestamp

<!-- task:context-content-hash -->

- [ ] **Estimated time:** 0.5d · **Steps:** ~10

**Target files:** `src/GlassCoder.Core/Provenance/ProvenanceStamper.cs`, `src/GlassCoder.Core/Metrics/RunMetrics.cs`

**Oracle:** `dotnet test --filter ProvenanceTests`

`JudgeFreshness` compares the newest `RootContextFiles` mtime against the newest
source file. That answers "might it be stale", which is the right question for a
warning and the wrong one for a comparison: two runs can carry identical context
with different timestamps, or different context with the same one.

Add a content hash of the concatenated root context files to `ProvenanceStamp`
and carry it into `RunMetrics`, so a fresh-versus-stale ablation keys on what the
run actually saw. Keep the mtime judgement — it is what makes the warning cheap;
the hash is what makes the comparison trustworthy.

## 82. Hint the intent sidecar on first touch of a file

<!-- task:intent-sidecar-hint -->

- [ ] **Estimated time:** 0.5d · **Steps:** ~12

**Target files:** `src/GlassCoder.Tools/FileSystem/ReadFileTool.cs`, `src/GlassCoder.Core/Context/ContextAssembler.cs`

**Oracle:** `dotnet test --filter IntentHintTests`

When the agent reads a source file for the first time in a run, append one line
to the observation naming its documentation page — `src/foo/Bar.cs` →
`docs/llms/foo/Bar.md`, with the `.intent.json` sidecar beside it. One line, on
the observation, not a dump of the tree: even a small documentation tree is the
wrong resident set, and the profile in task 77 deliberately tells the worker to
retrieve rather than preload.

Measure whether it moves tokens-to-solve. If it does not, the hint is costing a
line per read for nothing and should be deleted rather than kept out of
politeness.

## 83. Keep the task's plan visible after compaction

<!-- task:seed-and-reinject-todos -->

- [ ] **Estimated time:** 0.5d · **Steps:** ~12

**Target files:** `src/GlassCoder.Tools/Planning/TodoTool.cs`, `src/GlassCoder.Core/Context/IConversationCompactor.cs`

**Oracle:** `dotnet test --filter TodoCompactionTests`

`update_todos` is invented mid-run and falls out of the window when the
conversation is compacted. Seed it from the workplan task's description and
target files at step 0, and re-inject the current list after each compaction.

The failure this addresses is not "lost in a huge tree" — on a small C# solution
that does not happen. It is run `e426f418`: a finished todo list impersonating a
finished goal. Seeding from a task with an oracle attached means the list and the
ground truth come from the same place, so agreeing with itself is no longer
evidence.

## 84. Run a tutorial workplan from empty as a project-scale benchmark

<!-- task:tutorial-workplan-benchmark -->

- [ ] **Estimated time:** 1d

**Target files:** `src/GlassCoder.Lab/Suite/`, `config/arm-tutorial-benchmark.json`

**Oracle:** `dotnet test --filter SuiteFixtureTests`

An ordinary workplan executes against a live, moving repository, so its runs
measure progress rather than comparable capability — keep those out of ablation
comparisons. GlassContext's Module F tutorial generator is the exception: it
mines a finished project's history into a from-scratch rebuild plan, and a plan
that starts from an empty directory starts byte-identical on every arm, exactly
like a suite fixture.

Run one as a suite entry with per-task oracles, and compare it against the same
plan under `no-context`. That is a project-scale benchmark generated from a real
project rather than hand-written — the natural successor to suite-01 through
suite-08. Depends on 79 and 80; the oracles are what make it a benchmark rather
than a demo.

### Acceptance

- Running the suite with `CLAUDE.agent.md` as root context produces a measured
  pass@1 and tokens-to-solve delta against `no-context` — a number, not an
  opinion.
- A workplan checkbox is ticked by the harness only after the task's named tests
  passed; a run the model merely believed finished leaves the box unticked.
- Re-invoking the runner after an interruption resumes at the first unchecked
  task, with attempt numbers intact in the metrics.
- `metrics.jsonl` records each run against the task's **slug**, and
  GlassContext's Workplan tab joins them onto the plan with no unmatched ids.
- A task whose oracle filter matches no tests fails loudly rather than passing.
- Two runs carrying identical root context share a context hash, whatever their
  file timestamps say.
- A tutorial workplan executed from an empty directory yields per-arm comparable
  metrics and counts as a project-scale suite entry.
