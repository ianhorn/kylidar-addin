# Area of Interest

All AOI tools set the same clip AOI, so only the last one you use takes effect. The current AOI
is summarized as text below the AOI buttons (e.g. "AOI ready: Polygon AOI" or "AOI ready: AOI from
3 selected feature(s)").

## Draw Point / Draw Line / Draw Polygon

Click one of the three **Draw** buttons to activate an ArcGIS Pro sketch tool on the active map --
draw your shape and it becomes the clip AOI. Requires an open map view.

A point or line AOI has no area on its own, so it needs a
[buffer](clip-and-run.md#buffer) before it can be clipped; a polygon AOI can be clipped as drawn,
with no buffer.

## Select Feature

Click **Select Feature**, then click (or drag a box over) existing features on the active map.
Their combined geometry (unioned if more than one, and split back out into separate pieces if the
result is multi-part -- e.g. two disjoint features) becomes the clip AOI, and the picked features
get the normal Pro selection highlight.

If nothing is found where you clicked/dragged, a message box says so and the AOI is left
unchanged -- click directly on a feature, or drag a box that overlaps one.

## What actually gets searched vs. clipped

The area used to *search* the LiDAR catalog is the AOI exactly as drawn or selected (point, line,
or polygon) -- no buffer applied. The buffer only widens the region the output `.las` is cropped
to afterward. In practice this means: drawing a point and setting a 50 ft buffer finds tiles that
cover that point, then crops the merged output to a 50 ft-radius circle around it.
