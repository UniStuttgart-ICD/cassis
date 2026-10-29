# Cassis

MCP bridge that lets agents inspect and edit Grasshopper documents on the canvas.

## Language

**Component**:
A Grasshopper document object with an InstanceGuid and canvas pivot. Includes scripts, params, panels, and sliders when listed broadly.
_Avoid_: Node (ambiguous with graph layout units), Widget

**Connection**:
A directed data link from a source parameter to a target parameter. Layout maps each end to its owner object.
_Avoid_: Wire (prompt slang only), Edge (internal layout term)

**Group**:
A `GH_Group` membership list with optional NickName and colour. Does not move its members by itself.
_Avoid_: Cluster (layout candidate before naming)

**Supernode**:
A layout unit for an outermost group. Members keep relative offsets when the supernode moves.
_Avoid_: Nested group (child membership inside a supernode)

**Layer**:
A Sugiyama column index. Data flows left to right as layer index rises.
_Avoid_: Rank (synonym used only inside the solver)

**Functional block**:
A set of ungrouped objects that share one purpose. Named by the agent, not invented by the solver.
_Avoid_: Module, Island (WCC before split)

**Scribble**:
A canvas title note (`GH_Scribble`) for a functional block. Not a Panel and not a Group NickName alone.
_Avoid_: Label, Annotation

**Layout plan**:
The dry-run result of Organize_Canvas: proposed pivots, optional group candidates, and scribble positions.

**Layout mode**:
`compact` (default) keeps component places and pushes apart X-overlapping stacks. `reflow` runs Sugiyama per island, wraps tall ranks, and places islands on an L-to-R strip.
_Avoid_: Snapshot (canvas inventory / image tools)

**Canvas**:
The Grasshopper document coordinate plane where pivots live.
_Avoid_: Viewport (Rhino 3D view), Snapshot (logical map or PNG)
