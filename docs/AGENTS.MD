# AGENTS.md — Spec-to-PR Agent Orchestrator

Context for coding agents working on this repo. Read fully before changing code.

## What this is
A durable .NET orchestrator that takes a written spec and drives Claude Code to implement it, story by story, using a Ralph loop. **Temporal is the only orchestrator.** Claude Code is a CLI process started by an activity; it knows nothing about Temporal.

Portfolio project: every design choice must be simple and explainable. Prefer fewer moving parts.

## Current milestone: M1 (thin slice)
One spec → one Temporal workflow → Ralph loop over stories → all stories pass on a feature branch.

**Out of scope for M1 — do not build:** PR creation, LangFuse/tracing, Docker, HTTP API/webhooks, human approval signals, planner, reviewer agent, CI gate, cost budgets, parallel tasks. If a change needs one of these, stop and ask.

## Milestone M2: spec-only input
`SpecToPrWorkflow` takes only a spec file path (`SpecToPrInput`). It:
1. Copies the spec into a worktree on `spec/{featureId}`.
2. Has the agent draft `stories.json`, validated by `ValidateStories`, then waits for the `ApproveStories` signal. A rejection or validation error is fed back into the next draft.
3. Commits spec + stories.
4. Has the agent draft failing acceptance tests, checked by `VerifyTests`: only acceptance/solution files changed, the build succeeds, every story test exists and fails, and the other tests pass.
5. Commits the tests.
6. Runs `FeatureWorkflow` unchanged as a child (`{workflowId}-impl`) from the tests commit.

The agent runs reuse the `RunAgent` activity with an `AgentProfile` (`Implement | DraftStories | DraftTests`). The profile picks the prompt file and the allowed tools. Tests have no human gate.

## Decisions (do not change without asking)
| ADR | Decision |
|---|---|
| 1 | No Microsoft Agent Framework. Small LLM steps use `Microsoft.Extensions.AI` `IChatClient` inside activities. |
| 2 | Spec lives in the target repo. Workflow input: `{ repoPath, specPath, baseCommitSha, featureId }`. Local repo path (no cloning) for M1. Never pass document content through Temporal. |
| 3 | Claude Code spawned via CLI in headless mode using CliWrap, behind `IAgentRunner` (`ClaudeCodeRunner`). No Agent SDK. |
| 4 | Ralph loop lives in workflow code. Iteration = `RunAgent` → `Verify` → (`CommitStory` \| `SummarizeFailure`). Failure feedback is an `IChatClient` summary written to `last_failure.md`; raw log kept on disk; fallback to raw-log tail if summarizer fails. |
| 4b | Story status source of truth = workflow state. `status.json` is an orchestrator-written projection. Agent writes only code and `progress.md`. |
| 6 | Runtime: `temporal server start-dev` + worker on host. No Docker for M1. |
| 7 | Spec-only input is a separate parent workflow (`SpecToPrWorkflow`) that calls `FeatureWorkflow` as a child. `FeatureWorkflow` keeps assuming committed spec, stories and tests. Only the spec file path crosses Temporal, never its content. |

## Solution layout
```
src/
  Orchestrator.Contracts/    # inputs/outputs, DTOs, records only
  Orchestrator.Workflows/    # deterministic workflow code only
  Orchestrator.Activities/   # agent runner, git, verify, summarizer
  Orchestrator.Worker/       # Generic Host + Temporal worker
tests/
  Orchestrator.Workflows.Tests/    # time-skipping env, mocked activities
  Orchestrator.Activities.Tests/   # parsers, runner arg building, git
samples/hello-target/        # tiny .NET repo used as an end-to-end target
docs/adr/
```

## Target repo conventions (the repo being worked on, not this one)
```
specs/<featureId>/
  spec.md               # human-written, LOCKED
  stories.json          # agent-drafted, human-approved, LOCKED
  acceptance/           # tests per story, written before the loop, LOCKED
.ralph/<featureId>/
  PROMPT.md             # rendered by orchestrator each iteration
  status.json           # written ONLY by orchestrator
  last_failure.md       # written ONLY by orchestrator
  progress.md           # append-only, written by the agent
```

`stories.json` shape:
```json
{ "featureId": "f1",
  "stories": [ { "id": "S1", "title": "...", "priority": 1,
                 "acceptanceCriteria": ["..."], "testIds": ["Namespace.Class.Method"] } ] }
```

`status.json` shape:
```json
{ "stories": [ { "id": "S1", "passes": false, "attempts": 2, "lastFailureSignature": "..." } ] }
```

## Workflow: `FeatureWorkflow`
1. `PrepareWorkspace` — create git worktree at `{WorkRoot}/{workflowId}/` from `baseCommitSha` on branch `ralph/{featureId}`. Idempotent: reuse if it exists.
2. `LoadStories` — read and validate `stories.json` (every story has ≥1 testId).
3. Loop: pick highest-priority story with `passes == false`.
   - `WriteLoopFiles` — write `status.json` + render `PROMPT.md` for that story.
   - `RunAgent` — run Claude Code once (fresh context). Heartbeat while running.
   - `Verify` — story passes only if ALL hold:
     - build succeeds (exit code)
     - the story's `testIds` exist and pass (parse TRX)
     - full test suite passes
     - diff does not touch locked paths (`spec.md`, `stories.json`, `acceptance/`, `status.json`, `last_failure.md`)
   - Pass → `CommitStory` (orchestrator commits, not the agent), set `passes = true`.
   - Fail → `SummarizeFailure` → `last_failure.md`; increment attempts.
4. End: all stories pass → `Completed`. Otherwise → `Escalated` with reason.

Caps (workflow input, with defaults): `MaxAttemptsPerStory`, `MaxTotalIterations`. Stall: same failure signature twice in a row for a story → escalate early. In M1, escalation just ends the workflow with a result payload (story, attempts, last summary, raw log path).

Capture per `RunAgent`: exit code, turns, cost/tokens if the CLI JSON reports them, duration, raw output path. Return these in activity results (needed later for metrics).

## Claude Code runner
- Headless/print mode, JSON output, max-turns limit, explicit allowed-tools list, scoped permission mode, working dir = the worktree.
- **CLI flags change. Verify every flag against current Claude Code docs (`claude --help`) before relying on it.** Keep flag building in one place with unit tests.
- Never use flags that skip all permission checks.
- Large output goes to a file in the workspace, not into Temporal payloads.

## Rules
**Workflow code must be deterministic:** no `DateTime.Now`, `Guid.NewGuid`, `Random`, I/O, `Task.Run`, or non-Temporal async in `Orchestrator.Workflows`. Use `Workflow.UtcNow` etc. All side effects go in activities.

**Payloads small:** pass paths, SHAs, IDs. Never file contents, logs or diffs.

**Idempotency:** activities are safe to retry. Key on workflowId + storyId + iteration. Retrying must never create a duplicate branch, worktree or commit.

**Long activities:** `RunAgent` and `Verify` heartbeat and have explicit start-to-close + heartbeat timeouts.

**Config:** `WorkRoot`, CLI path, model, caps via `appsettings.json` + env vars. No secrets in the repo.

**Style:** .NET (latest LTS), nullable enabled, records for DTOs, file-scoped namespaces, small classes. No new frameworks or packages without asking.

## Commands
```bash
temporal server start-dev                       # local Temporal (UI on :8233)
dotnet build
dotnet test
dotnet run --project src/Orchestrator.Worker
temporal workflow start --task-queue orchestrator --type FeatureWorkflow \
  --workflow-id feature-f1 --input '{"repoPath":"...","specPath":"specs/f1/spec.md","baseCommitSha":"...","featureId":"f1"}'
```
Verify Temporal CLI and SDK APIs against current docs; don't guess.

## Definition of done for M1
- `samples/hello-target` with a 2–3 story spec runs end to end: all stories pass, one commit per story on `ralph/f1`.
- Killing the worker mid-loop and restarting resumes from the last completed activity (demonstrate it).
- A forced failure produces `last_failure.md` and a retry; repeated failure escalates.
- Workflow tests cover: happy path, retry-then-pass, cap escalation, stall escalation, locked-file violation.

## How to work
- Build in this order: Contracts → Workflow + tests with mocked activities → activities one by one → sample target → end-to-end run.
- Small commits. Run `dotnet build` and `dotnet test` before finishing any task.
- Unsure about an API or flag: say so and check docs. Don't invent.
- Conflict with this file: stop and ask.