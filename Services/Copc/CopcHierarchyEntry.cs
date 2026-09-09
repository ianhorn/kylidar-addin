namespace KylidarAddin.Services.Copc
{
    /// <summary>
    /// One 32-byte entry from a COPC hierarchy page: an octree node's location (level/x/y/z) plus
    /// where its point data (or, if IsPagePointer, a child hierarchy page) lives in the file.
    /// </summary>
    public class CopcHierarchyEntry
    {
        public int Level { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public ulong Offset { get; set; }
        public int ByteSize { get; set; }
        public int PointCount { get; set; }

        public bool IsPagePointer => PointCount == -1;
        public bool IsEmpty => PointCount == 0;

        /// <summary>
        /// This node's X/Y bounds (COPC's octree cube is centered/sized from the file's COPC info
        /// VLR). Z is intentionally not computed -- readers.copc's "polygon" crop option is a 2D
        /// polygon with no Z restriction, so a node's Z extent never affects whether it's needed.
        /// </summary>
        public (double MinX, double MinY, double MaxX, double MaxY) GetBoundsXY(CopcInfo copcInfo)
        {
            double size = (2.0 * copcInfo.HalfSize) / (1 << Level);
            double minX = copcInfo.CenterX - copcInfo.HalfSize + X * size;
            double minY = copcInfo.CenterY - copcInfo.HalfSize + Y * size;
            return (minX, minY, minX + size, minY + size);
        }
    }
}
