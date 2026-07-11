---
version: "0.1.2"
level: copilot
processes:
  design: pair
  implementation: copilot
  testing: copilot
  documentation: copilot
  review: assist
  deployment: none
components:
  src/: copilot
  tests/: copilot
---

This format is based on AI-DECLARATION.md (https://ai-declaration.md), at the involvement
levels defined in v0.1.2. It follows Dalamud's AI usage policy
(https://dalamud.dev/plugin-publishing/ai-policy).

## Notes

**Summary:** AI assistance (Claude Code) is used heavily in this plugin at the **copilot**
level. The AI acts on whole tasks and I prompt it, review its output, and gate every
commit, pull request, and design decision. It is never autonomous: plans require my
approval, commits and PRs require my explicit approval, and the AI asks me for
clarification rather than deciding on its own.

**Who I am and why AI usage is heavy but accountable.** I am a Senior Software Engineer
(frontend / React / TypeScript) with no prior C# experience. I am building this plugin
partly as a deliberate opportunity to **learn C#**. That learning goal is a core, load-
bearing part of how this project is built, and it is exactly what keeps AI-heavy
development honest and safe here:

- **I understand and can explain the code.** Per Dalamud's policy, AI-assisted code is
  held to the same standard as hand-written code, and the author must test, understand,
  and be able to explain it. Because my explicit aim is to learn C#, I read, question, and
  understand every change rather than accepting generated code blindly. Each substantial
  change ends with a plain-language "learning summary" (see the repo's
  `.claude/skills/learning-summary` skill) so I can explain what the code does and why.
- **The code is heavily commented.** Every non-trivial construct is commented in plain
  language — both so I can follow C#/Dalamud idioms as I learn them, and so future
  open-source contributors can understand the codebase. This is a required convention for
  the project, not an afterthought.
- **Dalamud guidelines and user safety are non-negotiable.** The plugin is built to follow
  all Dalamud plugin-development guidelines (https://dalamud.dev/category/plugin-development)
  so that end users are safe: it reads only the local player, hashes the character ContentId
  client-side before anything leaves the game, is fully opt-in and per-category, honors the
  server's remote kill switches, and sends data only over HTTPS to the user's own account.

**Per-process detail.**

- **design: pair** — Architecture and the sync API contract were decided by me (documented
  in the companion `xiv-shinies` repo's plan and design docs); the AI proposes options and
  I approve them. Both parties contribute and I understand the internals.
- **implementation: copilot** — The AI writes most of the C# while I direct, review, and
  approve. I ask it to explain unfamiliar constructs as we go.
- **testing: copilot** — The AI writes the xUnit tests (pure logic); game-touching surfaces
  are verified by me through in-game QA, which the AI cannot do.
- **documentation: copilot** — The AI drafts comments, the README, and other docs; I review
  them for accuracy and clarity.
- **review: assist** — I am the final reviewer of all changes (central to the learning
  goal); the AI assists with review passes but does not have the final say.
- **deployment: copilot** — The AI authored the release automation (the tag-triggered
  GitHub Actions workflow that packages and publishes releases, and the gated release
  process it enforces), with me reviewing and approving. Every release itself is
  human-driven: I approve the changelog and version bump, merge the release PR, and push
  the tag; CI only verifies and publishes what I approved. Nothing releases without my
  explicit action.

This declaration will be kept current as the project evolves.
