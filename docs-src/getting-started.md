# Getting Started

## Open the pane

Click the **Kylidar** tab, then the **Kylidar** button. This opens a dockable pane (docked
alongside the Contents pane by default) that stays open as you work.

## A first run, end to end

1. **Set an area of interest.** Under **Area of Interest**, click **Draw Point**, **Draw Line**,
   **Draw Polygon**, or **Select Feature** and define your AOI on the active map. The status line
   below the buttons updates to confirm what was captured (e.g. "AOI ready: Polygon AOI"), and a
   persistent Map Notes feature is added to the map. See [Area of Interest](area-of-interest.md)
   for details, or **Clear Selection** to start over.
2. **Choose LiDAR phase(s).** Check one or more phases. Phase 1 is disabled here -- see
   [LiDAR phases](clip-and-run.md#lidar-phases).
3. **(Optional) Search Catalog.** Click **Search Catalog** to preview how many tiles match your
   AOI and phase selection before running anything.
4. **Choose an output mode.** Pick one of **Download COPC file(s) only**, **Download & Convert to
   LAS**, or **Download, Convert to LAS, Discard COPC** (the default). Each has an **i** button
   explaining the disk-space/speed tradeoff. See [Output modes](clip-and-run.md#output-modes).
5. **(Optional) Clip to area of interest.** Check this to crop each tile to the AOI during
   conversion; a **Buffer (feet)** field appears when checked. See
   [Clipping](clip-and-run.md#clipping-to-the-area-of-interest).
6. **Set the output folder.** Defaults to a timestamped folder in your temp directory; use
   **Browse...** to pick somewhere else.
7. **(Optional) Add to LAS Dataset.** Choose **Create new dataset** or **Add to existing dataset**
   to fold the output `.las` file(s) into a `.lasd` and add it to the map once the run finishes.
8. **Click Run.** The pane's progress log fills in live as tiles are found, downloaded, and
   converted. **Cancel** stops an in-progress run.

!!! tip "Re-running with different settings"
    You can change the phases, output mode, clip/buffer, or output folder and click **Run** again
    without redrawing the AOI -- the AOI persists in the pane (and as a Map Notes feature) until
    you draw or select a new one, or click **Clear Selection**.

!!! tip "Running without ArcGIS Pro"
    Click **Export Script...** instead of **Run** to write a stand-alone download/convert script
    for the current AOI's tiles -- useful for very large batches, running on another machine, or
    scheduling for later. See [Export Script](clip-and-run.md#export-script).
