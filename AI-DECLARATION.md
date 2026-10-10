---
version: "0.1.2"
level: copilot
processes:
  design: pair
  implementation: copilot
  testing: pair
  documentation: copilot
  review: hint
---

This format is based on [AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2).

## Notes

- Built with Claude (Anthropic), Claude Opus 5.5 in Claude Code, acting on direction
  from the repo owner: Nonunon.
- This started from the original Supermarket Sweep by Rem, which had no AI-written
  code. It has since been reworked to the point where next to nothing of it remains,
  so the levels here describe the plugin as it is now.
- Design was worked out piece by piece in conversation: the owner asked about and
  decided each feature (route planner rules, HQ handling, the route runner flow, the
  gil reserve, trip costs being off by default), taking the AI's suggestions only
  where they were sensible defaults or clearly right, and turning others down
  (auto-pulling prices, ending routes on the home data center), hence `design: pair`.
- Implementation (search, shopping list, route planner, buy assistant, automated
  buying, route runner, UI, config) was carried out by the AI against that direction,
  checking in before anything that acts in the live game, hence
  `implementation: copilot`.
- There is no automated test suite, since the plugin only runs inside the game. The
  AI checked pure logic (search, route planner, buy advisor) in throwaway harnesses,
  which caught real planner bugs, and read the game logs after test runs; the owner
  did all in-game testing, which is the final word, hence `testing: pair`.
- The README, in-plugin text and code comments were written by the AI from the
  owner's prompts, hence `documentation: copilot`.
- Changes come in small, digestible checkpoints, and the owner reviews each one
  (questioning code where it looked off) before it is committed. The AI's part is
  laying out what each checkpoint contains and answering questions about it, hence
  `review: hint`.
- The plugin is not distributed (it is loaded as a dev plugin), so there is no
  deployment to declare.
