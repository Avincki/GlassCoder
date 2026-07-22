# Building a Local AI Coding Agent — Evaluation & Build Guide

> **Field guide.** An evaluation of ClaudeContextGenerator, the case for pairing it with a local LLM, and a development roadmap for a learning environment that teaches you how an application like Claude actually works — by measuring it.

**Rev. 2026-07-22** · Hardware: DGX Spark · 8 × RTX PRO 6000 · DGX Station (option) · Stack: C# / .NET · Goal: understand, then build

```
capability ≈ model × harness × context
```

The throughline of this whole document. The first revision assumed a local build would be badly weaker on *model* — Part 03 retires that assumption: 768 GB of VRAM runs frontier-class open weights. What stays scarce is interconnect, wall-clock, and long-horizon reliability, so the interesting engineering is still *harness* and *context*. It's a product, not a sum: a zero on any term zeroes the result.

## Contents

- [Part 00 — How to read this](#part-00--how-to-read-this)
- [Part 01 — Evaluating ClaudeContextGenerator](#part-01--evaluating-claudecontextgenerator)
- [Part 02 — Drift & the TeamCity loop](#part-02--drift-solved-by-pipeline--and-what-that-changes)
- [Part 03 — The machines you have](#part-03--the-machines-you-have--and-what-each-retires)
- [Part 04 — Recalibrating the case](#part-04--recalibrating-what-context-can-and-cant-buy-back)
- [Part 05 — Local vs API economics](#part-05--what-it-actually-costs--local-vs-the-claude-api)
- [Part 06 — Installing the local LLM](#part-06--installing-the-local-llm)
- [Part 07 — Harness architecture](#part-07--harness-architecture)
- [Part 08 — Building it in C#](#part-08--building-it-in-c--yes-and-its-a-better-fit-than-it-looks)
- [Part 09 — Compile feedback](#part-09--compiling--how-the-agent-learns-it-was-wrong)
- [Part 10 — The measurement lab](#part-10--the-measurement-lab--the-actual-teacher)
- [Part 11 — Build roadmap](#part-11--build-roadmap--one-variable-at-a-time)
- [Part 12 — Principles & pitfalls](#part-12--principles--pitfalls)
- [Part 13 — Starter kit](#part-13--starter-kit--enough-to-begin-building)

---

## Part 00 — How to read this

This is two documents fused into one. Parts 01–04 are an **evaluation** — of ClaudeContextGenerator (CCG) on its own terms, of the pipeline that keeps its output fresh, of the hardware you're building on, and of what that hardware does to the original "weak local model" thesis. Parts 05–11 are a **build guide** — installing the model server, a reference architecture, the C# stack, a measurement lab, a phased roadmap, and a startable kit. Read it top to bottom the first time; return to Parts 05–08 as working references.

> **◆ What changed in this revision**
>
> Several inputs reshaped the document. **The hardware is now known** — and it's far stronger than "a local box," which retires most of the weak-model pessimism (Part 03, Part 04). **Drift is already automated** via TeamCity, which closes the largest open risk in Part 01 and moves the highest-ROI investment elsewhere (Part 02). **The stack is C#**, which turns out to cost you almost nothing (Part 08). And there are now concrete chapters on **the economics** against the Claude API (Part 05), **installing the model server** (Part 06), and **compile feedback** — the fastest oracle in the loop, and the one most likely to be built badly (Part 09).

### One correction to the mental model first

The instinct to picture Claude as "an LLM plus a large collection of agents" slightly overweights the swarm. Claude Code is mostly **one strong model in a tight agentic loop** with a good tool set and disciplined context management. Sub-agents and parallel fan-out are a *layer you add*, not the core. So if the aim is to understand how it works, the foundational thing to build and instrument is the single-agent loop — get that deeply observable before you orchestrate many of them.

> **◆ Insight**
>
> Capability is a **product of three factors**. A brilliant model with no tools can't edit a file; a great harness driving a model that can't emit a valid tool call stalls on the first step; perfect context handed to a model that can't reason is wasted. Your local experiments are really about measuring the shape of that product surface.

---

## Part 01 — Evaluating ClaudeContextGenerator

Judge each artifact CCG produces by one question: does it tell the agent something it **couldn't cheaply get on its own**, reliably, and fresh? Modern coding agents can `Read`, `Grep`, and `Glob` the source themselves, so the value of any generated file is roughly:

```
value ≈ (what it adds beyond trivial discovery) × reliability of consumption × freshness
```

Minus the cost to build, maintain, and the risk of going stale. Almost every verdict below falls out of this.

### Really added value

*High-leverage for preparing a repo to be executed well*

| Artifact | Why it lifts execution | Verdict |
|---|---|---|
| **Root `CLAUDE.md`** (esp. example-aware / P4) | Auto-read on session start. Front-loads structure, build facts, conventions, doc map, path rules — exactly what's expensive to rediscover each time. Reading real example projects captures conventions a template can't. | **Double down** |
| **`WORKPLAN.md`** with checkbox progress | Turns a vague goal into an ordered, resumable plan the agent reads and ticks off across sessions. The most direct embodiment of "execute better." | **Keep** |
| **History-file convention** (dated session logs) | Cheap, compounding cross-session memory — decisions made, what's open, next steps. The difference between re-learning the repo and resuming. | **Keep** |
| **Intent enrichment** (Path 2) | The one docs feature that adds what the agent can't cheaply get: cross-file intent, gotchas, side-effects, test-mined examples. Worth caching — *on the files that carry intent.* | **Keep, targeted** |
| **PathGuard** immutability | Not a feature, an enabler. A tool that writes into many repos is unusable without a hard guarantee it never mutates source. | **Foundational** |
| **Context Test** | Feeds the agent a task using only the generated docs and checks the result — a rare, direct feedback loop on whether the prepared context actually works. | **Underrated** |

### Less relevant / over-invested for this aim

*Effort exceeds payoff when the goal is "prepare the repo for an agent"*

| Feature | Why it's lower-relevance | Verdict |
|---|---|---|
| **Signature-level `docs/llms`** | A parallel tree of signatures + doc-comments largely re-solves what the agent already does by reading source, and adds an indirection hop. Low marginal value unless intent-enriched. | **Trim** |
| **Multi-model reviewer scoring** (Module C) | Heavy machinery — six-criterion composite scores, o3-pro polling, parse-error banners — to polish a document authored once and human-edited. The tell: the score threshold is explicitly advisory and never blocks Accept. | **Simplify** |
| **Multiple reviewer providers** (OpenAI + Grok tiers) | Breadth that doesn't advance the goal. A second-opinion critique needs one reliable path, not tiered subscription handling. | **Trim** |
| **Tutorial Generator** | Aimed at human onboarding, not the agent's execution context. Tangential. | **Out of scope** |
| **Whole-repo Path 2 coverage** | Intent value is concentrated in ~10–20% of files; running whole-file re-documentation everywhere spends the most tokens on the files that need it least and maximizes stale surface. | **Target it** |

> **▲ Trap — staleness (now largely closed)**
>
> The biggest structural risk in CCG isn't a missing feature — it's **drift**. Generated context that no longer matches the source doesn't just lose value; it *actively misleads* the agent, which is worse than no context. The content-hash cache controls regeneration cost, not drift.
>
> This revision downgrades the risk: **TeamCity already triggers CCG on source change**, so mechanical freshness is handled. That's a significant win — but it relocates the problem rather than eliminating it. Part 02 covers what the pipeline solves, what it doesn't, and the three failure modes automation introduces.

**Net for CCG:** compressed to what genuinely makes a project execute better, it's a sharp example-aware `CLAUDE.md` + a checkbox workplan + the history convention + *selective* intent enrichment, all regenerated by the pipeline. With freshness automated, the highest-ROI remaining investment is no longer a freshness mechanism — it's **making the Context Test a build gate**, so the pipeline proves the context still works instead of only proving it's current.

---

## Part 02 — Drift, solved by pipeline — and what that changes

With TeamCity watching VCS changes and invoking CCG automatically, generated context stops being *a document somebody has to remember to refresh* and becomes something much better defined:

> **◆ Insight — context is a build artifact**
>
> Once regeneration is triggered by the same event that triggers a compile, `CLAUDE.md` and `docs/llms/**` are **derived outputs**, not documents. Every consequence follows from that reframe: they're reproducible from *source + pinned generator version*, merge conflicts in them are resolved by regenerating rather than by hand, they don't belong in code review as prose, and their correctness is established by *testing*, not by reading. It also means a broken context build is a broken build — treat it with the same urgency.

### What automation buys, and what it quietly introduces

Continuous regeneration solves the hard part. It also creates four failure modes that don't exist when a human presses the button — each cheap to prevent up front and annoying to discover later.

*Design the build configuration around these:*

| Concern | Why it bites | What the build config should do |
|---|---|---|
| **Trigger loop** | CCG's own commit is a VCS change, which retriggers the build, which commits again. The classic CI ouroboros. | Add a trigger rule excluding the generated paths and the bot identity — e.g. `-:user=ccg-bot:**` plus `-:**/CLAUDE.md`, `-:docs/llms/**`. Verify by watching the build queue after a bot commit, not by reasoning about it. |
| **Cost per commit** | Whole-repo Path 2 intent enrichment on every push spends the most tokens on the files that change least. This is Part 01's "target it" verdict, now with a meter running. | Let the content-hash cache do its job: enrich only files whose hash changed *and* that are on the intent-carrying list. Log tokens per build so the cost is visible. |
| **Diff churn** | LLM-generated prose differs run to run even when the source didn't change, producing noisy diffs that train everyone to ignore the file. | Pin the generator model and use temperature 0 for enrichment; never rewrite a file whose source hash is unchanged. Deterministic-in, deterministic-out. |
| **Silent pipeline failure** | If the context build goes red and nobody notices, the repo looks fresh and is quietly frozen — the exact failure the automation was meant to prevent, now invisible. | Alert on failure, and stamp provenance *into* the generated file (source SHA + UTC timestamp + generator version) so staleness is legible from the artifact itself. |

> **▲ Trap — fresh is not the same as correct**
>
> The pipeline guarantees the context was *regenerated from current source*. It guarantees nothing about whether the result actually helps an agent execute. A confidently wrong intent summary regenerated on every commit is **perfectly fresh and still harmful** — and a weaker local model is precisely the one least likely to notice the contradiction.
>
> This is why the **Context Test belongs in the pipeline**. It was flagged as "underrated" in Part 01 for a reason: it's the only artifact CCG produces that closes the loop empirically. As a build step it becomes a regression gate — run a fixed task suite against the regenerated context and fail (or warn) when pass rate drops. That converts your context from *maintained* to *verified*, and it's the single highest-value thing left to add.

### The integration surface

One practical note, since CCG is a WPF application: a build agent can't drive a desktop UI. The pipeline needs a **non-interactive entry point** — a console host that runs the same services, takes a config path and a repo root, writes nothing outside the managed output allow-list, and returns a meaningful exit code.

The architecture already supports this cleanly: `ClaudeContextGenerator3.Core` is specified as a class library with *zero UI dependency*, so a console host is a thin shell over the services that already exist rather than a parallel implementation. The same discipline that made Core testable makes it automatable — which is the usual reward for keeping a UI-free core.

**Build config — shape of the TeamCity job (reference):**

- **Trigger:** VCS change on the default branch, with exclusion rules for generated paths and the bot author.
- **Steps:** `ccg.exe generate --repo . --config ccg.ccgproj` → `ccg.exe context-test --suite tests/context/` → commit + push if the working tree changed.
- **Artifacts:** Token spend, files regenerated, Context Test pass rate — published per build so the trend is visible.
- **Gate:** Context Test pass rate must not regress. Warn first, enforce once the suite is stable.

**Net:** automating regeneration is the right call and closes the risk Part 01 named. It moves CCG's remaining weak point from *currency* to *usefulness* — and usefulness is measurable, which is convenient, because measuring things is what the rest of this document is about.

---

## Part 03 — The machines you have — and what each retires

The first revision of this guide hedged on hardware and reasoned about a single "weaker local model." This revision has to hold **three machines** in view: the **DGX Spark** already on your desk, the **8 × RTX PRO 6000 build** this guide is specified against, and a possible **DGX Station GB300**. They are not three sizes of the same thing — each maximizes a different term of the capability formula, and each retires a different assumption. The comparison at the end of this part is the honest answer to "which machine for which job."

### The anchor: the eight-GPU build

Everything downstream — the economics in Part 05, the topology advice in Part 06, the ablation grid in Part 10 — is calibrated against this box, so it stays the anchor. And the headline stands: **this is not a weak-model machine.** 768 GB of GPU memory puts frontier-class open weights fully resident in VRAM — the same tier of model that scores near the top of public agentic-coding leaderboards.

| Spec | Value | Detail |
|---|---|---|
| GPU memory | **768 GB** | 8 × RTX PRO 6000 Blackwell, 96 GB GDDR7 ECC each, ~1.79 TB/s per card. |
| Host RAM | **1.5 TB** | DDR5 ECC — expert offload, page-cached weights, many containers. |
| CPU threads | **256** | 2 × EPYC 9535. Your verification engine, not a bottleneck. |
| Data NVMe | **15.36 TB** | Gen5. Roughly 15–20 frontier checkpoints resident at once. |

### What actually fits

Rule of thumb for weights: **bytes ≈ params × bytes-per-param** (FP8 ≈ 1, FP4/NVFP4 and INT4 ≈ 0.5–0.6), then add 15–30% for KV cache, activations and CUDA graphs. Figures below are planning-grade — the open-weights field churns monthly, so re-check before committing to a family.

*Open-weight coding models against 768 GB — as of July 2026*

| Model | Params | ≈ FP8 weights | GPUs needed | Role on this box |
|---|---|---|---|---|
| **Qwen3-Coder-Next** 80B-A3B | 80B / 3B active | ~80 GB | 1 (4-bit) – 2 | The workhorse. Fast enough to run many replicas — sub-agents, greps, summarizers. |
| **DeepSeek V4 Flash** | 284B / 13B active | ~284 GB | 3–4 | Strong mid-tier with a 1M context window. |
| **GLM-4.6** | 357B | ~357 GB | 4–5 (2–3 at 4-bit) | MIT-licensed, widely reported at Claude-Sonnet-class coding. |
| **Qwen3-Coder-480B** A35B | 480B / 35B active | ~480 GB | 6 (3 at 4-bit) | Apache-2.0, ~69.6% SWE-bench Verified. The primary drafter candidate. |
| **Kimi K2** 1T-A32B | 1T / 32B active | ~1 TB — won't fit | 6–7 at 4-bit | Agentic-coding specialist; 4-bit only, and it eats the box. |
| **DeepSeek V4 Pro** | 1.6T / 49B active | ~800 GB at FP4 | All 8, tight | Ceiling experiment. Little room left for KV cache. |

Read that table twice, because it inverts the premise of the original document. Your *model* term is not the weak link — you can host something in the same conversation as a frontier API model. The constraint moved somewhere less obvious.

### The real constraint: interconnect

The RTX PRO 6000 Blackwell has **no NVLink**. Cards talk over PCIe Gen5 ×16 — roughly 128 GB/s bidirectional, against ~900 GB/s for NVLink on an H100 SXM node. For anything that shards a single model's tensors across all eight GPUs, that gap is the whole story: published benchmarks put 8-way tensor parallel on these cards at around **a third** of an 8×H100 SXM node's throughput on models that require TP8.

> **◆ Insight — prefer replicas over tensor parallelism**
>
> The instinctive move is one giant model sharded across all eight cards. On a PCIe box that's the *worst* use of the hardware: it maximizes the traffic crossing your slowest link. The better topology is **several independent replicas, each sharded across as few GPUs as it fits on** — four copies of a 2-GPU model rather than one 8-GPU instance. Communication stays inside small groups, and aggregate throughput climbs.
>
> For an agent lab this isn't a compromise, it's the goal. Parallel sub-agents, concurrent ablation arms, a drafter and a critic and a judge panel all resident simultaneously — the topology that suits the hardware is exactly the topology the experiments want.

Which reverses a staging decision from the first revision. Multi-agent work was deferred to "a bigger machine later." You have the bigger machine. Orchestration is still the *last* thing you build — because a loop you can't yet measure isn't worth multiplying — but it's no longer gated on hardware, and the full ablation grid in Part 10 can run its arms concurrently instead of in sequence.

### Budgeting the memory you can't see

Weights are the easy half. The half that surprises people is the KV cache, which scales with *context length × concurrent streams* — and agent loops are unusually bad on both axes, since every step appends to a conversation that never resets.

```
# KV cache is the hidden budget line. Per stream:
KV bytes/token ≈ 2 × layers × kv_heads × head_dim × bytes_per_elem

# A 64-layer GQA model, 8 KV heads, head_dim 128, FP8 KV cache:
  2 × 64 × 8 × 128 × 1  =  131 KB / token
  131 KB × 100,000 tok  ≈   13 GB  per agent

# 16 concurrent agents at 100k context ≈ 210 GB — two whole GPUs,
# before a single byte of model weights.
```

> **▲ Trap — the VRAM you can fill is not the VRAM you should fill**
>
> Loading a model that consumes 90% of your GPUs leaves nothing for the caches that determine how many agents you can actually run. Keep **20–30% headroom**, set `--max-model-len` to the context you genuinely use rather than the model's maximum (allocation scales with it), and turn on FP8 KV cache — it halves this line item for a quality cost that is nearly always invisible on coding tasks.

#### Two smaller notes worth acting on early

- **Don't let the model cache land on the boot drive.** Hugging Face defaults to `~/.cache`. Two frontier checkpoints will fill a 512 GB boot NVMe and take the OS down with them. Set `HF_HOME` to the data array on day one, before the first download.
- **The 1.5 TB of host RAM is a real tool, used sparingly.** CPU-offloading MoE experts lets an oversized model run at all — but host memory bandwidth is an order of magnitude below GDDR7, so offload converts a memory error into a latency cliff. Treat it as a way to *try* a model that doesn't fit, not a way to serve one.

### The other two machines: Spark in hand, Station on the table

The **DGX Spark** is the smallest Grace Blackwell: a GB10 superchip — one Blackwell GPU fused to a 20-core Arm CPU — with **128 GB of unified LPDDR5X** shared between them at roughly 273 GB/s. It runs DGX OS on aarch64, which matters below the seam (inference-server containers must be arm64 builds — NVIDIA ships them) and not at all above it: your harness talks to the same OpenAI-compatible endpoint either way. It draws about as much power as a gaming laptop, sits silently on the desk, and is already paid for.

The **DGX Station** is the same idea scaled to its ceiling: a GB300 Grace Blackwell Ultra superchip pairing one Blackwell Ultra GPU — **288 GB of HBM3e at ~8 TB/s** — with a 72-core Grace CPU carrying another 496 GB of LPDDR5X, all one coherent 784 GB address space across a 900 GB/s NVLink-C2C link. One GPU, so nothing ever shards: the interconnect problem that defines the eight-GPU box simply does not exist here. It's a kilowatt-class desk-side tower; OEM pricing was still unannounced at this revision — plan for the same class of capital decision as the build itself.

*Three machines side by side — vendor headline figures, planning-grade*

| | DGX Spark *(in hand)* | 8 × RTX PRO 6000 *(the build)* | DGX Station GB300 *(the option)* |
|---|---|---|---|
| **Silicon** | GB10 — 1 Blackwell GPU + 20-core Grace, one superchip | 8 discrete Blackwell cards + 2 × EPYC 9535 | GB300 — 1 Blackwell Ultra GPU + 72-core Grace |
| **GPU memory** | 128 GB LPDDR5X, unified with CPU | 768 GB GDDR7 (8 × 96 GB) | 288 GB HBM3e + 496 GB LPDDR5X, coherent |
| **Bandwidth** | ~273 GB/s | ~1.79 TB/s per card | ~8 TB/s (HBM3e) |
| **Interconnect** | One chip — none needed; two Sparks pair over 200 GbE | PCIe Gen5 ×16, ~128 GB/s — *the* constraint above | One GPU — none needed; 900 GB/s C2C to host memory |
| **FP4 compute** | ~1 PFLOP | ~4 PFLOPS/card, ~30 aggregate | ~20 PFLOPS |
| **Power / placement** | ~170 W, silent, on the desk | 4–6 kW, machine room | Kilowatt-class, desk-side tower |
| **Price class** | ~$4k, paid | ~$150–200k (Part 05) | Unannounced — budget six figures |

### What each machine can actually achieve

Same models, same KV arithmetic as above. Read this as "which term of *model × harness × context* each box maximizes" — and notice that no machine dominates another across all six rows.

*Capability comparison — models from the fit table, KV math from this part*

| Capability | DGX Spark | 8 × RTX PRO 6000 | DGX Station GB300 |
|---|---|---|---|
| **The workhorse**<br>Qwen3-Coder-Next 80B-A3B | Runs at 4-bit (~44 GB) at ~30–50 tok/s — one solid interactive agent | 4–6 independent replicas running concurrently — the fleet | Very fast, in a corner of HBM — but it time-shares the one GPU |
| **The primary drafter**<br>Qwen3-Coder-480B | No — even two paired Sparks (256 GB pooled) fall short at FP4; the pairing tops out around 405B-class | 6 GPUs at FP8, 3 at 4-bit — works, taxed by PCIe tensor parallel | The headline: ~260 GB at FP4 fits *in HBM on one GPU* — fastest single-stream of the three by a wide margin |
| **The ceiling**<br>Kimi K2 · DeepSeek V4 Pro | Out of reach | 4-bit, all eight cards, tight | Weights spill to LPDDR5X over C2C — runs as an experiment; the latency cliff applies |
| **Concurrent 100k-context agents**<br>(~13 GB KV each) | 1–2 — memory allows more, but all of them share 273 GB/s | 16+ (210 GB ≈ two cards' worth of KV) | ~6 in HBM beside a mid-size model; beside the 480B, KV lives in LPDDR |
| **The ablation grid** (Part 10) | One arm at a time — a bench, not a lab | Five arms as five concurrent 1–2-GPU jobs — the lab | Batches one model superbly; arms needing *different* models queue for the single GPU |
| **Role** | **harness bench** — build and debug the loop, today | **fleet & lab** — replicas, critics, grids | **the drafter** — biggest model, highest single-stream speed |

> **◆ Insight — three machines, three different labs**
>
> Notice what the comparison does to this part's central constraint. The PCIe bottleneck that dominates the eight-GPU box *does not exist* on the other two — the Spark is one chip, and the Station is one GPU with 288 GB attached at 8 TB/s. The Station is what the "one giant model" instinct actually wants: it retires the interconnect problem by never sharding at all.
>
> But the reverse also holds. Eight discrete cards are eight isolation domains, and the replica topology the PCIe box *forces* is exactly the topology an agent lab *wants*. So the three machines maximize three different things: the Spark maximizes learning per euro already spent, the build maximizes the **harness** term, and the Station maximizes the **model** term. None is a superset of another.

> **▲ Trap — capacity statements are not speed statements**
>
> The Spark's "runs 200B-parameter models" is a statement about *fitting*, not about *serving*: at 273 GB/s, a dense 70B at 4-bit decodes at single-digit tokens per second. The machine only feels interactive on MoE models with a small active set — the 80B-A3B workhorse reads ~1.7 GB per token and is fine; anything dense feels like dial-up. Choose its models by *active* parameters, not total.
>
> The Station carries the symmetric fine print: one very fast GPU **multiplexes, it doesn't multiply**. MIG slices and co-hosted servers all share the same 8 TB/s and the same HBM — it will batch one model brilliantly, but it cannot be five independent two-GPU replicas the way the eight-card box can.

**The sequencing writes itself.** Build the harness against the Spark now — Parts 06–09 run against it essentially unchanged (same CUDA stack, same HTTP seam; only the model tier drops to "workstation" in Part 04's table), and every line of controller, tool, and verifier code ports to the big box the day it arrives. The eight-GPU build then opens the fleet and the measurement lab. The Station is the one purchase not to make on instinct: it improves exactly one number — single-stream speed on the largest open models — and the grid in Part 10 already measures what that number is worth on your repo, via the local-80B vs local-480B vs API arms, before you commit six figures. If the 480B arm wins decisively *and* wall-clock per step is what hurts, the Station is the targeted fix. If the 80B fleet plus verification closes the gap — Part 04 argues it often will — it isn't.

---

## Part 04 — Recalibrating: what context can and can't buy back

The original thesis — *better context offsets a weaker model and fewer agents* — was written for a weak-model machine. Part 03 removed that constraint, so the analysis needs adjusting rather than discarding. The underlying asymmetry is still real, and it still governs the small models you'll deliberately run; what changes is *which regime you're optimizing in*.

The compensation was never symmetric. Context repays two of a weak model's deficits and not the other two:

**Context *does* buy back:**

- **Knowledge / navigation.** Where things are, how it builds, conventions — a weak model burns scarce reasoning rediscovering these, so removing that is real leverage.
- **Convention matching.** Output that fits the codebase's patterns instead of generic boilerplate.

**Context does *not* buy back:**

- **Reasoning & long-horizon planning.** A weaker model still makes worse decisions and loses the thread over many steps.
- **Tool-use reliability.** The killer locally: a model that emits malformed tool calls can't be fixed by any `CLAUDE.md`.

### Which regime are you in?

Your box can run three quite different classes of model, and the advice above applies with different force to each. You will end up running all three simultaneously — so it's worth being explicit about which deficit you're compensating for at any moment.

*The same harness, three regimes — pick per role, not per project*

| Regime | Example | Where the deficit is | What buys it back |
|---|---|---|---|
| **Frontier-adjacent** | Qwen3-Coder-480B or Kimi K2 at 4-bit, most of the box | Small raw-capability gap. Real gaps: long-horizon agentic reliability, and wall-clock per step. | Verification loop, lean context, prefix caching. Context helps least here — the model can navigate on its own. |
| **Workstation tier** | 80B-A3B on 1–2 GPUs, 4–6 replicas | Moderate reasoning gap; occasional lost threads. | More attempts (they're fast), more harness, good context. The sweet spot for most of the lab. |
| **Deliberately small** | 7–14B on one GPU | Large gap — and tool-call fidelity starts failing. | Constrained decoding first, then tight tools and heavy verification. Run these to *see* the failure modes, not to ship. |

### You still need *more* harness, not fewer agents

"Weaker model **and** fewer agents" is the one combination that compounds against you. The main thing a multi-agent harness buys is **error correction through redundancy and adversarial checking** — run the tests, critique the diff, have a second pass try to refute the first. A stronger model needs less of this; a weaker model needs *more*. The right economy is **lean context + a heavier verification loop**, even if "the agents" are just the same model invoked in different roles.

> **◆ Insight — locally, verification is nearly free**
>
> This is the economics change nobody mentions, and it's the strongest argument for the whole local build. Against a metered API, every extra verification pass, every best-of-N sample, every reviewer in a judge panel is a line on an invoice — so you ration them. On hardware you already own, the marginal cost of a second opinion is **idle GPU time**, and you have eight cards' worth.
>
> That inverts the usual design pressure. Techniques dismissed as too expensive in production — three independent critics voting, generating five candidate diffs and keeping the one that passes, re-running the whole task under a different model family to see if it agrees — become the *default*. Redundancy is exactly what closes the gap between an open-weight model and a frontier one, and it is precisely the thing your hardware makes cheap. Spend it.

> **◆ Insight — the make-or-break**
>
> For an agentic loop, **tool-use format fidelity matters more than raw code quality.** Select your local model on function-calling reliability and instruction-following first, benchmark codegen scores second. And you can *guarantee* valid tool calls from a weak model with **grammar-constrained / structured decoding** (e.g. outlines, llguidance, xgrammar) — a concrete technique that directly converts a fragile model into a usable agent. Building this yourself is one of the most instructive things in the whole project.

> **▲ Trap — don't dump context (softened, not repealed)**
>
> The models you can now run advertise 256K–1M context windows, which makes "just send everything" tempting. Don't. *Effective* context still lags the advertised maximum — "lost in the middle" degradation is a property of attention, not of VRAM — so an exhaustive doc tree still dilutes attention on the tokens that mattered.
>
> What *has* changed is the currency. On a metered API, over-stuffing costs money; here it costs **wall-clock**, and in an agentic loop that penalty compounds — every one of dozens of sequential steps re-prefills a bloated prompt. The winning pattern is unchanged: a **lean, always-loaded root `CLAUDE.md` + retrieval on demand**, with intent enrichment reserved for the files that carry intent. Prefix caching (Part 06) softens the repeat cost, but nothing softens the attention dilution.

**So CCG's role locally:** a solid *context-provider module* — roughly one of six subsystems — best used lean, on-demand, and (thanks to the pipeline in Part 02) automatically fresh. Keep the A/B against no-context anyway: with a strong model, context earns less than intuition suggests, and the only way to know how much less on *your* repo is to measure it.

---

## Part 05 — What it actually costs — local vs the Claude API

These are not two prices; they are two **cost structures**. The API is a variable cost that scales with every token you spend. The box is a fixed cost that scales with nothing — it costs the same whether it runs flat out or sits dark. So the useful question is never "which is cheaper per token," it's **at what utilization does fixed beat variable**. That has a computable answer, and it's more favorable than most people guess.

### 5.1 · What an agent task costs on the API

Published pricing per million tokens (as of this revision — re-check before budgeting):

*Claude API list pricing, per MTok*

| Model | Input | Output | Notes |
|---|---|---|---|
| **Claude Opus 4.8** | $5.00 | $25.00 | 1M context |
| **Claude Sonnet 5** | $3.00 | $15.00 | Introductory $2 / $10 through 2026-08-31 |
| **Claude Haiku 4.5** | $1.00 | $5.00 | 200K context |

**Two multipliers that dominate agent economics:** cache *reads* cost ~**0.1×** the base input price (writes cost 1.25× at the 5-minute TTL, 2× at 1 hour), and the **Batch API is 50% off** for anything that doesn't need to be interactive.

> **◆ Insight — most local-vs-API comparisons overstate the API by ~4×**
>
> An agent loop re-sends a growing but mostly identical prefix on every step. That is precisely the workload prompt caching exists for — and cache reads bill at a tenth of normal input. Naive math that multiplies total input tokens by the list input price is therefore wrong by roughly the ratio you'd expect: it prices as full-rate the ~95% of tokens that are cache hits.
>
> Note the exact symmetry with Part 06: `--enable-prefix-caching` on vLLM and `cache_control` on the API are the same optimization applied to the same repetition. Whichever side you run on, the agent loop's dominant cost is a re-read of something the server already has.

```
# One agentic coding task, modelled: 30 steps, context growing 20k → 80k tokens,
# ~1k output tokens per step. On Claude Opus 4.8.

Without prompt caching
  input   30 steps × ~50k avg  = 1.5M tok × $5/M       = $7.50
  output  30 × 1k              =  30k tok × $25/M      = $0.75
                                                  total ≈ $8.25

With prompt caching
  cache writes  ~80k tok × $5/M × 1.25                 = $0.50
  cache reads  ~1.4M tok × $5/M × 0.10                 = $0.70
  output        30k tok × $25/M                        = $0.75
                                                  total ≈ $1.95

# Same task on Sonnet 5 with caching ≈ $1.20. On the Batch API, halve it again.
```

So the number to hold onto: **roughly $1–2 per solved agentic coding task**, cached, on a frontier hosted model. Not $8, and not the $0.05 that per-token intuition suggests either.

### 5.2 · What the box costs

Planning-grade — you know your actual acquisition cost, and GPU pricing has moved sharply (the RTX PRO 6000 launched around $8,565 and listed near $13,250 by mid-2026, a ~55% rise). The structure matters more than the digits.

| Line | Value | Detail |
|---|---|---|
| Capital | **~$150–200k** | 8 GPUs dominate; plus dual EPYC, 1.5 TB DDR5, 15 TB Gen5 NVMe, chassis and power. |
| Amortized | **~$60k/yr** | Straight-line over 3 years. The single largest line by far. |
| Under load | **~4–6 kW** | 8 × 600 W peak GPU, plus CPUs and RAM. Inference sits below TDP — decode is bandwidth-bound, not compute-bound. |
| Power + cooling | **~$6–10k/yr** | At €0.15–0.30/kWh with a 1.3–1.5 PUE, mixed load and idle. |

Call it **~$66k/year all-in** before anyone's time. A useful sanity anchor from the other direction: cloud marketplaces rent this card around $1.35/GPU-hour, so eight of them represent roughly **$11/hour — about $95k/year — of rentable capacity**. That is also, precisely, what the box costs you every hour it sits idle.

### 5.3 · The crossover

Divide the fixed cost by the work actually done. If the box is available 8,760 hours a year and busy for a fraction *u* of them, each busy hour carries `$66,000 / (8,760 × u)`. Assume an agent task takes ~5 minutes on a 2-GPU slice, and the box runs four such slices concurrently — so a task consumes about 0.021 box-hours.

*Cost per solved task, local vs API — utilization is the whole variable*

| Sustained utilization | Busy hours/yr | Cost per box-hour | Cost per task | vs API (~$2 cached) |
|---|---|---|---|---|
| 5% | 438 | $151 | $3.14 | **API wins** |
| **8%** | 700 | $94 | $1.96 | **Break-even** |
| 25% | 2,190 | $30 | $0.63 | **Local 3× cheaper** |
| 50% | 4,380 | $15 | $0.31 | **Local 6× cheaper** |
| 90% | 7,884 | $8.40 | $0.18 | **Local 11× cheaper** |

Read across the same line two ways and it holds: break-even is about **8% sustained utilization — roughly 90 agent-task-equivalents per day, every day**. For one developer running interactive sessions, that is out of reach and the API is simply cheaper. For a team whose CI pipeline (Part 02) fires agents on every commit, plus overnight ablation grids and batch jobs, it is very reachable — and past it the curve moves hard in your favour.

> **▲ Trap — the line item nobody budgets**
>
> Neither column above includes **your time**. Driver updates, CUDA/framework version churn, a model release worth re-benchmarking, a wedged NCCL job at 2am, capacity planning, monitoring. Budget one engineer-day a month as a floor; at a loaded rate that is on the order of **€10k/year** — larger than the electricity bill, and entirely absent from the API side, where the equivalent work is someone else's problem.
>
> Fold it in honestly and break-even moves from ~8% to ~10%. That doesn't change the conclusion, but a business case that omits it isn't a business case.

### 5.4 · What the spreadsheet can't price

Cost is rarely the deciding factor either way, and pretending otherwise produces bad decisions. The genuinely load-bearing considerations:

**Favours local:**

- **Source code never leaves the building.** For proprietary repos this is often not a cost question at all — it's the whole decision, and it ends the discussion before the first spreadsheet cell.
- **Unmetered experimentation.** The ablation grid in Part 10 is thousands of runs. Metered, you'd run it once and stop measuring; local, it's an overnight electricity bill.
- **Free verification.** Per Part 04 — three critics and best-of-N cost idle GPU time, not invoice lines.
- **No rate limits, no per-seat, no queue.** Deterministic capacity you control.
- **The learning.** Which is the actual point of the project.

**Favours the API:**

- **Zero fixed cost.** Low volume costs nearly nothing; you never pay for idle.
- **The model improves under you.** Frontier hosted models step forward every few months. Your local weights are frozen the day you download them — and re-qualifying a new checkpoint is a project.
- **No ops.** No drivers, no NCCL, no capacity planning.
- **Still the ceiling on the hardest tasks.** Part 03 narrowed that gap; it did not close it.

> **◆ Recommendation — route, don't choose**
>
> This is a false binary, and the C# design in Part 08 already dissolves it: local vLLM and the Claude API are both `IChatClient`, so which one serves a request is a *configuration* decision rather than an architectural one. Route on the properties that actually differ — send high-volume, private, and experimental work local, and escalate the hardest or highest-stakes tasks to the API.
>
> That also gives you the measurement the whole document is building toward: with both behind one interface, "how much worse is local, on our code?" stops being a matter of opinion and becomes a column in the ablation grid.

---

## Part 06 — Installing the local LLM

Everything in this part happens *below* your application. It's infrastructure you install and configure once, not code you write — and understanding where that boundary sits is the single most useful thing in the chapter, because it's also the answer to "can I build this in C#?"

### The stack, bottom to top

| Layer | What | How |
|---|---|---|
| Hardware | 8 × RTX PRO 6000 Blackwell, PCIe Gen5 — the DGX Spark runs this same stack on arm64 | given |
| Driver | NVIDIA open kernel modules, recent branch | apt |
| CUDA | 12.8 or newer — required for Blackwell sm_120 | apt |
| Containers | Docker + nvidia-container-toolkit | apt |
| Inference server | vLLM · SGLang · llama.cpp — Python/C++, you run it, you don't write it | docker |
| **HTTP /v1** | **OpenAI-compatible endpoint — the seam your language choice stops mattering at** | **the boundary** |
| Your harness | Controller loop, tools, context, verification, measurement | C# |

> **◆ Insight — the endpoint is the seam**
>
> Every serious local runtime speaks the same OpenAI-compatible HTTP dialect: `POST /v1/chat/completions`, with `tools`, streaming, and structured-output parameters. Below that line the ecosystem is Python and CUDA. Above it, it's ordinary HTTP and JSON. You install the bottom half; you write the top half in whatever you like. That's why Part 08 can answer "yes" without qualification.

### 5.1 · Base install

Version numbers below are planning-grade — check current ones — but the *constraints* are firm: Blackwell RTX PRO 6000 is compute capability `sm_120`, which needs CUDA 12.8+ and a matching PyTorch build. Anything older simply won't start.

```bash
# Ubuntu 24.04. 1 — driver (open kernel modules) and toolkit
sudo apt install -y nvidia-driver-580-open nvidia-container-toolkit
sudo nvidia-ctk runtime configure --runtime=docker && sudo systemctl restart docker
nvidia-smi          # expect 8 × RTX PRO 6000, ~97 GB each

# 2 — model cache on the DATA array, NEVER the 512 GB boot NVMe
sudo mkdir -p /data/hf && sudo chown $USER /data/hf
echo 'export HF_HOME=/data/hf' >> ~/.bashrc && source ~/.bashrc

# 3 — five-minute sanity check before any real serving work
curl -fsSL https://ollama.com/install.sh | sh
ollama run qwen3-coder:latest "write a python function that clamps x to [lo,hi]"
```

That last step exists to give you a working endpoint in minutes so you can validate your C# client against *something* while the real serving stack is still being tuned. Ollama will not use eight GPUs well — it's a smoke test and a fallback, not the lab.

### 5.2 · Choose topology before framework

Per Part 03, the PCIe interconnect makes this decision matter more than the vLLM-versus-SGLang question. Decide how you're carving up eight cards first.

*Four layouts for the same eight GPUs*

| Goal | Topology | Sketch |
|---|---|---|
| Max single-agent quality | One large model, TP 8 — accepts the PCIe tax | `--tensor-parallel-size 8` |
| **Max agent concurrency** *(recommended default)* | 4 replicas × TP 2, each on its own port | 4 × `--tensor-parallel-size 2`, `CUDA_VISIBLE_DEVICES=0,1` … |
| Mixed lab — the interesting one | 1 drafter (TP 4) + 2 fast workers (TP 1) + 1 critic (TP 1), all resident | Pin each with `CUDA_VISIBLE_DEVICES`; one config file per role |
| Very large MoE | Pipeline / expert parallel instead of tensor parallel — far less cross-GPU chatter | `--pipeline-parallel-size`, `--enable-expert-parallel` |

### 5.3 · Serving for agents specifically

```bash
# One replica of the workhorse model, pinned to two GPUs.
docker run --gpus '"device=0,1"' --ipc=host -p 8001:8000 \
  -v /data/hf:/root/.cache/huggingface \
  vllm/vllm-openai:latest \
  --model Qwen/Qwen3-Coder-Next-80B-A3B-Instruct \
  --served-model-name worker \
  --tensor-parallel-size 2 \
  --max-model-len 131072 \
  --enable-prefix-caching \
  --kv-cache-dtype fp8 \
  --enable-auto-tool-choice --tool-call-parser hermes
```

The flags that separate a chat server from an agent server:

*Why each flag earns its place in an agentic loop*

| Flag | What it does for an agent |
|---|---|
| `--enable-prefix-caching` | **The biggest single throughput win here.** An agent loop re-sends the same system prompt and context on every step, growing by one turn each time. Prefix caching turns each step's prefill into a cache hit on everything but the new tokens. This is precisely the workload the feature was designed for. |
| `--enable-auto-tool-choice` `--tool-call-parser` | Makes the server parse tool calls out of the model's output into a structured `tool_calls` field. Without them the model still tries to call tools — you just receive JSON embedded in prose and have to parse it yourself, badly. |
| `--kv-cache-dtype fp8` | Halves the per-stream KV budget from Part 03, roughly doubling concurrent agents. Quality cost on coding tasks is generally not measurable. |
| `--max-model-len` | Set it to the context you actually use. KV allocation scales with this number, so an aspirational value silently costs you concurrency. |
| `--served-model-name` | A stable alias (`worker`, `drafter`, `critic`) so your harness config never hardcodes a checkpoint path. Swap models by restarting a container, not by editing C#. |

#### The smoke test that matters

Before writing a line of harness code, confirm the server returns *structured* tool calls:

```bash
curl -s localhost:8001/v1/chat/completions -H 'content-type: application/json' -d '{
  "model": "worker",
  "messages": [{"role":"user","content":"What files are in the src folder?"}],
  "tools": [{"type":"function","function":{
      "name":"glob",
      "parameters":{"type":"object","properties":{"pattern":{"type":"string"}},
                    "required":["pattern"]}}}],
  "tool_choice": "auto"
}' | jq '.choices[0].message.tool_calls'
```

If that prints a populated array, the whole stack is sound. If it prints `null` while `.content` contains JSON-as-prose, your `--tool-call-parser` doesn't match the model's chat template — **fix that before anything else**. Nearly every "the local model can't do agents" report traces back to this one mismatch, and no amount of prompt engineering compensates for it.

#### Constrained decoding is a request field

The reliability technique praised in Part 04 lives on the server, reachable from any HTTP client: pass a JSON schema and the server constrains generation to match it. In vLLM that's guided decoding (xgrammar by default) applied automatically to tool parameters, or explicitly via `response_format` / `guided_json`. Nothing to port, nothing to install — which is the second reason your language choice is unconstrained.

### 5.4 · Traps specific to this hardware

*Each of these has cost somebody a weekend*

| Trap | Symptom | Response |
|---|---|---|
| **`sm_120` ≠ `sm_100`** | Kernel launch failures on a model that "works on Blackwell." Datacenter Blackwell (B200) is `sm_100`; your cards are `sm_120` and are *not* binary-compatible. This has broken DeepSeek support in vLLM. | Check the framework's issue tracker for your model family *and* `sm_120` before committing to it. Prefer families with confirmed reports on RTX PRO 6000. |
| **NCCL over PCIe** | Multi-GPU startup hangs with no error — the classic silent stall. | Almost always NCCL, not the server. Use a recent NCCL (2.27.3+), pass `--ipc=host`, and set `NCCL_P2P_LEVEL` explicitly rather than trusting autodetection. |
| **Borrowed benchmarks** | Throughput lands far below a published figure. | Most public numbers come from NVLink nodes. On TP8 workloads expect roughly a third of an 8×H100 SXM node — then avoid TP8 (§5.2) and win the throughput back a different way. |
| **Quant availability** | The checkpoint you want has no kernel-supported quantization on your arch. | FP8 is native on Blackwell and the safe default; NVFP4 is newer and faster where supported. Verify the *specific checkpoint × framework × arch* combination, not just "supports FP8." |

One framework note to close: **vLLM** is the sane default — broadest model coverage, best documentation, mature tool-call parsers. **SGLang** is worth benchmarking once you're serious, since it tends to lead on MoE throughput and on multi-turn workloads with shared prefixes, which describes your agent loop exactly. Both take similar flags; running the pair against your own task suite costs an afternoon and settles the question with data rather than blog posts.

---

## Part 07 — Harness architecture

Everything Claude-like reduces to a small controller running a loop over a model and a set of tools, wrapped in memory, verification, and guardrails. Build it in exactly that shape so each subsystem is legible.

### The single-agent loop

```
01 Observe → 02 Think → 03 Act → 04 Result
   ↻ repeat until the goal is met AND verification passes —
     or a step / token / budget limit trips

01 Observe  System prompt + lean context + latest tool result.
02 Think    Model reasons about the next step.
03 Act      Emit one structured tool call (read / edit / run).
04 Result   Controller executes the tool, feeds output back.
```

That loop is the whole engine. Everything else makes it more capable or more reliable.

### The six subsystems

*Build them in this order of dependency*

| Subsystem | Role | What building it teaches you about Claude |
|---|---|---|
| **Tools** | A typed registry: `read`, `grep`, `glob`, `edit`, `run_tests`, `bash`. Each has a schema and a real executor. | Capability is granted by tools, not prompts. Watch the model's reach expand as you add each one. |
| **Loop / controller** | Parses the tool call, runs it, appends the observation, re-invokes the model; enforces step/token/time limits. | The "agency" is here, not in the model. This code *is* the agent. |
| **Context & memory** | Assembles the window: system prompt + lean `CLAUDE.md` + retrieved-on-demand docs; compacts/summarizes when it fills. | Context engineering is a first-class discipline. This is where CCG plugs in — lean. |
| **Planning / decomposition** | A todo list the agent maintains; optional sub-agents for isolated sub-tasks. | Long tasks need externalized state. Watch coherence improve when the plan lives outside the model's head. |
| **Verification** | Run the tests; self-critique the diff; optional second-pass reviewer that tries to refute. | Reliability comes from checking, not from a bigger model. The single biggest lever locally. |
| **Guardrails** | Permission prompts, a path allow-list, a sandbox for `bash`. (CCG's PathGuard is a reusable pattern.) | Safety is structural. An agent you don't trust to run unattended isn't finished. |

### Allocating the box to the harness

With 768 GB you don't choose *a* model — you assign models to roles and run them concurrently. A workable steady-state layout, with every role addressable as a separate endpoint from the same C# harness:

*A standing model zoo — start narrower, grow into it*

| Role | Model tier | GPUs | Why |
|---|---|---|---|
| **Primary drafter** | Qwen3-Coder-480B or GLM-4.6, FP8 | 4 | Best single-agent quality you can host. Handles planning and the hard edits. |
| **Fast workers** | 80B-A3B, 2 replicas | 2 | Sub-agents, searches, summarization, retrieval. High tokens/sec matters more than peak reasoning. |
| **Critic / verifier** | A *different family* from the drafter | 1 | Diversity beats size for refutation — same-family models share blind spots and cheerfully agree with their own mistakes. |
| **Headroom** | — | 1 | KV spikes, a small model for ablations, the experiment you haven't thought of yet. |

That last row is not padding. The most common local-serving mistake is allocating to 100% and then having no room to run the comparison that would tell you whether the allocation was right.

> **◆ Insight — latency is a design input**
>
> An agentic loop is *many sequential* model calls. A smaller, faster model that iterates more — plus verification — can beat a slower, "smarter" one that gets fewer attempts. Measure `steps-to-solve × latency-per-step`, not just single-shot accuracy. On this hardware that trade is live in both directions: the drafter is genuinely stronger, and the 80B-A3B worker is genuinely several times faster per step.

#### Model shortlist (pick for tool-use, not just benchmarks)

- **Qwen3-Coder-480B-A35B** — Apache-2.0, ~69.6% SWE-bench Verified, 256K context. The default primary drafter.
- **GLM-4.6** (357B, MIT) — widely reported at Claude-Sonnet-class coding; a strong *second family* for the critic role.
- **Qwen3-Coder-Next 80B-A3B** — 3B active parameters means workstation-tier speed at far better quality than the size suggests. Your workhorse.
- **Kimi K2** (1T-A32B) — tuned specifically for agentic coding; the interesting ceiling experiment if 4-bit quality holds up on your tasks.
- **A deliberately small model** (7–14B) — not to ship, but to *see* where the loop breaks and where constrained decoding + verification rescue it. Keep one in the zoo permanently; it's the best teacher in the rack.

Select on **tool-call fidelity first**, benchmark scores second — a model that emits malformed calls is worth nothing in a loop regardless of its leaderboard position. Your task suite (Part 13) settles this in an afternoon.

---

## Part 08 — Building it in C# — yes, and it's a better fit than it looks

The short answer: **build it in C#.** The instinct that serious AI work must be Python is right about *training* and wrong about *agents*, and the distinction is worth being precise about, because it determines your whole project layout.

Look at what a harness actually is: an HTTP client, a JSON serializer, a process runner, a file-system sandbox, a concurrency scheduler, and a state machine. There is no tensor math anywhere in that list. The numerical work happens inside the inference server — which you install as a container (Part 06) and address over HTTP. **The Python in your stack lives below the endpoint, and you don't write it.** Above the endpoint, the work is exactly the kind of long-lived, strongly-typed, concurrent systems programming .NET is unusually good at.

### The .NET stack, piece by piece

*Every subsystem from Part 07 has a first-class .NET answer*

| Need | .NET option | Note |
|---|---|---|
| Model client | `Microsoft.Extensions.AI` (`IChatClient`) + `Microsoft.Extensions.AI.OpenAI` | Points at any OpenAI-compatible endpoint, so your local vLLM server and the Claude API are the same interface. Swapping the two is a one-line change — which is exactly what an ablation needs. |
| Tool schemas | `AIFunctionFactory.Create(method)`; `JsonSchemaExporter` (.NET 9) | Schema generated *from the C# signature*. Hand-written JSON schemas drift from their implementations; generated ones can't. |
| Constrained decoding | Request parameters — `strict`, `response_format`, `guided_json` | Server-side (§5.3). Python libraries like Outlines and xgrammar run *inside vLLM*; you reach them through a JSON field. No capability lost. |
| Streaming | `IAsyncEnumerable` + `await foreach` | Native, cancellable, and already the pattern CCG uses for Claude streaming. |
| Concurrency / fan-out | `Channels`, `Parallel.ForEachAsync`, TPL | **Where C# is outright better.** Real threads and no GIL, on 256 of them — for running parallel agents alongside parallel test suites, this is a genuine advantage over the Python default. |
| Tool execution | `Process` via CCG's existing `IProcessRunner` seam | Already built, already testable, already faked in unit tests. |
| Sandboxing | `Docker.DotNet`, containers, job objects | Give `bash` and `run_tests` a container each — you have the cores for it. |
| Tool interop | Official `ModelContextProtocol` C# SDK (v1.0, Microsoft-maintained) | Expose your tools over MCP and Claude Code itself can use them. Your lab's tools become portable rather than captive. |
| Context provider | **CCG Core, referenced directly** | The decisive argument. It's already .NET 8 with zero UI dependency — an in-process project reference, not a subprocess with a text protocol between you and your own code. |
| Observability | `.UseOpenTelemetry()`, Serilog | The measurement lab (Part 10) is mostly structured logging, and .NET's story here is excellent. |
| Compiler feedback | `Microsoft.CodeAnalysis` (Roslyn) in-process | Typed `Diagnostic` objects instead of parsed compiler text — see Part 09. Only available because harness and target share a language. |

> **◆ Insight — CCG is already the context subsystem**
>
> This is the part that tips the decision. In a Python harness, CCG is an external tool you shell out to and whose output you parse. In a C# harness it's `services.AddSingleton<DocGenService>()` — one of the six subsystems from Part 07, in-process, sharing types, debuggable in one step-through. `PathGuard` becomes the guardrail layer for your agent's file writes, verbatim; `SafeFileWriter` becomes the `edit_file` tool's write path. You aren't porting a context provider. You already wrote one, and it was built with exactly the immutability discipline an autonomous agent needs.

### What C# genuinely costs you

An honest ledger, because the answer isn't "no downside" — it's "the downsides don't apply to this project":

- **Fine-tuning and LoRA are Python.** PEFT, TRL, Unsloth — no realistic .NET equivalent. If you later want to train, that's a Python project. Inference-only, which is this project, is unaffected.
- **Modifying decoding internals is Python.** Consuming constrained decoding is a request field; *changing how it works* means patching a Python library. Only relevant if you want to do decoding research rather than use it.
- **Analysis of the ablation grid.** pandas plus matplotlib is still the nicer way to slice results. Mitigation is trivial and standard: emit JSONL from C#, analyze in a notebook. The harness stays typed; the exploration stays fluid.
- **New model features land in Python SDKs first.** Occasionally you'll set a raw JSON property instead of a typed one for a few weeks. `AdditionalProperties` exists precisely for this and it's a small tax.

### Sketch of the solution

```csharp
// A tool is just a method. The JSON schema is generated from the signature,
// so the contract the model sees can never drift from the code that runs.
[Description("Replace an exact, unique string in a file. Errors if absent or ambiguous.")]
static string EditFile(
    [Description("Repo-relative path")] string path,
    string oldString,
    string newString)
{
    PathGuard.AssertWritable(path);          // CCG's guardrail, reused verbatim
    var text = File.ReadAllText(path);
    if (Occurrences(text, oldString) != 1)
        return "ERROR: old_string must match exactly once.";   // errors are observations
    SafeFileWriter.WriteAllText(path, text.Replace(oldString, newString));
    return "OK";
}

// One client shape for the local server AND the Claude API — swap the endpoint.
IChatClient model = new OpenAIClient(
        new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri("http://localhost:8001/v1") })
    .GetChatClient("worker")
    .AsIChatClient()
    .AsBuilder()
        .UseOpenTelemetry()      // every call traced → the measurement lab, free
    .Build();

var options = new ChatOptions
{
    Tools    = [AIFunctionFactory.Create(EditFile), AIFunctionFactory.Create(RunTests)],
    ToolMode = ChatToolMode.Auto,
    // server-side constrained decoding — the Part 06 reliability win, from C#
    AdditionalProperties = new() { ["guided_decoding_backend"] = "xgrammar" }
};
```

*API surfaces move between releases — treat the above as shape, not copy-paste.*

> **▲ Trap — don't let the framework write your loop for you**
>
> `Microsoft.Extensions.AI` offers `.UseFunctionInvocation()`, which runs the tool-call loop automatically. For shipping an app, use it. For *this* project, think twice: the loop is the thing you're trying to understand, and a loop you didn't write is a loop you can't instrument, interrupt, budget, or compact. Use the built-in invoker for a day to confirm the endpoint works — then write the twenty lines yourself (Part 13) and keep them. The whole premise of the guide is that *the controller is the agent*; delegating it to a library hands away the lesson.

#### Suggested layout

```
LocalAgent.sln
├── Agent.Core        // controller loop, context assembly, verification, budgets
├── Agent.Tools       // read/grep/glob/edit/build/run_tests + PathGuard sandbox
├── Agent.Models      // IChatClient factories, one per served role (drafter/worker/critic)
├── Agent.Lab         // ablation runner → JSONL; the measurement harness (Part 10)
├── Agent.Host        // console entry point — also what TeamCity calls (Part 02)
└── ClaudeContextGenerator3.Core   // referenced, not reimplemented

// The only Python you run:
docker run ... vllm/vllm-openai:latest ...
```

**Net:** C# above the HTTP line, containers below it, a notebook for the plots. You lose access to training you weren't going to do, and you gain in-process reuse of a context generator you already built, genuine parallelism on a 256-thread machine, and static types across a system whose whole purpose is to be measured precisely.

---

## Part 09 — Compiling — how the agent learns it was wrong

Everything so far gives the agent a way to *act*. This part is about the fastest way to tell it that the action was wrong. For compiled languages — which is what your target repos are — the compiler is the single highest-value signal in the whole loop, and the easiest one to get badly wrong.

### Why compile feedback outranks tests

Tests are the oracle for *correctness*. The compiler is the oracle for *coherence*, and it is better than tests on every axis that matters to a loop: faster, fully deterministic, and far more precise about the failure. A failing test says "something is wrong somewhere." A compile error says `Foo.cs(42,17): error CS1061: 'Widget' does not contain a definition for 'Recalculate'` — file, line, column, code, and cause.

That precision maps exactly onto the most common failure mode of any code-generating model, local or hosted: **a plausible method that doesn't exist**. Hallucinated APIs, wrong overloads, a parameter that moved. No amount of context prevents these entirely; the compiler catches all of them, instantly, for free.

*The verification ladder — run each rung only if the one below passed*

| Rung | Typical latency | Catches | Run it |
|---|---|---|---|
| **1 · Syntax check** (single file) | < 1 s | Malformed edits, unbalanced braces, broken strings | After every `edit_file` |
| **2 · Compile** (affected project) | 1–30 s | Hallucinated APIs, type errors, wrong signatures, missing usings | Before ever running tests |
| **3 · Analyzers / lint** | seconds | Convention drift, nullability, obvious smells | Warn, don't gate |
| **4 · Unit tests** | seconds–minutes | Wrong behaviour | Once it compiles |
| **5 · Full suite / integration** | minutes | Regressions elsewhere | Before accepting |

> **◆ Insight — cheapest oracle first**
>
> A loop that runs the full test suite after every edit spends most of its wall-clock discovering typos. Order the ladder by cost and **fail fast**: a syntax error should cost the agent one second and one observation, not a three-minute build-and-test cycle. On a weaker model — which makes more of these mistakes — the ordering matters more, not less.
>
> This is also where CCG earns a specific keep: the "build and platform facts" section of a generated root `CLAUDE.md` is what tells the agent *how* to build this repo. It's a small artifact that unlocks the most valuable tool in the set.

### 9.1 · The trap that eats your context window

One bad edit to a widely-included header produces four hundred errors. Feed that to the model raw and you have burned the entire context window on a single mistake, most of it duplicate cascade noise pointing at files the agent never touched. This is the most common way compiled-language agent loops fall over, and it is entirely preventable.

```
// ✗ What the compiler gives you
423 errors, 1,180 lines of output, 38k tokens.
The first error is the cause; the other 422 are consequences.

// ✓ What the agent should receive
{
  "ok": false,
  "errors": 423, "warnings": 12,
  "summary": "1 root cause, 422 cascading",
  "diagnostics": [
    { "file": "src/geo/Point.h", "line": 42, "code": "C2065",
      "message": "'Vector3': undeclared identifier" },
    { "file": "src/geo/Mesh.cpp", "line": 17, "code": "C2065",
      "message": "'Vector3': undeclared identifier", "andMore": 421 }
  ]
}
```

The summarizer earns its keep with four rules: **report the first error per file**, **cap the list** (ten is plenty), **deduplicate by error code**, and **always report the true total** so the agent knows the scale of what it broke. Sort by file position — the earliest error is usually the cause and the rest evaporate when it's fixed.

### 9.2 · Two paths in C#

Because the harness and the target share a language, you get an option most agent harnesses don't have.

*Shelling out vs compiling in-process*

| | `dotnet build` + parse | Roslyn in-process |
|---|---|---|
| How | Run the SDK, parse the canonical MSBuild line format | `MSBuildWorkspace` → `GetCompilationAsync()` → `GetDiagnostics()` |
| You get | Text to regex — `path(line,col): error CS####: msg` | Typed `Diagnostic` objects: `Id`, `Severity`, `Location`, `GetMessage()` |
| Latency | Seconds (incremental), tens on a cold build | **Milliseconds** for a single changed document |
| Fidelity | Exactly what CI sees — source generators, targets, custom steps, everything | Compiler + analyzer diagnostics; some MSBuild-level behaviour won't reproduce |
| Use it for | The authoritative gate before accepting a change | The sub-second pre-flight after every edit |

> **◆ Insight — reject the bad edit before it enters the loop**
>
> Roslyn can re-analyze a single edited document in milliseconds. That turns compilation from something the agent *does* into something the `edit_file` tool *enforces*: apply the edit to an in-memory `Document`, pull the diagnostics, and if the edit introduced a new error, return it as the tool result and never write to disk.
>
> The agent now cannot leave the tree broken — the same structural-safety idea as `PathGuard`, applied to correctness instead of paths. And because there's no subprocess and no text parsing, there is no format to drift: the contract between compiler and harness is a typed object.

```csharp
// The build tool. Shape, not copy-paste — verify the API surface as you go.
[Description("Compile the affected project. Returns errors with file, line, and code.")]
async Task<BuildResult> Build(string projectPath, CancellationToken ct)
{
    var compilation = await _workspace.CurrentSolution
        .GetProject(projectPath)!.GetCompilationAsync(ct);

    var errors = compilation!.GetDiagnostics(ct)
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .OrderBy(d => d.Location.SourceTree?.FilePath)
        .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
        .ToList();

    return new BuildResult
    {
        Ok       = errors.Count == 0,
        Total    = errors.Count,                     // always report the real number
        // one per file, then cap — the cascade is noise, not signal
        Reported = errors.GroupBy(d => d.Location.SourceTree?.FilePath)
                         .Select(g => g.First())
                         .Take(10)
                         .Select(Format)
                         .ToList(),
    };
}
```

### 9.3 · The C++ side, where builds are slow

A C++ repo won't give you millisecond feedback, and a full rebuild is far too slow to sit inside an agent loop. Use the two-tier trick instead: **syntax-check the changed file first**, and only run the real build once that passes.

```
:: MSVC — parse and semantic-analyse only, no output files, sub-second
cl /Zs /nologo src\geo\Point.cpp

# Clang / clangd equivalent
clang++ -fsyntax-only src/geo/Point.cpp

# Per-file flags come from the compilation database, so the check
# uses the same includes and defines as the real build:
cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON -B build   # → compile_commands.json
```

This catches essentially every hallucinated-API and type error in under a second, which is the overwhelming majority of what the agent gets wrong. The full build then becomes a gate you run once per accepted change rather than once per edit — and with 128 cores, that full build is one of the few things on this machine that is genuinely fast.

> **▲ Trap — a build is arbitrary code execution**
>
> MSBuild targets, source generators, pre-build events, CMake scripts and `configure` steps all run code that came from the repository. If the agent can edit the repo, it can edit the build — so "run the build" is exactly as privileged as "run bash," and deserves the same sandbox. Put builds in a container, mount the repo, and drop the network unless the restore step genuinely needs it. You have 256 threads; running several isolated builds concurrently costs you nothing.

#### Metrics this unlocks

- **Compile-error rate per edit** — the fraction of edits that break the build. A direct, sensitive measure of model quality that needs no test suite, and the fastest way to rank two models on *your* code.
- **Edits-to-green** — how many attempts to get back to compiling after a break. This is recovery rate with a much tighter feedback signal than pass@1.
- **Cascade ratio** — errors reported vs root causes. Watch it to confirm the summarizer is doing its job rather than quietly flooding the context.

Add all three to the ablation grid. A model that compiles clean on the first try 80% of the time is worth more in a loop than one that scores higher on a benchmark and breaks the build every third edit — and until you measure this, you have no way of telling those two apart.

---

## Part 10 — The measurement lab — the actual teacher

For a learning environment, the most valuable thing you build is not the agent — it's the **ablation harness** that turns your beliefs into measurements. It converts "I think context helps" into a curve you can point at.

- **A task suite with tests.** A dozen small coding tasks, each with a passing test as ground truth. Tests are the oracle — no human grading.
- **Total instrumentation.** Log every prompt, every tool call (and whether it parsed), token counts, wall-clock, and outcome. The transcript is the lesson.
- **One variable at a time.** Change model, or context, or tools, or verification — never two at once — and watch the metric move.
- **Run the arms in parallel.** Distinct endpoints on distinct GPUs (Part 07) means a five-level sweep runs as five concurrent jobs, not five sequential ones. A grid that would take a week becomes an overnight run — which is the difference between measuring occasionally and measuring by default.

*The ablation grid — each axis isolates one factor*

| Variable | Levels to sweep | What it isolates |
|---|---|---|
| Model | 7B · 80B-A3B · 480B · a second family | The raw *model* term — the ceiling everything else works within. Cross-family matters as much as size. |
| Context | none · lean CCG · full CCG | The asymmetric-compensation curve — and the point where dumping *hurts*. |
| Tools | read-only · +edit · +run-tests | How much capability is granted by tools vs the model. |
| Verification | off · compile · +tests · +critique · 3-critic vote | How much reliability the harness claws back from checking. Note the compile-only rung — it's cheap and may capture most of the gain (Part 04, Part 09). |
| Serving side | local 80B · local 480B · Claude API | The measured local-vs-hosted gap on *your* repo — the number Part 05's economics turn on. |
| Agents | solo · +reviewer · +parallel fan-out | Specialization payoff vs coordination cost. |
| **Serving topology** | TP8 · 4×TP2 · 8×TP1 | Hardware-specific, and unique to a PCIe box: aggregate throughput vs single-agent quality. Nobody publishes this curve for your machine. |

#### Metrics worth tracking

- **pass@1** — did the tests go green on the first completed run?
- **Tool-call validity rate** — fraction of tool calls that parsed and executed. Your single best early diagnostic for a weak model.
- **Steps- and tokens-to-solve** — efficiency, and the input to the latency trade-off.
- **Recovery rate** — after a failing test or bad edit, did the agent get back on track? This is what verification and good context most improve.
- **Wall-clock per solved task** — locally this *is* your cost function. Tokens are free; your afternoon isn't. Track it alongside pass@1 or you'll optimize quality into unusability.
- **Compile-error rate per edit** — the sharpest cheap signal you have (Part 09), and the fastest way to rank two models on your own code without waiting for a test suite.
- **Cost per solved task** — box-hours consumed for local arms, cached token spend for API arms. The only way to make Part 05's break-even concrete instead of hypothetical.

> **◆ Insight**
>
> Run the grid and you will have *measured* your original question: context lifts a weak model a lot on navigation-heavy tasks and barely on reasoning-heavy ones; a verification loop lifts it more than extra context does; and there's a floor below which the model simply can't hold the loop together no matter what you feed it. That surface is the understanding you're after.
>
> With a frontier-adjacent model in the top row you get a second, sharper reading: **how much of the remaining gap to Claude is model and how much is harness.** That number is not on any leaderboard, it's specific to your repo and your tasks, and it's the one that tells you where to spend the next month.

---

## Part 11 — Build roadmap — one variable at a time

Each phase adds one subsystem, states the lesson, and names the metric to watch. Don't advance until the current phase is instrumented and green on a couple of tasks. Tags show how much of the box each phase needs — most of it needs very little, which is the point.

### Phase 0 — Instrument a bare loop *(1 GPU · C#)*

- **Build:** One 80B-A3B worker on one GPU + `read`/`grep`/`glob` + the controller loop + full logging. No editing yet. Resist the urge to start with the 480B — a fast model shortens the feedback loop while the harness is what's broken.
- **Learn:** See the loop breathe: prompts grow, tokens accrue, tool calls sometimes malformed. This is the agent's skeleton.
- **Watch:** Tool-call validity rate. If it's low, check `--tool-call-parser` first (§5.3), then add constrained decoding.

### Phase 1 — Close the loop *(1 GPU)*

- **Build:** Add `edit`, `build`, and `run_tests` — in that order, and with the diagnostic summarizer from Part 09 from the very first version. The agent can now change code and observe consequences.
- **Learn:** Acting + observing is the leap from chatbot to agent. Watch it read a compile error, fix the signature, then read a failing test and try again.
- **Watch:** Compile-error rate per edit and edits-to-green first; then pass@1 and steps-to-solve.

### Phase 2 — Add verification *(2 GPUs)*

- **Build:** Re-run tests as a gate; add a critique pass that tries to refute the diff before accepting. Put the critic on a *second model family* on its own GPU — you have the room, and cross-family disagreement is worth far more than self-critique.
- **Learn:** Reliability from redundancy — the concrete lesson of "more harness beats a bigger model."
- **Watch:** Recovery rate; pass@1 delta vs Phase 1; how often the critic disagrees usefully vs pedantically.

### Phase 3 — Plug in CCG — lean *(2 GPUs · in-process)*

- **Build:** Reference `ClaudeContextGenerator3.Core` directly (Part 08). Feed a lean root `CLAUDE.md` + retrieval-on-demand; reserve intent enrichment for intent-heavy files. A/B against no-context.
- **Learn:** The asymmetric-compensation curve, first-hand — including the task where full-dump context makes things *worse*, and the uncomfortable possibility that a strong model barely needs it.
- **Watch:** pass@1 and tokens-to-solve, context-on vs context-off, per task type.

### Phase 4 — Scale the model, not the code *(6 GPUs)*

- **Build:** Stand up the 480B drafter alongside the workers and point the same harness at it — one config change, no code change, if Part 08's layout held. Add the Claude API as a fourth arm while you're here — same interface, and it turns Part 05's economics into measured numbers.
- **Learn:** How much of your agent's competence was model and how much was harness. Everything built so far now runs unchanged on a far stronger brain; the delta is the answer.
- **Watch:** pass@1 and wall-clock per task, 80B vs 480B. Watch the trade: fewer steps, slower steps.

### Phase 5 — Orchestrate *(8 GPUs)*

- **Build:** Sub-agents for isolated sub-tasks; parallel fan-out for independent work; a three-critic panel that votes. C# `Channels` and `Parallel.ForEachAsync` do the coordinating.
- **Learn:** Where specialization pays and where coordination cost eats the gain. Last on purpose — not because the hardware forced it, but because multiplying a loop you can't measure just multiplies the confusion.
- **Watch:** Quality delta vs solo; wall-clock with parallelism; GPU utilization across all eight cards.

### Phase 6 — Close the freshness loop *(Pipeline)*

- **Build:** The console host TeamCity calls (Part 02); the Context Test as a build gate; provenance headers; trigger-loop exclusions. Harden the path allow-list and the `bash` sandbox while you're there.
- **Learn:** Stale context is worse than none — and now, that regenerating it automatically proves currency but not usefulness. The gate is what closes that gap.
- **Watch:** pass@1 on a repo you deliberately let drift, fresh vs stale — then the Context Test trend across real commits.

---

## Part 12 — Principles & pitfalls

- **The agent is the controller code, not the model.** Understand the loop and you understand the product. Don't let a framework write it for you.
- **Grant capability with tools; grant reliability with verification;** grant knowledge with lean context. Keep those three levers separate so you can measure each.
- **Pick the model for tool-use fidelity.** Rescue a weak one with constrained decoding, not a longer prompt — and check the server's tool-call parser before blaming the model.
- **Lean, on-demand context.** More is not better on any effective window, and locally the overage is paid in wall-clock on every step of the loop.
- **Treat generated context as a build artifact.** Regenerated from source, verified by test, never hand-maintained.
- **Spend the redundancy you own.** Verification is the cheapest quality on a machine you've already bought — three critics cost idle GPU time, not money.
- **Replicas over tensor parallelism** on a PCIe box. The obvious topology is the slow one.
- **Cheapest oracle first.** Syntax, then compile, then tests. And never hand the model raw compiler output — summarize the cascade or it eats the context window.
- **Fixed cost only beats variable at utilization.** Know your break-even (~8–10%), and treat idle hardware as the ongoing expense it is.
- **Measure before you believe.** The ablation grid is the curriculum; the transcripts are the lessons.
- **Add multi-agent last.** It's a layer over a working loop, not a substitute for one — even when the hardware would let you start there.

> **◆ What success looks like**
>
> You'll be able to **plot the capability surface** — model × context × verification × agents against pass@1 and wall-clock — for your own hardware, your own models, and your own repositories. At that point you won't just have a local coding agent; you'll understand, with numbers, exactly which parts of "how Claude works" are the model, which are the harness, and which are the context. CCG earns its place as the lean, automatically-fresh context term in that product — no more, and no less.
>
> And with 768 GB and a frontier-class open-weight model in the top row of the grid, one of those numbers is genuinely worth having: **the real, measured distance between a local agent and a hosted one** — on your code, not on a benchmark someone else designed.

---

## Part 13 — Starter kit — enough to begin building

Concrete artifacts to start from: a controller skeleton, the tool-call contract, a task suite the measurement lab can run immediately, and the constrained-decoding options that make a weak model usable. Written in C# to match your stack — the shape ports to any language, because all of it sits above the HTTP seam from Part 06.

### 13.1 · The controller loop

**The agent, in about forty lines**

```csharp
// The controller IS the agent. Instrument every line of it.
async Task<string> RunAsync(AgentTask task, ToolRegistry tools,
                             IChatClient model, ContextBuilder ctx,
                             CancellationToken ct)
{
    var messages = new List<ChatMessage> { SystemPrompt, ctx.Lean(task), task.AsUser() };

    for (var step = 0; step < MaxSteps; step++)
    {
        var reply = await model.GetResponseAsync(messages, new ChatOptions
        {
            Tools    = tools.Schemas,          // constrained → always a valid call
            ToolMode = ChatToolMode.Auto
        }, ct);

        var calls = reply.ToolCalls().ToList();

        if (calls.Count == 0)                  // the model thinks it's done
        {
            if (await VerifyAsync(task, ct))    // compile → tests → critique (Part 09)
                return reply.Text;
            messages.Add(ReviewerNote(task));  // nudge back on track, keep going
            continue;
        }

        foreach (var call in calls)             // { Name, Arguments }
        {
            var result = await tools.RunAsync(call, ct);   // writes are PathGuard-checked
            Log(step, call, result, CountTokens(messages)); // the transcript is the lesson
            messages.Add(new(ChatRole.Assistant, [call]));
            messages.Add(new(ChatRole.Tool,      [result]));
        }

        if (CountTokens(messages) > Budget)
            messages = Compact(messages);      // summarize older turns
    }
    return GiveUp(task);
}
```

Every subsystem from Part 07 hangs off one line here: `tools`, the `GetResponseAsync` call, `ctx.Lean`, `VerifyAsync`, `Compact`, and the `Log`. Keep the loop this small; put the intelligence in the tools and the verifier. Note what it does *not* do — no retry cleverness, no hidden state, no framework magic. That legibility is the entire point.

### 13.2 · The tool contract

**One schema per tool — the exact shape local runtimes accept**

This is the JSON your server sees. In C# you don't write it by hand — `AIFunctionFactory.Create` generates it from the method signature (Part 08) — but you should be able to read it, because when tool calls misbehave this is where you look.

```json
{
  "name": "edit_file",
  "description": "Replace an exact, unique string in a file. Errors if absent or ambiguous.",
  "input_schema": {
    "type": "object",
    "properties": {
      "path":       { "type": "string", "description": "Repo-relative path" },
      "old_string": { "type": "string" },
      "new_string": { "type": "string" }
    },
    "required": ["path", "old_string", "new_string"]
  }
}
```

Start with six: `read_file`, `grep`, `glob`, `edit_file`, `build`, `run_tests` (add `bash` behind a sandbox later). Note that `build` comes before `run_tests` in every sense — in the list, in the loop, and in how much it earns (Part 09). The round trip the model sees:

```json
// model emits (constrained to a tool schema):
{ "tool": "run_tests", "arguments": { "path": "tests/test_calc.py" } }

// controller executes and feeds back ONE observation:
{ "tool": "run_tests", "ok": false,
  "observation": "1 failed, 3 passed — test_neg: expected 0, got -2" }
```

### 13.3 · A task suite you can run today

**Tests are the oracle — no human grading**

*Order by what they stress, so ablations separate skills*

| # | Task | Stresses | Oracle |
|---|---|---|---|
| 1 | Make one failing unit test pass | Navigation + local reasoning | The test goes green |
| 2 | Implement a documented-but-empty function | Reasoning from a spec | Provided test |
| 3 | Find and fix an off-by-one bug | Debugging | Regression test |
| 4 | Thread a new parameter through a call-chain | Multi-file edit | Existing suite + new test |
| 5 | Refactor a function, behavior unchanged | Discipline / restraint | Full suite stays green |
| 6 | Wire a new module into the build | Build/config knowledge | Build + smoke test |
| 7 | Add a feature spanning three files | Long-horizon planning | Acceptance test |
| 8 | Reproduce a bug from a description, then fix it | Comprehension + repro | New failing → passing test |

**Worked example — task 1, end to end**

```python
# calc.py — the agent must make the test pass.
def clamp(x, lo, hi):
    return x            # BUG: ignores lo / hi

# tests/test_calc.py — the oracle.
def test_clamp():
    assert clamp(5,  0, 10) == 5
    assert clamp(-2, 0, 10) == 0
    assert clamp(99, 0, 10) == 10
```

The agent's ideal trace: `grep clamp` → `read_file calc.py` → `read_file test_calc.py` → `edit_file` → `run_tests` → green. Every arrow is a logged, measurable step.

### 13.4 · Making tool calls impossible to malform

**Constrained decoding — the highest-ROI reliability fix**

*Guarantee valid output instead of hoping for it*

| Approach | Where it lives | Guarantees | Best for |
|---|---|---|---|
| JSON-schema guided (Outlines) | Library over HF / vLLM | Output matches a JSON schema | The easiest path to valid tool calls |
| Grammar-constrained (xgrammar / llguidance) | Integrated in vLLM | Output matches an arbitrary grammar | Fast, low-overhead tool-call grammars |
| GBNF grammar | llama.cpp / Ollama | Matches a hand-written grammar | Local GGUF models |
| Native structured output (`guided_json` / `response_format`) | Server flag or request field | Schema-valid JSON | **Your default** — least code, and reachable from C# |

All four run *inside the inference server*. From C# you reach them through the request — a `strict` tool definition, a `response_format`, or an `AdditionalProperties` entry. The Python-only appearance of this list is an artifact of where the libraries are written, not of where they can be used.

> **◆ Insight — start here in Phase 0**
>
> The fastest reliability win on a weak model isn't a better prompt — it's making a malformed tool call *impossible*. Add constrained decoding first, and the rest of the harness has solid ground to stand on. You'll watch the tool-call validity rate jump to ~100% and the loop stop stalling on parse errors — a vivid, measurable lesson in why the harness, not the model, holds the agent together.

---

**Structure:** Parts 01–05 are the evaluation — CCG on its own terms, the pipeline that keeps it fresh, the hardware, what that hardware does to the weak-model thesis, and what the whole thing costs against the Claude API. Parts 06–09 are the platform — installing the model server, the harness architecture, the C# stack, and compile feedback. Parts 10–13 are the practice — measurement, roadmap, principles, and a startable kit.

**Caveats.** Hardware, pricing, and model figures are planning-grade: GPU specs and street prices, quantization sizes, API rates, and the open-weights shortlist all move quickly, and they reflect July 2026. The cost model in Part 05 is a structure to re-run with your own numbers, not a quote — the break-even is sensitive to acquisition cost, electricity rate, and how long a task actually takes on your repo. Framework flags and SDK surfaces drift between releases — treat code as shape, not copy-paste. Two claims are worth verifying before you architect around them: the PCIe-versus-NVLink throughput penalty on 8-way tensor parallel, and `sm_120` kernel support for whichever model family you pick.
