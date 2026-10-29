---
name: cassis-organize
description: Clean up a Grasshopper canvas with Cassis Organize_Canvas. Use when the user asks to tidy, organize, disentangle, or layout a definition, or to group functional blocks with Scribble titles.
---

# Cassis Organize

Lay out the active Grasshopper document by **computational blocks** (function-like clusters), then align those blocks. Keep existing groups rigid. Name new blocks yourself. Default font for titles is Consolas. Default `layoutMode` is `compact`. Use `reflow` to also strip islands left-to-right.

## When to use

- Messy wires, overlapping components, or unclear flow
- User asks to tidy, organize, or group by function
- After building a definition and before handoff

## Steps

1. Call `Get_AllComponents` and `GetAllConnections` for context. Do not trust connections alone for layout. `Organize_Canvas` walks param Sources itself so panels and sliders count.
2. Call `Organize_Canvas` with `apply` false. Optional: `proposeGroups` true (only larger clusters). Optional: `componentIds` to limit the move set. Use `layoutMode` `reflow` only when the user wants a full island strip.
3. Read `moves`, `wireHides`, `warnings`, and `groupCandidates`. Give each candidate a short purpose name (one job per block). Skip tiny clusters and already-grouped ids.
4. Call `Organize_Canvas` again with `apply` true and `namedGroupsJson` as a JSON array of `{ "name", "componentIds", "color?" }`. Apply hides long wires (`GH_ParamWireDisplay.hidden` on destination params). Each named group gets a Consolas Scribble (uppercase) at the top-left and a `GH_Group` that includes the scribble.
5. Call `Capture_Canvas` or `Canvas_Snapshot` to check the result.

## Rules

- One computational block ≈ one function: series of components that do one job. Organize inside the block first (L→R, align input↔output Y when 1:1).
- Then align blocks as wholes (top-aligned rows by flow).
- Scribbles only for larger clusters (not every 2–3 node chain).
- Do not regroup objects that already belong to a group.
- Prefer dry-run before apply. Prefer `compact` over `reflow`.
- If `undoRecorded` is false, warn that undo may need several Ctrl+Z steps.
- Escape hatch: `GroupExistingComponents` for wrap-only without a new layout pass.

## Gaps

- Does not route wires (only hides long ones).
- Does not run Leiden or Infomap. You name the blocks.
- Nested sub-group trees are out of scope. Flat group plus scribble only.
- Pin align uses component pivots (not exact grip pixels).
