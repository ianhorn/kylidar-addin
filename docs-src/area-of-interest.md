# Area of Interest

All AOI tools set the same AOI, so only the last one you use takes effect. The current AOI is
summarized as text below the AOI buttons (e.g. "AOI ready: Polygon AOI" or "AOI ready: AOI from 3
selected feature(s)").

## Draw Point / Draw Line / Draw Polygon

Click one of the three **Draw** buttons to activate an ArcGIS Pro sketch tool on the active map --
draw your shape and it becomes the AOI. Requires an open map view.

A point or line AOI has no area on its own. This is fine for searching (the STAC search just needs
an intersecting geometry), but if you also turn on
[Clip to area of interest](clip-and-run.md#clipping-to-the-area-of-interest), it needs a
[buffer](clip-and-run.md#buffer) to have any actual area to crop tiles down to -- without one, the
crop removes every point.

## Select Feature

Click **Select Feature**, then click (or drag a box over) existing features on the active map.
Their combined geometry (unioned if more than one, and split back out into separate pieces if the
result is multi-part -- e.g. two disjoint features) becomes the AOI, and the picked features get
the normal Pro selection highlight.

If nothing is found where you clicked/dragged, a message box says so and the AOI is left
unchanged -- click directly on a feature, or drag a box that overlaps one.

A selected polygon with holes, gaps, or islands can make PDAL's crop step error if clipping is
enabled -- see the warning that appears under **Clip to area of interest** in that case.

## Clear Selection

Clears the current AOI and the active map's feature selection. **Search Catalog**, **Run**, and
**Export Script** all require an AOI, so this is mainly useful before drawing/selecting a fresh
one.

## Map Notes

Every draw/select action also adds the AOI geometry as a feature in one of Esri's built-in Map
Notes layers (Point/Line/Polygon Map Notes, the same templates on Pro's **Insert** ribbon tab) --
not just held in memory. This gives you a real, persistent, editable record of what you drew: it
survives switching AOI tools, can be edited like any other feature, and can be picked back up as an
AOI later via **Select Feature**.

## What actually gets searched vs. clipped

The area used to *search* the LiDAR catalog is the AOI exactly as drawn or selected (point, line,
or polygon), buffered first if you've entered a buffer and turned on clipping (see
[Clipping](clip-and-run.md#clipping-to-the-area-of-interest)) -- otherwise the buffer field has no
effect at all. Every tile that intersects that search area is downloaded in full; clipping (when
enabled) only affects what each tile is *cropped to* during conversion, not which tiles are found.
