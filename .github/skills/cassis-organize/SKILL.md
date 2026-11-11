---
name: cassis-organize
description: Clean up a Grasshopper canvas with Cassis Organize_Canvas. Use when the user asks to tidy, organize, disentangle, or layout a definition, or to group functional blocks with Scribble titles.
---

# Cassis Organize

Layout by Leiden modules (computational blocks), then L→R stages. Existing GH groups stay rigid. You name modules. Default: `compact`, Consolas scribbles. Prefer `detail=summary` (token-cheap).

## Steps

1. **Dry-run only** — `Organize_Canvas` with `apply=false`, `proposeGroups=true`, `detail=summary` (default). Do **not** call `Get_AllComponents` / `GetAllConnections` first; Sources are walked inside the tool.
2. Read `graph.modules` (id, size, componentIds), `moveCount`, `warnings`. Skip `detail=full` unless debugging coordinates.
3. Name each module (one job). Skip tiny / already-grouped ids.
4. **Apply** — `apply=true` + `namedGroupsJson` `[{name, componentIds, color?}]`. Hides long wires; adds Scribble + GH_Group per named block.
5. `Capture_Canvas` once to verify.

## Rules

- One module ≈ one function. Prefer `compact` over `reflow`.
- Do not regroup existing group members.
- If `undoRecorded` is false, warn about multi-step undo.
- Escape: `GroupExistingComponents` for wrap-only.

## Gaps

- No wire routing (hide only). Leiden cuts need your names. No nested subgroup trees. Pin align uses pivots.
