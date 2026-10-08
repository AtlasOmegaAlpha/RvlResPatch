namespace RvlResPatch
{
    public enum PrimitiveType : byte
    {
        QUADS = 0x80,
        TRIANGLES = 0x90,
        TRIANGLE_STRIP = 0x98,
        TRIANGLE_FAN = 0xA0,
        LINES = 0xA8,
        LINE_STRIP = 0xB0,
        POINTS = 0xB8
    }

    public class Primitive
    {
        public PrimitiveType type;
        public byte vertexFormat;
        public List<int> vertices = new List<int>();
    }

    public class PrimitiveGroup
    {
        public byte[] commands = [];
        public List<Primitive> primitives = new List<Primitive>();
    }

    public class DisplayList
    {
        public List<PrimitiveGroup> groups = new List<PrimitiveGroup>();
        public List<byte[]> vertices = new List<byte[]>();
        public int vertexSize;

        readonly Dictionary<string, int> _vertexIds = new Dictionary<string, int>();

        public static DisplayList Read(byte[] data, int vertexSize)
        {
            DisplayList dList = new DisplayList
            {
                vertexSize = vertexSize
            };

            EndianReader reader = new EndianReader(data, Endianness.BigEndian);
            List<byte> commands = new List<byte>();
            PrimitiveGroup? group = null;

            while (reader.Position < data.Length)
            {
                int start = (int)reader.Position;
                byte opCode = reader.ReadByte();

                if (opCode == 0x00)
                    continue;

                if ((opCode & 0x80) != 0)
                {
                    if (group == null || commands.Count > 0)
                    {
                        group = new PrimitiveGroup
                        {
                            commands = commands.ToArray()
                        };

                        dList.groups.Add(group);
                        commands.Clear();
                    }

                    Primitive primitive = new Primitive
                    {
                        type = (PrimitiveType)(opCode & 0xF8),
                        vertexFormat = (byte)(opCode & 0x07)
                    };

                    if (!Enum.IsDefined(primitive.type))
                        throw new InvalidDataException($"unknown primitive 0x{opCode:X2}");

                    int count = reader.ReadUInt16();
                    for (int i = 0; i < count; i++)
                    {
                        primitive.vertices.Add(dList.GetVertexId(reader.ReadBytes(vertexSize)));
                    }

                    group.primitives.Add(primitive);
                    continue;
                }

                int length;
                switch (opCode)
                {
                    case 0x08:
                        length = 6;
                        break;

                    case 0x10:
                        length = 5 + 4 * (((data[start + 1] << 8) | data[start + 2]) + 1);
                        break;

                    case 0x20:
                    case 0x28:
                    case 0x30:
                    case 0x38:
                    case 0x61:
                        length = 5;
                        break;

                    case 0x48:
                        length = 1;
                        break;

                    default:
                        throw new InvalidDataException($"unknown display list command 0x{opCode:X2} at 0x{start:X}");
                }

                reader.Position = start;
                commands.AddRange(reader.ReadBytes(length));
            }

            if (commands.Count > 0)
                dList.groups.Add(new PrimitiveGroup { commands = commands.ToArray() });

            return dList;
        }

        public int GetVertexId(byte[] vertex)
        {
            string key = Convert.ToHexString(vertex);
            if (_vertexIds.TryGetValue(key, out int id))
                return id;

            id = this.vertices.Count;
            this.vertices.Add(vertex);
            _vertexIds[key] = id;
            return id;
        }

        public byte[] Write()
        {
            MemoryStream stream = new MemoryStream();
            EndianWriter writer = new EndianWriter(stream, Endianness.BigEndian);

            foreach (PrimitiveGroup group in this.groups)
            {
                writer.WriteBytes(group.commands);

                foreach (Primitive primitive in group.primitives)
                {
                    writer.WriteByte((byte)((byte)primitive.type | primitive.vertexFormat));
                    writer.WriteUInt16((ushort)primitive.vertices.Count);

                    foreach (int vertex in primitive.vertices)
                    {
                        writer.WriteBytes(this.vertices[vertex]);
                    }
                }
            }

            while (stream.Length % 0x20 != 0)
            {
                writer.WriteByte(0);
            }

            return stream.ToArray();
        }

        public int GetVertexCount()
        {
            return this.groups.Sum(x => x.primitives.Sum(x => x.vertices.Count));
        }

        public int GetFaceCount()
        {
            return this.groups.Sum(x => x.primitives.Sum(x => GetFaceCount(x.type, x.vertices.Count)));
        }

        public static int GetFaceCount(PrimitiveType type, int count)
        {
            switch (type)
            {
                case PrimitiveType.QUADS:
                    return count / 4 * 2;

                case PrimitiveType.TRIANGLES:
                    return count / 3;

                case PrimitiveType.TRIANGLE_STRIP:
                case PrimitiveType.TRIANGLE_FAN:
                    return Math.Max(0, count - 2);

                case PrimitiveType.LINES:
                    return count / 2;

                case PrimitiveType.LINE_STRIP:
                    return Math.Max(0, count - 1);

                default:
                    return count;
            }
        }

        public static int GetMaxVertices()
        {
            return ushort.MaxValue;
        }

        public static int GetVertexSize(uint vcdLow, uint vcdHigh)
        {
            int size = 0;

            for (int i = 0; i < 9; i++)
            {
                if (((vcdLow >> i) & 1) != 0)
                    size++;
            }

            for (int i = 0; i < 4 + 8; i++)
            {
                uint type = i < 4 ? (vcdLow >> (9 + 2 * i)) & 3 : (vcdHigh >> (2 * (i - 4))) & 3;

                if (type == 1)
                    return -1;

                if (type == 2)
                    size += 1;
                else if (type == 3)
                    size += 2;
            }

            return size;
        }
    }
}
