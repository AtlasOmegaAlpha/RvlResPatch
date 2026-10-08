namespace RvlResPatch
{
    public class ResResize
    {
        public int blockStart;
        public byte[] data = [];
    }

    public class ResOffsetMap
    {
        readonly List<(int start, int oldEnd, int newSize, int delta)> _regions;

        public ResOffsetMap(List<(int start, int oldEnd, int newSize, int delta)> regions)
        {
            _regions = regions;
        }

        public int Map(int offset)
        {
            int shift = 0;

            foreach ((int start, int oldEnd, int newSize, int delta) in _regions)
            {
                if (offset >= oldEnd)
                {
                    shift += delta;
                    continue;
                }

                if (offset >= start)
                {
                    if (offset - start > newSize)
                        throw new InvalidOperationException($"offset 0x{offset:X} is in a removed part of the file");

                    return offset + shift;
                }

                break;
            }

            return offset + shift;
        }
    }

    public static class ResRebuilder
    {
        const int DATA_ALIGN = 0x20;

        public static byte[] Rebuild(byte[] data, ResLayout layout, List<ResResize> resizes, out ResOffsetMap offsMap)
        {
            if (!layout.CanRebuild)
                throw new InvalidOperationException("the file layout is not fully known: " + layout.errors[0]);

            Dictionary<int, ResBlock> arrays = layout.blocks.Where(x => x.IsResizable()).ToDictionary(x => x.start);
            List<(int start, int oldEnd, int newSize, int delta)> regions = new List<(int, int, int, int)>();
            Dictionary<int, ResResize> resizeByStart = new Dictionary<int, ResResize>();

            foreach (ResResize resize in resizes.OrderBy(x => x.blockStart))
            {
                if (!arrays.TryGetValue(resize.blockStart, out ResBlock? block))
                    throw new InvalidOperationException($"0x{resize.blockStart:X} is not a resizable block");

                if (!resizeByStart.TryAdd(resize.blockStart, resize))
                    throw new InvalidOperationException($"vertex array at 0x{resize.blockStart:X} resized twice");

                int oldSize = block.end - block.start;
                int newSize = GetNewSize(block, resize.data.Length);
                regions.Add((block.start, block.end, newSize, newSize - oldSize));
            }

            offsMap = new ResOffsetMap(regions);
            byte[] output = new byte[offsMap.Map(data.Length)];
            int position = 0;

            foreach ((int start, int oldEnd, int newSize, int delta) in regions)
            {
                ResBlock block = arrays[start];
                int headerEnd = start + block.dataOffset;
                Array.Copy(data, position, output, offsMap.Map(position), headerEnd - position);

                byte[] newData = resizeByStart[start].data;
                Array.Copy(newData, 0, output, offsMap.Map(headerEnd), newData.Length);
                position = oldEnd;
            }

            Array.Copy(data, position, output, offsMap.Map(position), data.Length - position);
            EndianWriter writer = new EndianWriter(new MemoryStream(output), Endianness.BigEndian);

            foreach (ResPointer pointer in layout.pointers)
            {
                int location = offsMap.Map(pointer.location);
                long value = (long)offsMap.Map(pointer.target) - offsMap.Map(pointer.baseOffset);
                writer.Position = location;

                switch (pointer.type)
                {
                    case PointerType.ABS_U16:
                        if (value < 0 || value > ushort.MaxValue)
                            throw new InvalidOperationException($"{pointer.description} does not fit in 16 bits");

                        writer.WriteUInt16((ushort)value);
                        break;

                    case PointerType.SIZE:
                        writer.WriteUInt32((uint)value);
                        break;

                    default:
                        writer.WriteInt32((int)value);
                        break;
                }
            }

            return output;
        }

        public static int GetNewSize(ResBlock block, int dataLength)
        {
            int oldSize = block.end - block.start;
            int newSize = block.dataOffset + dataLength;
            return newSize > oldSize ? oldSize + AlignUp(newSize - oldSize) : oldSize - AlignDown(oldSize - newSize);
        }

        private static int AlignUp(int value)
        {
            return (value + DATA_ALIGN - 1) & ~(DATA_ALIGN - 1);
        }

        private static int AlignDown(int value)
        {
            return value & ~(DATA_ALIGN - 1);
        }
    }
}
