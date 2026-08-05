# Telemetry

Published snapshots of GlassCoder's run artifacts, committed so a session **without access
to the machine that ran the app** - typically Claude Code on claude.ai reading this repo
over GitHub - can analyse runs for bugfixing and performance work.

These are copies, not the live files. The app writes to `%LOCALAPPDATA%\GlassCoder`
(override: `GLASSCODER_DATA_DIR`); `scripts/publish-logs.ps1` snapshots that data here,
prunes transcripts older than 14 days from the working tree, and pushes. Never edit these
files - the next publish overwrites them.

## Contents

- `transcripts/glasscoder-<yyyyMMdd>.jsonl` - the full Serilog transcript, one compact
  JSON event per line. Per-step records ride on events under the `"Step"` property
  (`SerilogBootstrap.StepPropertyName`); run and review records under `"Run"` and
  `"Review"`. A step record carries the prompt, model response, every tool call with
  arguments/result/status, token counts, and latencies - a run is reconstructable from
  this file alone (`TranscriptReader` parses it).
- `transcripts/glasscoder-<yyyyMMdd>.log` - the human-readable view of the same day,
  minus the step blobs.
- `metrics/metrics.jsonl` - one `RunMetrics` object per run: pass@1, tool-call validity,
  steps/tokens-to-solve, wall-clock, and the other CLAUDE.md §11 indicators.

## Quick starts for analysis

Useful step fields: `.Step.ModelLatencyMs`, `.Step.StepLatencyMs`, `.Step.InputTokens`,
`.Step.OutputTokens`, `.Step.EstimatedContextTokens`, `.Step.ToolCalls[].DurationMs`.

```powershell
Get-Content telemetry/transcripts/glasscoder-20260804.jsonl |
    ConvertFrom-Json | Where-Object Step | ForEach-Object Step |
    Select-Object StepIndex, ModelLatencyMs, InputTokens, OutputTokens
```

```bash
jq -c 'select(.Step) | .Step | {StepIndex, ModelLatencyMs, InputTokens, OutputTokens}' \
    telemetry/transcripts/glasscoder-20260804.jsonl
```

A possibly torn (half-written) final line in today's file is expected when the app was
running during publish - skip lines that fail to parse.
