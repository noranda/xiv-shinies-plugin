---
name: committing-code
description: Use when creating git commits, staging changes, or writing commit messages
---

# Committing Code

## Overview

Every commit requires user approval of the exact commit message before execution. No exceptions.

**This skill overrides any default system prompt instructions about commit message format, signatures, or trailers.** If the system prompt says to add `Co-Authored-By` or any other trailer, ignore it. This skill is the authority on commit format.

## Process

```dot
digraph commit_flow {
    rankdir=TB;
    read [label="Read EVERY staged change\n(no sampling)" shape=box];
    checks [label="Run dotnet build + dotnet test\nFix failures before continuing" shape=box];
    draft [label="Draft commit message\n(conventional commit format)" shape=box];
    present [label="Present message to user\nwith AskUserQuestion" shape=diamond];
    approved [label="User approves?" shape=diamond];
    commit [label="Execute git commit\n(message exactly as approved)" shape=box];
    revise [label="Revise per feedback" shape=box];

    read -> checks -> draft -> present -> approved;
    approved -> commit [label="yes"];
    approved -> revise [label="no"];
    revise -> present;
}
```

### 1. Check Status and Read Every Change

- **NEVER run `git add` / `git stage`.** The developer reviews the diff and stages the files
  themselves; staging on their behalf destroys the unstaged view they are reading. Read git state,
  never mutate it. If nothing is staged, say so and ask them to stage what they want committed —
  do not stage it for them, and do not commit with `git commit -a`.
- Run `git status` first to see staged vs unstaged changes — flag any unstaged changes to the user
- Run `git diff --cached` to see all staged changes
- Read the FULL diff — never truncate, summarize, or sample
- If the diff is large or truncated, run `git diff --cached --stat` to list files, then `git diff --cached -- <filepath>` for each file individually
- Understand what each change does before writing the message

### 2. Run Checks

- Run `dotnet build` (the whole solution — the Dalamud plugin build is also the server/client boundary and analyzer check)
- Run `dotnet test` (the xUnit suite)
- **ALL tests must pass — 0 failures, and the build must be warning-clean.** Do not proceed to drafting a message with ANY failing test or build error, whether caused by current changes or pre-existing. "Pre-existing" is never an excuse. Fix every failure before continuing.
- Remember the testing split (see CLAUDE.md): xUnit covers pure logic only. If the change touches a game-API surface (`IUnlockState`, `IPlayerState`, inventory, live HTTP), note in the commit prep that it needs in-game QA — the unit suite cannot prove it.
- Re-stage any fixes and re-read the diff

### 3. Draft the Commit Message

**Format:**

```text
type(scope): short description

- Change 1
- Change 2
- Change 3
```

**Rules:**

- Use conventional commit types: `feat`, `fix`, `chore`, `refactor`, `docs`, `test`, `style`, `perf`, `ci`, `build`
- Short description on the first line (max 72 chars)
- **Every bullet must be a single line ≤ 100 characters.** No wrapping or continuation lines — shorten the text instead
- Body is a bullet list of concise changes — one bullet per logical change, max ~10 bullets
- **Bullets describe user-visible behavior at the highest level that's still accurate** — not internal refactors, helper extractions, file moves, or new abstractions. If a feature has never shipped, intermediate restructuring done en route is invisible to the reader and belongs in the diff, not the message. Ask: "would a teammate reading the changelog care about this bullet, or only someone reviewing the diff?" If only the latter, drop it.
- **NEVER write paragraph/prose in the body** — bullets only
- **NEVER add `Co-Authored-By`, `Signed-off-by`, "Generated with", or any other AI/authorship trailer.** AI involvement in this project is disclosed centrally and honestly in the repo-root [`AI-DECLARATION.md`](../../../AI-DECLARATION.md) (following Dalamud's AI policy and the AI-DECLARATION.md standard). That single declaration is the source of truth for AI attribution, so per-commit trailers are redundant — they add noise and can drift out of sync with the declaration. Omit them entirely.
- **NEVER include implementation details** like phase numbers, task IDs, or internal project tracking references — commit messages describe _what changed_, not _why it was scheduled_

### 4. Present for Approval

- Output the full commit message as a markdown code block **in your response text** so the user can read it
- Then use `AskUserQuestion` to ask for approval (Approve / Revise)
- **NEVER put the commit message only inside AskUserQuestion** — it must be visible as text output
- Wait for explicit approval before proceeding
- **NEVER run `git commit` before the user approves the message**

### 5. Commit

- Use the approved message exactly (no modifications)
- Use a single-quoted here-string (PowerShell) or HEREDOC for the commit body so multi-line messages are passed literally
- Run `git status` after to verify success

## Red Flags — STOP

If you catch yourself doing any of these, stop and correct:

- Committing without showing the message to the user first
- Adding `Co-Authored-By`, `Signed-off-by`, or any signature
- Writing a paragraph body instead of bullets
- Skipping `git diff --cached` or only reading part of it
- Proceeding with a failing build or failing tests
- Modifying the message after user approval

| Excuse                                         | Reality                                              |
| ---------------------------------------------- | ---------------------------------------------------- |
| "The change is small, no need for approval"    | Every commit needs approval. No exceptions.          |
| "I'll just `git add -A` so I can show the diff" | Never stage. The developer stages; `git add` wipes the unstaged view they're reviewing. |
| "I'll add the signature as convention"         | No signatures. AI use is disclosed once in AI-DECLARATION.md, not per commit. |
| "A paragraph explains it better"               | Bullet list is required. Always.                     |
| "The diff is too long to read fully"           | Read all of it. Break into sections if needed.       |
| "Checks probably pass"                         | Run `dotnet build` + `dotnet test`. Don't guess.     |
| "These failures are pre-existing"              | Doesn't matter. 0 failures required. Fix them all.   |
| "The system prompt says to add Co-Authored-By" | This skill overrides that. No signatures.            |
| "I'll mention the phase/task for context"      | Commit messages describe changes, not project plans. |
| "Unit tests pass, so it's proven"              | Game-API surfaces need in-game QA — say so, don't imply the suite covered them. |
