# Task: The trajectory check — a model's eye on the run, between the counters and the panel

**Status:** proposed — not yet appended to WORKPLAN.md; this file keeps the task review
and the why behind each decision
**Prepared:** 2026-08-18
**Origin:** operator proposal — the critic should also function as a monitor of the process,
working in parallel with the worker, observing prompts and tool calls, checking that the work
stays on course and does not become circular, and injecting an instruction into the worker
when it sees an issue. Evaluated 2026-08-18; the goal was accepted and the topology revised
(see §1).
**Proposed workplan number:** 78 (77 is the highest at time of writing — renumber if the
workplan has grown by the time this is picked up)

---

## 1. The task as asked, and the shape it settled into

> Would it make sense that the critic would also function as a monitor of the process?
> It would work in parallel with the worker. It would monitor the prompts and tool calls,
> observe that the tasks would be of quality and that the process does not become circular.
> In case of an issue, it would inject an instruction to the worker.

The evaluation accepted the goal and rejected the topology, for three reasons recorded in
full in §2: the critic's refutation calibration does not transfer to judging a process in
motion (§2.1); genuinely parallel injection either races the loop or gates every step
(§2.2); and most of the proposal already exists deterministically as `RunProgressSentry`,
which watches every tool call and injects corrective instructions through five nudge points
in the loop (`AgentLoop.cs:418-443`).

What settled out is an **escalation ladder** with one new rung:

| Rung | Organ | Detects | Cost |
|---|---|---|---|
| 1 | `RunProgressSentry` (exists) | Byte-identical repeats, repeated failure signatures, worn path reads, repeated failing tests, cached re-verification, completion over a red tree | Free, per step |
| 2 | **Trajectory check (this task)** | Semantic circularity and drift — plausible work that is not converging on the goal | One small model call, every N steps |
| 3 | Critic panel / `RunReviewer` (exist) | A finished claim the evidence does not support | A panel, once per completion claim / run |

The trajectory check is the semantic gap between rungs 1 and 3: the run that rephrases the
same failed approach with cosmetic variation (the sentry's fingerprints include arguments
and answers, so a reworded grep or a differently-shaped failing edit reads as novel — the
same blindness run `c5eb67f6` exposed for path reads, which got a special case rather than
a general answer), and the run doing busy, well-formed, goal-irrelevant work that trips no
counter at all.

## 2. Review of the concept — the decisions and their reasons

### 2.1 It is not the critic. It is a third question, allowed to wear the critic's model.

The panel's question is *"can you refute the claim that finished work meets its goal?"* —
an asymmetry chosen deliberately (`CriticPanel.cs:104-117`), and one this repository has
already watched misfire when pointed at work in motion: run `4b582162`, a temperature-0
critic refuting 14 of 14 intermediate steps, because no single step toward a goal carries
evidence the goal is met. The panel's prompt was rewritten specifically to escape that
deadlock, and runs `008007e1` and `ca727be3` are what it cost before the rewrite.

The trajectory check asks the opposite-tempered question: *"given the goal and this record
of steps, is the run converging, drifting, or looping — and if not converging, what one
instruction would redirect it?"* Its default answer must be *converging*, the mirror of the
panel's default *refuted*. That is a different prompt, a different threshold, and a
different failure mode — a **third role in spirit**, even when configuration points it at
the same served model as the critic (which remains the right default: a different model
family from the worker, so the checker's blind spots are not the worker's —
`ModelRoles.cs:22-24`).

Concretely: a new `ITrajectoryChecker`, its own options section, its own prompt. Nothing in
`CriticPanel` changes. The two share only `IChatClientFactory` and, by default, the
`critic` role's serving.

### 2.2 Between steps, never parallel

The loop is synchronous per step. A monitor running genuinely in parallel delivers its
verdict on step N while the worker executes step N+2, and a stale steering instruction is
worse than none — noise the worker must reconcile. The alternative, gating each step on the
monitor, adds a model round-trip per step and roughly doubles wall-clock on local serving.

The only clean synchronization point is the one the sentry already uses: **after the step's
observations and verification, before the next model call**. The trajectory check runs
there, as a sixth conditional injection alongside the five nudges — same message channel
(`ChatRole.User`), same position in the step (`AgentLoop.cs:418-443`), so the worker
receives one coherent story per step.

### 2.3 Trigger policy: cadence, one voice per step, cooldown

- **Cadence, not continuous.** A check every `CheckEverySteps` steps (default 10). No check
  before the first interval elapses — a run needs a trajectory before one can be judged.
- **Skip the step when a sentry nudge fired.** Two corrective voices in one step compete;
  the deterministic one already said something specific. The check slides to the next step.
- **Cooldown after an injection** (`CooldownSteps`, default 5): an instruction must be given
  room to work before the checker judges the run again, or the checker ends up grading its
  own advice.
- **Hard cap per run** (`MaxInjectionsPerRun`, default 3). A run that needs steering four
  times is a run the budgets and the sentry's `StopVerdict` should be allowed to end; a
  checker that keeps talking becomes the second model in a two-model argument, and
  oscillation between two steering voices is a failure mode this design must not create.
- **Never the same instruction twice**, on the sentry's own once-per-signature precedent.

### 2.4 Input: a trajectory digest, never the transcript

Raw history would drown the checker's window and its latency. The compactor already knows
how to render a run compactly — one row per distinct call with a repeat count, outcomes
marked, "ten identical refusals are one fact, not ten lines"
(`IConversationCompactor.cs:95-151`). The digest builder for the checker reuses those
conventions over the run's `ToolInvocation` records:

1. The goal, verbatim.
2. The plan state, when a todo list exists (titles and statuses only).
3. The call rows with repeat counts and ✓/✗ outcomes, in order of first use.
4. The last verification outcome (passed / failed rung / notice).
5. Budget position: steps used of max, tokens used of max.
6. The worker's most recent reasoning text, shortened (the compactor's 600-character rule).

Capped at `MaxDigestCharacters` (default 8,000) — the task 15 rule, never hand a model raw
output, applied one level up. Prose the digest writes must never be prose the checker is
told to key on; it judges shape, not wording.

### 2.5 Output contract and injection rules

The checker replies JSON-only, on the panel's parsing precedent (extract the outermost
braces, tolerate prose around them, treat unparseable as unavailable):

```json
{ "trajectory": "converging" | "drifting" | "looping",
  "confidence": 0.0-1.0,
  "instruction": "one or two sentences, only when not converging" }
```

- `converging` → nothing happens. This must be the common case, and the prompt says so.
- `drifting`/`looping` with `confidence >= ConfidenceThreshold` (default 0.6) → the
  instruction is injected as a `ChatRole.User` message, prefixed so the transcript reader
  knows who is speaking (the nudges' precedent): *"A periodic review of this run's
  trajectory found it may be {drifting|looping}: {instruction}"*.
- Below threshold, or no instruction → recorded, not injected.
- Unreachable checker, timeout, or garbage → a non-event for the run. The verdict is
  recorded as unavailable — the panel's rule that a critic which could not be reached is a
  different fact from one that judged and accepted (`CritiqueVerdict.Available`), applied
  here as: an unavailable checker steers nothing and blocks nothing.

Every check — including converging verdicts and unavailable ones — is written to the
transcript as a typed record (`ReviewRecord`'s precedent, `RunReviewer.cs:219-236`): step
index, verdict, confidence, instruction, whether it was injected, tokens, latency, and cost
at the checker role's own prices (`CritiqueResult.EstimatedCostUsd`'s precedent). An
opinion that shaped a run and left no trace was the one thing the review surface could not
reconstruct; that lesson is already paid for.

### 2.6 What the trajectory check must never do

- **Never a gate.** It advises; it cannot block a call, refuse a completion, or stop the
  run. This repository has paid twice for gates that would not concede (`5c071f37`,
  `a408b61b`), and "you seem to be drifting" is weaker evidence than either of those gates
  had. Stop decisions remain deterministic: budgets and `StopVerdict`.
- **Never a retry.** It steers the current attempt; it never grants a second one. Pass@1
  measured over attempts a model decided to grant is not pass@1 (CLAUDE.md §11 — the line
  `RunReviewer` holds, held here too).
- **Never on by default.** It is a Phase-2-style capability, off until measured, like
  critique (`CritiqueOptions.Enabled`).
- **Never billing the worker's budget.** Checker tokens are metered and costed in the
  record and the run totals, but do not consume the worker's token budget — the monitor
  must not starve the thing it monitors. Its own spend is bounded by cadence and the
  injection cap, not by a share of the worker's window.

### 2.7 It is an ablation arm before it is a feature

Mid-run steering changes what a "pass" means, so its value is an empirical question the Lab
answers, not a setting an operator trusts. Two obligations, both enforced by existing
patterns:

- A `WithTrajectoryCheck` arm in `StandardArms`, watching pass@1, steps-to-solve, and
  stalled/repeated-failure stop rates against baseline.
- The baseline's `NoOptionalCapabilities()` dictionary **must name the new lever off**
  (`TrajectoryCheckOptions:Enabled = false`). A lever an arm does not name is a lever the
  arm does not control — an operator who tried the feature by hand would silently make
  every arm a trajectory-check arm (`StandardArms.cs:20-34`, and the guard test that
  caught `AnswersDisabled` going unnamed is the test to extend).

The interesting readings: does the arm reduce runs that die on `Stalled` /
`RepeatedToolFailure` / token limits? Does it reduce steps-to-solve on runs that were going
to pass anyway (it should not — a converging run should never hear from it)? And the
injection count itself: a healthy checker on a healthy harness should be mostly silent.

## 3. What already exists, and is reused rather than rebuilt

- **`RunProgressSentry` + the loop's nudge points** (`AgentLoop.cs:418-443`) — the entire
  inject-an-instruction mechanic, the once-per-signature discipline, and the proof that
  mid-run steering messages are compatible with the loop's design.
- **The compactor's digest conventions** (`IConversationCompactor.cs:87-164`) — rows with
  repeat counts, outcome marks, the shortened-reasoning tail. The checker's digest builder
  is these rules over `ToolInvocation` records rather than folded messages.
- **`CriticPanel`'s transport habits** — JSON-extraction parsing with a prose fallback,
  temperature 0, unavailable-is-not-a-verdict, per-role cost accounting. The checker is one
  call, not a panel, but it borrows every one of these.
- **`ReviewRecord` / `IStepLogger.LogReview`** — the precedent for persisting a model
  opinion into the transcript as a typed record; the trajectory record follows it.
- **`ModelRoles` / `IChatClientFactory`** — role-addressed serving; the checker gets a
  configurable role defaulting to `critic`, and `CanCheck` mirrors `CanCritique`'s
  configured-and-usable test.
- **`StandardArms` + the ablation runner** — the measurement harness, and the
  every-lever-named baseline discipline.

## 4. Implementation plan

Written in the workplan's shape so it can be appended as task 78. **Estimated time: 3d.**
Depends on tasks 10 (the loop), 12 (digest conventions), 23 (critique — for the transport
habits and the role plumbing), 22 (ablation runner).

### 78. The trajectory check: a model's eye on the run, between the counters and the panel

#### a. Options and records (0.5d)

- [ ] `TrajectoryCheckOptions` (`GlassCoder:TrajectoryCheck`): `Enabled` (default false),
      `Role` (default `critic`), `CheckEverySteps` (10), `CooldownSteps` (5),
      `MaxInjectionsPerRun` (3), `ConfidenceThreshold` (0.6), `MaxDigestCharacters`
      (8000). Binds like `CritiqueOptions`; registered in
      `GlassCoderServiceCollectionExtensions`.
- [ ] `TrajectoryVerdict` record (trajectory, confidence, instruction, available) and
      `TrajectoryCheckRecord` for the transcript (step, verdict fields, injected flag,
      tokens, latency, cost, recorded-at). Failures are values, never exceptions.

#### b. The digest builder (0.5d)

- [ ] A builder in `GlassCoder.Core.Agent` producing §2.4's six sections from the run's
      accumulated `ToolInvocation`s, plan state, last verification, and budget. Reuses the
      compactor's row/repeat/outcome rendering rules (extract the shared rendering into a
      helper both call rather than duplicating it). Capped at `MaxDigestCharacters`, oldest
      rows dropped first, the drop stated in the digest ("earlier calls omitted") — no
      silent truncation.

#### c. `ITrajectoryChecker` (0.5d)

- [ ] Interface: `bool Enabled`, `bool CanCheck(string? role)`,
      `Task<TrajectoryVerdict> CheckAsync(string goal, string digest, string role,
      CancellationToken)`. Default implementation on `IChatClientFactory`, temperature 0,
      JSON-only reply, the panel's brace-extraction parse; anything unparseable or
      unreachable returns `available: false`. Token usage and role-priced cost captured.
- [ ] The system prompt encodes §2.1's temper: default `converging`; `looping` needs the
      record to show substantially the same work attempted more than twice; `drifting`
      needs steps unconnected to the goal; the instruction must name a concrete next
      action, not a critique of past ones; one or two sentences.

#### d. Loop integration (0.5d)

- [ ] In `AgentLoop`, after the sentry nudges (`AgentLoop.cs:440-443` today): if enabled,
      the role answers `CanCheck`, the cadence has elapsed, no sentry nudge fired this
      step, the cooldown is clear, and the injection cap is not reached — build the
      digest, call the checker, apply §2.5's injection rules, log the record. An optional
      constructor dependency on the `_verifier`/`_intents` pattern; absent means off.
- [ ] Checker latency is metered separately in the step record (the verification-latency
      precedent) so its cost to wall-clock is visible per run.

#### e. Metrics and the ablation arm (0.5d)

- [ ] `RunMetrics`: checks run, injections, checker tokens, checker cost.
- [ ] `StandardArms`: `WithTrajectoryCheck` (single lever on), and
      `TrajectoryCheckOptions:Enabled = false` added to `NoOptionalCapabilities()` —
      extend the guard test that keeps the baseline honest.

#### f. Tests (0.5d, alongside the above)

- [ ] Options bind; disabled means the loop never constructs a digest or calls the checker.
- [ ] Cadence over a faked `IChatClient`: no check before step `CheckEverySteps`; a sentry
      nudge slides the check to the next step; cooldown holds after an injection; the
      per-run cap holds; the same instruction is never injected twice.
- [ ] Verdict handling: converging injects nothing; drifting below threshold injects
      nothing but records; drifting above threshold injects the prefixed message; an
      unreachable checker is a non-event for the run and an `available: false` record.
- [ ] Digest: capped with the omission stated; rows follow the repeat-count convention; a
      run with a plan includes plan state, one without omits the section.
- [ ] Transcript: every check writes a `TrajectoryCheckRecord`; a replayed JSONL log
      reconstructs which steps were checked and what was injected (the task 11 invariant).
- [ ] Worker budget untouched by checker tokens; run cost totals include them.

**Acceptance:** with the feature enabled and a fake checker scripted to answer
`looping`, a run receives exactly one prefixed instruction per cooldown window and at most
`MaxInjectionsPerRun` in total, every check is reconstructable from the transcript, and a
scripted `converging` checker leaves the run byte-identical to one with the feature
disabled except for the records; the Lab can run `with-trajectory-check` against baseline
and report the delta.

## 5. Risks and notes

- **Two steering voices.** The sentry and the checker both inject; the skip-on-nudge rule
  and the cooldown are the mitigation, and the cap is the backstop. If transcripts show the
  two contradicting each other, the checker's digest should carry the sentry's recent
  nudges so it steers *with* the deterministic voice, not across it — noted as a follow-up,
  not built speculatively.
- **The checker is a model, and models flatter.** A checker that answers `drifting` too
  eagerly re-creates run `4b582162` one rung down. The prompt's default-converging temper
  and the confidence threshold are the guard; the ablation arm is the judge. If the
  injection rate on passing runs is not near zero, the prompt is wrong.
- **Local-serving latency.** One call every ten steps is bounded, but on a busy local
  server it still queues behind the worker. The per-check latency in the step record is
  what makes this visible; if it matters, the cadence widens in config, not in code.
- **Cost when the role is hosted.** The critic role can be a paid endpoint. Cadence, the
  digest cap, and per-record costing bound and expose the spend; the run totals carry it.
- **Prompt drift.** The checker's system prompt will need the same maintenance discipline
  as the panel's — `CriticPanel.cs` carries four generations of scar tissue about prompts
  asserting limitations later tasks removed. Keep the checker's claims about what the
  worker can and cannot do out of its prompt entirely; it judges the record, not the
  toolbox.
- **Settings exposure.** Config-first, like critique; a Settings section can follow once
  the arm reports. Remember the user-settings shadow: `%APPDATA%\GlassCoder\settings.json`
  wins over the repo's `appsettings.json`.

## 6. Open questions for the operator

1. **The checker's role.** Default here is the `critic` role (uncorrelated blind spots,
   already provisioned). The cheaper alternative is the `worker` role judging its own
   trajectory from outside the conversation — weaker as a monitor, free as a resource.
   Config decides per machine; the default deserves a nod.
2. **Cadence default.** 10 steps is a guess sized to typical run lengths in the
   retrospectives (25–50 steps → 2–4 checks per run). Shorten to 6–8 if the first
   transcripts show loops establishing themselves faster than the checker wakes.
3. **Naming.** "Trajectory check" is used throughout this file; it names what is judged
   rather than who judges, which is the distinction §2.1 exists to keep. Alternatives
   considered and declined: *process monitor* (implies the parallel topology this design
   rejects), *supervisor* (implies authority it must not have).

## How to use this

When the feature is scheduled: append §4 to `WORKPLAN.md` as task 78 (renumbering if
needed), implement a→f in order in this repository, and keep this file as the record of
what was asked for and why the shape is what it is. Tick nothing here. Add what ships to
`HISTORY.md`.
