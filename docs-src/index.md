# Kylidar Add-in

**Kylidar** is an ArcGIS Pro add-in for finding, downloading, and clipping
[Kentucky From Above](https://kyfromabove.ky.gov/) LiDAR point-cloud tiles to an area of interest,
producing a single merged `.las` file -- without leaving ArcGIS Pro.

It adds a dockable **Kylidar** pane with tools to define an area of interest on the map, set a
buffer and LiDAR phase(s), and run a search-download-clip pipeline backed by a
[STAC API](https://github.com/radiantearth/stac-api-spec) and [PDAL](https://pdal.io/).

## Features

- **Area of interest** tools: draw a point, line, or polygon on the map, or select existing
  features and use their combined geometry.
- **Buffer** a point or line AOI by a distance in feet before clipping (a polygon AOI can be
  clipped as-is, with no buffer).
- **Phase selection**: choose which LiDAR phase(s) to search (COPC-backed phases only -- see
  [Clip Settings & Running](clip-and-run.md#lidar-phases)).
- **Live progress log** in the pane itself as tiles are found, fetched, and merged, with a
  cancel button.
- **Add to LAS dataset**: after a successful clip, fold the output `.las` straight into a new or
  existing `.lasd` and add it to the map.

## Where to start

- [**Installation**](installation.md) -- build and deploy the add-in in ArcGIS Pro
- [**Getting Started**](getting-started.md) -- your first clip, end to end
- [**Area of Interest**](area-of-interest.md) -- every way to define a clip AOI
- [**Clip Settings & Running**](clip-and-run.md) -- buffer, phases, output, and the run/cancel/progress workflow

!!! note "Point clouds only, not raster"
    Kylidar is scoped to LiDAR point-cloud (COPC/LAS) data. For imagery, DEM, or other raster
    products from Kentucky From Above, see the companion
    [kyfromabove-stac-addin](https://ianhorn.github.io/kyfromabove-stac-addin/).
