# Spec-to-PR Agent Orchestrator

A durable .NET orchestrator that takes a written spec and drives [Claude Code](https://docs.claude.com/en/docs/claude-code) to implement it, story by story, until every acceptance test passes. [Temporal](https://temporal.io) runs the loop, so a run survives worker crashes and restarts and resumes from the last completed step.

## Overview

There are two ways to start:

- **From a spec only** (`SpecToPrWorkflow`): you give it a spec file. The agent drafts the stories, you approve them, the agent writes failing acceptance tests, and the orchestrator commits both and runs the loop below. See [Starting from a spec only](#starting-from-a-spec-only).
- **From a prepared repo** (`FeatureWorkflow`): you write the spec, stories and acceptance tests yourself, in a **target repo**.

Either way, the orchestrator then runs a [Ralph loop](https://ghuntley.com/ralph/) over those stories:

1. Pick the highest-priority story that doesn't pass yet.
2. Run Claude Code once, headless, with a fresh context and a prompt for that story.
3. Verify the result independently: build, the story's tests, the full suite, and a check that no locked file was touched.
4. If it passes, the orchestrator commits it, one commit per story. If it fails, an LLM summarizes the failure into `last_failure.md` for the next attempt.

The run ends when every story passes (`Completed`), or when a story runs out of attempts, the run hits its iteration cap, or a story fails the same way twice in a row (`Escalated`).

All work happens on a `ralph/<featureId>` branch in a separate git worktree. Your checkout of the target repo is never changed.

```mermaid
flowchart LR
    A[PrepareWorkspace] --> B[LoadStories]
    B --> C{Next failing story?}
    C -- none --> Z[Completed]
    C -- yes --> D[WriteLoopFiles]
    D --> E[RunAgent<br/>Claude Code]
    E --> F[Verify]
    F -- pass --> P{New NuGet<br/>packages?}
    P -- no --> G[CommitStory]
    P -- yes --> H[/Wait for<br/>ApprovePackages signal/]
    H -- approved --> G
    H -- rejected --> S
    G --> C
    F -- fail --> S[SummarizeFailure]
    S --> X{Cap or stall?}
    X -- no --> C
    X -- yes --> Y[Escalated]
```

### Key design choices

- **Temporal is the only orchestrator.** Claude Code is a CLI process started by an activity, and it knows nothing about Temporal.
- **The workflow owns story status.** `status.json` is a copy the orchestrator writes. The agent writes only code and `progress.md`.
- **The orchestrator decides what passes, not the agent.** Verification is a separate activity that parses TRX test results. Commits are made by the orchestrator.
- **Locked inputs.** An iteration fails if it touches `spec.md`, `stories.json`, `acceptance/`, `status.json` or `last_failure.md`.
- **A human approves dependencies.** A passing iteration that adds NuGet packages waits for a human decision before it is committed.
- **Small payloads.** Only paths, SHAs and ids go through Temporal. Logs and file contents stay on disk.
- **Optional tracing.** One Langfuse trace per run, with cost, tokens and scores (see [Observability](#observability-langfuse)).

The full design notes and decision records are in [docs/AGENTS.MD](docs/AGENTS.MD).

## Project layout

```
src/
  Orchestrator.Contracts/    Workflow and activity inputs/outputs (records only)
  Orchestrator.Workflows/    FeatureWorkflow and the Ralph loop policy (deterministic code only)
  Orchestrator.Activities/   Claude Code runner, git/worktree, stories, verify, failure summarizer, telemetry
  Orchestrator.Worker/       Generic Host + Temporal worker, configuration, OpenTelemetry
tests/
  Orchestrator.Workflows.Tests/    Workflow tests in Temporal's time-skipping environment, activities mocked
  Orchestrator.Activities.Tests/   Parsers, CLI argument building, git, verification, Langfuse scores
docs/AGENTS.MD               Design notes and decisions
```

## Setup

### Prerequisites

Install these and make sure they are on `PATH`. The worker checks for `git`, `dotnet` and the Claude CLI at startup and exits if any is missing.

| Tool | Check | Notes |
|---|---|---|
| .NET 10 SDK | `dotnet --version` | |
| git | `git --version` | |
| Temporal CLI | `temporal --version` | Provides the local dev server. |
| Claude Code CLI | `claude --version` | Signed in, or `ANTHROPIC_API_KEY` set. |

You also need an Anthropic API key for the failure summarizer. It is read from the environment only:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

### Configure the worker

Settings are in [src/Orchestrator.Worker/appsettings.json](src/Orchestrator.Worker/appsettings.json). Put machine-specific values in `appsettings.Development.json` next to it, which git ignores, or in environment variables named `Orchestrator__<Setting>`.

`WorkRoot` is the only required setting. Point it at a folder **outside** the target repo:

```json
{
  "Orchestrator": {
    "WorkRoot": "D:\\temporal-runs"
  }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `WorkRoot` | *(required)* | Worktrees go in `{WorkRoot}/{workflowId}`, raw logs in `{WorkRoot}/logs/{workflowId}`. |
| `TemporalAddress` | `localhost:7233` | Temporal frontend. |
| `Namespace` | `default` | Temporal namespace. |
| `ClaudeCliPath` | `claude.cmd` (Windows) / `claude` | Claude Code executable. |
| `AgentModel` | `sonnet` | Model Claude Code uses. |
| `MaxBudgetUsdPerRun` | `2.0` | Spending cap for one agent run. |
| `MaxTurns` | `50` | Turn cap for one agent run. |
| `AllowedTools` | Read, Edit, Write, Glob, Grep, `dotnet build/test/add package` | Tools the agent may use while implementing a story. |
| `StoryAuthoringAllowedTools` | Read, Write, Glob, Grep | Tools the agent may use while drafting `stories.json`. |
| `TestAuthoringAllowedTools` | Read, Edit, Write, Glob, Grep, `dotnet build/test/new/sln/add` | Tools the agent may use while drafting acceptance tests. |
| `SummarizerModel` | `claude-haiku-5-5` | Model that writes `last_failure.md`. |
| `RawLogTailBytes` | `16384` | Raw-log tail used when the summarizer is unavailable. |

The launch profile sets `DOTNET_ENVIRONMENT=Development`, so `dotnet run` picks up `appsettings.Development.json`.

### Build and test

```powershell
dotnet build
dotnet test
```

## Preparing a target repo

The agent works on a separate local git repo. For each feature, commit these files at the commit you will start from:

```
specs/<featureId>/
  spec.md          # what to build, written by a human
  stories.json     # the stories, each with at least one test id
  acceptance/      # acceptance tests for every story, written before the run
```

Example `stories.json`:

```json
{
  "featureId": "f1",
  "stories": [
    {
      "id": "S1",
      "title": "Health endpoint",
      "priority": 1,
      "acceptanceCriteria": ["GET /health returns 200"],
      "testIds": ["Retail.Acceptance.Tests.HealthTests.Returns_ok"]
    }
  ]
}
```

- `featureId` must match the workflow input, and story ids must be unique.
- `testIds` are `Namespace.Class.Method`, matched against the TRX test results.
- The acceptance test project must be part of the solution, because verification runs `dotnet test` from the repo root.
- Tests of stories not reached yet are expected to fail, and the full-suite check ignores them.

## Running a feature

### 1. Start Temporal

```powershell
temporal server start-dev
```

The server listens on `localhost:7233` and the UI is at http://localhost:8233.

### 2. Start the worker

In a second terminal, with `ANTHROPIC_API_KEY` set (and the Langfuse keys, if you want traces):

```powershell
dotnet run --project src/Orchestrator.Worker
```

The worker polls the `orchestrator` task queue. One worker serves every feature and repo, so leave it running.

### 3. Start the workflow

Write the input to a file. Windows PowerShell 5.1 strips the quotes from inline JSON passed to native programs, so `--input '{...}'` fails there.

There are two workflows. Pick one.

#### Option A: from a spec only (`SpecToPrWorkflow`)

Give it a spec file anywhere on disk. The agent drafts the stories, you approve them, the agent writes the acceptance tests, and then the Ralph loop runs. The target repo needs no `specs/<featureId>/` folder.

```powershell
$sha = git -C D:\temporal-work rev-parse HEAD

@"
{"repoPath":"D:\\temporal-work","specFilePath":"D:\\specs\\f3.md","baseCommitSha":"$sha","featureId":"f3"}
"@ | Set-Content -Encoding ascii spec-f3.json

temporal workflow start `
  --task-queue orchestrator `
  --type SpecToPrWorkflow `
  --workflow-id spec-f3 `
  --input-file spec-f3.json
```

| Field | Required | Meaning |
|---|---|---|
| `repoPath` | yes | Absolute path to the local target repo. |
| `specFilePath` | yes | Absolute path to the spec on the worker's disk. It is copied to `specs/<featureId>/spec.md`. |
| `baseCommitSha` | yes | Commit to branch from. It does not need `specs/<featureId>/`. |
| `featureId` | yes | Names `specs/<featureId>/` and the `spec/<featureId>` and `ralph/<featureId>` branches. |
| `maxStoryDrafts` | no | Default `3`. Stories drafts allowed, counting invalid and rejected ones. |
| `maxTestDrafts` | no | Default `3`. Acceptance test drafts allowed. |
| `maxAttemptsPerStory` | no | Default `3`. Passed to `FeatureWorkflow`. |
| `maxTotalIterations` | no | Default `20`. Passed to `FeatureWorkflow`. |

The run then waits for you to approve the stories. See [Starting from a spec only](#starting-from-a-spec-only) for the approval commands and what each step checks.

#### Option B: from a prepared repo (`FeatureWorkflow`)

The spec, stories and acceptance tests are already committed in the target repo (see [Preparing a target repo](#preparing-a-target-repo)).

```powershell
@'
{"repoPath":"D:\\temporal-work","specPath":"specs/f1/spec.md","baseCommitSha":"<sha>","featureId":"f1"}
'@ | Set-Content -Encoding ascii feature-f1.json

temporal workflow start `
  --task-queue orchestrator `
  --type FeatureWorkflow `
  --workflow-id feature-f1 `
  --input-file feature-f1.json
```

| Field | Required | Meaning |
|---|---|---|
| `repoPath` | yes | Absolute path to the local target repo. |
| `specPath` | yes | Repo-relative path to the spec. |
| `baseCommitSha` | yes | Commit to branch from (`git -C <repoPath> rev-parse HEAD`). It must contain `specs/<featureId>/`. |
| `featureId` | yes | Matches `specs/<featureId>/`. |
| `maxAttemptsPerStory` | no | Default `3`. |
| `maxTotalIterations` | no | Default `20`. |

For either workflow, use a new workflow id for each run. The id also names the worktree folder.

### 4. Follow progress

Changes appear in the worktree, not in your checkout of the target repo.

| What | Where |
|---|---|
| Activity timeline | Temporal UI → http://localhost:8233 → your workflow id |
| Current activity and heartbeat | `temporal workflow describe --workflow-id feature-f1` |
| Story pass/fail state | `{WorkRoot}/feature-f1/.ralph/f1/status.json` |
| One commit per passing story | `git -C {WorkRoot}/feature-f1 log --oneline` |
| Agent notes | `{WorkRoot}/feature-f1/.ralph/f1/progress.md` |
| Last failure feedback | `{WorkRoot}/feature-f1/.ralph/f1/last_failure.md` |
| Raw agent, build and test output | `{WorkRoot}/logs/feature-f1/` |
| Final result | `temporal workflow result --workflow-id feature-f1` |

`Escalated` results include the reason (`MaxAttemptsPerStory`, `MaxTotalIterations` or `Stalled`), the story, and the paths to the last summary and raw log.

### 5. Approve or reject new packages

When an iteration passes but adds or changes NuGet packages, the workflow waits until you decide. It waits indefinitely, and the worker logs a warning.

```powershell
# What is waiting?
temporal workflow query --workflow-id feature-f1 --name PendingPackageApproval

# Approve
'{"approved":true}' | Set-Content -Encoding ascii decision.json
temporal workflow signal --workflow-id feature-f1 --name ApprovePackages --input-file decision.json

# Reject. The comment goes to the agent's next attempt.
'{"approved":false,"comment":"Use System.Text.Json instead"}' | Set-Content -Encoding ascii decision.json
temporal workflow signal --workflow-id feature-f1 --name ApprovePackages --input-file decision.json
```

### 6. Merge the result

`ralph/<featureId>` is a normal branch in the target repo. When the run completes, merge it from your own checkout and remove the worktree:

```powershell
cd <repoPath>
git checkout master
git merge --ff-only ralph/f1          # or --no-ff for a merge commit
git worktree remove <WorkRoot>\feature-f1
git branch -d ralph/f1
```

The branch also contains `.ralph/<featureId>/`, the loop's working files. Keep it as a record of the run, or `git rm -r .ralph` after merging.

### Stopping, resuming and rerunning

- **Stop and resume:** stop the worker with Ctrl+C at any time and start it again. The workflow resumes from the last completed activity. Activities are idempotent, so retries never create duplicate worktrees, branches or commits.
- **Abandon a run:** `temporal workflow terminate --workflow-id feature-f1 --reason "abandoned"`.
- **Rerun a feature from scratch:** remove the worktree **and** delete the branch. `PrepareWorkspace` reuses an existing `ralph/<featureId>` branch, so a rerun on the old branch would start from the finished code.

  ```powershell
  git -C <repoPath> worktree remove <WorkRoot>\feature-f1
  git -C <repoPath> branch -D ralph/f1
  ```

  Then start the workflow again, preferably with a new workflow id (for example `feature-f1-r2`).

## Starting from a spec only

`SpecToPrWorkflow` needs only a spec file. Start Temporal and the worker (steps 1 and 2 above), then start it with [Option A of step 3](#option-a-from-a-spec-only-spectoprworkflow).

What happens:

1. **Workspace.** A worktree `{WorkRoot}/spec-f3` on a new branch `spec/f3`, and the spec copied into it.
2. **Stories.** The agent drafts `specs/f3/stories.json` from the spec. `ValidateStories` checks it: valid JSON, unique ids, a title, acceptance criteria and `Namespace.Class.Method` test ids for every story, and no file changed except `stories.json`. An invalid draft goes back to the agent with the errors.
3. **Your approval.** A valid draft waits for you, indefinitely:

   ```powershell
   temporal workflow query --workflow-id spec-f3 --name PendingStoriesApproval   # draft number, path, story list
   # Read the file at the path it shows, then approve:
   '{"approved":true}' | Set-Content -Encoding ascii decision.json
   temporal workflow signal --workflow-id spec-f3 --name ApproveStories --input-file decision.json
   # or reject. The comment goes to the next draft:
   '{"approved":false,"comment":"Split S2 into create and cancel"}' | Set-Content -Encoding ascii decision.json
   temporal workflow signal --workflow-id spec-f3 --name ApproveStories --input-file decision.json
   ```

   Approved stories are committed with `spec.md` (`docs(f3): add spec and approved stories`).
4. **Tests.** The agent writes an xUnit project under `specs/f3/acceptance/` and adds it to the solution. `VerifyTests` accepts the draft only if all of these hold:
   - Only the acceptance folder and solution files changed.
   - The solution builds.
   - Every story test id is found and **fails**, because nothing is implemented yet.
   - Every other test still passes.

   A rejected draft goes back to the agent with a `last_failure.md`. Valid tests are committed (`test(f3): add acceptance tests`). There is no human gate on tests.
5. **Implementation.** A child `FeatureWorkflow` with id `spec-f3-impl` runs the Ralph loop on `ralph/f3`, branched from the tests commit. Package approvals for this phase go to `spec-f3-impl`.

`temporal workflow query --workflow-id spec-f3 --name Stage` shows where the run is: `DraftingStories`, `AwaitingStoriesApproval`, `DraftingTests`, `Implementing` or `Done`.

The result includes the approved stories, the spec commit, and the child's `FeatureWorkflowResult`. `Escalated` gives a reason:

| Reason | Meaning | `detail` |
|---|---|---|
| `StoriesNotApproved` | No draft was approved within `maxStoryDrafts`. | The last feedback. |
| `TestsInvalid` | No test draft passed `VerifyTests` within `maxTestDrafts`. | Path of the last failure summary. |
| `ImplementationEscalated` | The child `FeatureWorkflow` escalated. | Its last failure summary. See `feature.escalation`. |

Merge `ralph/f3` as usual. It contains the spec commits too. Neither the `spec/f3` worktree nor the `spec/f3` branch is needed after the run.

Things to know:
- The target repo's `.gitignore` must ignore `bin/` and `obj/`. Otherwise build output shows up as changes outside the acceptance folder.
- Acceptance tests must compile before the feature exists, so the prompt asks the agent to test from the outside in (for example over HTTP with `WebApplicationFactory<Program>`), following the style of earlier features.
- Rerunning a feature needs the same cleanup as before, for both branches: `spec/<featureId>` and `ralph/<featureId>`.

## Observability (Langfuse)

The worker can export traces to [Langfuse](https://langfuse.com) over OpenTelemetry. Tracing is off unless both keys are set **in the worker's environment before it starts**:

```powershell
$env:Langfuse__PublicKey = "pk-lf-..."
$env:LANGFUSE_SECRET_KEY = "sk-lf-..."                        # environment only, never in config
$env:Langfuse__BaseUrl   = "https://cloud.langfuse.com"       # must match your project's region
dotnet run --project src/Orchestrator.Worker
```

| Setting | Default | Meaning |
|---|---|---|
| `Langfuse:BaseUrl` | `https://cloud.langfuse.com` | Langfuse instance, e.g. `https://us.cloud.langfuse.com` or a self-hosted URL. |
| `Langfuse:PublicKey` | *(empty: tracing off)* | Project public key. |
| `Langfuse:CaptureContent` | `true` | Also send summarizer prompts/responses and the agent's final result text. |
| `Langfuse:Environment` | `development` | Langfuse environment name. |

What you get:

- **One trace per workflow run**, grouped under a session named after the workflow id. Activity spans are tagged with `featureId`, `storyId` and `iteration`.
- **A `claude-code` generation for each agent run**, with the model, tokens, turns, exit code and the cost reported by the CLI.
- **A generation for each failure summary.**
- **Scores:** `verify_passed` on each Verify span, plus `feature_outcome`, `iterations`, `stories_passed_ratio` and `escalation_reason` on the trace.

Telemetry never fails a run. If Langfuse is unreachable, spans are dropped and score errors are only logged.

## Development rules

- Workflow code must be deterministic: no `DateTime.Now`, `Guid.NewGuid`, `Random`, I/O or `Task.Run` in `Orchestrator.Workflows`. All side effects go in activities.
- Activities must be safe to retry. Key them on workflow id + story id + iteration.
- Long activities (`RunAgent`, `Verify`) heartbeat and have explicit timeouts.
- No secrets in the repo. API keys come from environment variables.
- Run `dotnet build` and `dotnet test` before committing.
