# Tool evaluation for AI code generation

An evaluation of GlassCoder’s harness tools against the product’s stated purpose (from `docs/index.html`, `docs/wpf-app.html`, `docs/settings.html`, and the NewFeatures proposals). Written as an outside review of effectiveness for AI code generation: weak points, improvements, and proposed tools.

---

## What GlassCoder is optimising for

From the operator’s guide and WPF docs, GlassCoder is **the harness, not the model**. Its job is to make a coding agent **safe, observable, and measurable**:

| Principle | Implication for tools |
|-----------|------------------------|
| **capability ≈ model × harness × context** | Tools must return *actionable* observations, not opaque failures |
| **Errors are observations** | Failures should teach the model what to do next |
| **Defaults that refuse** | Power without structure (especially `bash`) is last and gated |
| **Verification ladder** | Cheap checks before expensive ones; refuse broken writes before disk |
| **Diffs + transcripts + metrics** | Every write must go through change log / structured results |
| **.NET-first** | `build` / `run_tests` / Roslyn matter more than general shell |

The tool surface should be judged against **goal completion under step/token budgets**, not against “can it do anything a human shell can.”

---

## Current toolkit (effectiveness snapshot)

Docs list eight default tools; the product also ships `list_projects` and `dotnet_project`. Opt-in: `bash`, five git tools.

| Tool | Role in AI codegen | Effectiveness | Notes |
|------|-------------------|---------------|--------|
| **`update_todos`** | Plan / focus | Good | Whole-list replace keeps plan and transcript aligned |
| **`read_file`** | Ground truth | Strong | Line endings + clip warnings fix a real thrash mode |
| **`grep`** | Locate symbols / call sites | Strong | Cheap; primary navigation tool |
| **`glob`** | Discover structure | Good | Skips noise dirs |
| **`list_projects`** | Map .NET graph | Strong (newer) | Collapses multi-step discovery |
| **`create_file`** | Scaffold / full write | Strong with `overwrite` | Overwrite closes the “generated stub” trap |
| **`edit_file`** | Surgical change | Strong after line-ending fix | Still brittle for multi-hunk work |
| **`build`** | Compile oracle | Strong | Cache + MSB1003 messaging help |
| **`run_tests`** | Behavioural oracle | Strong | Named failures > pass counts |
| **`dotnet_project`** | Scaffolding | Strong (newer) | Stops hand-authored `.csproj` disasters |
| **`bash`** | Escape hatch | Weak by design | Last, sandboxed, unstructured output |
| **Git suite** | Publish work | Good when enabled | Policy-heavy; not for *generation*, for *completion* |

**Verdict:** For a **local, .NET-centric, measured agent**, the default stack is already above average: structured oracles, path guardrails, pre-write compile, ladder feedback. Weakness is less “missing tools” than **gaps in multi-file navigation, multi-edit throughput, external knowledge, and recovery after partial failure**—exactly where step budgets get burned.

---

## Weak points (ranked)

### 1. Multi-file / multi-hunk edits are expensive

**Symptom:** One logical change (rename, interface update, move type) becomes N unique `edit_file` calls, each with re-read + pre-write compile.

**Why it hurts codegen:** Models plan in patches; the harness forces micro-surgery. High step cost, high chance of ambiguous/not-found mid-sequence, partial trees.

**Improve**

- Prefer **multi-hunk apply** in one tool call (ordered replacements, all-or-nothing or atomic-by-file).
- Or **`apply_patch`** with a unified-diff / search-replace list, still through path guard + pre-write + change log.
- Keep single-string `edit_file` for small fixes; document “one hunk → edit_file, many → apply_patch.”

---

### 2. No structural navigation (symbols, references, hierarchy)

**Symptom:** Agent greps for `class Foo`, then reads whole files to find callers.

**Why it hurts:** Grep is text-level; C# needs **type-aware** “go to definition / find references / list members.” Without it, large repos burn tokens on wrong files.

**Improve**

- **`find_symbol` / `find_references`** via Roslyn (workspace already has analyzer infrastructure).
- Return: path, line, signature, accessibility—not full file bodies.
- Cap results; point at `read_file` for the body.

This fits GlassCoder better than raw `bash` + `dotnet` scripts: structured, sandbox-free, measurable.

---

### 3. No delete / move / rename

**Symptom:** Cleanup, extract file, rename type → invent workarounds (`create_file` + hope something deletes the old path, or leave dead code).

**Why it hurts:** Real refactors and “stop using this stub” tasks stall or leave the tree dirtier.

**Improve**

- **`delete_file`** — writable set only, change log “before → empty,” approval gate.
- **`move_file` / `rename_file`** — same gates; optional “update usings” later as a separate smart tool.

Safety: never delete outside writable paths; never touch denied globs; always propose on the Changes surface first.

---

### 4. Pre-write compile can still mislead or go silent

**Recent progress:** incomplete refs → `Inconclusive` (correct).

**Remaining weak spots**

- Multi-project / WPF / generated code still weak under scavenged refs.
- Agent may not distinguish “your edit is wrong” vs “harness couldn’t judge.”
- Ladder compile vs agent `build` can still disagree if targets differ.

**Improve**

- Surface **`Inconclusive`** clearly in the observation text with a fixed next step: “run `build` on X.”
- Prefer **project-aware pre-write** when `ProjectLocator` already knows the owner (partially present on the ladder).
- After N inconclusive pre-writes, nudge once: “trust `build`, stop re-editing for compile feedback.”

---

### 5. Context assembly is passive; tools don’t help “what matters”

**Symptom:** Agent re-discovers the same types every step; always-loaded root files help, but mid-run relevance is still grep+read.

**Why it hurts:** Step and token budgets die on re-orientation, not coding.

**Improve (tools, not only context layer)**

- **`summarize_file` / `file_outline`** — signatures only (types, methods, XML docs), cheap for large files.
- **`list_changes`** — what this run already applied (agent often doesn’t “see” the change log as a tool).
- Optional **`search_docs`** over `CONTEXT.md` / always-loaded set with line anchors.

Related proposals already on disk: **MCP retrieval** (`docs/NewFeatures/mcp-retrieval.html`) and **harness advisor** (`docs/NewFeatures/harness-advisor.html`).

---

### 6. No external / API knowledge under hermetic measurement

**Symptom:** Hallucinated package APIs, wrong NuGet versions, invented WPF patterns.

**Why it hurts:** Verification catches *after* waste; models invent libraries.

**Improve (aligned with docs)**

- Build **MCP Learn + NuGet + GitHub search** *only* with **record/replay cache** so ablation stays hermetic (as `mcp-retrieval.html` argues).
- Until then: **`nuget_search`** / **`package_info`** against a local cache or pinned feed is safer than free `bash` + network.

---

### 7. Test feedback is good; “write the right test” is under-supported

**Symptom:** Runs that build forever and never add tests (seen in harness thrash analyses).

**Why it hurts:** Ladder can pass with no new tests; model doesn’t get a *tool* that answers “what to test.”

**Improve**

- **`list_tests`** — discover existing tests for a type/path (from attributes / naming).
- **`run_tests`** already exists—pair with **`suggest_test_targets`** or force system-prompt nudges when goal mentions tests and zero test files appear after N steps (harness nudge, not only tools).
- Scaffold path is better now via `dotnet_project` + `create_file` overwrite.

---

### 8. Conflict / recovery tools are thin

Docs note: rebase conflict aborts and names files; **agent has no tool to resolve or abort cleanly mid-mess** beyond reading files.

**Also:** no explicit **`undo_change` / `revert_file`** to last applied snapshot on the change log.

**Improve**

- **`revert_file`** — restore last pre-edit content for a path this run changed (bounded, logged).
- Git: optional **`git_abort_rebase`** (host, like other git tools) if git stays enabled.

---

### 9. `bash` is a poor primary codegen tool (by design)

Good safety story; weak *effectiveness* if the agent leans on it for:

- formatting, listing, `sed`-style edits, ad-hoc scripts.

**Improve**

- Keep `bash` last.
- Add **narrow tools** for common bash uses: `format` (`dotnet format`), `list_directory` (structured), `count_lines`—so the model never needs shell for navigation.

---

### 10. Docs / schema lag vs product

Operator guide still says `create_file` “refuses to overwrite” and doesn’t list `list_projects` / `dotnet_project`. Models that get tool schemas from code are fine; **operators and system prompts** that paraphrase the guide can mis-teach the agent.

**Improve:** Keep tool tables and system prompt examples in lockstep with the `[GlassCoderTool]` surface.

---

## What already works well (don’t dilute)

1. **Observation-shaped errors** with hints (`overwrite: true`, re-read after clip).
2. **Pre-write refuse** vs ladder **report and continue**—right split for measurement.
3. **Writable-path + approval + change log** on every write—keeps “visible agent.”
4. **Parsed build/test** over raw shell output.
5. **Identical-failure stop / step budget warning / build cache**—harness fighting thrash, not only tools.
6. **Opt-in power** (bash, git)—correct for a scientific harness.

Adding tools that bypass these would make the product *more* capable and *less* GlassCoder.

---

## Proposed additional tools (priority order)

| Priority | Tool | Purpose | Design constraints |
|----------|------|---------|-------------------|
| **P0** | **`apply_patch`** (multi-hunk / multi-file) | Throughput for real refactors | All paths through guard; one change-log entry per file; pre-write per file; abort remaining if one fails |
| **P0** | **`find_symbol` / `find_references`** | Structural navigation | Roslyn; read-only; capped; no full sources |
| **P0** | **`file_outline`** | Cheap large-file orientation | Signatures only; works with line-clip limits |
| **P1** | **`delete_file`** | Cleanup / replace workflows | Writable only; approval; full “before” in change log |
| **P1** | **`move_file`** | Rename/extract without dual create | Writable only; log both paths |
| **P1** | **`list_run_changes`** | Self-awareness of applied work | Read change log; no second source of truth |
| **P1** | **`revert_file`** | Cheap recovery | Only files this run applied |
| **P2** | **`list_tests` + filter-aware `run_tests` defaults** | Test-writing loops | Discover + run named subset |
| **P2** | **`nuget_info` / Learn via MCP + replay cache** | Stop API hallucination | Hermetic by default for Lab |
| **P2** | **`dotnet_format`** (or format-on-write option) | Stop style thrash | Sandbox-friendly; optional |
| **P3** | **`git_abort_rebase` / conflict helpers** | Host git recovery | Same policy as other git tools |
| **P3** | **Harness advisor tool** (offline) | Meta: improve tools from transcripts | Not in the worker loop by default—Lab/ops |

---

## Improvements to *existing* tools (high leverage, less surface area)

1. **`edit_file`**
   - Optional `replace_all: true` when occurrence count is known and safe (with hard cap).
   - Return a short **diff excerpt** in the observation so the model needn’t re-read the whole file.

2. **`read_file`**
   - Optional **`outline: true`** mode (or separate tool) instead of always shipping bodies.
   - For clipped lines, include a **hash or fingerprint** so the model doesn’t quote garbage.

3. **`grep`**
   - Context lines (`-C`) and “files with matches only” mode to cut follow-up reads.
   - Optional path-type filter (`*.cs` default for C# goals).

4. **`build` / `run_tests`**
   - Always echo **resolved target path** (project/sln) so ladder and agent share vocabulary.
   - Cache key already exists; also cache **last failure signature** briefly so identical fail can short-circuit with “unchanged failure.”

5. **`create_file`**
   - Keep default `overwrite: false`; system prompt should say when to use overwrite vs edit.

6. **System prompt / tool order**
   - After `list_projects` / `dotnet_project`, teach: *scaffold with SDK tools, not XML.*
   - Document multi-edit strategy once `apply_patch` exists.

---

## Suggested roadmap (aligned with GlassCoder phases)

```text
Now (harness effectiveness, no new model)
  ├─ apply_patch + find_symbol/file_outline
  ├─ delete_file + move_file
  ├─ list_run_changes + revert_file
  └─ tighten observations (diff snippets, resolved build target)

Next (context quality under measurement)
  ├─ MCP Learn/NuGet with record/replay (as proposed)
  └─ list_tests + test-writing nudges

Later (orchestration phase)
  └─ sub-agent tools that reuse the same small set, not more bash
```

---

## Bottom line

GlassCoder’s tools are **already well matched to its purpose**: safe, structured, .NET-verified code generation with measurable recovery—not an open shell with a chat UI. The big effectiveness gaps for AI codegen are:

1. **Throughput** (multi-hunk / multi-file edits)
2. **Navigation** (symbols & outlines, not only text grep)
3. **Tree hygiene** (delete/move/revert)
4. **Knowledge** (APIs/packages under hermetic retrieval)
5. **Self-state** (what this run already changed)

Close those with **narrow, structured tools** that still go through the path guard, change log, pre-write checks, and observation contract—rather than widening `bash` or inventing a second write path. That keeps the product’s identity: *a coding agent you can see through and measure*, with a stronger harness factor in `capability ≈ model × harness × context`.
