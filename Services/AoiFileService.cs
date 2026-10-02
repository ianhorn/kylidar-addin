/*
 * Reads a local shapefile or file-geodatabase feature class -- picked via the dock pane's "Browse
 * for File..." button (ArcGIS.Desktop.Catalog.OpenItemDialog, not a plain Windows file picker, so
 * it can browse into a .gdb) -- and unions its features into a single AOI geometry via
 * AoiState.UnionByDimension, the same union logic SelectFeatureAoiTool uses for an in-map
 * selection. The dataset doesn't need to already be a layer on the map.
 *
 * Must run inside QueuedTask.Run: it uses both ArcGIS.Core.Data (opening the dataset) and
 * GeometryEngine (via UnionByDimension), neither of which is safe off the MCT thread.
 */
using System;
using System.Collections.Generic;
using System.IO;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;

namespace KylidarAddin.Services
{
    public static class AoiFileService
    {
        /// <summary>Reads every feature's shape from the shapefile/feature class at fullPath,
        /// projecting to targetSr when given and different, and unions them into one AOI geometry.
        /// Returns null if the dataset has no (non-empty) features.</summary>
        public static Geometry ReadAoiFromFile(string fullPath, SpatialReference targetSr)
        {
            List<Geometry> shapes;

            // OpenItemDialog's FeatureClasses_All filter hands back one of two path shapes: a
            // shapefile ("...\name.shp") or a geodatabase feature class ("...\name.gdb\FcName",
            // optionally under a feature dataset) -- distinguished here by whether ".gdb\" appears.
            var gdbMarker = fullPath.IndexOf(".gdb" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (gdbMarker >= 0)
            {
                var gdbPath = fullPath.Substring(0, gdbMarker + 4);
                var fcName = Path.GetFileName(fullPath);
                using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                using var featureClass = geodatabase.OpenDataset<FeatureClass>(fcName);
                shapes = ReadShapes(featureClass, targetSr);
            }
            else if (fullPath.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.GetDirectoryName(fullPath);
                var name = Path.GetFileNameWithoutExtension(fullPath);
                using var datastore = new FileSystemDatastore(new FileSystemConnectionPath(new Uri(folder), FileSystemDatastoreType.Shapefile));
                using var featureClass = datastore.OpenDataset<FeatureClass>(name);
                shapes = ReadShapes(featureClass, targetSr);
            }
            else
            {
                throw new NotSupportedException($"\"{Path.GetFileName(fullPath)}\" isn't a shapefile or a file geodatabase feature class.");
            }

            return shapes.Count == 0 ? null : AoiState.UnionByDimension(shapes);
        }

        /// <summary>Datastore and feature class must both stay open for this whole read -- disposing
        /// either early can invalidate the other's native handle -- so this runs while both are still
        /// in scope in the caller's using blocks, rather than handing back a bare FeatureClass.</summary>
        private static List<Geometry> ReadShapes(FeatureClass featureClass, SpatialReference targetSr)
        {
            var shapes = new List<Geometry>();
            using var cursor = featureClass.Search(null, false);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                var shape = feature.GetShape();
                if (shape == null || shape.IsEmpty) continue;
                if (targetSr != null && shape.SpatialReference != null && !shape.SpatialReference.IsEqual(targetSr))
                    shape = GeometryEngine.Instance.Project(shape, targetSr);
                shapes.Add(shape);
            }
            return shapes;
        }
    }
}
