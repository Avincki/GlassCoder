# History

Dated session logs: what shipped, what was decided and why, and what is still
open. Newest first.

The point of this file is resumption. Anything derivable from the source or the
commit log does not belong here — decisions, their reasoning, and open threads
do, because those are what a later session cannot cheaply rediscover.

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
