# Starting and running the agent orchestrator

The orchestrator takes a spec that lives in a target repo and drives Claude Code to implement it, story by story. It is a Temporal worker (`src/Orchestrator.Worker`) that runs `FeatureWorkflow`.

## 1. Prerequisites

Install these and make sure they are on `PATH`. The worker checks for `git`, `dotnet` and the Claude CLI at startup and exits if any is missing.

| Tool | Checked with | Notes |
|---|---|---|
| .NET 10 SDK | `dotnet --version` | |
| git | `git --version` | |
| Temporal CLI | `temporal --version` | Provides the local dev server. |
| Claude Code CLI | `claude --version` | Signed in, or `ANTHROPIC_API_KEY` set. Default path is `claude.cmd` on Windows and `claude` elsewhere. |

You also need an Anthropic API key. The failure summarizer reads it from the environment, and it is never stored in config:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

## 2. Prepare the target repo

The agent works in a separate local git repo, not this one. Commit these files at the commit you will start from:

```
specs/<featureId>/
  spec.md          # the human-written spec
  stories.json     # stories, each with at least one testId
  acceptance/      # acceptance tests for the stories, written before the run
```

`stories.json`:

```json
{
  "featureId": "f1",
  "stories": [
    {
      "id": "S1",
      "title": "...",
      "priority": 1,
      "acceptanceCriteria": ["..."],
      "testIds": ["Namespace.Class.Method"]
    }
  ]
}
```

`featureId` must match the workflow input. Story ids must be unique.

Get the base commit SHA:

```powershell
git -C <repoPath> rev-parse HEAD
```

The orchestrator creates a worktree from that SHA on branch `ralph/<featureId>`. It does not change your checkout.

## 3. Configure the worker

Settings are in `src/Orchestrator.Worker/appsettings.json` under `Orchestrator`. You can override any of them with environment variables named `Orchestrator__<Setting>`.

`WorkRoot` is required and starts empty. Set it to a folder outside the target repo:

```powershell
$env:Orchestrator__WorkRoot = "D:\ralph-work"
```

| Setting | Default | Meaning |
|---|---|---|
| `WorkRoot` | *(required)* | Worktrees go in `{WorkRoot}/{workflowId}` and raw logs in `{WorkRoot}/logs/{workflowId}`. |
| `TemporalAddress` | `localhost:7233` | Temporal frontend address. |
| `Namespace` | `default` | Temporal namespace. |
| `ClaudeCliPath` | `claude.cmd` / `claude` | Claude Code executable. |
| `AgentModel` | `sonnet` | Model Claude Code uses. |
| `MaxBudgetUsdPerRun` | `2.0` | Spending cap for one agent run. |
| `MaxTurns` | `50` | Turn cap for one agent run. |
| `AllowedTools` | Read, Edit, Write, Glob, Grep, `dotnet build/test/add package` | Tools the agent may use. |
| `SummarizerModel` | `claude-haiku-5-5` | Model that writes `last_failure.md`. |
| `RawLogTailBytes` | `16384` | Size of the raw-log tail used if summarizing fails. |

## 4. Start Temporal

In its own terminal:

```powershell
temporal server start-dev
```

The server listens on `localhost:7233`. The UI is at http://localhost:8233.

## 5. Build, test and start the worker

From the repo root, in a second terminal with `ANTHROPIC_API_KEY` and `Orchestrator__WorkRoot` set:

```powershell
dotnet build
dotnet test
dotnet run --project src/Orchestrator.Worker
```

The worker polls the `orchestrator` task queue. Leave it running.

## 6. Start a feature run

In a third terminal:

```powershell
temporal workflow start `
  --task-queue orchestrator `
  --type FeatureWorkflow `
  --workflow-id feature-f1 `
  --input '{"repoPath":"D:\\path\\to\\target-repo","specPath":"specs/f1/spec.md","baseCommitSha":"<sha>","featureId":"f1"}'
```

Input fields:

| Field | Required | Default |
|---|---|---|
| `repoPath` | yes | Absolute path to the local target repo. Escape backslashes in JSON. |
| `specPath` | yes | Repo-relative path to the spec. |
| `baseCommitSha` | yes | Commit to branch from. |
| `featureId` | yes | Matches `specs/<featureId>/`. |
| `maxAttemptsPerStory` | no | `3` |
| `maxTotalIterations` | no | `20` |

The workflow id names the worktree folder, so use a new id for each run.

## 7. Follow the run

- **Temporal UI:** http://localhost:8233 shows each activity: `PrepareWorkspace`, `LoadStories`, then `WriteLoopFiles`, `RunAgent`, `Verify` and either `CommitStory` or `SummarizeFailure` for each iteration.
- **Worktree:** `{WorkRoot}/feature-f1/`. Loop state is in `.ralph/f1/`: `PROMPT.md`, `status.json`, `last_failure.md` and the agent's `progress.md`.
- **Raw logs:** `{WorkRoot}/logs/feature-f1/`.
- **Result:**

  ```powershell
  temporal workflow result --workflow-id feature-f1
  ```

  `Completed` means every story passed and was committed on `ralph/<featureId>`. `Escalated` gives the reason (`MaxAttemptsPerStory`, `MaxTotalIterations` or `Stalled`), the story, and the paths to the last summary and raw log.

## 8. Approve or reject new NuGet packages

If an iteration passes but has added or changed NuGet packages, the workflow waits for you before it commits. It waits indefinitely, and the worker log shows a warning.

See what is waiting:

```powershell
temporal workflow query --workflow-id feature-f1 --name PendingPackageApproval
```

Approve:

```powershell
temporal workflow signal --workflow-id feature-f1 --name ApprovePackages --input '{"approved":true}'
```

Reject. The comment is passed to the agent's next attempt:

```powershell
temporal workflow signal --workflow-id feature-f1 --name ApprovePackages --input '{"approved":false,"comment":"Use System.Text.Json instead"}'
```

## 9. Stop and resume

You can stop the worker (Ctrl+C) at any time. When you start it again with `dotnet run --project src/Orchestrator.Worker`, the workflow resumes from the last completed activity. Activities are idempotent, so a retry does not create a duplicate worktree, branch or commit.

To abandon a run:

```powershell
temporal workflow terminate --workflow-id feature-f1 --reason "abandoned"
```

The worktree stays on disk. To remove it, run `git -C <repoPath> worktree remove <WorkRoot>\feature-f1`.

## 10. Observability (Langfuse)

The worker can send traces to [Langfuse](https://langfuse.com) over OpenTelemetry. It is off unless both keys are set:

```powershell
$env:Langfuse__PublicKey = "pk-lf-..."
$env:LANGFUSE_SECRET_KEY = "sk-lf-..."       # environment only, never in config
$env:Langfuse__BaseUrl   = "https://cloud.langfuse.com"   # or https://us.cloud.langfuse.com, or your self-hosted URL
```

| Setting | Default | Meaning |
|---|---|---|
| `Langfuse:BaseUrl` | `https://cloud.langfuse.com` | Langfuse instance. |
| `Langfuse:PublicKey` | *(empty: tracing off)* | Project public key. |
| `Langfuse:CaptureContent` | `true` | Also send the summarizer's prompt and response, and the agent's final result text. Set `false` to send metrics only. |
| `Langfuse:Environment` | `development` | Langfuse environment name (lowercase). |

What you get:

- **One trace per workflow run**, with the workflow id as the Langfuse session. Each activity is a span tagged with `featureId`, `storyId` and `iteration`.
- **A `claude-code` generation per `RunAgent`**: model, input/output tokens, turns, exit code, and the cost reported by the CLI.
- **A generation per failure summary**, from the summarizer's `IChatClient`.
- **Scores:** `verify_passed` on each Verify span, and `feature_outcome`, `iterations`, `stories_passed_ratio` and `escalation_reason` on the trace when the workflow finishes.

Raw logs, `PROMPT.md` and the spec are never sent. With `CaptureContent` on, the summarizer prompt includes the tail of the build/test log, so it can contain code from the target repo.

Useful dashboard widgets: total cost grouped by `featureId` metadata, `verify_passed` average, `iterations` per feature, and a count of `escalation_reason` values.

Telemetry never fails a run: if Langfuse is down, spans are dropped and score errors are only logged.
