# Draft the stories for feature {{featureId}}

You are splitting a feature spec into small, independently testable stories. This is draft {{draft}}.

## Input
- Feature spec: `specs/{{featureId}}/spec.md`. Read it fully, and read the existing code so the stories fit it.
- If `specs/{{featureId}}/stories.json` already exists, it is your previous draft. Revise it to address the feedback below rather than starting over.

## Feedback on the previous draft
{{feedback}}

## Output
Write exactly one file: `specs/{{featureId}}/stories.json`, in this shape:

```json
{
  "featureId": "{{featureId}}",
  "stories": [
    {
      "id": "S1",
      "title": "Short imperative title",
      "priority": 1,
      "acceptanceCriteria": ["Observable behaviour a test can check"],
      "testIds": ["<Namespace>.<Class>.<Method>"]
    }
  ]
}
```

## Rules for good stories
- Each story is a thin, vertical slice that can be built and verified on its own, in priority order (1 = first).
  A later story may build on earlier ones, never the other way round.
- Every acceptance criterion is observable behaviour, specific enough to write an automated test for.
- Every story has at least one test id. Test ids are fully qualified `Namespace.Class.Method` names of xUnit tests
  that do not exist yet; a later step writes them. Use one acceptance test namespace for the feature, such as
  `<Product>.Acceptance.Tests`, with one class per story or area.
- Cover everything the spec requires, and nothing it does not. Do not invent requirements.
- Story ids are unique: S1, S2, S3, ...

## Restrictions
- Do NOT edit `specs/{{featureId}}/spec.md` or any other file. Only write `specs/{{featureId}}/stories.json`.
- Do NOT write code or tests.
- Do NOT run git commands.
