# GrokReview 2026-08-08T12:41:24 — MCP retrieval (when and how)

**Audience:** code agent implementing (or deliberately not implementing) Microsoft Learn / GitHub MCP in GlassCoder  
**Status:** design guidance from session analysis — **not shipped**  
**Related product doc:** `docs/NewFeatures/mcp-retrieval.html` (proposal)  
**Related run reviews:** `GrokReview20260808.md`, `GrokReview20260808114442.md`, `GrokReview20260808122256.md`  
**Method:** `HISTORY.md` (transcript-driven harness fixes; schema budget; errors as observations)

---

## 1. Thought process (why this document exists)

### 1.1 Starting question

Would wiring **Microsoft Learn MCP** and **GitHub MCP** improve the quality of the GlassCoder process?

### 1.2 What “process quality” means for this product

GlassCoder optimises **capability ≈ model × harness × context**, with a measurable, hermetic Lab. Quality is not “smarter vibes”; it is:

- fewer wasted steps/tokens per goal  
- higher first-try correctness where oracles apply  
- tool-call validity and recovery  
- **comparable ablation arms** (byte-identical fixtures, reproducible tool outcomes)

External docs help **context**. They do not fix **harness defects**.

### 1.3 Evidence from live desktop runs (same day)

| Run | Stop | Dominant waste | MCP would help? |
|-----|------|----------------|-----------------|
| `ca727be3` | Completed | TFM hand-edit, empty solution, soft SDK fail | No |
| `f4ed50e0` | Completed | Post-critique packages (FlaUI/Moq), `todo_write` | No |
| `c5eb67f6` | TokenLimit | `read_file(offset)` ignored → 13-step re-read thrash; TFM miss | No |

Failure modes were **tool contracts, scaffolding, thrash, critique recovery** — not “model lacked Learn docs for Multiply.”

### 1.4 Conclusion of the chain

```text
MCP Learn/GitHub
  → improves quality WHEN bottleneck = external API knowledge
  → does NOT fix current desktop-suite thrash
  → HURTS Lab quality if live network without record/replay
  → HURTS efficiency if always registered / uncapped

Therefore:
  1. Do not default-on for current task suite
  2. If built: admission + invocation gate + budget + cache modes
  3. Harness efficiency tasks (GrokReview *122256*, *114442*) remain higher priority
```

### 1.5 What a tool call to those MCP services actually does

| Target | Mutates content? | Hits network? | Typical cost |
|--------|------------------|---------------|--------------|
| Microsoft Learn MCP (`https://learn.microsoft.com/api/mcp`) | No (search/fetch only) | Yes when Live/Record | Free per MS docs; ToS; public docs only |
| GitHub code search (via MCP) | No for search/read | Yes; **auth + rate limits** | Quota risk under ablation |
| GlassCoder run | Observation tokens + steps | — | Context bulk; non-determinism if uncached |

MCP is **another tool**, not ambient knowledge. Same observation contract as `grep` / `build`.

---

## 2. Recommendation summary (for the implementing agent)

| Decision | Recommendation |
|----------|----------------|
| Build MCP now for desktop suite? | **No** — fix harness first (`offset`/`startLine`, TFM, stall, critique gate) |
| Build MCP eventually? | **Yes, gated** — for API-heavy tasks and a Lab arm, not always-on |
| Default config | **Retrieval disabled** (tools not in schema) |
| Lab / suite / ablate | **Replay only**; miss = hard error, never silent Live |
| Interactive desktop | Live or Record, still under admit policy + budget |
| Learn vs GitHub | **Learn first**; GitHub = narrow `symbol_exists` / optional second |
| Authority | Learn = authoritative; GitHub snippets = untrusted, not “truth” |
| Substitute for | Nothing — does not replace `build`, `run_tests`, operator Run app |

---

## 3. When retrieval is “required” (operational definition)

A call is **required** only if **all** of:

1. Retrieval is **enabled** for this run/profile, **and**  
2. **Budget** remains (`MaxCallsPerRun`, max returned chars), **and**  
3. At least one **admit signal** holds:

| Signal | Example |
|--------|---------|
| External compile diagnostic | CS0246 / CS1061 on a type/member **not** in workspace (`find_symbol` / project sources) |
| Pre-write inconclusive naming missing package API | In-memory compile cannot see external API |
| Suite flag | `RequiresExternalDocs = true` on the task |
| Explicit structured reason | Tool arg `reason` ∈ {`unknown_api`, `version_check`, `symbol_exists`} — not free “curiosity” |

A call is **not required** (must **fail closed** or tool absent) when:

- Greenfield scaffold / WPF template / TFM / solution wiring (use `dotnet_project`)  
- UI layout, click handlers, local ViewModel tests (workspace tools + ladder)  
- Post-critique “find evidence” without an external API error (use operator Run app / real tests)  
- Duplicate of a query already answered this run (serve cache; do not re-hit)  
- Budget exhausted  

**Do not rely on system-prompt alone** (“only call when needed”). Models over-call optional tools (see FlaUI thrash). **Policy enforces.**

---

## 4. Architecture: three layers

```text
Layer 1  ADMISSION     → tool registered in schema?  (default no)
Layer 2  INVOCATION    → may this call run?          (budget + signals)
Layer 3  UPSTREAM      → Live / Record / Replay      (cache is the Lab feature)
```

### 4.1 Layer 1 — Admission (schema presence)

Mirror `EnableBashTool` / `Git:Enabled`:

```text
Retrieval:Enabled = false          # master
Retrieval:Learn:Enabled = false
Retrieval:GitHub:Enabled = false
Retrieval:Mode = Replay | Record | Live
Retrieval:MaxCallsPerRun = 3
Retrieval:MaxResultChars = 3000
```

When disabled: **do not register** MCP tools → zero schema rent, zero calls.

Optional profiles:

- `desktop-scaffold` → all off  
- `api-heavy` / Lab arm `with-retrieval` → Learn on, GitHub optional  

### 4.2 Layer 2 — Invocation gate (`IRetrievalPolicy`)

Every MCP-facing tool method starts with:

```csharp
if (!_policy.TryAdmit(ctx, toolName, args, out var denial))
    return Observation.Fail(toolName, denial.Code, denial.Message, denial.Hint);
```

**Admit checks (order):**

1. Enabled for this server  
2. Calls this run &lt; MaxCallsPerRun  
3. Result budget remaining  
4. Required signal present (unless `AllowProactive` — default **false**)  
5. Not more than K retrievals since last **Applied** change (anti-search-loop)  
6. Cache key: if hit and mode allows, return cached observation without counting as new upstream (policy choice: still count toward MaxCalls or not — **recommend count once per unique key**)

**Stable error codes** (extend `ToolErrorCodes`):

- `retrieval_disabled`  
- `retrieval_budget_exhausted`  
- `retrieval_not_indicated` — no external diagnostic / suite flag  
- `upstream_unavailable` — timeout, 429, dead server  
- `retrieval_cache_miss` — Replay mode only  

Errors are **observations**, never exceptions out of the loop (CLAUDE.md / HISTORY).

### 4.3 Layer 3 — Cache modes

| Mode | Network | Cache write | Cache miss |
|------|---------|-------------|------------|
| **Live** | Yes | Optional | N/A |
| **Record** | Yes | Always | N/A |
| **Replay** | Never | No | **Fail** (never silent Live) |

Cache key: `hash(server, toolName, normalizedArgs)`  
Normalize: trim, case-fold type names, collapse whitespace.

**Why cache is mandatory for Lab:** live search results change; ablation arms must be comparable (`mcp-retrieval.html` § hermeticity). GitHub rate limits make later arms different without Replay.

---

## 5. Tool surface (keep narrow)

Prefer purpose-shaped tools over generic `search`:

| Name | Purpose | When allowed |
|------|---------|--------------|
| `learn_api` | Official API / doc hit for type or member (+ optional TFM) | External diagnostic or suite flag |
| `learn_fetch` | Fetch one article by id/url returned from `learn_api` | Only after a successful `learn_api` this run |
| `gh_symbol_exists` | Existence / hit count for an exact symbol | Hallucination check; prefer over free code search |

**Avoid or heavily cost:** free-form `gh_search_code(q)` as default (authority and staleness problems).

**Namespace** on registration (`learn_*`, `gh_*`) so transcripts stay filterable.

**Descriptions** (prompt): short; state “only after external compile/pre-write errors; prefer workspace tools and build.” Enforcement remains in `IRetrievalPolicy`.

**Validate MCP schemas at registration** via existing `ToolFunctionFactory.ValidateSchema`; refuse bad tools at startup, not mid-run.

---

## 6. Optional stronger design: harness-initiated retrieval

Model-initiated tools still risk over-call even with gates.

**Alternative / complement:**

1. Ladder or pre-write emits external-looking diagnostic.  
2. Harness classifies symbol not in workspace.  
3. Auto one `learn_api` (cache-aware) and **append summary to verification message**.  
4. Model does not spend a step “deciding” to search.

Implement **after** gated model tools exist, if metrics show the model still under-uses retrieval when indicated.

---

## 7. Interaction with existing tools (do not confuse)

| Existing | Role | MCP relation |
|----------|------|--------------|
| `read_file` / `grep` / `find_symbol` | Workspace ground truth | Prefer first; MCP never replaces |
| `build` / `run_tests` / ladder | Oracles | Prefer; MCP is not verification |
| `dotnet_project` | Scaffold / packages | Prefer for TFM, refs, packages |
| `git_*` | Local VCS + PR | **Not** GitHub code-search MCP |
| Operator **Run app** | Live UI | Critics’ UI evidence; MCP does not provide this |
| `bash` | Escape hatch | Keep last; do not use bash to curl Learn |

---

## 8. Metrics (prove “only when required”)

Add to run metrics / jsonl:

- `retrievalCallsAllowed`  
- `retrievalCallsBlocked` (by code)  
- `retrievalCacheHits` / `Misses`  
- `retrievalUpstreamCalls`  
- `retrievalCharsReturned`  

**Success for the gate:** on scaffold desktop tasks, `Blocked ≫ Allowed` and `Upstream ≈ 0` if disabled. On API-heavy arms, `Allowed` correlates with fewer invent-then-compile cycles.

---

## 9. Implementation tasks (priority)

### Priority vs other work

```text
HIGHER PRIORITY (ship first — GrokReview 122256 / 114442):
  R1–R4  read_file offset/startLine, int coerce, stall, TFM auto-widen
  E1–E4  todo alias, package trim, critique gate, soft-fail sentry

THEN (this document):
  M0  config + policy stubs (no network)
  M1  cache + Replay/Record/Live
  M2  learn_api thin client to Learn MCP
  M3  metrics + Lab arm
  M4  gh_symbol_exists (optional)
  M5  harness-initiated attach (optional)
```

### Task M0 — Options + policy without network (P0 if starting MCP track)

**Do:**

- `RetrievalOptions` bound from config; validate on start.  
- `IRetrievalPolicy.TryAdmit` with budget + “not indicated” + disabled.  
- Stub tool `learn_api` registered only when enabled; always goes through policy; returns Fail with `retrieval_not_indicated` when no signal.  
- Unit tests: disabled → not registered; enabled + no signal → fail; enabled + fake external diagnostic in run context → admit.

**Files (suggested):**

- `src/GlassCoder.Tools/Retrieval/RetrievalOptions.cs`  
- `src/GlassCoder.Tools/Retrieval/IRetrievalPolicy.cs` / `RetrievalPolicy.cs`  
- `src/GlassCoder.Tools/Retrieval/LearnApiTool.cs` (stub)  
- DI in `ToolsServiceCollectionExtensions.cs`  
- Tests under `GlassCoder.Tools.Tests`

**Acceptance:**

- [ ] Default appsettings: retrieval off; tool count unchanged.  
- [ ] Enabling registers exactly the namespaced tools.  
- [ ] Policy unit tests cover budget and not-indicated.  
- [ ] `PromptBudgetTests` updated if tools enabled in that test host.

### Task M1 — Record / Replay / Live cache (P0 for any Lab use)

**Do:**

- `IRetrievalCache` disk-backed under AppData or repo `.glasscoder/retrieval-cache/`.  
- Mode Replay: miss → `retrieval_cache_miss`, no network.  
- Mode Record: network + write.  
- Mode Live: network, optional write.

**Acceptance:**

- [ ] Two suite runs in Replay with same cache → identical retrieval observations.  
- [ ] Replay miss fails loudly.

### Task M2 — Learn MCP client (P1)

**Do:**

- Use pinned `ModelContextProtocol` package.  
- Endpoint configurable; default Learn public MCP URL.  
- Map to `learn_api` / optional `learn_fetch`.  
- Truncate to `MaxResultChars`; observation-shaped timeouts.  
- No auth unless Microsoft changes requirements.

**Acceptance:**

- [ ] Live manual call returns Ok with truncated body under admit.  
- [ ] Server down → `upstream_unavailable` observation.

### Task M3 — Wire diagnostics into admit signals (P1)

**Do:**

- Run context or last verification summary exposes recent diagnostic codes/messages.  
- Policy parses external-looking failures (heuristic list + “not found in workspace”).  
- Suite task metadata `RequiresExternalDocs`.

**Acceptance:**

- [ ] After synthetic CS0246 for `Nonexistent.Microsoft.Type`, admit allows one `learn_api`.  
- [ ] After only local CS0103 on user’s own type, admit denies.

### Task M4 — `gh_symbol_exists` (P2)

**Do:** Authenticated GitHub search or MCP; return count + few paths; never full file dumps. Same policy + cache.

**Acceptance:** Known symbol → hits &gt; 0; invented symbol → 0; rate limit → observation.

### Task M5 — Metrics + optional auto-attach (P2)

**Do:** Metrics fields above; optional harness append of one Learn blurb on first external CS0246.

---

## 10. Explicit non-goals

| Do not | Why |
|--------|-----|
| Enable MCP by default on desktop multiply suite | No ROI; adds thrash risk |
| Silent Live fallback in Replay | Destroys hermeticity trust |
| Uncapped full-article injection | TokenLimit (see `c5eb67f6`) |
| Free-form GitHub search as primary knowledge | Stale / wrong / untrusted |
| Replace Run app or critics with docs | Different evidence class |
| Use bash to curl Learn | Bypasses policy, cache, metrics |
| Raise token limits to “make room for MCP” | Hides process cost |

---

## 11. Config sketch (proposed, not shipped)

```json
"GlassCoder": {
  "Retrieval": {
    "Enabled": false,
    "Mode": "Replay",
    "MaxCallsPerRun": 3,
    "MaxResultChars": 3000,
    "AllowProactive": false,
    "MaxCallsWithoutAppliedChange": 2,
    "Learn": {
      "Enabled": false,
      "Endpoint": "https://learn.microsoft.com/api/mcp"
    },
    "GitHub": {
      "Enabled": false
    },
    "CacheDirectory": ""
  }
}
```

Empty `CacheDirectory` → under `AppPaths` local app data.

---

## 12. How a code agent should use this document

1. **If fixing run thrash / efficiency:** ignore MCP; implement GrokReview `*122256*` / `*114442*` first.  
2. **If implementing retrieval:** start **M0 + M1**, not a raw MCP client. Gates and cache before network.  
3. **If asked “add Learn to the agent”:** enable only via config/profile; default remains off.  
4. **If asked to measure benefit:** Lab arm with Replay corpus vs no-retrieval arm; compare steps, compile errors per edit, recovery — not single interactive anecdotes.  
5. **HISTORY.md:** when shipping, record decision (why gated, default off, Replay for Lab).

---

## 13. Bottom line

**Thought process:** Recent GlassCoder quality limits are harness and control-loop issues. MCP Learn/GitHub address a **different** limit (external knowledge). Adding them always-on would **not** improve the process for the current suite and would **hurt** reproducibility and efficiency unless gated.

**Recommendation:** Treat retrieval as an **opt-in, policy-gated, budgeted, cache-backed** tool family. Tools absent by default; when present, admit only on external-knowledge signals; Lab only in Replay. Prefer Learn over GitHub; never let MCP substitute for build, tests, or operator UI proof.

---

*Generated 2026-08-08T12:41:24 for a code agent. Session synthesis: MCP quality assessment + “only when required” implementation design.*
