namespace KylidarAddin.Services.Copc
{
    /// <summary>Parsed COPC info VLR (160-byte payload, user_id "copc", record_id 1).</summary>
    public class CopcInfo
    {
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double CenterZ { get; set; }
        public double HalfSize { get; set; }
        public double Spacing { get; set; }
        public ulong RootHierOffset { get; set; }
        public ulong RootHierSize { get; set; }
    }

    /// <summary>Fields pulled from a COPC file's LAS 1.4 header + VLRs, no point data.</summary>
    public class CopcFileInfo
    {
        public int OffsetToPointData { get; set; }
        public int NumberOfVlrs { get; set; }
        public byte PointFormat { get; set; }
        public ushort PointRecordLength { get; set; }
        public ulong NumberOfPointRecords { get; set; }
        public CopcInfo CopcInfo { get; set; }

        /// <summary>WKT of the point cloud's native CRS, from the "LASF_Projection"/2112 VLR
        /// (the modern WKT-SRS VLR used by LAS 1.4 point formats 6+, confirmed present on the
        /// real sample tile). Null if that VLR wasn't found within the fetched header region.</summary>
        public string SrsWkt { get; set; }
    }
}
