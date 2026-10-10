# Supermarket Sweep

[![AI-DECLARATION: copilot](https://img.shields.io/badge/%E4%B7%BC%20AI--DECLARATION-copilot-fee2e2?labelColor=fee2e2)](./AI-DECLARATION.md)

A Final Fantasy XIV plugin (Dalamud) that turns the marketboard into a shopping list. Add what you need, pull prices from [Universalis](https://universalis.app), and let it plan which worlds to visit to buy everything cheaply, then travel you there.

Open it with `/shop`.

## Features

- **Fuzzy item search:** words in any order and partial words ("courtly fending" finds "Courtly Lover's Gauntlets of Fending"), with a little typo tolerance. Click results to add them; the search stays put so you can add several in a row.
- **Shopping list with progress:** every row shows `[owned/needed]` and turns blue once you have enough. Owned counts include your retainers, and optionally your alts.
- **Prices per world:** every listing across your region or data center, sortable, with tax included. Click a world to travel there.
- **Route planner:** works out which worlds to visit and what to buy at each.
  - Pay up to a set percentage more to visit fewer worlds.
  - Optionally allow buying bigger stacks than you need when that's cheaper.
  - Per-item HQ rules: any quality, prefer HQ, or HQ only.
  - Starts at your current world, then the rest of your data center, then other data centers.
  - Optionally includes Oceania when shopping North America.
- **Travel and marketboard:** world travel through Lifestream, walking to the marketboard through vnavmesh, and one-click marketboard searches for an item.
- **Imports:** MakePlace lists and clipboard text like `10x Ipe Log`.

## Requirements

These plugins are used over IPC. Supermarket Sweep loads without them, but the related features won't work:

- **Allagan Tools:** owned item counts.
- **Lifestream:** world travel.
- **vnavmesh:** walking to the marketboard.
