---
name: learning-summary
description: Use after building or changing something substantial in this repo, to explain it to a C#-beginner React dev. The maintainer refines this skill over time.
---

# Learning Summary

## Who this is for

This project keeps a learning-oriented explanation with every substantial change, and the
codebase doubles as C# learning material. The **reader** to write for is a frontend
**React/TypeScript developer learning C#** (the maintainer — see the repo CLAUDE.md).

Whoever is driving Claude Code — the maintainer or an outside contributor — should follow
this skill and pitch summaries at that reader. Note the React/TS comparisons target the
**reader's** background, not the author's: include them even if you (the contributor) don't
come from React. This skill is **living** — the maintainer refines the format, depth, and
examples over time (see the Refinement log at the bottom).

## Session opt-in

Learning summaries are **opt-in per developer, per session** — not forced on contributors.
The first time in a session you're about to write one, ask the developer once whether they
want C#-learning summaries after substantial changes this session, then honor that answer for
the rest of the session (the shared conversation is your memory of it — don't re-ask). Skip
the question when a standing preference is already set: the **maintainer defaults to on**, so
don't ask them — just write the summaries. A new session asks again (that's the "once per
session" behavior). The gate also lives in the repo `CLAUDE.md` so it applies before this
skill is even loaded.

## When to write one

- After scaffolding, adding a feature, introducing a new C#/.NET/Dalamud concept, or any
  change where a React dev would reasonably ask "wait, what is that / why is it done that
  way?"
- **Skip** for trivial edits (typo, comment tweak, version bump, pure rename) — a summary
  there is noise. When unsure, lean toward writing one.
- Place it at the **end of the response**, after the work and its verification.

## Format — two parts, always in this order

### Part a — Plain English (2–3 sentences)

- Assume the reader knows **nothing** about C#, .NET, or Dalamud.
- Say **what** you built and **roughly how**, in everyday language. No jargon, no C# keywords.
- Analogy to everyday things is fine; code terms are not.

### Part b — A bit more technical (with React comparisons)

- Explain the **mechanics**: the C#/Dalamud constructs involved and why they're used here.
- **Lead with a React/TypeScript comparison whenever one genuinely clarifies.** Good ones:
  - C# `interface` ≈ TS `interface` (a contract), but C# enforces it at compile time on classes.
  - `IDisposable` / `Dispose()` ≈ the cleanup function returned from `useEffect`.
  - `[PluginService]` dependency injection ≈ values handed in via props / React context, not imported.
  - `async`/`await` / `Task<T>` ≈ `async`/`await` / `Promise<T>` (very close).
  - A namespace ≈ a module; `using X;` ≈ `import` (but it imports a whole namespace).
  - Properties (`{ get; set; }`) ≈ getters/setters or plain fields with encapsulation.
- When a concept has **no clean React analog** (structs vs classes, value vs reference types,
  nullable reference types, attributes, generics constraints), say so plainly and explain it
  from scratch — don't force a bad analogy.
- Keep it tight: the goal is understanding, not a textbook. A few sentences to a short list.

## Style

- Honest and precise — a wrong analogy is worse than none.
- Reference the actual files/types you touched so the reader can go look.
- It's a teaching moment, not a changelog. Explain the *ideas*, not just *what changed*.

## Refinement log

_(The maintainer notes tweaks to this skill here as we go — depth, what analogies land,
what to drop.)_

- _(none yet)_
