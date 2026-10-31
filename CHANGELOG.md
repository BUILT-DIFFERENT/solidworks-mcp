# Changelog

## Unreleased: editing existing drawings (implemented, not yet run against a live SOLIDWORKS)
From a session spent finishing someone's partly done drawing, where nearly every edit went through `vba_run`. All of
it is implemented and compile-checked against the interop, with offline unit tests for the pure logic and live tests
written but not yet run; DESIGN §7.3 lists the status and what is still open.
- New tools (14, 141 in all):
  - `drw_annotation_list`, `drw_dimension_set` (prefix/suffix, text above/below, tolerances incl. fits, precision of
    value and tolerance, parentheses, height, position), `drw_annotation_set` (note text, move, height, angle; tables and
    any annotation), `drw_delete` (annotations, views, detail circles, section lines, view sketch segments; dry run);
  - `drw_view_set` (move by outline centre or corner, scale, configuration, alignment, lock; dry run);
  - `drw_title_block` (fields and their property links; fill properties where the title block links them; re-link or
    rewrite fields), `drw_hatch` (per component: pattern, angle, scale, material hatch), `drw_table_set` (cells, row and
    column order, insert column, BOM column → custom property, widths, row and text height, position);
  - `drw_check` (overlaps, outside the frame, dangling, unattached, duplicate dimensions, missing Ø, view overlaps),
    `drw_locate` (model ↔ sheet coordinates, what is at a sheet point, nearest edges), `drw_dimensions_arrange`
    (stack dimensions on one side), `drw_batch` (several edits as one undo step; failure or dry run undoes them);
  - `doc_standard` (load/save `.sldstd`, named document options, diff of every option against another document);
  - `feature_hole_edit` (change a Hole Wizard hole in place by kind/size/end names; verified or undone).
- Extended tools:
  - `drw_note_add`: `attachTo` (leader notes on a model edge or vertex); the escape `\n` is a line break;
  - `drw_dimension_add`: kind `chamfer`, and `baseline` / `chain` sets with `targets`; the default text spot stays in the
    drawing frame, and a dimension reaching outside it is reported;
  - `drw_view_section`: `throughModelMm` + `orientation`, `excludeComponents` (also in subassemblies), `autoHatch`, `flip`;
    `drw_view_detail`: `centerModelMm`, `radiusModelMm`, `labelAtMm`; both close the view sketch they used to leave open;
  - `drw_annotation_add` balloons: `components` and `layout`; `drw_center_marks`: `at` + `kind` (center marks on chosen
    circles, centerlines on round faces, e.g. a port in a section view);
  - `view_screenshot`: `regionMm` (zoom into a sheet rectangle);
  - `asm_mate_add`: `configurations: all`, `side` (outside/inside, flip chosen to match); `asm_mate_list`: `feature`
    (+`component`): the mates an edit of that feature can break, and each entity's owning feature.
- Fixes:
  - `doc_undo` works in drawings (the always-active sheet sketch no longer blocks it) and after `vba_run` on documents the
    server may edit (the macro is recorded as one undo step and counted as the server's change);
  - system options SOLIDWORKS changes by itself during a call (opening a document recomputes STL deviation, system
    double 2) are put back to the user's value; under a coarse/fine STL preset they are SOLIDWORKS-derived and left alone.
- Not addressed: `computer_open_application` launching a second SOLIDWORKS is the MCP client's computer-use tool, not part
  of this server (this server never launches a second instance, DESIGN §3.3).

## 0.1.0 (unreleased)
- Foundation:
  - STA COM dispatcher with timeouts, BUSY, late completions, early DIALOG_BLOCKING and a cross-process lock;
  - Win32 dialog watchdog with provenance;
  - target-document ownership (owned/adopted/foreign);
  - path policy with backups;
  - per-process preference journal with compare-and-swap restore;
  - verified feature creation with automatic rollback;
  - entity references by id, point, query and name;
  - compact input schemas (the EntityRef fields are explained once, in the server instructions).
- Tools (127):
  - session: `sw_status`, `sw_connect`, `sw_help`, `dialog_list`, `dialog_respond`;
  - documents: `doc_new` (part, assembly, drawing), `doc_open` (native files and STEP/IGES/Parasolid/SAT/STL imports),
    `doc_list`, `doc_activate`, `doc_save`, `doc_close`, `doc_rebuild`, `doc_undo`, `doc_units_set`;
  - inspection: `model_info`, `feature_list`, `face_list`, `edge_list`, `vertex_list`, `body_list`, `model_measure`,
    `model_mass`, `model_check`, `view_screenshot`;
  - sketch: `sketch_start`, `sketch_add`, `sketch_finish`, `sketch_edit`, `sketch_info`, `sketch_dimension_add`,
    `sketch_relation_add`, `sketch_trim`, `sketch_extend`, `sketch_offset`, `sketch_fillet`, `sketch_chamfer`,
    `sketch_pattern`, `sketch_mirror`, `sketch_convert`, `sketch_construction_set`, `sketch_delete`;
  - features: `feature_extrude`, `feature_revolve`, `feature_sweep`, `feature_loft`, `feature_fillet`,
    `feature_chamfer`, `feature_shell`, `feature_hole`, `feature_draft`, `feature_rib`, `feature_helix`,
    `feature_thread_cosmetic`, `feature_pattern_linear`, `feature_pattern_circular`, `feature_mirror`,
    `feature_info`, `feature_delete`, `feature_suppress_set`, `feature_rename`, `feature_rollback`,
    `dimension_list`, `dimension_set`;
  - reference geometry: `ref_plane`, `ref_axis`, `ref_point`, `ref_coordsys`;
  - bodies and surfaces: `body_move`, `body_delete`, `body_combine`, `body_split`, `face_edit` (delete and heal, offset,
    move faces of any body, imported ones too), `surface_extrude`,
    `surface_revolve`, `surface_planar`, `surface_offset`, `surface_knit`, `surface_thicken`;
  - sheet metal: `sheetmetal_base_flange`, `sheetmetal_edge_flange`, `sheetmetal_miter_flange` (around corners and
    closed trays), `sheetmetal_hem`, `sheetmetal_bend` (sketched bend), `sheetmetal_flatten`, `sheetmetal_export_flat`;
  - weldments: `weldment_member` (structural members along 2D or 3D sketch lines);
  - assembly: `asm_insert`, `asm_mate_add` (also limit distance/angle, symmetric, width, hinge, gear and rack-and-pinion
    mates), `asm_mate_list`, `asm_component_list`, `asm_component_set`,
    `asm_component_move`, `asm_interference`, `asm_replace`, `asm_pattern` (linear, circular, mirror, opposite-hand parts),
    `asm_explode` (exploded views);
  - drawing: `drw_sheet_setup`, `drw_views_standard`, `drw_view_projected`, `drw_view_section`, `drw_view_detail`,
    `drw_dimensions_auto`, `drw_dimension_add`, `drw_note_add`, `drw_annotation_add` (datum, surface finish, tolerance frame,
    balloons), `drw_table_add` (hole and revision tables), `drw_bom_add`, `drw_center_marks`, `drw_info`;
  - export: `model_export`;
  - properties: `material_list`, `material_set`, `prop_list`, `prop_set`, `prop_delete`, `equation_list`,
    `equation_set`, `equation_delete`, `config_list`, `config_add`, `config_activate`, `config_delete`,
    `appearance_set`;
  - simulation: `sim_static`, `sim_list`, `sim_delete`;
  - automation: `vba_run` (off by default).
- Tests: unit tests, offline contract tests, live tests per area with exact oracles, and `tools/mcp_smoke.py` with the
  golden scenarios (plate, enclosure, edit, assembly, drawing, sheet metal).
- Install: `tools/install.ps1` publishes into a versioned folder and registers the server with Claude Code; when Smart
  App Control blocks the exe, it registers `dotnet solidworks-mcp.dll` instead.
- Live-tested against SOLIDWORKS 2025 SP1.2: 352 live tests across all areas pass. Fixes found that way include:
  - coupled system options (STL quality with deviation and angle) are saved and restored as one group, also after a
    crash (an STL export had left a user's STL quality at coarse);
  - Hole Wizard holes are placed exactly (a model-space ray pick) and through-all holes work;
  - the standard material databases are found, so materials and simulation work;
  - body_split goes through Intersect; surface_knit can close a surface into a solid; sheet-metal reverse works;
  - drawing views, sections, details, BOMs, model-edge dimensions and sheet scales work (an empty sheet that SOLIDWORKS
    rescales on its first view gets the planned scale back);
  - measurements with arcs report the minimum distance; assembly mates report their real entity kinds.

