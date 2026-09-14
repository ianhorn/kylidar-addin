# Clip Settings & Running

## Buffer

Enter a distance in **feet**. For a point or line AOI this is required -- **Run** stays disabled
until it's set above zero, since PDAL needs an actual area (not a bare point or line) to crop to.
A polygon AOI can be run with a buffer of `0`; it already has an area of its own.

The buffer only affects the *output crop* -- see
[What actually gets searched vs. clipped](area-of-interest.md#what-actually-gets-searched-vs-clipped).

## LiDAR phases

Check one or more phases to search. **Phase 1 is disabled** -- hover over it to see why: Phase 1
tiles are in regular LAZ format (not COPC), and should be downloaded using the
[kyfromabove-stac-addin](https://ianhorn.github.io/kyfromabove-stac-addin/) tool instead. Phases 2
and 3 are COPC-backed and supported here.

## Output .las file

Defaults to a timestamped file under your temp folder
(`kylidar_clip_<yyyyMMdd_HHmmss>.las`). Use **Browse...** to choose a different location before
running.

## Run / Cancel

**Run** is disabled while:

- no AOI has been drawn/selected yet and you haven't entered a valid buffer for a point/line AOI, or
- a run is already in progress.

Click **Run** to start the search-download-clip pipeline; click **Cancel** to stop an
in-progress run (cancellation is checked between steps, so an in-flight download or the PDAL
step itself finishes before it takes effect).

## Progress log

The log below Run/Cancel fills in live, e.g.:

```
Searching STAC catalog for LiDAR coverage...
Found 3 LiDAR tile(s) intersecting the AOI.
Partial fetch: 35 range request(s), 16.3 MB of 102.0 MB tile.
Clipping and merging point cloud tiles...
Done: C:\Users\you\AppData\Local\Temp\kylidar_clip_20260101_120000.las
```

COPC tiles are fetched with true partial (HTTP range-request) reads where possible, pulling only
the points that intersect your AOI instead of the whole tile -- the "Partial fetch" lines report
how much of the tile that actually was. If a tile's partial fetch isn't possible for some reason,
the log says so and falls back to a full download for that tile.

## Add to LAS dataset

On a successful run, the pane shows **Create New...** and **Add To Existing...** buttons:

- **Create New...** prompts for a `.lasd` path, creates it containing the clipped `.las`, and
  adds it to the active map.
- **Add To Existing...** prompts you to pick an existing `.lasd`, appends the clipped `.las` to
  it, and adds it to the active map.

Either button hides the prompt once clicked, whether it succeeds or not, and a failure is reported
both in the progress log and a message box -- your clipped `.las` file is untouched either way, so
if the LAS dataset step fails you can still add it manually (e.g. via the Create LAS Dataset /
Add Files To LAS Dataset geoprocessing tools) without re-running the clip.
