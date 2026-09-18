# Kylidar Add-in

**Kylidar** is an ArcGIS Pro add-in for finding, downloading, and converting
[Kentucky From Above](https://kyfromabove.ky.gov/) LiDAR point-cloud tiles to `.las`, optionally
folding the result straight into a LAS dataset -- without leaving ArcGIS Pro.

It adds a dockable **Kylidar** pane with tools to define an area of interest on the map, preview
how many tiles match it, choose an output mode, and run a search-download-convert pipeline backed
by a [STAC API](https://github.com/radiantearth/stac-api-spec) and [PDAL](https://pdal.io/).

## Features

- **Area of interest** tools: draw a point, line, or polygon on the map, or select existing
  features and use their combined geometry. Every draw/select action also saves a persistent,
  editable [Map Notes](area-of-interest.md#map-notes) feature, so you have a record of what you
  drew and can reuse or edit it later.
- **Search Catalog** preview: see how many tiles match your AOI and phase selection before running
  anything.
- **Three output modes**: download the raw COPC/LAZ tiles as-is, convert to `.las` while keeping
  the raw tiles, or convert and discard the raw tiles -- each tile becomes its own `.las` file
  (there's no merge-into-one-file step).
- **Optional clip to area of interest**: crop each tile to the AOI (plus an optional buffer) during
  conversion, instead of always keeping every tile in full.
- **Concurrent, adaptive processing**: tiles download and convert in parallel, throttled by CPU
  count and live memory pressure so a large run doesn't starve the rest of the system.
- **Export Script**: write a stand-alone Python, Jupyter notebook, PowerShell, or shell
  download/convert kit for the current AOI's tiles, to run later or on another machine without
  ArcGIS Pro.
- **Live progress log** in the pane itself as tiles are found, fetched, and converted, with a
  cancel button and per-step timing.
- **Add to LAS dataset**: fold the output `.las` file(s) straight into a new or existing `.lasd`,
  optionally build pyramids, and add it to the map.
- **Hydro-enforced breaklines**: optionally download the Phase 2/3 breaklines inside the AOI, clip
  and reproject them to EPSG:3089, and add them to the LAS dataset as a surface constraint.

## Where to start

- [**Installation**](installation.md) -- build and deploy the add-in in ArcGIS Pro, or grab a
  pre-built download
- [**Getting Started**](getting-started.md) -- your first run, end to end
- [**Area of Interest**](area-of-interest.md) -- every way to define an AOI
- [**Output & Running**](clip-and-run.md) -- phases, output modes, clipping, LAS dataset options,
  Export Script, and the run/cancel/progress workflow

!!! note "Point clouds only, not raster"
    Kylidar is scoped to LiDAR point-cloud (COPC/LAS) data. For imagery, DEM, or other raster
    products from Kentucky From Above, see the companion
    [kyfromabove-stac-addin](https://ianhorn.github.io/kyfromabove-stac-addin/).
