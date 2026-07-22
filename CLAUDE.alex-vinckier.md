# CLAUDE.md — GlassCoder

> **Purpose of this file.** Persistent project context for Claude Code. Read this before generating or modifying code. It describes *what* GlassCoder is, *how* it is structured, and the *conventions and constraints* to follow. It intentionally contains **no code implementations** — only specifications, decisions, and guidance.

---

## 1. Project Overview

GlassCoder is a **local AI coding agent** delivered as a **C# / WPF desktop application**. It drives a local LLM (served over an OpenAI-compatible HTTP endpoint) through a tightly instrumented single-agent loop.

The application is defined by three first-class goals that override convenience whenever they conflict:

1. **Robust structured logging** — every prompt, tool call, result, token count, latency, and outcome is captured as reconstructable, machine-readable data.
2. **Transparent code changes** — every proposed and implemented change is clearly visible in the UI with diffs and status.
3. **Performance-indicator tracking** — defined metrics are recorded per task and per run and are comparable across runs.

Guiding principle for all design trade-offs:

```
capability ≈ model × harness × context
```

GlassCoder is the *harness* and *measurement* half of that product. It performs **no training and no tensor math**. All inference happens below the HTTP seam in an external model server (vLLM / SGLang / llama.cpp / Ollama). Everything GlassCoder builds sits **above** that seam.

### Non-goals (explicitly out of scope)
- Model training, fine-tuning, LoRA, or quantization.
- Running or managing the inference server itself (it is external infrastructure).
- Any tensor / numerical computation in application code.

---

## 2. Definitions

| Term | Meaning |
|---|---|
| **Seam** | The OpenAI-compatible HTTP boundary (`POST /v1/chat/completions`) between GlassCoder and the model server. |
| **Harness** | The controller loop, tools, context assembly, verification, and guardrails — i.e. GlassCoder itself. |
| **Arm** | One configuration variant in an ablation run (a specific model / context / verification setting). |
| **Transcript** | The full, reconstructable record of a single agent run. |
| **Role** | A served-model alias the harness targets (`worker`, `drafter`, `critic`). |

---

## 3. Architecture Overview

### 3.1 The single-agent loop

```
Observe → Think → Act → Result
  ↻ repeat until goal met AND verification passes,
    or a step / token / time / budget limit trips
```

- **Observe** — system prompt + lean context + latest tool result.
- **Think** — model reasons about the next step.
- **Act** — model emits one structured tool call.
- **Result** — controller executes the tool and feeds the observation back.

**The controller loop *is* the agent.** Keep it small (~40 lines), legible, and fully instrumented. **Do not** delegate the loop to a framework auto-invoker (e.g. `.UseFunctionInvocation()`); the loop must remain interruptible, budgetable, and loggable by us.

### 3.2 The six subsystems (build in dependency order)

| # | Subsystem | Responsibility |
|---|---|---|
| 1 | **Tools** | Typed registry: `read_file`, `grep`, `glob`, `edit_file`, `build`, `run_tests`, later `bash`. Each has a JSON schema and a real executor. |
| 2 | **Loop / controller** | Parses tool calls, executes them, appends observations, re-invokes the model; enforces step/token/time/budget limits. |
| 3 | **Context & memory** | Assembles the window: system prompt + lean root context + retrieved-on-demand docs; compacts/summarizes when full. |
| 4 | **Planning / decomposition** | An agent-maintained todo list; optional sub-agents (added last). |
| 5 | **Verification** | Compile → tests → self-critique → optional multi-critic refutation. |
| 6 | **Guardrails** | Path allow-list, permission prompts, sandbox for `bash` and builds. |

---

## 4. Technology Stack

- **Language / runtime:** C# on the current LTS .NET. **Target .NET 9 or newer** (`JsonSchemaExporter` requires .NET 9).
- **UI:** WPF, **MVVM** architecture. No business logic in code-behind.
- **Layering rule:** the WPF project references a **UI-free core library**. `GlassCoder.Core` must have **zero UI dependency** so it is testable and runnable headless.
- **Seam:** all model traffic goes over an OpenAI-compatible HTTP endpoint. Which server serves a request (local vs hosted) is **configuration**, never architecture.

### 4.1 Required / recommended NuGet packages

| Need | Package | Notes |
|---|---|---|
| Model client abstraction | `Microsoft.Extensions.AI` (`IChatClient`) | One interface for local and hosted. |
| OpenAI-compatible client | `Microsoft.Extensions.AI.OpenAI` | Point at any `/v1` endpoint. |
| Tool schema generation | `AIFunctionFactory` (in `Microsoft.Extensions.AI`) | Schema generated from method signature. |
| JSON schema export | `JsonSchemaExporter` (.NET 9 BCL) | — |
| Structured logging | `Serilog` + sinks (`Serilog.Sinks.File`, JSON/Seq) | Machine-readable **and** human-readable. |
| Tracing | `OpenTelemetry`, `Microsoft.Extensions.AI` `.UseOpenTelemetry()` | Trace every model call. |
| Compiler feedback (C# targets) | `Microsoft.CodeAnalysis` (Roslyn), `Microsoft.CodeAnalysis.Workspaces.MSBuild` | Typed diagnostics. |
| Sandboxing / build isolation | `Docker.DotNet` | Containerize builds and `bash`. |
| Tool interop (optional) | `ModelContextProtocol` (official C# SDK) | Expose tools over MCP. |
| JSON | `System.Text.Json` | — |
| DI / config / hosting | `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Hosting` | Shared by UI and console host. |

> **Version discipline:** treat all package versions and API surfaces as **shape, not copy-paste** — they drift between releases. Verify current signatures before relying on them. Pin versions in a central `Directory.Packages.props` (central package management).

---

## 5. Solution Layout

```
GlassCoder.sln
├── GlassCoder.Core       // controller loop, context assembly, verification, budgets (UI-free)
├── GlassCoder.Tools      // read/grep/glob/edit/build/run_tests + path-guard sandbox
├── GlassCoder.Models     // IChatClient factories, one per served role
├── GlassCoder.Lab        // ablation runner → JSONL, metrics harness
├── GlassCoder.Wpf        // WPF UI (MVVM): transcript, diff/change view, metrics dashboard
├── GlassCoder.Host       // console entry point for headless / CI runs
└── tests/                // unit + integration tests mirroring the above
```

**Dependency direction:** `Wpf` and `Host` → `Core`, `Tools`, `Models`, `Lab`. Core depends on nothing UI-related. Enforce this — a UI reference leaking into Core is a defect.

---

## 6. Model Client Configuration

- Use `IChatClient` as the **single** abstraction for both local server and any hosted API. Swapping between them must be a **one-line / config-only** change (this is what ablations depend on).
- Point the OpenAI client at the local endpoint (e.g. `http://localhost:8001/v1`) using a **served-model alias** (`worker`, `drafter`, `critic`) — never a hardcoded checkpoint path.
- Support multiple concurrently-served roles as distinct endpoints/aliases addressed from one harness.
- Wire in **request-side constrained decoding** for tool calls from the earliest phase (`strict` tool defs, `response_format`, `guided_json`, or a `guided_decoding_backend` entry via `AdditionalProperties`). This is the highest-ROI reliability fix; it makes malformed tool calls impossible rather than merely unlikely.
- Build the client via the `.AsBuilder()` pipeline and attach `.UseOpenTelemetry()` so every call is traced into the measurement layer automatically.

---

## 7. Tool Contract Conventions

- A tool is a C# method annotated with `[Description]` on the method and every parameter. Its JSON schema is **generated from the signature** — never hand-written — so the model's contract cannot drift from the executor.
- Tool results are returned as a **single observation object**. **Errors are observations, not exceptions** — never let a tool failure throw out of the loop.
- Start with exactly six tools: `read_file`, `grep`, `glob`, `edit_file`, `build`, `run_tests`. Add `bash` later, only behind a sandbox.
- `edit_file` replaces an **exact, unique** string; it must error if the target is absent or ambiguous.
- Every file write must pass the **path allow-list guardrail**. The agent must never mutate files outside the writable set.
- `build` precedes `run_tests` — in the tool list, in the loop ordering, and in priority (it is the cheaper, higher-value oracle).

---

## 8. Verification Ladder (Cheapest Oracle First)

Run each rung only if the one below passed. **Fail fast.**

| Rung | Catches | When to run |
|---|---|---|
| 1 · Syntax check (single file) | Malformed edits | After every `edit_file` |
| 2 · Compile (affected project) | Hallucinated APIs, type errors, wrong signatures | Before running any test |
| 3 · Analyzers / lint | Convention drift, nullability | **Warn only — do not gate** |
| 4 · Unit tests | Wrong behavior | Once it compiles |
| 5 · Full suite | Regressions elsewhere | Before accepting a change |

### 8.1 Compiler-feedback rules (C# targets)
- **Prefer Roslyn in-process** for sub-second per-document diagnostics. Use typed `Diagnostic` objects (`Id`, `Severity`, `Location`, `GetMessage()`) — **never regex over compiler text**.
- Optionally reject a bad edit **before writing to disk**: apply to an in-memory `Document`, pull diagnostics, and return any new error as the tool result without persisting.
- Use `dotnet build` (parsed output) as the **authoritative gate** that matches CI exactly.

### 8.2 Diagnostic summarization (mandatory)
Before feeding diagnostics to the model:
- report the **first error per file**,
- **cap** the list (~10 entries),
- **deduplicate by error code**,
- **always report the true total** so the agent knows the scale of the break,
- **sort by file position** (the earliest error is usually the root cause).

**Never hand raw compiler output to the model** — a cascade of hundreds of errors will consume the entire context window.

### 8.3 C++ targets (slow builds)
- Two-tier: **syntax-check the changed file** (`/Zs` for MSVC, `-fsyntax-only` for Clang, with flags from `compile_commands.json`) before ever running a full build.

### 8.4 Safety
- **A build is arbitrary code execution.** Run builds — and `bash` — in a **sandboxed container** with the repo mounted and the network dropped unless a restore step genuinely needs it. Treat "run the build" as exactly as privileged as "run bash."

---

## 9. Logging System *(First-Class Requirement)*

- Structured logging via **Serilog**, emitting to a **machine-readable sink (JSON / JSONL)** and a human-readable view.
- Log **per step:** step index, full prompt, model response, each tool call (name + arguments), whether the call parsed/validated, each tool result, token counts, per-call wall-clock latency, and outcome.
- Emit **OpenTelemetry traces** for every model call via the `IChatClient` pipeline.
- Every run must be **fully reconstructable as a transcript** from the logs alone — the transcript is the primary teaching artifact.
- Provide a **live, scrolling, filterable transcript view** in the WPF UI (filter by step, tool, and severity).
- **Redaction / privacy:** never log secrets or API keys. Source code content may be logged only within the local project's own log store; provide a config switch to disable content logging.

---

## 10. Code-Change Visibility *(First-Class Requirement)*

The WPF UI must make changes unmistakable:

- Present every **proposed** change as a **diff (before/after)** with affected file and line ranges, *before* it is applied.
- Present every **implemented** change with explicit status: **proposed / applied / rejected / reverted**.
- Maintain a **per-task change log** listing all edits, each navigable to its file and location.
- Tie **compile/test results** to the change that produced them.
- Support **human review/approval gating** of changes where configured (permission prompt as a guardrail before write).

---

## 11. Performance-Indicator Tracking *(First-Class Requirement)*

Record per task and per run; support cross-run comparison.

| Metric | Definition |
|---|---|
| **pass@1** | Tests green on the first completed run. |
| **Tool-call validity rate** | Fraction of tool calls that parsed and executed — best early weak-model diagnostic. |
| **Steps-to-solve** / **Tokens-to-solve** | Efficiency, and input to latency trade-offs. |
| **Recovery rate** | Did the agent recover after a failing test or bad edit. |
| **Wall-clock per solved task** | The local cost function — always track alongside pass@1. |
| **Compile-error rate per edit** | Sharpest cheap quality signal. |
| **Edits-to-green** | Attempts to restore a compiling state after a break. |
| **Cascade ratio** | Errors reported vs root causes — validates the summarizer. |
| **Cost per solved task** | Box-hours (local arms) / cached token spend (hosted arms). |

- Emit metrics as **JSONL** for downstream notebook analysis.
- Provide UI **charts/tables comparing runs** (ablation view).

---

## 12. Context-Management Conventions

- Assemble a **lean, always-loaded root context + retrieval on demand**. **Do not dump the full doc tree** — effective context lags advertised window size, and dilution costs wall-clock on *every* loop step.
- Reserve intent enrichment for the ~10–20% of files that carry intent.
- If integrating a context generator, **reference its UI-free core library in-process** rather than shelling out.
- **Compact/summarize** older turns when the token budget is exceeded.

---

## 13. Build & Configuration Settings

- Target current LTS .NET; enable **nullable reference types** and **`TreatWarningsAsErrors`** in `Core` and `Tools`.
- Enable analyzers; **treat analyzer output as warnings** (do not gate the app on them — mirrors rung 3).
- Externalize **all** endpoint URLs, model aliases, budgets, and limits into configuration (`appsettings.json` + environment overrides). No hardcoded hosts or checkpoints.
- The process-execution seam (`IProcessRunner`-style) must be **fakeable in unit tests**.
- Use **central package management** (`Directory.Packages.props`) and a `.editorconfig` shared across projects.

---

## 14. Coding Conventions

- **MVVM** in WPF; no business logic in code-behind.
- **Async throughout:** `async`/`await`, `IAsyncEnumerable` for streaming, and a `CancellationToken` on every long-running call.
- Use `Channels`, `Parallel.ForEachAsync`, and the TPL for parallel agents/tools — genuine parallelism is a C# strength here.
- Errors surfaced to the agent are returned as **tool observations**, never thrown out of the loop.
- Keep the controller loop small; put intelligence in the tools and the verifier.
- Follow standard .NET naming (`PascalCase` types/methods, `camelCase` locals, `_camelCase` private fields) and enforce it via `.editorconfig`.
- **Dependency injection** everywhere; construct nothing with `new` that has behavior worth faking in tests.

---

## 15. Testing Conventions

- Mirror the source layout under `tests/`.
- Fake the model client (`IChatClient`) and the process runner in unit tests — no live server dependency in unit tests.
- Provide **integration tests** that exercise the full loop against a smoke-test endpoint.
- The **task suite** (Section 16) doubles as the acceptance/ablation harness — its tests are the oracle; no human grading.

---

## 16. Task Suite (Oracle for the Lab)

A dozen small tasks, each with a passing test as ground truth. Order by the skill they stress so ablations separate concerns.

| # | Task | Stresses | Oracle |
|---|---|---|---|
| 1 | Make one failing unit test pass | Navigation + local reasoning | Test goes green |
| 2 | Implement a documented-but-empty function | Reasoning from a spec | Provided test |
| 3 | Find and fix an off-by-one bug | Debugging | Regression test |
| 4 | Thread a new parameter through a call-chain | Multi-file edit | Suite + new test |
| 5 | Refactor a function, behavior unchanged | Restraint | Full suite stays green |
| 6 | Wire a new module into the build | Build/config knowledge | Build + smoke test |
| 7 | Add a feature spanning three files | Long-horizon planning | Acceptance test |
| 8 | Reproduce a bug from a description, then fix | Comprehension + repro | New failing → passing test |

---

## 17. Development Roadmap (One Variable at a Time)

Do not advance until the current phase is **instrumented and green** on a couple of tasks.

| Phase | Build | Watch |
|---|---|---|
| **0 — Bare instrumented loop** | One worker model, `read`/`grep`/`glob`, controller loop, full logging, constrained decoding. No editing. | Tool-call validity rate |
| **1 — Close the loop** | Add `edit`, `build`, `run_tests` (with the diagnostic summarizer from day one). | Compile-error rate per edit, edits-to-green |
| **2 — Verification** | Test-gate + critique pass, ideally on a different model family/role. | Recovery rate |
| **3 — Context (lean)** | Plug in the context provider in-process; A/B against no-context. | pass@1, tokens-to-solve |
| **4 — Scale the model** | Point the same harness at a larger drafter and/or hosted API arm (config only). | pass@1 vs wall-clock |
| **5 — Orchestrate** | Sub-agents, parallel fan-out, multi-critic voting. Last on purpose. | Quality delta vs solo, GPU utilization |
| **6 — Freshness loop** *(if applicable)* | Headless console host for CI, provenance stamping, trigger-loop exclusions, hardened sandbox. | pass@1 fresh vs stale context |

The **console host** must run the same services non-interactively, take a config path and repo root, respect the output allow-list, and return **meaningful exit codes**.

---

## 18. Principles & Pitfalls

- **The agent is the controller code, not the model.** Never let a framework write the loop.
- Grant **capability with tools**, **reliability with verification**, **knowledge with lean context** — keep the three levers separate and measurable.
- Pick models for **tool-use fidelity first**; rescue a weak one with **constrained decoding**, not a longer prompt. Check the server's tool-call parser before blaming the model.
- **Cheapest oracle first:** syntax → compile → tests. **Never** hand the model raw compiler output.
- **Measure before you believe.** Transcripts and metrics are the deliverable.
- **Add multi-agent last** — it's a layer over a working loop, not a substitute for one.
- **Treat idle behavior and errors as first-class:** budgets, limits, and graceful give-up are part of the loop, not afterthoughts.

---

## 19. Caveats

- Package versions, API surfaces, and SDK options **drift between releases** — verify before relying on them. Treat any referenced API shape as guidance, not fixed contract.
- Endpoint URLs, model aliases, budgets, and any cost/economics figures are **environment-specific and must be configurable**.
- Model choice, quantization, and serving topology live **below the seam** and are outside this application's code — do not encode assumptions about them in the harness.