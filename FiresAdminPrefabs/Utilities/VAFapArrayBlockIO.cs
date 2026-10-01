using System;
using System.IO;

namespace VerdantsAscent.Utilities
{
    // Moves arrays of plain value types (float, int, Vector3, Color32...) through a reusable byte buffer
    // a megabyte at a time instead of one reader or writer call per value, which was several times slower
    // on large mesh caches. The bytes are identical to per-value little-endian writes, so file formats are
    // unaffected; every platform Valheim runs on is little-endian. Each thread keeps its own buffer.
    public static class VAFapArrayBlockIO
    {
        private const int BlockBufferSizeBytes = 1024 * 1024;

        [ThreadStatic] private static byte[] _blockBuffer;

        private static byte[] BlockBuffer
        {
            get
            {
                if (!BitConverter.IsLittleEndian)
                    throw new PlatformNotSupportedException("Block array I/O needs a little-endian host.");
                return _blockBuffer ?? (_blockBuffer = new byte[BlockBufferSizeBytes]);
            }
        }

        public static unsafe void Write<T>(BinaryWriter writer, T[] values, int count) where T : unmanaged
        {
            ThrowIfCountExceedsArray(values, count);
            var buffer = BlockBuffer;
            long totalBytes = (long)count * sizeof(T);
            fixed (T* valuesStart = values)
            fixed (byte* bufferStart = buffer)
            {
                for (long offset = 0; offset < totalBytes; offset += buffer.Length)
                {
                    int chunkBytes = (int)Math.Min(buffer.Length, totalBytes - offset);
                    Buffer.MemoryCopy((byte*)valuesStart + offset, bufferStart, buffer.Length, chunkBytes);
                    writer.Write(buffer, 0, chunkBytes);
                }
            }
        }

        public static unsafe void Read<T>(BinaryReader reader, T[] destination, int count) where T : unmanaged
        {
            ThrowIfCountExceedsArray(destination, count);
            var buffer = BlockBuffer;
            long totalBytes = (long)count * sizeof(T);
            fixed (T* destinationStart = destination)
            fixed (byte* bufferStart = buffer)
            {
                for (long offset = 0; offset < totalBytes; offset += buffer.Length)
                {
                    int chunkBytes = (int)Math.Min(buffer.Length, totalBytes - offset);
                    FillBuffer(reader, buffer, chunkBytes);
                    Buffer.MemoryCopy(bufferStart, (byte*)destinationStart + offset, totalBytes - offset, chunkBytes);
                }
            }
        }

        private static void FillBuffer(BinaryReader reader, byte[] buffer, int byteCount)
        {
            for (int filled = 0; filled < byteCount;)
            {
                int read = reader.Read(buffer, filled, byteCount - filled);
                if (read <= 0) throw new EndOfStreamException();
                filled += read;
            }
        }

        private static void ThrowIfCountExceedsArray(Array array, int count)
        {
            if (count < 0 || count > (array?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
