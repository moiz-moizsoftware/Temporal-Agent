# Story {{storyId}}: {{title}}

You are implementing one story of feature `{{featureId}}` in this repository. This is iteration {{iteration}}.

## Acceptance criteria
{{acceptanceCriteria}}

## Tests that must pass
{{testIds}}

The tests above already exist under `specs/{{featureId}}/acceptance/` or the test projects. Make them pass without weakening them. The full test suite must also pass.

## Context
- Feature spec: `specs/{{featureId}}/spec.md`
- Progress notes from earlier iterations: `.ralph/{{featureId}}/progress.md` (may not exist yet)
- If `.ralph/{{featureId}}/last_failure.md` exists, it explains why the previous attempt failed. Read it first.

## Rules
- Do NOT edit anything under `specs/{{featureId}}/` (spec.md, stories.json, acceptance/).
- Do NOT edit `.ralph/{{featureId}}/status.json`, `.ralph/{{featureId}}/last_failure.md` or this file.
- Do NOT commit, branch, or run other git commands. The orchestrator commits when verification passes.
- Run `dotnet build` and `dotnet test` to check your work before finishing.
- Before finishing, append a short note to `.ralph/{{featureId}}/progress.md`: what you changed and what is left.
