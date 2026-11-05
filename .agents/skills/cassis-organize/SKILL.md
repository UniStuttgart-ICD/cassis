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
2. Call `Organize_Canvas` with `apply` false and `proposeGroups` true. Optional: `componentIds` to limit the move set. Use `layoutMode` `reflow` only when the user wants a full island strip.
3. Read **`graph`** first: typed kinds, data vs display edge counts, and **Leiden modules** (SOTA communities on a weighted projection: data edges + weak spatial bridges; display edges down-weighted). Then read `groupCandidates`, `moves`, `wireHides`, `warnings`.
4. Name each module/candidate with a short purpose (one job per block). Prefer `graph.modules` ids over inventing new cuts. Skip tiny clusters and already-grouped ids.
5. Call `Organize_Canvas` again with `apply` true and `namedGroupsJson` as a JSON array of `{ "name", "componentIds", "color?" }`. Apply hides long wires (`GH_ParamWireDisplay.hidden` on destination params). Each named group gets a Consolas Scribble (uppercase) at the top-left and a `GH_Group` that includes the scribble.
6. Call `Capture_Canvas` or `Canvas_Snapshot` to check the result.

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
- Leiden modules are algorithmic — you still supply human/agent names.
- Nested sub-group trees are out of scope. Flat group plus scribble only.
- Pin align uses component pivots (not exact grip pixels).
