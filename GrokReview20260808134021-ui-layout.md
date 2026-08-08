# GrokReview 2026-08-08T13:40:21 — UI layout quality (WPF desktop)

**Audience:** code agent implementing harness / prompt / verification improvements  
**Primary evidence:** run `ea9a1f66a7fe4684b926e917cd0df08e`  
**Logs:** `%LocalAppData%\GlassCoder\logs\glasscoder-20260808.log` / `.jsonl`  
**Operator-reported defect:** multiplication **result field outside / clipped by the dialog window**  
**Related:** `GrokReview20260808122256.md` (read thrash), `GrokReview20260808114442.md` (efficiency), `GrokReview20260808124124-mcp-retrieval.md` (MCP — **not** the fix here)

---

## 1. Run snapshot

| Field | Value |
|-------|--------|
| Run id | `ea9a1f66a7fe4684b926e917cd0df08e` |
| Task | `desktop` · 13 tools · existing MultiplyApp tree |
| Stop | **Completed** |
| Steps / tokens / wall | 24 / **289 275** / ~245 s |
| Tool-call validity | **100%** (22/22) |
| Completion critique | **3/3 refuted** (step 3 and step 23) |
| Post-run review | **REFUTED** 3/3 |
| Metrics highlights | `testFailures: 10`, `testRuns: 14`, `cascadeRatio: 1`, `recoveries: 6/6`, `editsToGreen: 0` (for the real goal) |

### Timeline (compressed)

| Steps | Behaviour |
|------:|-----------|
| 0–2 | Read `MainWindow.xaml` → edit result row (`MinWidth="150"`, alignment) → build OK |
| 3 | Claim done → completion critique **3/3 REFUTED** (no proof result field is present/visible/used) |
| 4–5 | Re-read XAML + code-behind |
| 6–19 | Add / thrash on unit test `ResultTextBlock_HasMinimumWidth` (XAML string / MinWidth) — **many UnitTests fails + run_tests** |
| 20–22 | Build + all 7 tests pass |
| 23 | Claim done again → critique **3/3 REFUTED** → loop **Completed** anyway |
| post | Review **REFUTED** |

### Workspace XAML shape after the run (illustrative)

- `Window` fixed **`Height="300" Width="400"`**
- Grid with multiple `Auto` rows + `*` footer, margins ~20
- Result: `TextBlock` `x:Name="ResultTextBlock"` with `MinWidth="150"` in row 3

That combination commonly **clips** content when chrome + rows exceed client height. The agent treated “not visible” as **zero width**, not **outside / clipped by the window**.

---

## 2. Thought process

### 2.1 What “quality” failed

| Layer | Status |
|-------|--------|
| Compile / build | Green |
| Service/unit tests (logic) | Eventually green (7 tests) |
| **Visual layout (operator goal)** | **Failed** — result outside dialog |
| Critic / review | Correctly **refuted** “done” |
| Loop stop reason | Still **Completed** — process accepted a refuted claim |

So the failure is **not** “model needs Microsoft Learn.” It is:

1. **Wrong diagnosis** of a layout/sizing bug.  
2. **Wrong evidence class** after refute (proxy unit test on XAML text).  
3. **Harness allows Complete** after a full refute panel.  
4. **No oracle** for on-screen layout (by design today: Run app is human).

### 2.2 Why MCP is the wrong next step

MCP Learn/GitHub improve **external API knowledge**. This run needed:

- window/layout heuristics or better goals  
- thrash stop on identical test fails  
- completion gate after critique  
- operator **Run app** for visual acceptance  

Adding MCP would increase schema rent and step surface without proving “result is inside the window.” See `GrokReview20260808124124-mcp-retrieval.md` — **default off**; not for this suite.

### 2.3 What would have been a correct fix class

For “control outside window,” prefer (any of):

- Increase `Height` / use `SizeToContent="WidthAndHeight"` (or `Height`)  
- Reduce margins / collapse chrome  
- `ScrollViewer` if content can grow  
- Avoid packing many large fixed controls into a short fixed-height window  

**Not:** only `MinWidth` on the result `TextBlock`, and **not:** a unit test that asserts `MinWidth="150"` appears in the `.xaml` file text.

---

## 3. Findings (for implementers)

### F1 — Misdiagnosis: layout/sizing vs control width (P0 product)

Agent fix did not address fixed short window + multi-row content. Operator symptom remains layout-class.

### F2 — Proxy-test thrash after critique (P0 process)

After 3/3 refute, model spent ~15 steps on `ResultTextBlock_HasMinimumWidth`. That test cannot prove visibility; it can only burn tokens. Validity stayed 100%.

### F3 — Completed despite double 3/3 completion refute (P0 harness)

`critiqueSpent` allows a later no-tool completion without re-critique or requiring new evidence class. Post-run review still REFUTED — banner vs stop reason diverge.

### F4 — No layout-aware observation (P1 harness)

Nothing in pre-write or ladder warns: *fixed Height + dense Auto rows may clip.* Build stays green.

### F5 — Critics demand evidence worker cannot produce (P1 product/prompt)

Without Run app or UI automation, refutes on “visible in dialog” are correct but drive proxy tests unless the harness **redirects** recovery.

### F6 — MCP readiness (decision)

**Not ready / not appropriate** as next work for this failure mode. Harness + prompt + operator process first.

---

## 4. Recommended work (tasks for a code agent)

Implement in order. Prefer mechanism over long schema essays. Do **not** add MCP tools in this track.

### Task L1 — Post-completion-critique gate (P0)

**Goal:** A refuting completion critique must not be followed by `Completed` without new work or a second critique.

**Do:**

- In `AgentLoop`, after completion critique with `Refuted == true`:  
  - do **not** accept the next no-tool completion unless at least one **Applied** change landed since the refute, **or** a second critique is run and does not refute (config), **or** budget forces stop with caveat.  
- Inject **one** short recovery message (see L3 text).

**Files:** `src/GlassCoder.Core/Agent/AgentLoop.cs`, agent/critic tests.

**Acceptance:**

- [ ] Simulated: refute → immediate no-tool claim → not Completed.  
- [ ] Simulated: refute → applied edit → claim → can Complete (or second critique).  
- [ ] Run shaped like `ea9a1f66` would not Complete solely after second refute with only test thrash that critics already dismissed (if no new applied change of the right kind — minimum is “any applied change”; optional stricter “non-test-only” later).

**Motivates:** steps 3 and 23 of `ea9a1f66`.

---

### Task L2 — Stall / identical failure on same failing test (P0)

**Goal:** Stop edit ↔ `run_tests` loops on the same failing test name.

**Do:**

- Extend `RunProgressSentry` (or verification observer): if the same failing test FQN appears N times (e.g. 3) with no progress toward green for that test (or only micro-edits to the same assertion), **nudge then stop** (`RepeatedToolFailure` / `Stalled`).  
- Nudge: name the failing test; suggest overwrite test file or change approach; for UI goals, suggest layout/window size not string asserts on XAML.

**Files:** `RunProgressSentry.cs`, agent loop tests, verification summary parsing if needed.

**Acceptance:**

- [ ] Five cycles of “1 of 7 failed: Same.Test.Name” with edits only in that test → stop or hard nudge by cycle 3.  
- [ ] Different failing tests reset or track per name.  
- [ ] Validity metric remains honest (successful binds still 1.0).

**Motivates:** steps 9–18.

---

### Task L3 — UI-goal recovery nudge after critique (P0)

**Goal:** After refute on UI/visibility/layout, steer away from MinWidth/XAML-grep unit tests.

**Do:** inject (once per refute) a user message along the lines of:

```text
Critics refuted layout/visibility claims. Unit tests that only check XAML text or MinWidth
do not prove a control is on screen. Fix layout: window Height/SizeToContent, margins,
ScrollViewer if needed. Operator "Run app" is how visibility is confirmed. Prefer
edit_file on MainWindow.xaml (and code-behind) over new proxy tests.
```

Only when goal/task is UI/desktop or critique text mentions UI/dialog/visible/window.

**Files:** `AgentLoop.cs` (critique path), optional small classifier on critique summary.

**Acceptance:**

- [ ] After synthetic UI refute, next prompt contains the nudge.  
- [ ] Non-UI goals (pure library) do not get this nudge.

**Motivates:** post-step-3 thrash on `ea9a1f66`.

---

### Task L4 — Cheap XAML layout heuristic (P1)

**Goal:** Surface a layout risk **before** the operator has to notice clipping.

**Do:** On `edit_file` / `create_file` of `*.xaml` (UseWPF projects):

- Parse (regex/XML) for `Window`/`Page` with numeric `Height` below a threshold (e.g. &lt; 400) **and** several row/stack children or large fixed heights/margins.  
- Return Ok with **warning in summary** (or soft diagnostic), not necessarily refuse:  
  *“Layout risk: fixed Height={h} with dense content may clip controls; consider SizeToContent or a larger Height.”*

Do **not** attempt full layout engine simulation.

**Files:** shared helper under Tools Verification or FileSystem; call from create/edit for `.xaml`; tests with fixtures.

**Acceptance:**

- [ ] Fixture matching `ea9a1f66` window (Height 300, multi-row grid) → warning in observation summary.  
- [ ] Tall window or SizeToContent → no warning.  
- [ ] Non-XAML files unchanged.

**Motivates:** operator defect + agent MinWidth-only fix.

---

### Task L5 — Goal / system-prompt defaults for desktop tasks (P1)

**Goal:** Encode acceptance criteria the model can act on without MCP.

**Do:** Ensure desktop/system prompt (or default goal template) includes:

- All primary controls **fully visible** without clipping.  
- Prefer `SizeToContent="WidthAndHeight"` or sufficient `Height`/`Width`.  
- Result field bound or set in code-behind and **inside** the main panel.  
- Do not use unit tests that only grep XAML attributes as proof of UI.  
- Operator verifies with **Run app**.

**Files:** `config/appsettings.json` agent system prompt section and/or WPF default goal help; docs if any.

**Acceptance:**

- [ ] Prompt text present in config used by desktop role.  
- [ ] No large schema growth (prompt is not tool schema).

---

### Task L6 — Optional: “layout” mention in verification summary when UI files changed (P2)

**Do:** When applied changes include `*.xaml`, append one line to ladder summary:  
*“UI files changed — visual layout is not verified by compile/tests; use Run app.”*

**Acceptance:** Ladder message contains the line when XAML applied; omitted for pure `.cs` library edits.

---

## 5. Explicit non-goals

| Do not | Why |
|--------|-----|
| Enable Learn/GitHub MCP for this | Wrong bottleneck; see MCP GrokReview |
| Default FlaUI / UI automation in worker | Prior thrash (packages without real tests); Run app is intentional |
| Full WPF measure-pass layout engine in harness | Cost/complexity; heuristic is enough for v1 |
| Raise token/step limits so thrash “completes greener” | Hides the defect |
| Refuse all unit tests that mention XAML | Some static checks are fine; ban **only as sole proof after UI refute** via nudge/gate |

---

## 6. Implementation sequence

```text
Wave 1 (stop false Completes and proxy thrash)
  L1  post-critique completion gate
  L2  identical failing-test stall
  L3  UI recovery nudge

Wave 2 (catch layout class earlier)
  L4  XAML layout heuristic warning
  L5  desktop prompt acceptance criteria
  L6  verification footnote when XAML changes

Live trial:
  Clean GlassCoderTest → desktop multiply goal with explicit
  “result fully visible inside window”
  Operator Run app after green
  Success: no MinWidth-only thrash; no Complete after 3/3 refute without layout fix;
  clipped result less likely
```

---

## 7. Validation checklist

- [ ] Unit tests for L1–L3 behaviour (loop + sentry).  
- [ ] Fixture tests for L4 XAML heuristic.  
- [ ] `PromptBudgetTests` still green (L4/L5 must not bloat tool schemas).  
- [ ] Live re-run; record run id in `HISTORY.md`.  
- [ ] Compare to `ea9a1f66`: fewer steps after first critique; no Completed-with-3/3-refute-no-fix pattern.

---

## 8. Code anchors

| Area | Path |
|------|------|
| Loop + critique once | `src/GlassCoder.Core/Agent/AgentLoop.cs` |
| Progress / thrash | `src/GlassCoder.Core/Agent/RunProgressSentry.cs` |
| Ladder messages | `src/GlassCoder.Core/Verification/VerificationLadder.cs` |
| Edit / create XAML | `src/GlassCoder.Tools/FileSystem/EditFileTool.cs`, `CreateFileTool.cs` |
| XAML-aware pre-write (existing) | `RoslynCodeAnalyzer` / `XamlAwareGateTests` — extend carefully; layout ≠ generated partials |
| Run app (operator visual check) | WPF workspace “Run app” — host `dotnet run`, not agent tool |
| Desktop system prompt | `config/appsettings.json` → Agent / role prompts |

---

## 9. Operator process (no code — still part of “ready”)

Until L1–L4 ship:

1. After any UI task, press **Run app** and check clipping.  
2. Prefer goals that say **“fully visible inside the window”** and size constraints.  
3. On review REFUTED for visibility, use **Retry** with a layout-specific instruction, not “add more unit tests.”  
4. Use **Clean** before greenfield trials; goal restore does not clean the tree.

---

## 10. Bottom line for the code agent

Run **`ea9a1f66` completed with 100% tool validity** while the **operator-visible layout goal failed** and **critics/review refuted**. The model applied a weak MinWidth fix, then thrashed on a proxy XAML unit test.

**Next work is harness + prompt for UI layout evidence**, not MCP services.

**Ship L1 → L2 → L3 first**, then L4–L6. Revisit MCP only for external API knowledge tasks, under the gated design in `GrokReview20260808124124-mcp-retrieval.md`.

---

*Generated 2026-08-08T13:40:21 for a code agent. Evidence: run `ea9a1f66`, workspace MainWindow.xaml, session UI-layout analysis.*
