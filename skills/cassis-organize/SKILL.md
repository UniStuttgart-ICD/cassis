---
name: cassis-organize
description: Clean up a Grasshopper canvas with Cassis Organize_Canvas. Use when the user asks to tidy, organize, disentangle, or layout a definition, or to group functional blocks with Scribble titles.
---

# Cassis Organize

Lay out the active Grasshopper document left to right. Keep existing groups rigid. Name new functional blocks yourself. Default font for titles is Consolas. Default `layoutMode` is `compact` (keep places; push apart X-overlapping stacks). Use `reflow` for a full L-to-R Sugiyama strip with rank wrap.

## When to use

- Messy wires, overlapping components, or unclear flow
- User asks to tidy, organize, or group by function
- After building a definition and before handoff

## Steps

1. Call `Get_AllComponents` and `GetAllConnections` for context. Do not trust connections alone for layout. `Organize_Canvas` walks param Sources itself so panels and sliders count.
2. Call `Organize_Canvas` with `apply` false. Optional: `proposeGroups` true. Optional: `componentIds` to limit the move set. Objects outside that set stay fixed. Use `layoutMode` `reflow` only when the user wants a full L-to-R island strip.
3. Read `moves`, `warnings`, and `groupCandidates`. Give each candidate a short purpose name (one job per block). Skip ids that are already grouped. If width/height growth warnings fire, prefer a smaller `componentIds` set or stay on `compact`.
4. Call `Organize_Canvas` again with `apply` true and `namedGroupsJson` as a JSON array of `{ "name", "componentIds", "color?" }`. Each named group gets a Consolas Scribble (uppercase text) at the top-left and a `GH_Group` that includes the scribble.
5. Call `Capture_Canvas` or `Canvas_Snapshot` to check the result.

## Rules

- One functional block has one purpose and one Scribble.
- Do not regroup objects that already belong to a group.
- Prefer dry-run before apply. Prefer `compact` over `reflow`.
- If `undoRecorded` is false, warn that undo may need several Ctrl+Z steps.
- Escape hatch: `GroupExistingComponents` for wrap-only without a new layout pass.

## Gaps

- Does not route wires or set faint wire display.
- Does not run Leiden or Infomap. You name the blocks.
- Nested sub-group trees are out of scope. Flat group plus scribble only.
