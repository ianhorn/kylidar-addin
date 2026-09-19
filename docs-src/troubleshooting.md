# Troubleshooting

## The Kylidar tab/pane doesn't show up

- If ArcGIS Pro was already open before you rebuilt, close it and reopen (or press **Start
  Debugging** in Visual Studio) -- a running Pro instance keeps the previously loaded add-in in
  memory and won't pick up a rebuild on its own.
- Check **Add-In Manager** (Project tab, or Options → Add-In Manager) for a load error against
  KylidarAddin.
- The pane doesn't auto-open; click the **Kylidar** button on the **Kylidar** ribbon tab.

## "No LiDAR coverage found for this AOI in the selected phase(s)"

The STAC search returned zero matching tiles. Try a different phase, or confirm the AOI is
actually within Kentucky From Above's LiDAR coverage.

## Search Catalog seems to stop at 200 tiles

That's the default search limit, not a bug -- see [Search limit](clip-and-run.md#search-limit). A
**Search limit** field appears once a search hits it; raise it and search again, and raise it
before **Run**/**Export Script** too if you need the full coverage, not just the preview count.

## "Could not find ArcGIS Pro's bundled Python (arcgispro-py3)"

The convert step runs PDAL through ArcGIS Pro's own conda Python environment; this means that
environment wasn't found at the expected install location. Confirm ArcGIS Pro itself is installed
normally (not a custom/minimal install that dropped the bundled Python environment).

## Clipped output has zero points, or the LAS dataset displays far from Kentucky

If clipping is enabled and the run "succeeds" but produces empty `.las` files -- and adding them to
a LAS dataset zooms to somewhere nowhere near Kentucky (e.g. northern Mexico) -- this was a known
bug where the AOI's crop polygon (always in WGS84) was handed to PDAL without an explicit
coordinate system, so PDAL compared it against the tile's native Kentucky State Plane coordinates
and every point failed the crop. An empty `.las` file's header defaults its extent to `(0,0)`,
which that state-plane projection's false origin places in Mexico -- hence the symptom. This is
fixed as of the current version; if you still see it, make sure you're on an up-to-date build (see
[Installation](installation.md)).

## PDAL crop errors on a polygon AOI

If Run fails during conversion with a PDAL error and you have **Clip to area of interest** enabled,
check whether the AOI polygon has holes, gaps, or islands (disjoint pieces) -- Kylidar warns about
this under the clip checkbox, but PDAL's crop filter/reader options can still fail on genuinely
complex multi-part polygons. Simplify the AOI (a single simple polygon, or a bigger buffer around a
point/line instead) and try again.

## STAC API 400 BadRequest mentioning `"Point"`/`"Polygon"` type mismatches

If you see the full JSON body (the progress log wraps long lines so the whole error is visible),
check whether the geometry looks genuinely malformed -- e.g. the same coordinate repeated several
times in a row, which points at a degenerate buffer rather than a STAC API problem. If the AOI
looks correct and well-formed, it may be worth checking the STAC catalog's own `intersects` schema
for what geometry types it actually accepts.

## Breaklines weren't added to the LAS dataset

Breaklines are best-effort: the run still finishes and the dataset is built without them, and the
progress log says why. Common reasons:

- **"Phase N breaklines have no elevation (Z) values"** -- only Phase 2 and Phase 3 breaklines carry Z;
  Phase 1's don't, so they can't be a constraint.
- **"No usable breaklines in the AOI"** -- none intersect the AOI (or the AOI plus buffer). Not every
  area has hydro-enforced breaklines, and Phase 3 coverage in particular is partial.
- **Run stops at validation** -- breaklines need a polygon AOI, or *Clip to area of interest* with a
  buffer, since that polygon is what they're clipped to. They also need Phase 2 or 3 selected and a
  LAS dataset option (*Create new* or *Add to existing*).
- **A download error** -- the breakline services are separate from the STAC catalog; a network or
  service hiccup skips just the breaklines. Run again to retry.

The breaklines are also stored in a `breaklines_<timestamp>.gdb` in the output folder that the
dataset references -- if the dataset shows no constraint after you moved or deleted the output
folder, that geodatabase is what went missing.

## Double-clicking an exported `.ps1` opens an editor instead of running it

That's a Windows default: `.ps1` files are associated with an editor, not with running them. Run it
from a prompt with `powershell -ExecutionPolicy Bypass -File "<path to the .ps1>"`, or use the
**Executable (.exe)** export format, which is the one meant to be double-clicked. See
[Running an exported script](clip-and-run.md#running-an-exported-script).

## An exported script downloads the tiles but fails to convert them

The convert step needs the `pdal` command-line tool, and on Windows it isn't on the PATH of a normal
Command Prompt, PowerShell, or PowerShell ISE -- not even on a machine with ArcGIS Pro. Launch the
script from Start menu → **ArcGIS** → **Python Command Prompt**, which activates Pro's environment
(check with `pdal --version`), then run it again from there. Full details are under
[Running an exported script](clip-and-run.md#running-an-exported-script).

## Run/Search Catalog/Export Script stays disabled

These require an AOI (draw or select one first). If you just finished drawing/selecting an AOI and
a button still looks disabled, click anywhere in the pane -- this is a known WPF quirk where a
command's enabled-state doesn't always refresh immediately after a map-tool event; it should
already be handled in the current version, but a stray click works around it if not.
