/*
 * Binary parsing of a LAS 1.4 header + VLRs, just enough to find the COPC info VLR and the
 * offset/size of the point data region. No compression/point-data handling here at all --
 * see CopcPartialFetchService for how this feeds into a byte-faithful sparse local copy.
 *
 * Field offsets validated against a real Kentucky COPC tile (N114E297_LAS_Phase3.copc.laz):
 * header_size=375 (LAS 1.4), offset_to_point_data=1824, and the parsed COPC info VLR's
 * root_hier_offset/root_hier_size summed to exactly the file's total byte length.
 */
using System;
using System.Text;

namespace KylidarAddin.Services.Copc
{
    public static class CopcHeaderReader
    {
        public const string CopcInfoUserId = "copc";
        public const ushort CopcInfoRecordId = 1;
        public const string SrsWktUserId = "LASF_Projection";
        public const ushort SrsWktRecordId = 2112;

        /// <summary>
        /// Parse a LAS 1.4 header + VLRs from a buffer starting at file offset 0. The buffer must
        /// be at least as long as the header's own "offset to point data" field -- callers should
        /// over-fetch (e.g. 8KB) and call TryParse again with more bytes if it returns false only
        /// because the buffer was too short.
        /// </summary>
        public static bool TryParse(byte[] buffer, out CopcFileInfo info, out int bytesNeeded)
        {
            info = null;
            bytesNeeded = 0;

            if (buffer.Length < 104 ||
                buffer[0] != (byte)'L' || buffer[1] != (byte)'A' || buffer[2] != (byte)'S' || buffer[3] != (byte)'F')
                return false;

            ushort headerSize = BitConverter.ToUInt16(buffer, 94);
            uint offsetToPointData = BitConverter.ToUInt32(buffer, 96);
            uint numVlrs = BitConverter.ToUInt32(buffer, 100);
            byte pointFormat = buffer[104];
            ushort pointRecordLength = BitConverter.ToUInt16(buffer, 105);

            if (offsetToPointData > int.MaxValue)
                return false;
            bytesNeeded = (int)offsetToPointData;
            if (buffer.Length < bytesNeeded)
                return false; // caller must fetch more and retry

            ulong numberOfPointRecords = headerSize >= 375 ? BitConverter.ToUInt64(buffer, 247) : 0;

            CopcInfo copcInfo = null;
            string srsWkt = null;
            int pos = headerSize;
            for (int i = 0; i < numVlrs && pos + 54 <= buffer.Length; i++)
            {
                string userId = Encoding.ASCII.GetString(buffer, pos + 2, 16).TrimEnd('\0');
                ushort recordId = BitConverter.ToUInt16(buffer, pos + 18);
                ushort recordLength = BitConverter.ToUInt16(buffer, pos + 20);
                int dataStart = pos + 54;

                if (userId == CopcInfoUserId && recordId == CopcInfoRecordId && dataStart + 160 <= buffer.Length)
                {
                    copcInfo = new CopcInfo
                    {
                        CenterX = BitConverter.ToDouble(buffer, dataStart + 0),
                        CenterY = BitConverter.ToDouble(buffer, dataStart + 8),
                        CenterZ = BitConverter.ToDouble(buffer, dataStart + 16),
                        HalfSize = BitConverter.ToDouble(buffer, dataStart + 24),
                        Spacing = BitConverter.ToDouble(buffer, dataStart + 32),
                        RootHierOffset = BitConverter.ToUInt64(buffer, dataStart + 40),
                        RootHierSize = BitConverter.ToUInt64(buffer, dataStart + 48),
                    };
                }
                else if (userId == SrsWktUserId && recordId == SrsWktRecordId && dataStart + recordLength <= buffer.Length)
                {
                    // WKT VLR data is a null-terminated (or exactly-sized) ASCII/UTF-8 string.
                    int len = recordLength;
                    while (len > 0 && buffer[dataStart + len - 1] == 0) len--;
                    srsWkt = Encoding.UTF8.GetString(buffer, dataStart, len);
                }

                pos = dataStart + recordLength;
            }

            if (copcInfo == null)
                return false; // not a COPC file (or VLR wasn't in the fetched range)

            info = new CopcFileInfo
            {
                OffsetToPointData = (int)offsetToPointData,
                NumberOfVlrs = (int)numVlrs,
                PointFormat = pointFormat,
                PointRecordLength = pointRecordLength,
                NumberOfPointRecords = numberOfPointRecords,
                CopcInfo = copcInfo,
                SrsWkt = srsWkt
            };
            return true;
        }
    }
}
