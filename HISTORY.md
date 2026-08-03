# History

Dated session logs: what shipped, what was decided and why, and what is still
open. Newest first.

The point of this file is resumption. Anything derivable from the source or the
commit log does not belong here — decisions, their reasoning, and open threads
do, because those are what a later session cannot cheaply rediscover.

---

## 2026-08-03 — The drafter role, retired to a comment

**Shipped.** `Models:Roles:drafter` is commented out in `config/appsettings.json`
rather than deleted, and the DGX Spark guide no longer contradicts itself about
what standing one up would cost. No code changed; 361 tests green.

**Why it was ever there**, because the trace is not obvious from the source and
took a git archaeology pass to recover. `drafter` entered in the first
implementation commit (`1382d58`, tasks 1–10) because workplan task 4 said "one
per served role (`worker`, `drafter`, `critic`)" — and that phrasing came from
the hardware plan in `LocalAICodingAgent-LearningGuide.md`, where a drafter is a
real thing: the 480B model that handles planning and hard edits in an 8-GPU
serving layout. It was a *serving* role that got copied into a *harness* role
list. `ModelRoles.cs` has exactly one commit in its whole history, and this file
had never once mentioned the role — that silence, next to `critic-remote`'s four
entries, is the tell. One was built; the other was only declared.

**Decided**

- **The reason to retire it is the connection check, not the memory.** Clients
  are built lazily (`GetOrAdd` inside `GetClient`), so a configured-but-uncalled
  role never opens a socket and never costs a byte. What it did cost was
  `TestAllAsync`, which probes *every* configured role: with nothing serving
  :8002, "Test all" reported a standing failure forever. A check that is always
  partly red is a check that stops being read, which defeats the reason it was
  built. That is the whole argument — "it wastes memory" was never true.
- **Commented, not deleted.** The block is the only place a reader learns the
  intended shape and that :8002 is the port the serving layout reserves, and
  ladder phase 4 still plans to point the harness at a larger drafter. Deleting
  it would move that knowledge into prose only. The binder skips comments, so
  the cost of keeping it is zero.
- **`ModelRoles.Drafter` stays.** Nothing references it, but it is a documented
  `const` that costs nothing, and `ModelRoles` already states that config may
  define any number of roles and that none of these are required. Removing it is
  a source-breaking change to a public surface that buys nothing.
- **"Remove it from the UI" turned out not to be a UI task, and that is the
  design working.** The settings dialog has no hardcoded role list — `BuildRoles`
  enumerates `Settings.Models.Roles`, and `RoleNames` feeds the three editable
  combo boxes. Dropping the config entry removed the row and all three dropdown
  entries with no XAML, ViewModel or test change. Worth writing down because the
  next role question will sound like a UI change too, and won't be one.

**The thing that will surprise you later**

Editing `appsettings.json` does **not** remove the drafter from a machine that
has ever saved settings. Configuration layering is additive per key — it merges,
it never deletes — and saved settings sit *above* `appsettings.json`, while
`CollectRoles` rewrites the full `Roles` dictionary on every Save. So any
`settings.json` written before today still carries a complete drafter block and
will keep showing the row. Clearing it is the dialog's **Remove** button (enabled
whenever more than one role exists) or **Reset**. This cuts both ways: it is also
why a user who *wants* the drafter never needed us to ship it configured.

**The guide's contradiction, and what replaced it**

`dgx-spark-setup.html` said in section 4 that a second machine serving `drafter`
needs "one line of configuration and no code change", and then said in a caution
six days newer that nothing in the harness calls it. Both were still in the file.
The claim is true of relocating a role the harness *does* call, and false of this
one. It now says so explicitly: the seam is open at the configuration layer and
not at the call layer, so standing up a drafter is a harness change, not an
endpoint change.

**Open**

What a drafter is actually handed, and when. That is the missing call site and
the real content of ladder phase 4 — a decision about how work is split between
a fast worker and a slow strong model, which is exactly the kind of thing the
metrics exist to answer. Until it is made, the alias stays commented.

---

## 2026-07-28 — Settings that travel: per project, and between machines

**Shipped.** Three files now carry settings instead of one pair. The per-user
`settings.json` / `secrets.json` are unchanged; a project can carry
`.glasscoder.json` at its root; and `Export…` / `Import…` move a whole
configuration as a `.glassconfig` file with the API keys re-encrypted under a
passphrase. 361 tests green.

**Decided**

- **Copying settings never needed a feature; carrying the keys did.** The
  settings folder was already openable and `settings.json` is already plain
  JSON, so an export button over that is a wrapper around Explorer. The thing
  file-copying cannot do is the keys: DPAPI ciphertext is bound to one Windows
  account, so a copied `secrets.json` arrives decrypting to nothing. That is the
  whole justification for the export format, and it is why the passphrase is not
  optional decoration.
- **There is no "include keys in plain text" option.** Empty passphrase means
  the keys are left out. An export is exactly the kind of file that gets
  attached to a message, and a file that quietly contains a usable credential is
  how keys reach places nobody meant to send them.
- **AES-GCM rather than CBC, and PBKDF2 at 600k iterations.** GCM authenticates,
  so a wrong passphrase is *reported* rather than yielding a plausible wrong key
  that only fails later at the endpoint as a puzzling 401. All values failing to
  decrypt is therefore a certainty about the passphrase, not a guess.
- **A section belongs to the project when its values name things inside the
  repository.** Workspace, Context, Verification, VerificationLadder, Git,
  Provenance. Everything else — the served roles, the sandbox, budgets, sinks —
  describes the machine or the experiment and stays where one copy serves every
  project. One rule, not a taste, so the next section added has an obvious home.
- **The project file never carries a key, unconditionally.** Not "only when one
  is set" — the strip runs whether or not there is anything to strip, because
  `.glasscoder.json` is one `git add` from being public and a conditional is a
  thing a caller could get wrong.
- **It omits `Workspace:RepoRoot` too.** The file's location *is* the root, so an
  absolute path inside it would only be a way to be wrong after somebody clones
  the project elsewhere. The configuration layer supplies the containing
  directory instead.
- **The project layer sits in the same band as saved settings** — above the
  machine, below the environment and `--config`. A project is a saved preference
  like any other and must not redefine what an arm means.
- **Import populates, it does not apply.** The imported keys go back under DPAPI
  through the ordinary `Save`, not through a second write path that would have
  to get the same thing right twice. The workspace root is deliberately not
  imported: a path from another machine names nothing here.
- **`SettingsDocument` exists so that lifting keys out of a document has exactly
  one implementation.** Three writers now produce settings files; a second copy
  of that step would be a second chance to leave a key in one.

**The bug worth remembering**

`AddJsonFile` given an absolute path resolves a `PhysicalFileProvider` with
`ExclusionFilters.Sensitive`, which **refuses to serve dot-prefixed files**. So
`.glasscoder.json` was skipped — and skipped in silence, because the source is
optional. No error, no log line, no file: the feature simply did nothing. The
provider is now constructed explicitly with `ExclusionFilters.None`.

This was caught only because the layering test asserted the project value
actually won, rather than that the file had been written. A test that stopped at
"the file exists" would have passed.

**Verified**

Beyond the 13 new unit tests, the dialog was driven in the real window through
UI Automation: `Save to project` wrote a file whose sections were exactly
`Workspace, Context, Verification, VerificationLadder, Git, Provenance` with no
`Models`, no `ApiKey` and no `RepoRoot`; `Export…` produced `aes-gcm-pbkdf2` at
600000 iterations with no plaintext key; and that app-produced file was then
imported back to the exact key the app held — confirming the passphrase typed
into the `PasswordBox` is the one that opens it.

**Open**

- The eight tab screenshots in `docs/settings.html` predate the three new footer
  buttons. A separate footer figure documents them; regenerating all eight would
  mean re-tuning roughly thirty hand-placed callout percentages.
- `--config` is still ignored by the WPF app. The console host parses it
  (`CommandLine.cs`); `App.xaml.cs` passes `e.Args` but never a `configPath`.
  Pre-existing, and a one-line fix.
- Removing a *default* list entry still does not survive a reload — the binder
  appends to lists, so `appsettings.json` reasserts it and the deduplicating
  reader can only collapse duplicates. Pre-existing; it affects import the same
  way it already affects save.

---

## 2026-07-27 — Dark chrome around the content

**Shipped.** The surface list and the workspace pane are now dark blue
(`#3A5A7D`), framing a content area that stays light. The header keeps
`#1F2933` and was explicitly left alone. 348 tests green.

**Decided**

- **The header stays darker than the panes.** It holds the goal and the run
  controls; being the darkest thing in the window is what keeps it reading as
  the top of a hierarchy rather than a third panel. That is why `Chrome*` and
  `Pane*` are two palettes and not one.
- **The palette is `Color` resources with the brushes derived from them**, not
  brushes alone. The tree recolours selection by overriding four `SystemColors`
  brush keys, and those need raw `Color` values — as hardcoded hex they would
  have gone on matching the *old* pane colour after any recolour, silently. The
  first version of this change had exactly that bug in it.
- **Selection is templated, not merely recoloured.** The stock `ListBoxItem`
  and `TreeViewItem` paint the system highlight, a pale blue behind near-black
  text, which on a dark ground is worse than no styling at all. The list gets a
  full template (fill plus an accent bar, since a fill alone is weak at this
  contrast); the tree gets the four brush overrides instead, because its default
  template also owns the expander glyph and the indentation and neither needed
  changing. Inactive selection is styled too — the tree loses focus whenever the
  goal box is used, and a selection that vanishes then reads as a click that did
  not register.
- **The modified-file green and red are lightened here and left alone on the
  Changes surface.** `#1B5E20` and `#B00020` are chosen against white. The panes
  agree on the hue, which is the part that carries meaning; agreeing on the hex
  would only mean one of the two is invisible.

**Verified**

Every candidate was built, captured and compared as a real window rather than
reasoned about. Two traps cost a round each and are worth writing down:

- Capture with **`PrintWindow(hwnd, hdc, 2)`** — flag 2 is
  `PW_RENDERFULLCONTENT`. `CopyFromScreen` grabs whatever is on top and is
  offset by the invisible resize border; plain `PrintWindow` without flag 2
  captures a WPF window as solid black.
- Call **`SetProcessDpiAwarenessContext(-4)` before any window measurement**.
  This display runs at 125%, so a DPI-unaware process gets virtualised
  coordinates from `GetWindowRect`, sizes the bitmap in logical units, and
  `PrintWindow` then fills only the top-left corner. The tell is a capture that
  is sharp and correctly framed but missing content — 1400x950 against a real
  1550x950.

Tree selection was checked by driving UI Automation to select a node and
capturing that, rather than assuming the brush overrides took.

---

## 2026-07-27 — The window that never opened

**Shipped.** A fix for a startup deadlock, the desktop composition root pulled
out of `App.OnStartup` so it can be built by something other than the
application, and `tests/GlassCoder.Wpf.Tests` to build it. 348 tests green.
Reported as "when I launch the app from the debugger, the application hangs" —
and it hung outside the debugger too; the debugger was incidental.

**What it was.** Enabling the git tools closed a cycle in the graph:
`ChangesViewModel` takes `GitTool` so it can decide whether to show its git
controls, `GitTool` takes `IApprovalGate` so a push still asks a human, and
`WpfApprovalGate` took `ChangesViewModel` to ask with. It appeared the moment
the previous session's Git tab was used to switch the tools on — the settings
file grew `Git:Enabled: true` three minutes after that commit, and from then on
the window never appeared.

**Decided**

- **The cycle is broken at the gate, which takes `Func<ChangesViewModel>`.**
  Of the three edges that is the one that is genuinely late: the gate needs the
  view only when something is waiting on a decision, whereas the change view
  needs to know at construction whether to show its buttons, and `GitTool`
  needs a gate it can rely on. Breaking a different edge would have made a
  constructor lie about when its dependency is really used.
- **The composition root moved to `AddGlassCoderDesktop`.** Registrations that
  live inside `OnStartup` can only be exercised by starting the application,
  which is why a defect this total went out. `App.OnStartup` is now the shared
  bootstrap plus that one call, and the test makes the same two calls.
- **The test resolves under a timeout, on an STA thread.** This class of bug
  does not throw. `Microsoft.Extensions.DependencyInjection` detects cycles
  while building call sites, and a factory registration is opaque to that — so
  instead of `InvalidOperationException` the resolver recursed, its `StackGuard`
  handed the work to a thread pool thread, and that thread blocked on the
  singleton lock the resolving thread still held. Silent, at 0% CPU, with
  nothing in the log. A test calling `GetRequiredService` directly would have
  hung the run rather than failed a case, so `UiThread.Run` gives the graph 30
  seconds on a background STA thread and fails with a `TimeoutException` that
  names the likely cause.
- **`ValidateOnBuild` would not have caught this** and was not reached for. It
  builds call sites, and the cycle is invisible at that level for the same
  reason cycle detection is.
- **Tests resolve view models, never windows.** A `Window` wants a running
  `Application`, and none of this is about rendering.

**Verified**

The diagnosis came from a stack, not a reading: `dotnet-stack` against the hung
process showed the UI thread recursing through the `ChangesViewModel` factory —
five turns of the loop visible — and a second thread parked in
`Monitor.Enter_Slowpath` inside `VisitRootCache`, which is the other half of the
deadlock. Confirmed by launching with `GlassCoder__Git__Enabled=false`, which
opened the window.

The regression test was then checked against the bug rather than merely run: the
`Func<ChangesViewModel>` registration was temporarily made to resolve eagerly,
recreating the exact cycle. The git-enabled case failed on timeout, the
git-disabled case passed, and the change was reverted. A test for a hang that
has never been watched to fail is a test that might be asserting nothing.

**Still open**

The approval flow is covered as far as the graph and no further — that the gate
and the shell share one `ChangesViewModel`, not that a request reaches the strip
and a decision comes back. That needs a pumped dispatcher.

---

## 2026-07-27 — The git settings, in the dialog

**Shipped.** A Git tab in the settings dialog with every `GitOptions` value
editable — enable, executable, timeout, hooks, commit trailer, remote, both
branch lists, the gh CLI and the pull-request base branch — plus the push
approval switch, which had been added to `ApprovalOptions` in task 41 and never
given a control. 338 tests green. Prompted by "only 8 tools are mentioned at
the bottom of the window": correct, because the tools are opt-in and there was
no way to opt in short of hand-editing `appsettings.json`.

**Decided**

- **`GitOptions` joins `GlassCoderSettings` rather than getting a parallel
  editable copy.** That aggregate is deliberately built from the *real* options
  classes, so a section becomes saveable the day it is added and only its editor
  has to be written. Adding one property was the whole of the persistence work.
- **Push approval lives on the Git tab, not in the Approval group.** Approval
  for writes sits with the sandbox because that is where writes are governed;
  someone configuring a push looks for it under Git. Discoverability decided it,
  since the complaint that started this was discoverability.
- **Git settings are validated only while the tools are enabled.** A remote of
  `--mirror` should block a save when git can act on it and should be nobody's
  problem when it cannot. The checks mirror `IsSafeRefName` in the tool, so a
  name git would read as an option is refused in the dialog rather than
  surfacing later as a puzzling tool failure. An allow-list wholly contained in
  the deny-list is refused too — it can only ever mean "nothing may be pushed".
- **Both branch lists are deduplicated on read**, joining the existing set. The
  binder appends to a list that already holds defaults, so without it a list
  doubles on every visit to the dialog. Ours default empty and would not have
  grown yet; they will the moment anyone gives them a default.

**Verified**

The dialog was opened through UI Automation and its tabs enumerated — Models,
Workspace, Agent, Verification, Sandbox, **Git**, Logging, Telemetry — so the
tab genuinely renders rather than merely compiling. A settings file carrying
`Git:Enabled` was then fed to the console host, which advertised all five git
tools: the whole chain from saved file to registered tool, not just the halves.

**Then, the same day: the bash switch too.**

`Sandbox:EnableBashTool` became a real property on `SandboxOptions` and got a
checkbox in the Sandbox tab, beside the guardrails that decide whether bash can
run at all. It had been read straight from configuration with `GetValue` and
existed nowhere as a property, so it could be set but never saved.

- **Both opt-in keys are now built from `nameof`.** `$"{SandboxOptions.
  SectionName}:{nameof(SandboxOptions.EnableBashTool)}"` cannot drift from the
  property the settings file writes; the two literal strings could, silently,
  and the failure mode is a capability that quietly does not appear.
- **`ToolRegistrationTests` is new, and covers a gap that predates all of
  this**: nothing had ever asserted which tools are advertised, or that either
  opt-in key is spelled the way the settings file spells it. It fakes
  `ICommandExecutor` first so `TryAddSingleton` leaves it alone and no test
  reaches for a Docker daemon.
- Verified the same way as the git tab: the Sandbox tab was opened through UI
  Automation and its checkboxes enumerated (the new one among them), then a
  settings file carrying the switch was fed to the console host, which
  advertised `bash`.

---

## 2026-07-27 — Every saved setting was being discarded at startup

**Shipped.** A one-character-class bug with a wide blast radius: saved settings
were layered *underneath* `appsettings.json` instead of over it, so everything
the settings dialog and the workspace pane ever wrote was read and then
overruled. Reported as "the project points at `bin\Debug\net10.0-windows`, and
choosing the right folder does not survive a restart". 333 tests green.

**What was wrong**

`AddGlassCoderUserSettings` inserted the user settings file ahead of the *first*
`EnvironmentVariablesConfigurationSource` it found. `HostApplicationBuilder`
registers **two**: the `DOTNET_`-prefixed host source, which sits *before*
`appsettings.json`, and the unprefixed application source after it. The scan
stopped at the first, so saved settings landed at index 1 and `appsettings.json`
at index 4 — and later sources win. Every saved value that `appsettings.json`
also mentions was silently discarded; only keys absent from it (and the API
keys, arriving through a sibling in-memory source) ever took effect.

The workspace root made it visible because `appsettings.json` says `"."`, which
`PathGuard` resolves against the *process working directory* — the executable's
own folder for a double-clicked window.

**Decided**

- **Scan backwards for the last environment source.** That restores the
  documented intent — saved settings beat `appsettings.json`, lose to
  environment variables and `--config` — and it is now stated in the doc
  comment as load-bearing rather than incidental.
- **The old test could not have caught this.** It built a hand-rolled
  `ConfigurationBuilder` with one environment source at the end, a shape that
  does not exist in production. The replacement drives the real
  `Host.CreateApplicationBuilder` with a real `appsettings.json` and a real
  saved file; reverting the fix fails it and nothing else.
- **`"."` stays working-directory for the console host and is discovered for
  the desktop.** `cd` into the repo then run is the right contract for a CLI
  and the wrong one for a window nobody launches from a shell. So
  `WorkspaceRootLocator` walks up from the executable for `.git` or a solution
  file, and the WPF app applies it *only* when no layer supplied a real root —
  anything chosen, saved or exported still wins. Outside a checkout it finds
  nothing and says so rather than guessing.
- **Both front ends now log the workspace root at startup.** The generic host
  announces its content root and nothing announced this one, which is exactly
  how a root silently resolving to build output survived unnoticed in an
  application whose first-class claim is instrumentation.

**Open**

- Worth auditing whether any behaviour previously attributed to configuration
  was really this bug — every dialog-saved endpoint, budget and sandbox mode
  was inert until now.

---

## 2026-07-27 — Git tools, step 3: pull requests and the manual path (workplan task 42)

**Shipped.** `create_pull_request` opens a GitHub PR for the current branch
through the `gh` CLI, behind the same approval gate as push. The changes pane
grew a git strip — commit message box, Commit and Push — that calls the very
same tool methods the model calls, and every button press lands in the
transcript as its own step. 323 tests green, build clean, app verified to
launch with git both disabled and enabled. Tasks 40–42 are closed; only 38
remains open.

**Decided**

- **`gh`, not Octokit.** The CLI already holds the credentials via `gh auth`,
  so GlassCoder still holds no token of its own — the same bargain step 1
  struck with the credential manager. Octokit would have meant a PAT this
  application stores, plus a new dependency, to reimplement what `gh pr
  create` already does. The cost is a second host prerequisite, which the
  failure path names explicitly rather than leaving as a mystery exit code.
- **No `IGitService` was extracted.** `GitTool` *is* the service: its methods
  already return `ToolObservation<T>`, which carries exactly the `Ok`,
  `Summary` and `Error` the UI wants. An interface mirroring the class one for
  one would be ceremony, and the codebase already set the precedent of driving
  tools directly — the verification ladder calls `build` and `run_tests`
  without going through the model-facing registry.
- **The buttons are the same code path, so the guardrails cannot diverge.** A
  manual push still asks the approval gate; a manual commit still filters
  staging through the writable set. Nothing about pressing a button makes the
  action more trusted than the model asking for it.
- **Manual actions are logged as `Role: "human"` steps.** They go through
  `IStepLogger`, which is the `TranscriptBus` — so they reach the durable
  JSONL *and* the live transcript view. Outside a run `RunContext.Current`
  yields `no-run`/`no-task`, which is honest: the action belonged to a person,
  not a run, and it therefore contributes to no run's metrics.
- **The buttons stand down mid-run.** The shell pushes `IsRunning` into the
  pane. Committing a tree the agent has not finished changing would record
  work in progress as though it were finished.
- **A pull request refuses to describe unpushed commits.** Ahead-of-upstream
  means the PR would show reviewers something other than what the agent
  built, so it errors with a "run git_push first" hint rather than opening
  something misleading.
- **`--flag=value` everywhere in the gh invocation.** A title beginning with a
  dash cannot then be parsed as an option — the same class of defence as
  `IsSafeRefName` on the git side.

**Open**

- Task 38 (measure the dormant capabilities) is the last one standing.
- The manual git strip has no "sync" or "open PR" button; those stayed
  model-only until there is a reason to widen the surface.
- Manual steps use their own index counter, so a transcript containing both
  manual and run steps has two independent sequences. Harmless for filtering,
  slightly odd if read as one timeline.
- `gh` is a second host prerequisite alongside git, and nothing checks for it
  until a PR is attempted.

---

## 2026-07-27 — Git tools, step 2: sync and push, behind a human (workplan task 41)

**Shipped.** The outward-facing half: `git_sync` (pull --rebase onto the
upstream, clean tree required, conflicts auto-aborted and reported) and
`git_push` (current branch to the configured remote, gated by human approval).
The approval seam now has an action shape — `AgentAction` +
`RequestActionAsync` — beside the diff shape, `RequireApprovalForPush`
defaults to true, and the WPF changes pane shows a push approval with the
outgoing commits where the diff would be. 314 tests green, build clean.

**Decided**

- **The approval policy lives in the gates, not the tool.** `GitTool` always
  asks `IApprovalGate`; `AutoApprovalGate` and `WpfApprovalGate` each consult
  `RequireApprovalForPush`, exactly mirroring the write path. So the headless
  host fails closed out of the box — a bare `GitTool` with no interactive gate
  cannot push at all — and a test proves it.
- **Push approval is default-on; write approval stays default-off.** A write
  can be reverted from the change log; a push has left the machine. The
  asymmetry is the point, and it is written into the options' doc comments.
- **A conflicted sync aborts itself.** The agent has no tool to resolve or
  abort a rebase, so leaving one in progress would wedge the repository. The
  tree comes back exactly as it was, the conflicted files are named in a
  `merge_conflict` observation, and reconciling is explicitly a human's call.
- **Branch policy is two lists, checked before anyone is asked.**
  `ProtectedBranches` (deny, wins) and `PushableBranches` (allow, empty =
  any); a refusal is `branch_not_allowed` and never consumes a human approval.
  There is no force flag and no free-form refspec anywhere in the schema, so
  the lists plus the configured remote are the entire policy surface.
- **The push approval reuses the changes strip wholesale.** `PendingApproval`
  now carries either a change or an action; an action's detail lines render
  through the existing diff template as context lines. Same buttons, same
  timeout-is-refusal, no new UI surface to maintain.
- **Failure hints teach the loop.** Auth failures hint at the host credential
  manager (GlassCoder still holds no tokens); non-fast-forward rejections hint
  at `git_sync`; `git_sync` without an upstream hints that the first
  `git_push` sets it (`-u`).

**Open**

- Step 3 (task 42): `create_pull_request` and manual Commit/Push buttons over
  the same code path, plus logging button-initiated actions into the run
  record.
- Sync counts movement by before/after SHA, not "commits pulled" — a rebase
  makes that number honest to compute only with more plumbing than it is
  worth so far.
- The commit SHA still is not tied into `IChangeLog`; unchanged from step 1.

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
