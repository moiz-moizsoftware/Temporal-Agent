# Write the acceptance tests for feature {{featureId}}

You are writing the acceptance tests for every story of feature `{{featureId}}`, before any of them is implemented.
This is draft {{draft}}.

## Input
- Feature spec: `specs/{{featureId}}/spec.md`
- Approved stories: `specs/{{featureId}}/stories.json`. Each story lists its acceptance criteria and the exact test ids to write.
- If `.ralph/{{featureId}}/last_failure.md` exists, it explains why the previous draft was rejected. Read it first and fix those problems.

## Output
- An xUnit test project under `specs/{{featureId}}/acceptance/` (create it with `dotnet new xunit` if it does not exist),
  referencing the production projects it needs, and added to the repository's solution file with `dotnet sln add`.
- One test method for every test id in stories.json, with exactly that `Namespace.Class.Method` name. Each test must
  check its story's acceptance criteria through the production code's public surface.
- Do not write any test that is not listed in stories.json.

## Required result
Running `dotnet build` and `dotnet test` from the repository root must show that:
- the solution builds, so tests may only use types and members that already exist, or that you add in the test project itself;
- every listed test id is found and **fails**, because nothing is implemented yet. A test that already passes proves nothing;
- every test that existed before still passes.

Test from the outside in, through an entry point that already exists, so the tests compile now and pass unchanged
once the feature is built. For a web API, call it over HTTP with `WebApplicationFactory<Program>`. Look at earlier
features under `specs/*/acceptance/` and follow their style. The tests are locked after this step: the implementation
cannot change them, so never make a test depend on a type or member that does not exist yet.

## Rules
- Only create or change files under `specs/{{featureId}}/acceptance/`, plus the solution file (`*.sln` or `*.slnx`).
- Do NOT edit `spec.md`, `stories.json`, production code, or any file under `.ralph/`.
- Only add test packages (for example `Microsoft.AspNetCore.Mvc.Testing`), and only to the acceptance test project.
- Do NOT commit, branch, or run other git commands. The orchestrator commits when verification passes.
