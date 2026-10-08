using System.Text.RegularExpressions;

namespace RvlResPatch
{
    public enum VertexFormat
    {
        U8,
        S8,
        U16,
        S16,
        F32
    }

    public enum NormalFormat
    {
        S8,
        S16,
        F32
    }

    public enum ColorFormat
    {
        RGB565,
        RGB8,
        RGBX8,
        RGBA4,
        RGBA6,
        RGBA8
    }

    public enum DataType
    {
        POSITION,
        NORMAL,
        COLOR,
        UV
    }

    public enum OptimizeMode
    {
        NONE,
        NW4R,
        LOSSLESS
    }

    public class ArrayEdit
    {
        public uint format;
        public int? div;
    }

    public class ArrayInfo
    {
        public DataType type;
        public string name = "";
        public int offset;
        public uint components;
        public uint format;
        public int div;
        public int count;
        public int stride;
        public int size;
    }

    public class PolygonInfo
    {
        public string name = "";
        public int offset;
        public int vertexCount;
        public int faceCount;
        public string primitives = "";
        public List<(string label, ArrayInfo array)> attributes = new List<(string, ArrayInfo)>();
    }

    public class ModelInfo
    {
        public string name = "";
        public int offset;
        public uint version;
        public List<PolygonInfo> polygons = new List<PolygonInfo>();
        public List<ArrayInfo> arrays = new List<ArrayInfo>();
    }

    public class PolygonConversion
    {
        public string name = "";
        public int offset;
        public int modelOffset;
        public int countsOffset;
        public int dlOffset;
        public int dlSize;
        public int oldVertexCount;
        public int oldFaceCount;
        public int vertexCount;
        public int faceCount;
        public byte[] data = [];
    }

    public class FormatConversion
    {
        public DataType type;
        public string name = "";
        public int headerOffset;
        public int dataOffset;
        public int maxSize;
        public uint components;
        public uint format;
        public byte div;
        public byte stride;
        public byte[] data = [];
    }

    public class BrresPatch
    {
        static readonly string[] SECTIONS_V10 =
        {
            "Defs",
            "Bones",
            "Verts",
            "Norms",
            "Colors",
            "UVs",
            "FurVecs",
            "FurPos",
            "Mats",
            "Shaders",
            "Objs",
            "Tex",
            "Plt"
        };

        static readonly string[] SECTIONS_V8 =
        {
            "Defs",
            "Bones",
            "Verts",
            "Norms",
            "Colors",
            "UVs",
            "Mats",
            "Shaders",
            "Objs",
            "Tex",
            "Plt"
        };

        static readonly string[] VERTEX_FORMATS_STR =
        {
            "U8",
            "S8",
            "U16",
            "S16",
            "F32",
            "Unknown (5)",
            "Unknown (6)",
            "Unknown (7)"
        };

        static readonly string[] COLOR_FORMATS_STR =
        {
            "RGB565",
            "RGB8",
            "RGBX8",
            "RGBA4",
            "RGBA6",
            "RGBA8",
            "Unknown (6)",
            "Unknown (7)"
        };

        static readonly (string section, DataType type)[] ARRAY_SECTIONS =
        {
            ("Verts", DataType.POSITION),
            ("Norms", DataType.NORMAL),
            ("Colors", DataType.COLOR),
            ("UVs", DataType.UV)
        };

        static readonly VertexFormat[] VERTEX_FORMATS =
        {
            VertexFormat.U8,
            VertexFormat.S8,
            VertexFormat.U16,
            VertexFormat.S16,
            VertexFormat.F32
        };

        static readonly NormalFormat[] NORMAL_FORMATS =
        {
            NormalFormat.S8,
            NormalFormat.S16,
            NormalFormat.F32
        };

        static readonly ColorFormat[] COLOR_FORMATS =
        {
            ColorFormat.RGB565,
            ColorFormat.RGBA4,
            ColorFormat.RGB8,
            ColorFormat.RGBA6,
            ColorFormat.RGBA8
        };

        const byte GX_NOP = 0x00;
        const byte GX_LOAD_CP_REG = 0x08;
        const byte GX_LOAD_XF_REG = 0x10;
        const byte GX_LOAD_BP_REG = 0x61;
        const byte CP_VCD_LO = 0x50;
        const byte CP_VCD_HI = 0x60;
        const byte CP_VAT_A = 0x70;
        const byte CP_VAT_B = 0x80;
        const byte CP_VAT_C = 0x90;

        byte[] _data;
        public VertexFormat? targetFormat;
        public NormalFormat? targetNormalFormat;
        public ColorFormat? targetColorFormat;
        public uint? divisor;
        public OptimizeMode optimize = OptimizeMode.NONE;
        public PrimitiveType? targetPrimitive;
        public List<string> arrayFilter = new List<string>();
        public Dictionary<int, ArrayEdit> arrayEdits = new Dictionary<int, ArrayEdit>();
        public Dictionary<int, PrimitiveType> primitiveEdits = new Dictionary<int, PrimitiveType>();
        HashSet<string>? shapeArrays;
        public int changes;


        public byte[] Data()
        {
            return _data;
        }

        public BrresPatch(byte[] data)
        {
            _data = data;
        }

        private List<(string name, int offset)> ReadGroup(EndianReader reader, int offset)
        {
            List<(string, int)> list = new List<(string, int)>();
            reader.Position = offset + 4;
            int count = reader.ReadInt32();

            for (int i = 1; i <= count; i++)
            {
                reader.Position = offset + 8 + 16 * i + 8;
                int nameOffset = reader.ReadInt32();
                int dataOffset = reader.ReadInt32();

                reader.Position = offset + nameOffset;
                string name = reader.ReadStringNT();

                list.Add((name, offset + dataOffset));
            }

            return list;
        }

        public void Run()
        {
            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
            List<(string name, int offset)> models = FindModels(reader);

            if (models.Count <= 0)
            {
                Console.WriteLine("ERROR: No models found");
                return;
            }

            List<FormatConversion> conversions = new List<FormatConversion>();
            if (this.targetFormat != null || this.targetNormalFormat != null || this.targetColorFormat != null || this.optimize != OptimizeMode.NONE || this.arrayEdits.Count > 0)
            {
                foreach ((string modelName, int modelOffset) in models)
                {
                    conversions.AddRange(GetConversions(reader, modelOffset, modelName));
                }
            }

            List<PolygonConversion> polygonConversions = new List<PolygonConversion>();
            if (this.targetPrimitive != null || this.primitiveEdits.Count > 0)
            {
                foreach ((string modelName, int modelOffset) in models)
                {
                    polygonConversions.AddRange(GetPolygonConversions(reader, modelOffset, modelName));
                }
            }

            ApplyConversions(conversions, polygonConversions);
            reader = new EndianReader(_data, Endianness.BigEndian);
            models = FindModels(reader);

            EndianWriter writer = new EndianWriter(new MemoryStream(_data), Endianness.BigEndian);
            foreach ((string modelName, int modelOffset) in models)
            {
                PatchModel(reader, writer, modelOffset, modelName);
            }
        }

        private List<(string name, int offset)> FindModels(EndianReader reader)
        {
            List<(string, int)> models = new List<(string, int)>();
            reader.Position = 0;
            string magic = reader.ReadString(4);

            if (magic == "MDL0")
            {
                models.Add(("(mdl0)", 0));
                return models;
            }

            if (magic != "bres")
                throw new InvalidDataException("not a BRRES or MDL0 file");

            if (reader.ReadUInt16() != 0xFEFF)
                throw new InvalidDataException("unsupported byte order (expected big endian)");

            reader.Position = 0xC;
            int rootOffset = reader.ReadUInt16();
            reader.Position = rootOffset;

            if (reader.ReadString(4) != "root")
                throw new InvalidDataException("BRRES root block not found");

            foreach ((string folder, int fOffset) in ReadGroup(reader, rootOffset + 8))
            {
                if (folder != "3DModels(NW4R)")
                    continue;

                foreach ((string subName, int subOffset) in ReadGroup(reader, fOffset))
                {
                    reader.Position = subOffset;
                    if (reader.ReadString(4) == "MDL0")
                        models.Add((subName, subOffset));
                }
            }

            return models;
        }

        private Dictionary<string, int>? ReadSections(EndianReader reader, int modelOffset, string modelName, out uint version)
        {
            reader.Position = modelOffset + 8;
            version = reader.ReadUInt32();
            string[]? names = version >= 10 ? SECTIONS_V10 : version >= 8 ? SECTIONS_V8 : null;

            if (names == null)
            {
                Console.WriteLine($"[{modelName}] MDL0 v{version} unsupported (skipped)");
                return null;
            }

            Dictionary<string, int>? sections = new Dictionary<string, int>();
            for (int i = 0; i < names.Length; i++)
            {
                reader.Position = modelOffset + 0x10 + 4 * i;
                int sOffset = reader.ReadInt32();

                if (sOffset != 0)
                    sections[names[i]] = modelOffset + sOffset;
            }

            return sections;
        }

        private void PatchModel(EndianReader reader, EndianWriter writer, int modelOffset, string modelName)
        {
            Dictionary<string, int>? sections = ReadSections(reader, modelOffset, modelName, out uint version);
            if (sections == null)
                return;

            Console.WriteLine($"[{modelName}] MDL0 v{version}");

            Dictionary<int, int> verts = ReadArrayOffsets(reader, sections, "Verts");
            Dictionary<int, int> norms = ReadArrayOffsets(reader, sections, "Norms");
            Dictionary<int, int> colors = ReadArrayOffsets(reader, sections, "Colors");
            Dictionary<int, int> uvs = ReadArrayOffsets(reader, sections, "UVs");

            if (!sections.TryGetValue("Objs", out int objOffs))
                return;

            foreach ((string pName, int pOffs) in ReadGroup(reader, objOffs))
            {
                PatchPolygon(reader, writer, pOffs, pName, verts, norms, colors, uvs);

                if (version >= 10)
                    PatchFurIds(reader, writer, pOffs, pName, sections.ContainsKey("FurVecs"), sections.ContainsKey("FurPos"));
            }
        }

        private Dictionary<int, int> ReadArrayOffsets(EndianReader reader, Dictionary<string, int> sections, string key)
        {
            Dictionary<int, int> arrays = new Dictionary<int, int>();

            if (!sections.TryGetValue(key, out int groupOffs))
                return arrays;

            foreach ((string arrName, int arrOffs) in ReadGroup(reader, groupOffs))
            {
                reader.Position = arrOffs + 0x10;
                arrays[reader.ReadInt32()] = arrOffs;
            }

            return arrays;
        }

        private (uint components, uint format, uint div) ReadArrayFormat(EndianReader reader, int arrayOffset)
        {
            reader.Position = arrayOffset + 0x14;
            uint components = reader.ReadUInt32();
            uint format = reader.ReadUInt32();
            uint div = reader.ReadByte();
            return (components, format, div);
        }

        private Dictionary<byte, int>? ReadRegisters(EndianReader reader, int polygonOffset)
        {
            reader.Position = polygonOffset + 0x1C;
            int dlSize = (int)reader.ReadUInt32();
            int dlStart = polygonOffset + 0x18 + reader.ReadInt32();

            if (dlSize <= 0 || dlStart + dlSize > _data.Length)
                return null;

            Dictionary<byte, int> registers = new Dictionary<byte, int>();
            reader.Position = dlStart;

            while (reader.Position < dlStart + dlSize)
            {
                byte opCode = reader.ReadByte();
                bool brk = false;

                switch (opCode)
                {
                    case GX_LOAD_CP_REG:
                        byte register = reader.ReadByte();
                        registers[register] = (int)reader.Position;
                        reader.Position += 4;
                        break;

                    case GX_LOAD_XF_REG:
                        int count = reader.ReadUInt16() + 1;
                        reader.Position += 2 + 4 * count;
                        break;

                    case GX_LOAD_BP_REG:
                        reader.Position += 4;
                        break;

                    case GX_NOP:
                        break;

                    default:
                        brk = true;
                        break;
                }

                if (brk)
                    break;
            }

            return registers;
        }

        private void PatchPolygon(EndianReader reader, EndianWriter writer, int polygonOffset, string polygonName, Dictionary<int, int> verts, Dictionary<int, int> norms, Dictionary<int, int> colors, Dictionary<int, int> uvs)
        {
            Dictionary<byte, int>? registers = ReadRegisters(reader, polygonOffset);
            if (registers == null)
            {
                Console.WriteLine($" {polygonName}: invalid vertex display list (skipped)");
                return;
            }

            if (!registers.TryGetValue(CP_VCD_LO, out int vcdLoOffs) || !registers.TryGetValue(CP_VCD_HI, out int vcdHiOffs))
            {
                Console.WriteLine($" {polygonName}: no vertex descriptor found (skipped)");
                return;
            }

            reader.Position = vcdLoOffs;
            uint vcdLow = reader.ReadUInt32();
            reader.Position = vcdHiOffs;
            uint vcdHigh = reader.ReadUInt32();
            reader.Position = polygonOffset + 0x48;
            short posId = reader.ReadInt16();
            short nrmId = reader.ReadInt16();

            short[] clrIds = new short[2];
            for (int i = 0; i < clrIds.Length; i++)
            {
                clrIds[i] = reader.ReadInt16();
            }

            short[] uvIds = new short[8];
            for (int i = 0; i < uvIds.Length; i++)
            {
                uvIds[i] = reader.ReadInt16();
            }

            for (byte b = 0; b < 8; b++)
            {
                if (!registers.TryGetValue((byte)(CP_VAT_A + b), out int vatAOffs))
                    continue;

                registers.TryGetValue((byte)(CP_VAT_B + b), out int vatBOffs);
                registers.TryGetValue((byte)(CP_VAT_C + b), out int vatCOffs);

                reader.Position = vatAOffs;
                uint vatA = reader.ReadUInt32();
                uint vatB = 0;
                uint vatC = 0;

                if (vatBOffs != 0)
                {
                    reader.Position = vatBOffs;
                    vatB = reader.ReadUInt32();
                }

                if (vatCOffs != 0)
                {
                    reader.Position = vatCOffs;
                    vatC = reader.ReadUInt32();
                }

                uint newVatA = vatA;
                uint newVatB = vatB;
                uint newVatC = vatC;

                // Position
                if (HasAttribute(vcdLow, 9) && verts.TryGetValue(posId, out int posOffs))
                {
                    (uint components, uint format, uint div) = ReadArrayFormat(reader, posOffs);
                    newVatA = SetBits(newVatA, 0, 1, components);
                    newVatA = SetBits(newVatA, 1, 3, format);
                    newVatA = SetBits(newVatA, 4, 5, div);
                }

                // Normal
                if (HasAttribute(vcdLow, 11) && norms.TryGetValue(nrmId, out int nrmOffs))
                {
                    (uint type, uint format, uint div) = ReadArrayFormat(reader, nrmOffs);
                    newVatA = SetBits(newVatA, 9, 1, type == 0 ? 0u : 1u);
                    newVatA = SetBits(newVatA, 10, 3, format);
                    newVatA = SetBits(newVatA, 31, 1, type == 2 ? 1u : 0u);
                }

                // Colors
                for (int i = 0; i < clrIds.Length; i++)
                {
                    if (!HasAttribute(vcdLow, 13 + 2 * i) || !colors.TryGetValue(clrIds[i], out int clrOffs))
                        continue;

                    (uint components, uint format, uint div) = ReadArrayFormat(reader, clrOffs);
                    int shift = i == 0 ? 13 : 17;
                    newVatA = SetBits(newVatA, shift, 1, components);
                    newVatA = SetBits(newVatA, shift + 1, 3, format);
                }

                // UVs
                for (int i = 0; i < uvIds.Length; i++)
                {
                    if (!HasAttribute(vcdHigh, 2 * i) || !uvs.TryGetValue(uvIds[i], out int uvOffs))
                        continue;

                    (uint components, uint format, uint frac) = ReadArrayFormat(reader, uvOffs);
                    switch (i)
                    {
                        case 0:
                            newVatA = SetBits(newVatA, 21, 1, components);
                            newVatA = SetBits(newVatA, 22, 3, format);
                            newVatA = SetBits(newVatA, 25, 5, frac);
                            break;

                        case 1:
                            newVatB = SetBits(newVatB, 0, 1, components);
                            newVatB = SetBits(newVatB, 1, 3, format);
                            newVatB = SetBits(newVatB, 4, 5, frac);
                            break;

                        case 2:
                            newVatB = SetBits(newVatB, 9, 1, components);
                            newVatB = SetBits(newVatB, 10, 3, format);
                            newVatB = SetBits(newVatB, 13, 5, frac);
                            break;

                        case 3:
                            newVatB = SetBits(newVatB, 18, 1, components);
                            newVatB = SetBits(newVatB, 19, 3, format);
                            newVatB = SetBits(newVatB, 22, 5, frac);
                            break;

                        case 4:
                            newVatB = SetBits(newVatB, 27, 1, components);
                            newVatB = SetBits(newVatB, 28, 3, format);
                            newVatC = SetBits(newVatC, 0, 5, frac);
                            break;

                        case 5:
                            newVatC = SetBits(newVatC, 5, 1, components);
                            newVatC = SetBits(newVatC, 6, 3, format);
                            newVatC = SetBits(newVatC, 9, 5, frac);
                            break;

                        case 6:
                            newVatC = SetBits(newVatC, 14, 1, components);
                            newVatC = SetBits(newVatC, 15, 3, format);
                            newVatC = SetBits(newVatC, 18, 5, frac);
                            break;

                        case 7:
                            newVatC = SetBits(newVatC, 23, 1, components);
                            newVatC = SetBits(newVatC, 24, 3, format);
                            newVatC = SetBits(newVatC, 27, 5, frac);
                            break;
                    }
                }

                List<string> msgs = new List<string>();

                if (newVatA != vatA)
                {
                    Describe(msgs, vatA, newVatA, 'A');
                    writer.Position = vatAOffs;
                    writer.WriteUInt32(newVatA);
                    changes++;
                }

                if (vatBOffs != 0 && newVatB != vatB)
                {
                    Describe(msgs, vatB, newVatB, 'B');
                    writer.Position = vatBOffs;
                    writer.WriteUInt32(newVatB);
                    changes++;
                }

                if (vatCOffs != 0 && newVatC != vatC)
                {
                    Describe(msgs, vatC, newVatC, 'C');
                    writer.Position = vatCOffs;
                    writer.WriteUInt32(newVatC);
                    changes++;
                }

                if (msgs.Count <= 0)
                    continue;

                Console.WriteLine($"{polygonName}: vertex format {b} updated");
                foreach (string msg in msgs)
                {
                    Console.WriteLine(" " + msg);
                }
            }
        }

        private void PatchFurIds(EndianReader reader, EndianWriter writer, int polygonOffset, string polygonName, bool hasFurVecs, bool hasFurPos)
        {
            reader.Position = polygonOffset + 0x60;
            short furVecId = reader.ReadInt16();
            short furPosId = reader.ReadInt16();

            if (!hasFurVecs && furVecId != -1)
            {
                Console.WriteLine($"{polygonName}: fur vector ID {furVecId} -> -1 (model has no fur arrays)");
                writer.Position = polygonOffset + 0x60;
                writer.WriteInt16(-1);
                changes++;
            }

            if (!hasFurPos && furPosId != -1)
            {
                Console.WriteLine($"{polygonName}: fur position ID {furPosId} -> -1 (model has no fur arrays)");
                writer.Position = polygonOffset + 0x62;
                writer.WriteInt16(-1);
                changes++;
            }
        }

        private static bool HasAttribute(uint vcd, int shift)
        {
            return ((vcd >> shift) & 3) != 0;
        }

        private static uint GetBits(uint value, int shift, int bits)
        {
            return (value >> shift) & ((1u << bits) - 1);
        }

        private static uint SetBits(uint value, int shift, int bits, uint field)
        {
            uint mask = ((1u << bits) - 1) << shift;
            return (value & ~mask) | ((field << shift) & mask);
        }

        private static void Describe(List<string> msgs, uint oldValue, uint newValue, char register)
        {
            msgs.Add($"VAT {register}: 0x{oldValue:X8} -> 0x{newValue:X8}");

            void DescribeField(string label, int formatShift, int divShift, bool isColor = false)
            {
                string[] formats = isColor ? COLOR_FORMATS_STR : VERTEX_FORMATS_STR;
                string oldText = formats[GetBits(oldValue, formatShift, 3)];
                string newText = formats[GetBits(newValue, formatShift, 3)];

                if (divShift >= 0)
                {
                    oldText += "/div " + GetBits(oldValue, divShift, 5);
                    newText += "/div " + GetBits(newValue, divShift, 5);
                }

                if (oldText != newText)
                    msgs.Add($" {label}: {oldText} -> {newText}");
            }

            switch (register)
            {
                case 'A':
                    DescribeField("position", 1, 4);
                    DescribeField("normal", 10, -1);
                    DescribeField("color0", 14, -1, true);
                    DescribeField("color1", 18, -1, true);
                    DescribeField("uv0", 22, 25);
                    break;

                case 'B':
                    DescribeField("uv1", 1, 4);
                    DescribeField("uv2", 10, 13);
                    DescribeField("uv3", 19, 22);
                    break;
            }
        }

        private static int GetFormatSize(VertexFormat format)
        {
            switch (format)
            {
                case VertexFormat.U8:
                case VertexFormat.S8:
                    return 1;

                case VertexFormat.U16:
                case VertexFormat.S16:
                    return 2;

                default:
                    return 4;
            }
        }

        private static VertexFormat ConvertToVertexFormat(NormalFormat format)
        {
            switch (format)
            {
                case NormalFormat.S8:
                    return VertexFormat.S8;

                case NormalFormat.S16:
                    return VertexFormat.S16;

                default:
                    return VertexFormat.F32;
            }
        }

        private static bool IsNormalFormat(VertexFormat format)
        {
            return format == VertexFormat.S8 || format == VertexFormat.S16 || format == VertexFormat.F32;
        }

        private static int FormatSize(ColorFormat format)
        {
            switch (format)
            {
                case ColorFormat.RGB565:
                case ColorFormat.RGBA4:
                    return 2;

                case ColorFormat.RGB8:
                case ColorFormat.RGBA6:
                    return 3;

                default:
                    return 4;
            }
        }

        private static (long minimum, long maximum) GetFormatRange(VertexFormat format)
        {
            switch (format)
            {
                case VertexFormat.U8:
                    return (0, byte.MaxValue);

                case VertexFormat.S8:
                    return (sbyte.MinValue, sbyte.MaxValue);

                case VertexFormat.U16:
                    return (0, ushort.MaxValue);

                case VertexFormat.S16:
                    return (short.MinValue, short.MaxValue);

                default:
                    return (0, 0);
            }
        }

        private static int GetNormalDiv(VertexFormat format)
        {
            return format == VertexFormat.S8 ? 6 : format == VertexFormat.S16 ? 14 : 0;
        }

        private static int GetComponentCount(DataType type, uint components)
        {
            switch (type)
            {
                case DataType.POSITION:
                    return components == 0 ? 2 : 3;

                case DataType.NORMAL:
                    return components == 0 ? 3 : 9;

                case DataType.UV:
                    return components == 0 ? 1 : 2;

                default:
                    return 4;
            }
        }

        private bool IsSelected(DataType type, string name)
        {
            if (this.arrayFilter.Count == 0)
                return true;

            foreach (string filter in this.arrayFilter)
            {
                string typePart = filter;
                string? namePart = null;
                int separator = filter.IndexOf(':');

                if (separator >= 0)
                {
                    typePart = filter.Substring(0, separator);
                    namePart = filter.Substring(separator + 1);
                }

                bool typeMatches = TypeMatches(typePart, type);
                if (separator >= 0 && typeMatches && namePart == name)
                    return true;

                if (separator < 0 && (typeMatches || filter == name))
                    return true;
            }

            return false;
        }

        private static bool TypeMatches(string text, DataType type)
        {
            switch (text.ToLowerInvariant())
            {
                case "pos":
                case "position":
                case "positions":
                case "verts":
                case "vertices":
                    return type == DataType.POSITION;

                case "nrm":
                case "normal":
                case "normals":
                case "norms":
                    return type == DataType.NORMAL;

                case "clr":
                case "color":
                case "colors":
                    return type == DataType.COLOR;

                case "uv":
                case "uvs":
                case "tex":
                case "texcoord":
                case "texcoords":
                    return type == DataType.UV;

                default:
                    return false;
            }
        }

        private List<FormatConversion> GetConversions(EndianReader reader, int modelOffset, string modelName)
        {
            List<FormatConversion> conversions = new List<FormatConversion>();
            Dictionary<string, int>? sections = ReadSections(reader, modelOffset, modelName, out uint version);
            if (sections == null)
                return conversions;

            Console.WriteLine($"[{modelName}] vertex arrays");
            HashSet<(DataType, int)> directArrays = FindDirectArrays(reader, sections);

            foreach ((string section, DataType type) in ARRAY_SECTIONS)
            {
                if (!sections.TryGetValue(section, out int groupOffs))
                    continue;

                foreach ((string aName, int aOffs) in ReadGroup(reader, groupOffs))
                {
                    bool hasEdit = this.arrayEdits.ContainsKey(aOffs);
                    if (!hasEdit && (this.arrayEdits.Count > 0 || !IsSelected(type, aName)))
                        continue;

                    if (HasShapeAnimation(reader, type, aName))
                    {
                        Console.WriteLine($" {type} {aName}: used by a shape animation (SHP0), which needs {(type == DataType.COLOR ? "RGBA8" : "F32")} data (skipped)");
                        continue;
                    }

                    reader.Position = aOffs + 0x10;
                    int arrayId = reader.ReadInt32();

                    if (directArrays.Contains((type, arrayId)))
                    {
                        Console.WriteLine($"{type} {aName}: used as direct, non-indexed data by a polygon (skipped)");
                        continue;
                    }

                    FormatConversion? conversion = type == DataType.COLOR ? ConvertColorArray(reader, aOffs, aName) : ConvertComponentArray(reader, aOffs, aName, type);
                    if (conversion != null)
                        conversions.Add(conversion);
                }
            }

            return conversions;
        }

        public bool HasShapeAnimation(EndianReader reader, DataType type, string arrayName)
        {
            if (type == DataType.UV)
                return false;

            if (this.shapeArrays == null)
            {
                this.shapeArrays = new HashSet<string>();
                FindShapeAnimationData(reader);
            }

            return this.shapeArrays.Contains(arrayName);
        }

        private void FindShapeAnimationData(EndianReader reader)
        {
            reader.Position = 0;
            if (reader.ReadString(4) != "bres")
                return;

            reader.Position = 0xC;
            int rootOffset = reader.ReadUInt16();

            foreach ((string folder, int fOffset) in ReadGroup(reader, rootOffset + 8))
            {
                if (folder != "AnmShp(NW4R)")
                    continue;

                foreach ((string anmName, int anmOffset) in ReadGroup(reader, fOffset))
                {
                    reader.Position = anmOffset;
                    if (reader.ReadString(4) != "SHP0")
                        continue;

                    reader.Position = anmOffset + 8;
                    int version = reader.ReadInt32();
                    reader.Position = anmOffset + 0x10;
                    int dictOffs = reader.ReadInt32();
                    int listOffs = reader.ReadInt32();
                    reader.Position = anmOffset + (version >= 4 ? 0x26 : 0x22);
                    int count = reader.ReadUInt16();

                    if (dictOffs != 0)
                    {
                        foreach ((string entryName, int entryOffset) in ReadGroup(reader, anmOffset + dictOffs))
                        {
                            this.shapeArrays?.Add(entryName);
                        }
                    }

                    for (int i = 0; listOffs != 0 && i < count; i++)
                    {
                        reader.Position = anmOffset + listOffs + 4 * i;
                        int stringOffs = reader.ReadInt32();
                        reader.Position = anmOffset + listOffs + stringOffs;
                        this.shapeArrays?.Add(reader.ReadStringNT());
                    }
                }
            }
        }

        private List<PolygonConversion> GetPolygonConversions(EndianReader reader, int modelOffset, string modelName)
        {
            List<PolygonConversion> conversions = new List<PolygonConversion>();
            Dictionary<string, int>? sections = ReadSections(reader, modelOffset, modelName, out uint version);
            if (sections == null || !sections.TryGetValue("Objs", out int objOffs))
                return conversions;

            Console.WriteLine($"[{modelName}] polygons");

            foreach ((string pName, int pOffs) in ReadGroup(reader, objOffs))
            {
                PrimitiveType target;
                if (this.primitiveEdits.TryGetValue(pOffs, out PrimitiveType edit))
                    target = edit;
                else if (this.primitiveEdits.Count == 0 && this.targetPrimitive != null && IsPolygonSelected(modelName, pName))
                    target = this.targetPrimitive.Value;
                else
                    continue;

                PolygonConversion? conversion = ConvertPolygon(reader, pOffs, pName, target, out string msg);
                Console.WriteLine($" {pName}: {msg}");

                if (conversion == null)
                    continue;

                conversion.modelOffset = modelOffset;
                conversion.countsOffset = modelOffset + GetModelInfoOffset(version) + 0x10;
                conversions.Add(conversion);
            }

            return conversions;
        }

        private static int GetModelInfoOffset(uint version)
        {
            return version >= 11 ? 0x4C : version == 10 ? 0x48 : 0x40;
        }

        private DisplayList? ReadDisplayList(EndianReader reader, int polygonOffset, out int dlOffset, out int dlSize, out string error)
        {
            dlOffset = 0;
            dlSize = 0;
            error = "";

            Dictionary<byte, int>? registers = ReadRegisters(reader, polygonOffset);
            if (registers == null || !registers.TryGetValue(CP_VCD_LO, out int vcdLoOffs) || !registers.TryGetValue(CP_VCD_HI, out int vcdHiOffs))
            {
                error = "no vertex descriptor found";
                return null;
            }

            reader.Position = vcdLoOffs;
            uint vcdLow = reader.ReadUInt32();
            reader.Position = vcdHiOffs;
            uint vcdHigh = reader.ReadUInt32();

            int vertexSize = DisplayList.GetVertexSize(vcdLow, vcdHigh);
            if (vertexSize <= 0)
            {
                error = "unsupported direct vertex data type";
                return null;
            }

            reader.Position = polygonOffset + 0x24;
            dlSize = reader.ReadInt32();
            reader.ReadInt32();
            dlOffset = polygonOffset + 0x24 + reader.ReadInt32();

            reader.Position = dlOffset;
            try
            {
                return DisplayList.Read(reader.ReadBytes(dlSize), vertexSize);
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private PolygonConversion? ConvertPolygon(EndianReader reader, int polygonOffset, string polygonName, PrimitiveType target, out string message)
        {
            DisplayList? dList = ReadDisplayList(reader, polygonOffset, out int dlOffset, out int dlSize, out string error);
            if (dList == null)
            {
                message = $"{error} (skipped)";
                return null;
            }

            string oldPrimitives = DescribePrimitives(dList);
            int oldVertexCount = dList.GetVertexCount();
            reader.Position = polygonOffset + 0x40;
            int headerVertexCount = reader.ReadInt32();
            int headerFaceCount = reader.ReadInt32();

            if (!PrimitiveConverter.Convert(dList, target))
            {
                message = PrimitiveConverter.IsTriangleType(target) && !dList.groups.Any(x => x.primitives.Any(x => PrimitiveConverter.IsTriangleType(x.type))) ? $"{oldPrimitives} cannot be converted to {GetPrimitiveName(target)} (skipped)" : $"already {oldPrimitives}";
                return null;
            }

            byte[] data = dList.Write();
            message = $"{oldPrimitives} -> {DescribePrimitives(dList)}, {oldVertexCount} -> {dList.GetVertexCount()} vertices, {dlSize} -> {data.Length} bytes";

            return new PolygonConversion
            {
                name = polygonName,
                offset = polygonOffset,
                dlOffset = dlOffset,
                dlSize = dlSize,
                oldVertexCount = headerVertexCount,
                oldFaceCount = headerFaceCount,
                vertexCount = dList.GetVertexCount(),
                faceCount = dList.GetFaceCount(),
                data = data
            };
        }

        public static string GetPrimitiveName(PrimitiveType type)
        {
            switch (type)
            {
                case PrimitiveType.QUADS:
                    return "quads";

                case PrimitiveType.TRIANGLES:
                    return "triangles";

                case PrimitiveType.TRIANGLE_STRIP:
                    return "triangle strips";

                case PrimitiveType.TRIANGLE_FAN:
                    return "triangle fans";

                case PrimitiveType.LINES:
                    return "lines";

                case PrimitiveType.LINE_STRIP:
                    return "line strips";

                default:
                    return "points";
            }
        }

        public static string DescribePrimitives(DisplayList dList)
        {
            List<PrimitiveType> types = dList.groups.SelectMany(x => x.primitives.Select(x => x.type)).Distinct().OrderBy(x => x == PrimitiveType.TRIANGLES ? 1 : 0).ToList();
            return types.Count == 0 ? "empty" : string.Join(" + ", types.Select(GetPrimitiveName));
        }

        private bool IsPolygonSelected(string modelName, string polygonName)
        {
            if (this.arrayFilter.Count == 0)
                return true;

            foreach (string filter in this.arrayFilter)
            {
                string text = filter;
                string lower = filter.ToLowerInvariant();

                if (lower == "polygon" || lower == "polygons" || lower == "objs")
                    return true;

                if (lower.StartsWith("polygon:"))
                    text = filter.Substring("polygon:".Length);
                else if (filter.Contains(':'))
                    continue;

                if (text.Contains('/'))
                {
                    string[] parts = text.Split('/', 2);
                    if (WildcardMatches(parts[0], modelName) && WildcardMatches(parts[1], polygonName))
                        return true;
                }
                else if (WildcardMatches(text, polygonName))
                    return true;
            }

            return false;
        }

        private static bool WildcardMatches(string pattern, string text)
        {
            return Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");
        }

        private HashSet<(DataType, int)> FindDirectArrays(EndianReader reader, Dictionary<string, int> sections)
        {
            HashSet<(DataType, int)> direct = new HashSet<(DataType, int)>();

            if (!sections.TryGetValue("Objs", out int objOffs))
                return direct;

            foreach ((string pName, int pOffs) in ReadGroup(reader, objOffs))
            {
                Dictionary<byte, int>? registers = ReadRegisters(reader, pOffs);
                if (registers == null || !registers.TryGetValue(CP_VCD_LO, out int vcdLoOffs) || !registers.TryGetValue(CP_VCD_HI, out int vcdHiOffs))
                    continue;

                reader.Position = vcdLoOffs;
                uint vcdLow = reader.ReadUInt32();
                reader.Position = vcdHiOffs;
                uint vcdHigh = reader.ReadUInt32();

                reader.Position = pOffs + 0x48;
                short[] ids = new short[12];
                for (int i = 0; i < ids.Length; i++)
                {
                    ids[i] = reader.ReadInt16();
                }

                if (GetBits(vcdLow, 9, 2) == 1)
                    direct.Add((DataType.POSITION, ids[0]));

                if (GetBits(vcdLow, 11, 2) == 1)
                    direct.Add((DataType.NORMAL, ids[1]));

                if (GetBits(vcdLow, 13, 2) == 1)
                    direct.Add((DataType.COLOR, ids[2]));

                if (GetBits(vcdLow, 15, 2) == 1)
                    direct.Add((DataType.COLOR, ids[3]));

                for (int i = 0; i < 8; i++)
                {
                    if (GetBits(vcdHigh, 2 * i, 2) == 1)
                        direct.Add((DataType.UV, ids[4 + i]));
                }
            }

            return direct;
        }

        private FormatConversion? ConvertComponentArray(EndianReader reader, int arrayOffset, string arrayName, DataType type)
        {
            reader.Position = arrayOffset;
            int size = reader.ReadInt32();
            reader.Position = arrayOffset + 0x08;
            int dataOffs = reader.ReadInt32();

            (uint components, uint format, uint div) = ReadArrayFormat(reader, arrayOffset);
            reader.Position = arrayOffset + 0x1D;
            byte stride = reader.ReadByte();
            int count = reader.ReadUInt16();

            if (format > (uint)VertexFormat.F32)
            {
                Console.WriteLine($" {type} {arrayName}: unknown format {format} (skipped)");
                return null;
            }

            VertexFormat oldFormat = (VertexFormat)format;
            if (type == DataType.NORMAL && !IsNormalFormat(oldFormat))
            {
                Console.WriteLine($" {type} {arrayName}: {oldFormat} is not a valid normal format (skipped)");
                return null;
            }

            int oldDiv = type == DataType.NORMAL ? GetNormalDiv(oldFormat) : (int)div;
            int compCount = GetComponentCount(type, components);
            float[] values = new float[count * compCount];
            double oldScale = Math.Pow(2, oldDiv);

            for (int i = 0; i < count; i++)
            {
                reader.Position = arrayOffset + dataOffs + i * stride;
                for (int j = 0; j < compCount; j++)
                {
                    values[i * compCount + j] = ReadComponent(reader, oldFormat, oldScale);
                }
            }

            string label = $" {type} {arrayName}";
            VertexFormat newFormat;
            int newDiv;

            if (this.arrayEdits.TryGetValue(arrayOffset, out ArrayEdit? edit))
            {
                newFormat = (VertexFormat)edit.format;

                if (type == DataType.NORMAL)
                    newDiv = GetNormalDiv(newFormat);
                else if (newFormat == VertexFormat.F32)
                    newDiv = 0;
                else
                    newDiv = edit.div ?? FindBestDiv(values, newFormat);

                if (newFormat == oldFormat && newDiv == oldDiv)
                    return null;
            }
            else if (this.optimize != OptimizeMode.NONE)
            {
                (newFormat, newDiv) = PickOptimizedFormat(values, type, oldFormat, oldDiv);

                if (newFormat == oldFormat && newDiv == oldDiv)
                {
                    Console.WriteLine($"{label}: {DescribeFormat(oldFormat, oldDiv)} is already optimal");
                    return null;
                }
            }
            else if (type == DataType.NORMAL)
            {
                if (this.targetNormalFormat == null)
                    return null;

                newFormat = ConvertToVertexFormat(this.targetNormalFormat.Value);
                newDiv = GetNormalDiv(newFormat);

                if (this.divisor != null && newFormat != VertexFormat.F32)
                    Console.WriteLine($"{label}: normals use a fixed divisor of {1 << newDiv} (--divisor ignored)");

                if (newFormat == oldFormat)
                {
                    Console.WriteLine($"{label}: already {DescribeFormat(oldFormat, oldDiv)}");
                    return null;
                }
            }
            else
            {
                if (this.targetFormat == null)
                    return null;

                newFormat = this.targetFormat.Value;

                if (newFormat == VertexFormat.F32)
                    newDiv = 0;
                else if (this.divisor != null)
                    newDiv = GetBitIndex(this.divisor.Value);
                else
                    newDiv = FindBestDiv(values, newFormat);

                if (newFormat == oldFormat && newDiv == oldDiv)
                {
                    Console.WriteLine($"{label}: already {DescribeFormat(oldFormat, oldDiv)}");
                    return null;
                }
            }

            int newStride = compCount * GetFormatSize(newFormat);
            byte[] newData = new byte[count * newStride];
            EndianWriter dataWriter = new EndianWriter(new MemoryStream(newData), Endianness.BigEndian);
            double newScale = Math.Pow(2, newDiv);
            int clampValues = 0;
            double maxError = 0;

            for (int i = 0; i < values.Length; i++)
            {
                float decoded = WriteComponent(dataWriter, newFormat, newScale, values[i], ref clampValues);
                maxError = Math.Max(maxError, Math.Abs(decoded - values[i]));
            }

            string msg = $"{label}: {DescribeFormat(oldFormat, oldDiv)} -> {DescribeFormat(newFormat, newDiv)}, {count * stride} -> {newData.Length} bytes, ";
            msg += maxError == 0 ? "lossless" : $"max error {maxError:G4}";
            Console.WriteLine(msg);

            if (clampValues > 0)
                Console.WriteLine($" WARNING: {clampValues} value(s) were out of range for {newFormat} and got clamped");

            return new FormatConversion
            {
                type = type,
                name = arrayName,
                headerOffset = arrayOffset,
                dataOffset = dataOffs,
                maxSize = size - dataOffs,
                components = components,
                format = (uint)newFormat,
                div = (byte)newDiv,
                stride = (byte)newStride,
                data = newData
            };
        }

        private static float ReadComponent(EndianReader reader, VertexFormat format, double scale)
        {
            switch (format)
            {
                case VertexFormat.U8:
                    return (float)(reader.ReadByte() / scale);

                case VertexFormat.S8:
                    return (float)((sbyte)reader.ReadByte() / scale);

                case VertexFormat.U16:
                    return (float)(reader.ReadUInt16() / scale);

                case VertexFormat.S16:
                    return (float)(reader.ReadInt16() / scale);

                default:
                    return BitConverter.Int32BitsToSingle((int)reader.ReadUInt32());
            }
        }

        private static float WriteComponent(EndianWriter writer, VertexFormat format, double scale, float value, ref int clampValues)
        {
            if (format == VertexFormat.F32)
            {
                writer.WriteUInt32(BitConverter.SingleToUInt32Bits(value));
                return value;
            }

            (long minimum, long maximum) = GetFormatRange(format);
            long roundVal = (long)Math.Round(value * scale, MidpointRounding.AwayFromZero);

            if (roundVal < minimum || roundVal > maximum)
            {
                roundVal = Math.Clamp(roundVal, minimum, maximum);
                clampValues++;
            }

            if (GetFormatSize(format) == 1)
                writer.WriteByte((byte)roundVal);
            else
                writer.WriteInt16((short)roundVal);

            return (float)(roundVal / scale);
        }

        private static int FindBestDiv(float[] values, VertexFormat format)
        {
            (long minimum, long maximum) = GetFormatRange(format);
            float lowest = 0;
            float highest = 0;

            foreach (float value in values)
            {
                lowest = Math.Min(lowest, value);
                highest = Math.Max(highest, value);
            }

            for (int i = 31; i > 0; i--)
            {
                double scale = Math.Pow(2, i);
                if (Math.Round(lowest * scale, MidpointRounding.AwayFromZero) >= minimum && Math.Round(highest * scale, MidpointRounding.AwayFromZero) <= maximum)
                    return i;
            }

            return 0;
        }

        private static int? FindLosslessDiv(float[] values, VertexFormat format, DataType type)
        {
            if (format == VertexFormat.F32)
                return 0;

            (long minimum, long maximum) = GetFormatRange(format);
            int firstDiv = type == DataType.NORMAL ? GetNormalDiv(format) : 0;
            int lastDiv = type == DataType.NORMAL ? GetNormalDiv(format) : 31;

            for (int i = firstDiv; i <= lastDiv; i++)
            {
                double scale = Math.Pow(2, i);
                bool lossless = true;

                foreach (float value in values)
                {
                    double scaled = value * scale;
                    double round = Math.Round(scaled);

                    if (round != scaled || round < minimum || round > maximum || (float)(round / scale) != value)
                    {
                        lossless = false;
                        break;
                    }
                }

                if (lossless)
                    return i;
            }

            return null;
        }

        private static (VertexFormat format, int div) GetOptimizedFormat(float[] values, DataType type, VertexFormat oldFormat, int oldDiv)
        {
            (VertexFormat format, int div) best = (oldFormat, oldDiv);
            VertexFormat[] formats = type == DataType.NORMAL ? Array.ConvertAll(NORMAL_FORMATS, ConvertToVertexFormat) : VERTEX_FORMATS;

            foreach (VertexFormat vFormat in formats)
            {
                if (GetFormatSize(vFormat) >= GetFormatSize(oldFormat))
                    break;

                int? losslessDiv = FindLosslessDiv(values, vFormat, type);
                if (losslessDiv == null)
                    continue;

                best = (vFormat, losslessDiv.Value);
                break;
            }

            return best;
        }

        private (VertexFormat format, int div) PickOptimizedFormat(float[] values, DataType type, VertexFormat oldFormat, int oldDiv)
        {
            (VertexFormat format, int div) best = GetOptimizedFormat(values, type, oldFormat, oldDiv);

            if (this.optimize != OptimizeMode.NW4R || values.Length == 0)
                return best;

            float[] sorted = values.Distinct().OrderBy(x => x).ToArray();
            (VertexFormat format, int div)? auto;
            switch (type)
            {
                case DataType.POSITION:
                    auto = GetNw4rPositionFormat(sorted);
                    break;

                case DataType.UV:
                    auto = GetNw4rUvFormat(sorted);
                    break;

                default:
                    auto = GetNw4rNormalFormat(sorted);
                    break;
            }

            if (auto != null && GetFormatSize(auto.Value.format) < GetFormatSize(best.format))
                best = auto.Value;

            return best;
        }

        private static (VertexFormat, int)? GetNw4rPositionFormat(float[] sorted)
        {
            int? bits = GetIntegerBits(sorted);
            if (bits == null)
                return null;

            float minimumGap = GetMinimumGap(sorted);
            bool signed = sorted[0] < 0;
            int div8 = (signed ? 7 : 8) - bits.Value;
            int div16 = (signed ? 15 : 16) - bits.Value;

            if (div8 >= 0 && Math.Pow(0.5, div8) < minimumGap && GetRelativeError(sorted, div8) < 0.005)
                return (signed ? VertexFormat.S8 : VertexFormat.U8, div8);

            if (div16 >= 0 && Math.Pow(0.5, div16) < minimumGap)
                return (signed ? VertexFormat.S16 : VertexFormat.U16, div16);

            return null;
        }

        private static (VertexFormat, int)? GetNw4rUvFormat(float[] sorted)
        {
            int? bits = GetIntegerBits(sorted);
            if (bits == null)
                return null;

            bool signed = sorted[0] < 0;
            int div8 = (signed ? 7 : 8) - bits.Value;
            int div16 = (signed ? 15 : 16) - bits.Value;

            // < 1/512
            if (div8 >= 0 && GetAbsoluteError(sorted, div8) < 0.001953125)
                return (signed ? VertexFormat.S8 : VertexFormat.U8, div8);

            if (div16 >= 0 && GetAbsoluteError(sorted, div16) < 0.001953125)
                return (signed ? VertexFormat.S16 : VertexFormat.U16, div16);

            return null;
        }

        private static (VertexFormat, int)? GetNw4rNormalFormat(float[] sorted)
        {
            int? bits = GetIntegerBits(sorted);
            if (bits == null || bits.Value > 1)
                return null;

            // < 1/64
            if (GetMinimumGap(sorted) <= Math.Pow(0.5, 6))
                return (VertexFormat.S16, 14);

            return (VertexFormat.S8, 6);
        }

        private static int? GetIntegerBits(float[] sorted)
        {
            double maxAbs = Math.Max(Math.Abs((double)sorted[0]), Math.Abs((double)sorted[sorted.Length - 1]));
            double rounded = Math.Floor(maxAbs + 0.5);

            for (int bits = 0; bits <= 16; bits++)
            {
                if (rounded < (1 << bits))
                    return bits;
            }

            return null;
        }

        private static float GetMinimumGap(float[] sorted)
        {
            float minimumGap = 100000.0f;

            for (int i = 1; i < sorted.Length; i++)
            {
                float gap = Math.Abs(sorted[i] - sorted[i - 1]);
                if (gap < minimumGap && gap > 0.00048828125f) // < 1/2048
                    minimumGap = gap;
            }

            return minimumGap;
        }

        private static float GetRelativeError(float[] sorted, int div)
        {
            float scale = (float)Math.Pow(2, div);
            double maxError = 0;

            foreach (float value in sorted)
            {
                float scaled = Math.Abs(value * scale);
                double error = Math.Abs(scaled - Math.Floor(scaled + 0.5)) / scaled;

                if (maxError < error)
                    maxError = error;
            }

            return (float)maxError;
        }

        private static float GetAbsoluteError(float[] sorted, int div)
        {
            float scale = (float)Math.Pow(2, div);
            double maxError = 0;

            foreach (float value in sorted)
            {
                float scaled = Math.Abs(value * scale);
                double error = Math.Abs(scaled - Math.Floor(scaled + 0.5));

                if (maxError < error)
                    maxError = error;
            }

            return (float)(Math.Pow(0.5, div) * maxError);
        }

        private static string DescribeFormat(VertexFormat format, int div)
        {
            if (format == VertexFormat.F32)
                return format.ToString();

            return $"{format} (divisor {1L << div})";
        }

        private static int GetBitIndex(uint power)
        {
            int index = 0;
            while ((1u << index) != power)
            {
                index++;
            }

            return index;
        }

        private FormatConversion? ConvertColorArray(EndianReader reader, int arrayOffset, string arrayName)
        {
            reader.Position = arrayOffset;
            int size = reader.ReadInt32();
            reader.Position = arrayOffset + 0x08;
            int dataOffs = reader.ReadInt32();

            (uint components, uint format, uint unused) = ReadArrayFormat(reader, arrayOffset);
            reader.Position = arrayOffset + 0x1C;
            byte stride = reader.ReadByte();
            reader.Position = arrayOffset + 0x1E;
            int count = reader.ReadUInt16();

            if (format > (uint)ColorFormat.RGBA8)
            {
                Console.WriteLine($" {DataType.COLOR} {arrayName}: unknown format {format} (skipped)");
                return null;
            }

            ColorFormat oldFormat = (ColorFormat)format;

            byte[][] colors = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                reader.Position = arrayOffset + dataOffs + i * stride;
                colors[i] = ReadColor(reader, oldFormat);
            }

            string label = $" {DataType.COLOR} {arrayName}";
            ColorFormat newFormat;

            if (this.arrayEdits.TryGetValue(arrayOffset, out ArrayEdit? edit))
            {
                newFormat = (ColorFormat)edit.format;

                if (newFormat == oldFormat)
                    return null;
            }
            else if (this.optimize != OptimizeMode.NONE)
            {
                newFormat = oldFormat;
                foreach (ColorFormat candidate in COLOR_FORMATS)
                {
                    if (FormatSize(candidate) >= FormatSize(oldFormat))
                        break;

                    if (IsColorLossless(colors, candidate))
                    {
                        newFormat = candidate;
                        break;
                    }
                }

                if (newFormat == oldFormat)
                {
                    Console.WriteLine($"{label}: {oldFormat} is already optimal");
                    return null;
                }
            }
            else
            {
                if (this.targetColorFormat == null)
                    return null;

                newFormat = this.targetColorFormat.Value;

                if (newFormat == oldFormat)
                {
                    Console.WriteLine($"{label}: already {oldFormat}");
                    return null;
                }
            }

            int newStride = FormatSize(newFormat);
            byte[] newData = new byte[count * newStride];
            EndianWriter writer = new EndianWriter(new MemoryStream(newData), Endianness.BigEndian);
            int changed = 0;

            foreach (byte[] color in colors)
            {
                WriteColor(writer, newFormat, color);
                if (!ColorsEqual(DecodeColor(newFormat, color), color))
                    changed++;
            }

            string message = $"{label}: {oldFormat} -> {newFormat}, {count * stride} -> {newData.Length} bytes, ";
            message += changed == 0 ? "lossless" : $"{changed} of {count} color(s) lose precision";
            Console.WriteLine(message);

            bool hasAlpha = newFormat == ColorFormat.RGBA4 || newFormat == ColorFormat.RGBA6 || newFormat == ColorFormat.RGBA8;
            if (!hasAlpha && colors.Any(color => color[3] != 0xFF))
                Console.WriteLine($" WARNING: these colors use alpha, which {newFormat} cannot store (it becomes fully opaque)");

            return new FormatConversion
            {
                type = DataType.COLOR,
                name = arrayName,
                headerOffset = arrayOffset,
                dataOffset = dataOffs,
                maxSize = size - dataOffs,
                components = newFormat == ColorFormat.RGB565 || newFormat == ColorFormat.RGB8 || newFormat == ColorFormat.RGBX8 ? 0u : 1u,
                format = (uint)newFormat,
                stride = (byte)newStride,
                data = newData
            };
        }

        private static byte Expand(uint value, int bits)
        {
            return (byte)((value << (8 - bits)) | (value >> (2 * bits - 8)));
        }

        private static byte[] ReadColor(EndianReader reader, ColorFormat format)
        {
            switch (format)
            {
                case ColorFormat.RGB565:
                    {
                        uint value = reader.ReadUInt16();
                        return [Expand(value >> 11, 5), Expand((value >> 5) & 0x3F, 6), Expand(value & 0x1F, 5), 0xFF];
                    }

                case ColorFormat.RGB8:
                    return [reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), 0xFF];

                case ColorFormat.RGBX8:
                    {
                        byte[] color =
                        {
                            reader.ReadByte(),
                            reader.ReadByte(),
                            reader.ReadByte(),
                            0xFF
                        };

                        reader.ReadByte();
                        return color;
                    }

                case ColorFormat.RGBA4:
                    {
                        uint value = reader.ReadUInt16();
                        return [(byte)((value >> 12) * 17), (byte)(((value >> 8) & 0xF) * 17), (byte)(((value >> 4) & 0xF) * 17), (byte)((value & 0xF) * 17)];
                    }

                case ColorFormat.RGBA6:
                    {
                        uint value = (uint)(reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte());
                        return [Expand(value >> 18, 6), Expand((value >> 12) & 0x3F, 6), Expand((value >> 6) & 0x3F, 6), Expand(value & 0x3F, 6)];
                    }

                default:
                    return [reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()];
            }
        }

        private static void WriteColor(EndianWriter writer, ColorFormat format, byte[] color)
        {
            uint r = color[0];
            uint g = color[1];
            uint b = color[2];
            uint a = color[3];

            switch (format)
            {
                case ColorFormat.RGB565:
                    writer.WriteInt16((short)((r >> 3) << 11 | (g >> 2) << 5 | (b >> 3)));
                    break;

                case ColorFormat.RGB8:
                    writer.WriteByte((byte)r);
                    writer.WriteByte((byte)g);
                    writer.WriteByte((byte)b);
                    break;

                case ColorFormat.RGBX8:
                    writer.WriteByte((byte)r);
                    writer.WriteByte((byte)g);
                    writer.WriteByte((byte)b);
                    writer.WriteByte(0xFF);
                    break;

                case ColorFormat.RGBA4:
                    writer.WriteInt16((short)((r >> 4) << 12 | (g >> 4) << 8 | (b >> 4) << 4 | (a >> 4)));
                    break;

                case ColorFormat.RGBA6:
                    {
                        uint value = (r >> 2) << 18 | (g >> 2) << 12 | (b >> 2) << 6 | (a >> 2);
                        writer.WriteByte((byte)(value >> 16));
                        writer.WriteByte((byte)(value >> 8));
                        writer.WriteByte((byte)value);
                    }
                    break;

                default:
                    writer.WriteByte((byte)r);
                    writer.WriteByte((byte)g);
                    writer.WriteByte((byte)b);
                    writer.WriteByte((byte)a);
                    break;
            }
        }

        private static byte[] DecodeColor(ColorFormat format, byte[] color)
        {
            byte[] buffer = new byte[4];
            WriteColor(new EndianWriter(new MemoryStream(buffer), Endianness.BigEndian), format, color);
            return ReadColor(new EndianReader(buffer, Endianness.BigEndian), format);
        }

        private static bool ColorsEqual(byte[] a, byte[] b)
        {
            return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
        }

        private static bool IsColorLossless(byte[][] colors, ColorFormat format)
        {
            foreach (byte[] color in colors)
            {
                if (!ColorsEqual(DecodeColor(format, color), color))
                    return false;
            }

            return true;
        }

        private void ApplyConversions(List<FormatConversion> conversions, List<PolygonConversion> polygonConversions)
        {
            ResLayout layout = ResLayout.Read(_data);

            if (!layout.CanRebuild)
            {
                if (conversions.Count > 0 || polygonConversions.Count > 0)
                    Console.WriteLine($"INFO: the file cannot be rebuilt ({layout.errors[0]}), so vertex arrays and polygons cannot change size");

                WriteConversionsOverwrite(conversions);
                WritePolygonsOverwrite(polygonConversions);
                return;
            }

            List<ResResize> resizes = new List<ResResize>();
            HashSet<int> converted = new HashSet<int>();

            foreach (FormatConversion conversion in conversions)
            {
                resizes.Add(new ResResize
                {
                    blockStart = conversion.headerOffset,
                    data = conversion.data
                });

                converted.Add(conversion.headerOffset);
            }

            foreach (PolygonConversion conversion in polygonConversions)
            {
                resizes.Add(new ResResize
                {
                    blockStart = conversion.offset,
                    data = conversion.data
                });
            }

            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
            foreach (ResBlock block in layout.blocks)
            {
                if (!block.isVertexArray || converted.Contains(block.start))
                    continue;

                reader.Position = block.start + (block.group == "Colors" ? 0x1C : 0x1D);
                int stride = reader.ReadByte();
                reader.Position = block.start + 0x1E;
                int length = stride * reader.ReadUInt16();
                int newSize = ResRebuilder.GetNewSize(block, length);

                if (newSize >= block.end - block.start)
                    continue;

                Console.WriteLine($" {block.description}: removed {block.end - block.start - newSize} bytes of unused space");
                reader.Position = block.start + block.dataOffset;
                resizes.Add(new ResResize
                {
                    blockStart = block.start,
                    data = reader.ReadBytes(length)
                });

                changes++;
            }

            if (resizes.Count == 0)
                return;

            int oldLength = _data.Length;
            _data = ResRebuilder.Rebuild(_data, layout, resizes, out ResOffsetMap map);

            EndianWriter writer = new EndianWriter(new MemoryStream(_data), Endianness.BigEndian);
            foreach (FormatConversion conversion in conversions)
            {
                WriteArrayHeader(writer, map.Map(conversion.headerOffset), conversion);
                changes++;
            }

            foreach (PolygonConversion conversion in polygonConversions)
            {
                WritePolygonHeader(writer, map.Map(conversion.offset), map.Map(conversion.countsOffset), conversion, conversion.data.Length);
                changes++;
            }

            if (_data.Length != oldLength)
                Console.WriteLine($"File rebuilt: {oldLength} -> {_data.Length} bytes");
        }

        private void WriteConversionsOverwrite(List<FormatConversion> conversions)
        {
            EndianWriter writer = new EndianWriter(new MemoryStream(_data), Endianness.BigEndian);

            foreach (FormatConversion conversion in conversions)
            {
                if (conversion.data.Length > conversion.maxSize)
                {
                    Console.WriteLine($" {conversion.type} {conversion.name}: the converted data does not fit (skipped)");
                    continue;
                }

                int dataStart = conversion.headerOffset + conversion.dataOffset;
                Array.Clear(_data, dataStart, conversion.maxSize);
                Array.Copy(conversion.data, 0, _data, dataStart, conversion.data.Length);
                WriteArrayHeader(writer, conversion.headerOffset, conversion);
                changes++;
            }
        }

        private static void WriteArrayHeader(EndianWriter writer, int headerOffset, FormatConversion conversion)
        {
            writer.Position = headerOffset + 0x14;
            writer.WriteUInt32(conversion.components);
            writer.WriteUInt32(conversion.format);

            if (conversion.type == DataType.COLOR)
            {
                writer.WriteByte(conversion.stride);
                return;
            }

            writer.WriteByte(conversion.div);
            writer.WriteByte(conversion.stride);
        }
        private void WritePolygonsOverwrite(List<PolygonConversion> conversions)
        {
            EndianWriter writer = new EndianWriter(new MemoryStream(_data), Endianness.BigEndian);

            foreach (PolygonConversion conversion in conversions)
            {
                if (conversion.data.Length > conversion.dlSize)
                {
                    Console.WriteLine($" {conversion.name}: the converted primitives do not fit (skipped)");
                    continue;
                }

                Array.Clear(_data, conversion.dlOffset, conversion.dlSize);
                Array.Copy(conversion.data, 0, _data, conversion.dlOffset, conversion.data.Length);
                WritePolygonHeader(writer, conversion.offset, conversion.countsOffset, conversion, conversion.dlSize);
                changes++;
            }
        }

        private void WritePolygonHeader(EndianWriter writer, int polygonOffset, int countsOffset, PolygonConversion conversion, int bufferSize)
        {
            writer.Position = polygonOffset + 0x24;
            writer.WriteInt32(bufferSize);
            writer.WriteInt32(conversion.data.Length);
            writer.Position = polygonOffset + 0x40;
            writer.WriteInt32(conversion.vertexCount);
            writer.WriteInt32(conversion.faceCount);

            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
            reader.Position = countsOffset;
            int totalVertices = reader.ReadInt32();
            int totalFaces = reader.ReadInt32();
            writer.Position = countsOffset;
            writer.WriteInt32(totalVertices + conversion.vertexCount - conversion.oldVertexCount);
            writer.WriteInt32(totalFaces + conversion.faceCount - conversion.oldFaceCount);
        }

        public List<ModelInfo> GetModels()
        {
            List<ModelInfo> models = new List<ModelInfo>();
            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);

            foreach ((string modelName, int modelOffset) in FindModels(reader))
            {
                Dictionary<string, int>? sections = ReadSections(reader, modelOffset, modelName, out uint version);
                if (sections == null)
                    continue;

                ModelInfo model = new ModelInfo
                {
                    name = modelName,
                    offset = modelOffset,
                    version = version
                };

                Dictionary<(DataType, int), ArrayInfo> arrays = new Dictionary<(DataType, int), ArrayInfo>();

                foreach ((string section, DataType type) in ARRAY_SECTIONS)
                {
                    if (!sections.TryGetValue(section, out int groupOffs))
                        continue;

                    foreach ((string aName, int aOffs) in ReadGroup(reader, groupOffs))
                    {
                        ArrayInfo array = ReadArrayInfo(reader, aOffs, aName, type);
                        reader.Position = aOffs + 0x10;
                        arrays[(type, reader.ReadInt32())] = array;
                        model.arrays.Add(array);
                    }
                }

                if (sections.TryGetValue("Objs", out int objOffs))
                {
                    foreach ((string pName, int pOffs) in ReadGroup(reader, objOffs))
                    {
                        model.polygons.Add(ReadPolygonInfo(reader, pOffs, pName, arrays));
                    }
                }

                models.Add(model);
            }

            return models;
        }

        private ArrayInfo ReadArrayInfo(EndianReader reader, int offset, string name, DataType type)
        {
            reader.Position = offset;
            int size = reader.ReadInt32();
            (uint components, uint format, uint div) = ReadArrayFormat(reader, offset);
            reader.Position = offset + 0x1C;
            byte info0x1c = reader.ReadByte();
            byte info0x1d = reader.ReadByte();
            int count = reader.ReadUInt16();

            return new ArrayInfo
            {
                type = type,
                name = name,
                offset = offset,
                components = components,
                format = format,
                div = type == DataType.COLOR ? 0 : type == DataType.NORMAL && format <= (uint)VertexFormat.F32 ? GetNormalDiv((VertexFormat)format) : (int)div,
                stride = type == DataType.COLOR ? info0x1c : info0x1d,
                count = count,
                size = size
            };
        }

        private PolygonInfo ReadPolygonInfo(EndianReader reader, int offset, string name, Dictionary<(DataType, int), ArrayInfo> arrays)
        {
            PolygonInfo polygon = new PolygonInfo
            {
                name = name,
                offset = offset
            };

            reader.Position = offset + 0x40;
            polygon.vertexCount = reader.ReadInt32();
            polygon.faceCount = reader.ReadInt32();

            DisplayList? dList = ReadDisplayList(reader, offset, out _, out _, out string error);
            polygon.primitives = dList != null ? DescribePrimitives(dList) : error;

            Dictionary<byte, int>? registers = ReadRegisters(reader, offset);
            if (registers == null || !registers.TryGetValue(CP_VCD_LO, out int vcdLoOffs) || !registers.TryGetValue(CP_VCD_HI, out int vcdHiOffs))
                return polygon;

            reader.Position = vcdLoOffs;
            uint vcdLow = reader.ReadUInt32();
            reader.Position = vcdHiOffs;
            uint vcdHigh = reader.ReadUInt32();

            reader.Position = offset + 0x48;
            short[] ids = new short[12];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = reader.ReadInt16();
            }

            void Add(string label, bool used, DataType type, short id)
            {
                if (used && arrays.TryGetValue((type, id), out ArrayInfo? array))
                    polygon.attributes.Add((label, array));
            }

            Add("Position", HasAttribute(vcdLow, 9), DataType.POSITION, ids[0]);
            Add("Normal", HasAttribute(vcdLow, 11), DataType.NORMAL, ids[1]);
            Add("Color0", HasAttribute(vcdLow, 13), DataType.COLOR, ids[2]);
            Add("Color1", HasAttribute(vcdLow, 15), DataType.COLOR, ids[3]);

            for (int i = 0; i < 8; i++)
            {
                Add($"UV{i}", HasAttribute(vcdHigh, 2 * i), DataType.UV, ids[4 + i]);
            }

            return polygon;
        }

        public string PreviewArray(ArrayInfo array, ArrayEdit edit)
        {
            Dictionary<int, ArrayEdit> savedEdits = this.arrayEdits;
            TextWriter savedOut = Console.Out;
            StringWriter output = new StringWriter();

            try
            {
                this.arrayEdits = new Dictionary<int, ArrayEdit>
                {
                    [array.offset] = edit
                };

                Console.SetOut(output);
                EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
                FormatConversion? conversion = array.type == DataType.COLOR ? ConvertColorArray(reader, array.offset, array.name) : ConvertComponentArray(reader, array.offset, array.name, array.type);

                if (conversion == null)
                    return "no change";
            }
            finally
            {
                this.arrayEdits = savedEdits;
                Console.SetOut(savedOut);
            }

            string text = string.Join(" ", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            int separator = text.IndexOf(": ");
            return separator >= 0 ? text.Substring(separator + 2) : text;
        }

        public ArrayEdit? GetOptimizedEdit(ArrayInfo array, OptimizeMode mode)
        {
            OptimizeMode savedMode = this.optimize;
            Dictionary<int, ArrayEdit> savedEdits = this.arrayEdits;
            TextWriter savedOut = Console.Out;

            try
            {
                this.optimize = mode;
                this.arrayEdits = new Dictionary<int, ArrayEdit>();
                Console.SetOut(TextWriter.Null);

                EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
                FormatConversion? conversion = array.type == DataType.COLOR ? ConvertColorArray(reader, array.offset, array.name) : ConvertComponentArray(reader, array.offset, array.name, array.type);
                if (conversion == null)
                    return null;

                return new ArrayEdit
                {
                    format = conversion.format,
                    div = conversion.type == DataType.COLOR ? null : conversion.div
                };
            }
            finally
            {
                this.optimize = savedMode;
                this.arrayEdits = savedEdits;
                Console.SetOut(savedOut);
            }
        }

        public int GetBestDivisor(ArrayInfo array, VertexFormat format)
        {
            if (array.type == DataType.NORMAL)
                return GetNormalDiv(format);

            if (format == VertexFormat.F32 || array.format > (uint)VertexFormat.F32)
                return 0;

            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
            reader.Position = array.offset + 0x08;
            int dataOffs = reader.ReadInt32();
            int compCount = GetComponentCount(array.type, array.components);
            float[] values = new float[array.count * compCount];
            double scale = Math.Pow(2, array.div);

            for (int i = 0; i < array.count; i++)
            {
                reader.Position = array.offset + dataOffs + i * array.stride;
                for (int j = 0; j < compCount; j++)
                {
                    values[i * compCount + j] = ReadComponent(reader, (VertexFormat)array.format, scale);
                }
            }

            return FindBestDiv(values, format);
        }

        public bool HasShapeAnimation(ArrayInfo array)
        {
            return HasShapeAnimation(new EndianReader(_data, Endianness.BigEndian), array.type, array.name);
        }

        public string PreviewPolygon(PolygonInfo polygon, PrimitiveType target)
        {
            EndianReader reader = new EndianReader(_data, Endianness.BigEndian);
            ConvertPolygon(reader, polygon.offset, polygon.name, target, out string message);
            return message;
        }

        public static string DescribeArrayFormat(DataType type, uint format, int div)
        {
            if (type == DataType.COLOR)
                return format <= (uint)ColorFormat.RGBA8 ? ((ColorFormat)format).ToString() : $"Unknown ({format})";

            if (format > (uint)VertexFormat.F32)
                return $"Unknown ({format})";

            return DescribeFormat((VertexFormat)format, div);
        }
    }
}

