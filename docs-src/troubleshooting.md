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

## "Could not find ArcGIS Pro's bundled Python (arcgispro-py3)"

The clip step runs PDAL through ArcGIS Pro's own conda Python environment; this means that
environment wasn't found at the expected install location. Confirm ArcGIS Pro itself is installed
normally (not a custom/minimal install that dropped the bundled Python environment).

## PDAL: "Geometrically invalid polygon in option 'polygon'"

This means the crop geometry sent to PDAL wasn't a valid simple polygon -- most likely because a
**Select Feature** AOI came back multi-part (e.g. two disjoint selected features, or a feature
that's itself multi-part). This is handled automatically as of the current version, which splits
multi-part AOIs into a proper `MULTIPOLYGON`/`MULTILINESTRING` instead of merging every ring into
one; if you still hit this, it's worth filing as a bug with the AOI's source (drawn vs. selected)
and roughly how many parts it likely has.

## STAC API 400 BadRequest mentioning `"Point"`/`"Polygon"` type mismatches

If you see the full JSON body (the progress log wraps long lines so the whole error is visible),
check whether the geometry looks genuinely malformed -- e.g. the same coordinate repeated several
times in a row, which points at a degenerate buffer rather than a STAC API problem. If the AOI
looks correct and well-formed, it may be worth checking the STAC catalog's own `intersects` schema
for what geometry types it actually accepts.

## Run stays disabled

- For a point or line AOI, enter a buffer distance greater than `0`.
- If you just finished drawing/selecting an AOI and Run still looks disabled, click anywhere in
  the pane (e.g. the buffer field) -- this is a known WPF quirk where a command's enabled-state
  doesn't always refresh immediately after a map-tool event; it should already be fixed in the
  current version, but a stray click works around it if not.
