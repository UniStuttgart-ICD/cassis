# Graph display: why wires still look messy

Context: LCRL organize preview (~1100 nodes). Node packing is now rank-column; turning **wires** on still reads as a bird’s nest.

## Diagnosis

Straight segment from source pivot → sink pivot ignores:

1. **Crossing minimization** — nodes in a layer are not ordered to cut crossings (Sugiyama phase 2).
2. **Routing** — no orthogonal / bundled paths; every hop is a chord across the canvas.
3. **Clutter control** — preview draws *all* edges; GH apply already has `WireHide` for long hops (`DefaultWireHideLength = 720`).

Node placement alone cannot fix wire mess. Display + sequencing matter as much as packing.

## Established techniques

| Technique | What it does | Fit for Cassis / GH |
|-----------|--------------|---------------------|
| **Sugiyama layered drawing** | Layer → sequence (min crossings) → coordinates | Core for dataflow; we do layer+pack, weak on sequence | [yFiles hierarchical](https://docs.yworks.com/yfiles-html/dguide/hierarchical_layout-edge_grouping), [yWorks layered](https://www.yworks.com/pages/layered-graph-layout) |
| **Barycentric / median ordering** | Reorder nodes in each rank to cut crossings | Cheap next solver step | Graphviz [dot](https://graphviz.org/docs/layouts/dot/), sun-hierarchy |
| **Orthogonal / port routing** | H/V segments between layers; respect input/output sides | Matches GH wire aesthetic | [ELK vertical segment routing](https://rtsys.informatik.uni-kiel.de/~biblio/downloads/theses/thw-bt.pdf), [DFD port constraints](https://rtsys.informatik.uni-kiel.de/~biblio/downloads/papers/diagrams12b.pdf) |
| **Edge bundling / edge grouping** | Merge parallel / similar routes | Preview clutter; optional | [yFiles edge bundling](https://docs.yfiles.com/yfiles-html/dguide/layout-edge_bundling/) |
| **Hide / fade long edges** | Drop or dim long links; keep local structure | Already in plan as `WireHideHint`; preview doesn’t use it | [grapher link distance](https://grapher.network/docs/link_distance); GH `GH_ParamWireDisplay.hidden` |
| **De-emphasise / aggregate / semantic zoom** | Dim non-focus, collapse groups | Interactive explore later | [TVCG 2024 group transforms](https://doi.org/10.1109/tvcg.2024.3397785) |

Grasshopper-adjacent: [SmartHopper `DependencyGraphUtils`](https://github.com/architects-toolkit/SmartHopper/blob/3f757d4b/src/SmartHopper.Core.Grasshopper/Graph/DependencyGraphUtils.cs) runs a fuller Sugiyama pipeline (layers → edge concentration → rows → crossing min → multi-layer sweep).

## Recommended order for Cassis

1. **Preview: hide or fade long wires** (reuse `WireHide` length; default wires off or “local only”). ✅ scrubber: local wires + optional long + elbow paths
2. **Solver: barycentric reorder within ranks** before packing Y — cuts crossings without new routing. ✅ `MedianSweep(6)` + `BarycentricNudgeY`
3. **Preview: orthogonal or simple bezier** between ranks (display-only; GH still owns real wires). ✅ soft cubic elbows
4. **Optional: edge bundling** for fan-ins/fan-outs in the scrubber.
5. **Later: focus mode** — show wires for selected component / group only.

## Non-goals (for now)

- Full ELK / yFiles dependency inside the Rhino plugin.
- Perfect zero-crossing layout on 1k+ node WCCs (NP-hard; heuristics only).
