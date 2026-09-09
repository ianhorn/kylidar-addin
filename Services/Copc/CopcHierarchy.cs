using System;
using System.Collections.Generic;

namespace KylidarAddin.Services.Copc
{
    public static class CopcHierarchy
    {
        private const int EntrySize = 32;

        /// <summary>Parse a hierarchy page's raw bytes into its flat list of entries.</summary>
        public static List<CopcHierarchyEntry> ParsePage(byte[] pageBytes)
        {
            var entries = new List<CopcHierarchyEntry>(pageBytes.Length / EntrySize);
            int count = pageBytes.Length / EntrySize;
            for (int i = 0; i < count; i++)
            {
                int o = i * EntrySize;
                entries.Add(new CopcHierarchyEntry
                {
                    Level = BitConverter.ToInt32(pageBytes, o + 0),
                    X = BitConverter.ToInt32(pageBytes, o + 4),
                    Y = BitConverter.ToInt32(pageBytes, o + 8),
                    Z = BitConverter.ToInt32(pageBytes, o + 12),
                    Offset = BitConverter.ToUInt64(pageBytes, o + 16),
                    ByteSize = BitConverter.ToInt32(pageBytes, o + 24),
                    PointCount = BitConverter.ToInt32(pageBytes, o + 28),
                });
            }
            return entries;
        }
    }
}
