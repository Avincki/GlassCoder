# History

Dated session logs: what shipped, what was decided and why, and what is still
open. Newest first.

The point of this file is resumption. Anything derivable from the source or the
commit log does not belong here — decisions, their reasoning, and open threads
do, because those are what a later session cannot cheaply rediscover.

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
