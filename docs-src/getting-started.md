# Getting Started

## Open the pane

Click the **Kylidar** tab, then the **Kylidar** button. This opens a dockable pane (docked
alongside the Contents pane by default) that stays open as you work.

## A first clip, end to end

1. **Set an area of interest.** Under **Area of Interest**, click **Draw Point**, **Draw Line**,
   **Draw Polygon**, or **Select Feature** and define your AOI on the active map. The status line
   below the buttons updates to confirm what was captured (e.g. "AOI ready: Polygon AOI"). See
   [Area of Interest](area-of-interest.md) for details on each option.
2. **Set a buffer** *(required for a point or line AOI; optional for a polygon)*. Enter a distance
   in feet under **Clip Settings**. See
   [Clip Settings & Running](clip-and-run.md#buffer) for why a polygon can skip this.
3. **Choose LiDAR phase(s).** Check one or more phases. Phase 1 is disabled here -- see
   [LiDAR phases](clip-and-run.md#lidar-phases).
4. **Set the output path.** The output `.las` file defaults to a timestamped file in your temp
   folder; use **Browse...** to pick somewhere else.
5. **Click Run.** The pane's progress log fills in live as tiles are found, downloaded, and
   merged. **Run** is disabled until the AOI/buffer requirements above are satisfied, and again
   while a run is in progress; **Cancel** stops an in-progress run.
6. **Add to a LAS dataset** *(optional)*. On success, the pane offers **Create New...** or
   **Add To Existing...** to fold the output `.las` into a `.lasd` and add it to the map.

!!! tip "Re-running with different settings"
    You can change the buffer, phases, or output path and click **Run** again without redrawing
    the AOI -- the AOI persists in the pane until you draw or select a new one.
