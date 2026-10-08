namespace RvlResPatch
{
    public enum PointerType
    {
        OFFSET,
        SIZE,
        ABS_U16
    }

    public class ResPointer
    {
        public int location;
        public int baseOffset;
        public int target;
        public PointerType type;
        public string description = "";
    }

    public class ResBlock
    {
        public int start;
        public int end;
        public string description = "";
        public bool isVertexArray;
        public bool isPolygon;
        public int dataOffset;
        public string group = "";

        public bool IsResizable()
        {
            return this.isVertexArray || this.isPolygon;
        }
    }

    public class ResLayout
    {
        const int DICT_ENTRY_SIZE = 0x10;
        const int TEX_PLTT_INFO_SIZE = 0x34;

        static readonly string[] MDL0_GROUPS_V8 =
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

        static readonly string[] MDL0_GROUPS_V10 =
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

        readonly byte[] _data;
        readonly EndianReader _reader;
        readonly HashSet<int> _pointerLocations = new HashSet<int>();
        public List<ResBlock> blocks = new List<ResBlock>();
        public List<ResPointer> pointers = new List<ResPointer>();
        public List<string> errors = new List<string>();
        public int fileSize;

        public bool CanRebuild
        {
            get
            {
                return this.errors.Count == 0;
            }
        }

        private ResLayout(byte[] data)
        {
            _data = data;
            _reader = new EndianReader(data, Endianness.BigEndian);
        }

        public static ResLayout Read(byte[] data)
        {
            ResLayout layout = new ResLayout(data);

            try
            {
                layout.ReadFile();
            }
            catch (Exception ex)
            {
                layout.errors.Add($"could not parse the file ({ex.Message})");
            }

            if (layout.errors.Count == 0)
                layout.CheckBlocks();

            return layout;
        }

        private bool InFile(int offset, int length = 0)
        {
            return offset >= 0 && length >= 0 && (long)offset + length <= _data.Length;
        }

        private byte ReadByte(int offset)
        {
            _reader.Position = offset;
            return _reader.ReadByte();
        }

        private ushort ReadUInt16(int offset)
        {
            _reader.Position = offset;
            return _reader.ReadUInt16();
        }

        private int ReadInt32(int offset)
        {
            _reader.Position = offset;
            return _reader.ReadInt32();
        }

        private string ReadMagic(int offset)
        {
            if (!InFile(offset, 4))
                return "";

            _reader.Position = offset;
            return _reader.ReadString(4);
        }

        private void AddBlock(int start, int end, string description, bool isVertexArray = false, int dataOffset = 0, string group = "")
        {
            if (end < start || !InFile(start, end - start))
            {
                this.errors.Add($"{description}: invalid range 0x{start:X}-0x{end:X}");
                return;
            }

            this.blocks.Add(new ResBlock
            {
                start = start,
                end = end,
                description = description,
                isVertexArray = isVertexArray,
                dataOffset = dataOffset,
                group = group
            });
        }

        private int AddPointer(int location, int baseOffset, string description)
        {
            int value = ReadInt32(location);
            if (value == 0)
                return -1;

            int target = baseOffset + value;
            if (!InFile(target))
            {
                this.errors.Add($"{description}: offset points out of bounds (0x{target:X})");
                return -1;
            }

            Register(location, baseOffset, target, PointerType.OFFSET, description);
            return target;
        }

        private int AddSize(int location, int baseOffset, string description)
        {
            int end = baseOffset + ReadInt32(location);
            if (end < baseOffset || end > _data.Length)
            {
                this.errors.Add($"{description}: invalid size");
                return baseOffset;
            }

            Register(location, baseOffset, end, PointerType.SIZE, description);
            return end;
        }

        private void Register(int location, int baseOffset, int target, PointerType type, string description)
        {
            if (!_pointerLocations.Add(location))
                return;

            this.pointers.Add(new ResPointer
            {
                location = location,
                baseOffset = baseOffset,
                target = target,
                type = type,
                description = description
            });
        }

        private string AddString(int location, int baseOffset, string description)
        {
            int targetOffs = AddPointer(location, baseOffset, description);
            if (targetOffs < 0)
                return "";

            if (!InFile(targetOffs - 4, 4))
            {
                this.errors.Add($"{description}: invalid string offset");
                return "";
            }

            int length = ReadInt32(targetOffs - 4);
            if (length < 0 || !InFile(targetOffs, length + 1) || _data[targetOffs + length] != 0 || Array.IndexOf(_data, (byte)0, targetOffs, length) >= 0)
            {
                this.errors.Add($"{description}: offset does not point to a valid string (0x{targetOffs:X})");
                return "";
            }

            AddBlock(targetOffs - 4, targetOffs + length + 1, "string");
            _reader.Position = targetOffs;
            return _reader.ReadString(length);
        }

        private List<(string name, int offset)> ReadDict(int dict, string description, bool addBlock)
        {
            List<(string, int)> entries = new List<(string, int)>();
            int count = ReadInt32(dict + 4);
            int size = 8 + DICT_ENTRY_SIZE * (count + 1);

            if (count < 0 || !InFile(dict, size))
            {
                this.errors.Add($"{description}: invalid dictionary");
                return entries;
            }

            if (addBlock)
                AddBlock(dict, dict + size, description);

            for (int i = 1; i <= count; i++)
            {
                int entry = dict + 8 + DICT_ENTRY_SIZE * i;
                string name = AddString(entry + 8, dict, $"{description} entry {i} name");
                int targetOffs = AddPointer(entry + 12, dict, $"{description} '{name}'");

                if (targetOffs >= 0)
                    entries.Add((name, targetOffs));
            }

            return entries;
        }

        private void ReadFile()
        {
            string magic = ReadMagic(0);

            if (magic == "MDL0")
            {
                this.fileSize = _data.Length;
                ReadMDL0(0, "model");
                return;
            }

            if (magic != "bres")
                throw new InvalidDataException("not a BRRES or MDL0 file");

            if (ReadUInt16(4) != 0xFEFF)
                throw new InvalidDataException("unsupported byte order");

            this.fileSize = AddSize(0x08, 0, "BRRES file size");
            int root = ReadUInt16(0x0C);
            Register(0x0C, 0, root, PointerType.ABS_U16, "BRRES root offset");
            AddBlock(0, 0x10, "BRRES header");

            if (this.fileSize != _data.Length)
                this.errors.Add($"file size field (0x{this.fileSize:X}) does not match the file length (0x{_data.Length:X})");

            if (ReadMagic(root) != "root")
                throw new InvalidDataException("BRRES root block not found");

            int rootEnd = AddSize(root + 4, root, "BRRES root size");
            AddBlock(root, root + 8, "BRRES root header");

            foreach ((string folder, int folderDict) in ReadDict(root + 8, "root dictionary", true))
            {
                if (folderDict < root || folderDict >= rootEnd)
                    this.errors.Add($"folder '{folder}' is outside of the root block");

                foreach ((string name, int offset) in ReadDict(folderDict, $"folder '{folder}'", true))
                {
                    ReadSubFile(offset, name);
                }
            }
        }

        private void ReadSubFile(int offset, string name)
        {
            string magic = ReadMagic(offset);
            int version = InFile(offset, 0x10) ? ReadInt32(offset + 8) : -1;
            string description = $"{magic} '{name}'";

            switch (magic)
            {
                case "MDL0":
                    ReadMDL0(offset, name);
                    return;

                case "TEX0":
                    ReadTEX0(offset, description, version);
                    return;

                case "PLT0":
                    ReadPLT0(offset, description, version);
                    return;

                case "CHR0":
                case "CLR0":
                case "SRT0":
                case "VIS0":
                    ReadAnimationCommon(offset, description, magic, version);
                    return;

                case "PAT0":
                    ReadPAT0(offset, description, version);
                    return;

                case "SHP0":
                    ReadSHP0(offset, description, version);
                    return;

                case "SCN0":
                    ReadSCN0(offset, description, version);
                    return;

                default:
                    this.errors.Add($"Unknown NW4R resource '{name}' at 0x{offset:X}");
                    return;
            }
        }

        private int ReadSubFileHeader(int offset, string description)
        {
            int end = AddSize(offset + 4, offset, $"{description} size");
            AddPointer(offset + 0x0C, offset, $"{description} BRRES offset");
            AddBlock(offset, end, description);
            return end;
        }

        private void CheckInResource(int target, int start, int end, string description)
        {
            if (target >= 0 && (target < start || target > end))
                this.errors.Add($"{description} points outside of its resource (0x{target:X})");
        }

        private void ReadMDL0(int mdlOffs, string name)
        {
            string description = $"MDL0 '{name}'";
            int version = ReadInt32(mdlOffs + 8);
            string[] groups;
            int userDataField = 0;
            int nameField;
            int info;

            switch (version)
            {
                case 8:
                case 9:
                    groups = MDL0_GROUPS_V8;
                    nameField = 0x3C;
                    info = 0x40;
                    break;

                case 10:
                    groups = MDL0_GROUPS_V10;
                    nameField = 0x44;
                    info = 0x48;
                    break;

                case 11:
                    groups = MDL0_GROUPS_V10;
                    userDataField = 0x44;
                    nameField = 0x48;
                    info = 0x4C;
                    break;

                default:
                    this.errors.Add($"{description}: unsupported version ({version})");
                    return;
            }

            int end = AddSize(mdlOffs + 4, mdlOffs, $"{description} size");
            AddPointer(mdlOffs + 0x0C, mdlOffs, $"{description} BRRES offset");
            AddString(mdlOffs + nameField, mdlOffs, $"{description} name");

            info += mdlOffs;
            int infoEnd = info + ReadInt32(info);
            AddBlock(mdlOffs, infoEnd, $"{description} header");
            AddPointer(info + 0x04, info, $"{description} info model offset");
            AddString(info + 0x18, info, $"{description} original path");

            int mtxTableOffs = AddPointer(info + 0x24, info, $"{description} matrix table");
            if (mtxTableOffs >= 0)
                AddBlock(mtxTableOffs, mtxTableOffs + 4 + 4 * ReadInt32(mtxTableOffs), $"{description} matrix table");

            if (userDataField != 0)
            {
                int userDataOffs = AddPointer(mdlOffs + userDataField, mdlOffs, $"{description} user data");
                if (userDataOffs >= 0)
                    ReadUserData(userDataOffs, $"{description} user data", true);
            }

            Dictionary<string, int> sections = new Dictionary<string, int>();
            for (int i = 0; i < groups.Length; i++)
            {
                int groupOffs = AddPointer(mdlOffs + 0x10 + 4 * i, mdlOffs, $"{description} {groups[i]} dictionary");
                if (groupOffs >= 0)
                    sections[groups[i]] = groupOffs;
            }

            foreach ((string group, int dict) in sections)
            {
                foreach ((string entryName, int offset) in ReadDict(dict, $"{description} {group}", true))
                {
                    string entry = $"{description} {group} '{entryName}'";

                    switch (group)
                    {
                        case "Defs":
                            ReadMdlByteCode(offset, entry);
                            break;

                        case "Bones":
                            ReadBone(offset, entry);
                            break;

                        case "Verts":
                        case "Norms":
                        case "Colors":
                        case "UVs":
                            ReadVertexArray(offset, entry, group, true);
                            break;

                        case "FurVecs":
                        case "FurPos":
                            ReadVertexArray(offset, entry, group, false);
                            break;

                        case "Mats":
                            ReadMaterial(offset, entry, version);
                            break;

                        case "Shaders":
                            ReadCommonBlock(offset, entry);
                            break;

                        case "Objs":
                            ReadPolygon(offset, entry, version);
                            break;

                        case "Tex":
                        case "Plt":
                            ReadTexPlttLinks(offset, entry);
                            break;
                    }
                }
            }

            foreach (ResBlock block in this.blocks)
            {
                if (block.description.StartsWith(description) && (block.start < mdlOffs || block.end > end))
                    this.errors.Add($"{block.description} is outside of the model data");
            }
        }

        private int ReadCommonBlock(int offset, string description)
        {
            int end = AddSize(offset, offset, $"{description} size");
            AddPointer(offset + 4, offset, $"{description} model offset");
            AddBlock(offset, end, description);
            return end;
        }

        private void ReadMdlByteCode(int offset, string description)
        {
            int position = offset;

            while (true)
            {
                if (!InFile(position, 1))
                {
                    this.errors.Add($"{description}: byte code has no end");
                    return;
                }

                byte opCode = _data[position];

                switch (opCode)
                {
                    case 0x00:
                        position += 1;
                        continue;

                    case 0x01:
                        AddBlock(offset, position + 1, description);
                        return;

                    case 0x02:
                    case 0x05:
                    case 0x06:
                        position += 5;
                        continue;

                    case 0x03:
                        position += 4 + 6 * ReadByte(position + 3);
                        continue;

                    case 0x04:
                        position += 8;
                        continue;

                    default:
                        this.errors.Add($"{description}: unknown byte code 0x{opCode:X2} at 0x{position:X}");
                        return;
                }
            }
        }

        private void ReadBone(int offset, string description)
        {
            int end = ReadCommonBlock(offset, description);
            AddString(offset + 0x08, offset, $"{description} name");
            AddPointer(offset + 0x5C, offset, $"{description} parent");
            AddPointer(offset + 0x60, offset, $"{description} child");
            AddPointer(offset + 0x64, offset, $"{description} next sibling");
            AddPointer(offset + 0x68, offset, $"{description} prev sibling");

            int userDataOffs = AddPointer(offset + 0x6C, offset, $"{description} user data");
            if (userDataOffs >= 0)
                ReadUserData(userDataOffs, $"{description} user data", userDataOffs < offset || userDataOffs >= end);
        }

        private void ReadVertexArray(int offset, string description, string group, bool canResize)
        {
            int end = AddSize(offset, offset, $"{description} size");
            AddPointer(offset + 4, offset, $"{description} model offset");
            int dataOffs = AddPointer(offset + 8, offset, $"{description} data offset");
            AddString(offset + 0x0C, offset, $"{description} name");
            CheckInResource(dataOffs, offset, end, $"{description} data offset");
            AddBlock(offset, end, description, canResize, dataOffs - offset, group);
        }

        private void ReadMaterial(int offset, string description, int version)
        {
            int end = ReadCommonBlock(offset, description);
            AddString(offset + 0x08, offset, $"{description} name");
            AddPointer(offset + 0x28, offset, $"{description} shader");

            int numTextures = ReadInt32(offset + 0x2C);
            int texInfoOffs = AddPointer(offset + 0x30, offset, $"{description} texture info");
            int userDataField = version >= 10 ? 0x38 : 0x34;
            int displayListField = version >= 10 ? 0x3C : 0x38;

            if (version >= 10)
            {
                int furOffs = AddPointer(offset + 0x34, offset, $"{description} fur data");
                CheckInResource(furOffs, offset, end, $"{description} fur data");
            }

            int displayListOffs = AddPointer(offset + displayListField, offset, $"{description} display list");
            CheckInResource(displayListOffs, offset, end, $"{description} display list");

            int userDataOffs = AddPointer(offset + userDataField, offset, $"{description} user data");
            if (userDataOffs >= 0)
                ReadUserData(userDataOffs, $"{description} user data", userDataOffs < offset || userDataOffs >= end);

            if (texInfoOffs < 0)
                return;

            CheckInResource(texInfoOffs + TEX_PLTT_INFO_SIZE * numTextures, offset, end, $"{description} texture info");
            for (int i = 0; i < numTextures; i++)
            {
                int entry = texInfoOffs + TEX_PLTT_INFO_SIZE * i;
                AddString(entry, entry, $"{description} texture {i} name");
                AddString(entry + 4, entry, $"{description} texture {i} palette name");
            }
        }

        private void ReadPolygon(int offset, string description, int version)
        {
            int end = ReadCommonBlock(offset, description);
            AddString(offset + 0x38, offset, $"{description} name");

            int vtxSetupPrimitiveDlOffs = AddPointer(offset + 0x20, offset + 0x18, $"{description} vertex setup display list");
            int primitiveDlOffs = AddPointer(offset + 0x2C, offset + 0x24, $"{description} primitive display list");
            int mtxSetOffs = AddPointer(offset + (version >= 10 ? 0x64 : 0x60), offset, $"{description} matrix set");

            CheckInResource(vtxSetupPrimitiveDlOffs + ReadInt32(offset + 0x18), offset, end, $"{description} vertex setup display list");
            CheckInResource(primitiveDlOffs + ReadInt32(offset + 0x24), offset, end, $"{description} primitive display list");
            CheckInResource(mtxSetOffs, offset, end, $"{description} matrix set");

            ResBlock? block = this.blocks.LastOrDefault(x => x.start == offset && x.description == description);
            if (block != null && primitiveDlOffs > offset && primitiveDlOffs + ReadInt32(offset + 0x24) == end)
            {
                block.isPolygon = true;
                block.dataOffset = primitiveDlOffs - offset;
                block.group = "Objs";
            }
        }

        private void ReadTexPlttLinks(int offset, string description)
        {
            int count = ReadInt32(offset);
            AddBlock(offset, offset + 4 + 8 * count, description);

            for (int i = 0; i < count; i++)
            {
                AddPointer(offset + 4 + 8 * i, offset, $"{description} link {i} material");
                AddPointer(offset + 8 + 8 * i, offset, $"{description} link {i} texture info");
            }
        }

        private void ReadUserData(int offset, string description, bool addBlock)
        {
            if (addBlock)
                AddBlock(offset, offset + ReadInt32(offset), description);

            foreach ((string name, int entry) in ReadDict(offset + 4, description, false))
            {
                AddString(entry + 0x10, entry, $"{description} '{name}' name");

                int count = ReadInt32(entry + 0x08);
                int type = ReadInt32(entry + 0x0C);

                if (type == 2 && count > 1)
                    this.errors.Add($"{description} '{name}': string lists are unsupported");
                else if (type == 2 && count == 1)
                    AddString(entry + 0x04, entry, $"{description} '{name}' value");
            }
        }

        private void ReadTEX0(int offset, string description, int version)
        {
            int end = ReadSubFileHeader(offset, description);

            switch (version)
            {
                case 1:
                case 3:
                    AddString(offset + 0x14, offset, $"{description} name");
                    AddString(offset + 0x30, offset, $"{description} original path");
                    if (version == 3)
                        ReadSubFileUserData(offset, offset + 0x34, end, description);
                    break;

                case 2:
                    AddString(offset + 0x18, offset, $"{description} name");
                    AddString(offset + 0x1C, offset, $"{description} original path");
                    break;

                default:
                    this.errors.Add($"{description}: version {version} is unsupported");
                    break;
            }
        }

        private void ReadPLT0(int offset, string description, int version)
        {
            int end = ReadSubFileHeader(offset, description);

            if (version != 1 && version != 3)
            {
                this.errors.Add($"{description}: version {version} is unsupported");
                return;
            }

            AddString(offset + 0x14, offset, $"{description} name");
            AddString(offset + 0x20, offset, $"{description} original path");

            if (version == 3)
                ReadSubFileUserData(offset, offset + 0x24, end, description);
        }

        private void ReadSubFileUserData(int offset, int field, int end, string description)
        {
            int userDataOffs = AddPointer(field, offset, $"{description} user data");
            CheckInResource(userDataOffs, offset, end, $"{description} user data");

            if (userDataOffs >= 0)
                ReadUserData(userDataOffs, $"{description} user data", false);
        }

        private void ReadAnimationCommon(int offset, string description, string magic, int version)
        {
            int end = ReadSubFileHeader(offset, description);
            int newVersion = magic == "CHR0" || magic == "SRT0" ? 5 : 4;
            int oldVersion = newVersion - 1;
            bool hasUserData = version == newVersion;

            if (version != newVersion && version != oldVersion && !(magic == "CHR0" && version == 3))
            {
                this.errors.Add($"{description}: version {version} is unsupported");
                return;
            }

            int nameField = hasUserData ? 0x18 : 0x14;
            AddString(offset + nameField, offset, $"{description} name");
            AddString(offset + nameField + 4, offset, $"{description} original path");

            if (hasUserData)
                ReadSubFileUserData(offset, offset + 0x14, end, description);

            ReadAnimationNodes(offset, offset + 0x10, end, description, 0);
        }

        private void ReadAnimationNodes(int offset, int dictField, int end, string description, int nameOffset)
        {
            int dictOffs = AddPointer(dictField, offset, $"{description} dictionary");
            if (dictOffs < 0)
                return;

            CheckInResource(dictOffs, offset, end, $"{description} dictionary");

            foreach ((string name, int node) in ReadDict(dictOffs, description, false))
            {
                CheckInResource(node, offset, end, $"{description} '{name}'");
                AddString(node + nameOffset, node, $"{description} '{name}' name");
            }
        }

        private void ReadPAT0(int offset, string description, int version)
        {
            int end = ReadSubFileHeader(offset, description);

            if (version != 3 && version != 4)
            {
                this.errors.Add($"{description}: version {version} is unsupported");
                return;
            }

            int shift = version == 4 ? 4 : 0;
            AddString(offset + 0x24 + shift, offset, $"{description} name");
            AddString(offset + 0x28 + shift, offset, $"{description} original path");

            if (version == 4)
                ReadSubFileUserData(offset, offset + 0x24, end, description);

            ReadAnimationNodes(offset, offset + 0x10, end, description, 0);
            AddPointer(offset + 0x1C, offset, $"{description} texture pointer table");
            AddPointer(offset + 0x20, offset, $"{description} palette pointer table");

            ReadNameTable(offset, offset + 0x14, ReadUInt16(offset + 0x30 + shift), end, $"{description} texture names");
            ReadNameTable(offset, offset + 0x18, ReadUInt16(offset + 0x32 + shift), end, $"{description} palette names");
        }

        private void ReadNameTable(int offset, int field, int count, int end, string description)
        {
            int tableOffs = AddPointer(field, offset, description);
            if (tableOffs < 0)
                return;

            CheckInResource(tableOffs + 4 * count, offset, end, description);

            for (int i = 0; i < count; i++)
            {
                AddString(tableOffs + 4 * i, tableOffs, $"{description} {i}");
            }
        }

        private void ReadSHP0(int offset, string description, int version)
        {
            int end = ReadSubFileHeader(offset, description);

            if (version != 3 && version != 4)
            {
                this.errors.Add($"{description}: version {version} is unsupported");
                return;
            }

            int shift = version == 4 ? 4 : 0;
            AddString(offset + 0x18 + shift, offset, $"{description} name");
            AddString(offset + 0x1C + shift, offset, $"{description} original path");

            if (version == 4)
                ReadSubFileUserData(offset, offset + 0x18, end, description);

            ReadAnimationNodes(offset, offset + 0x10, end, description, 4);
            ReadNameTable(offset, offset + 0x14, ReadUInt16(offset + 0x22 + shift), end, $"{description} vertex set names");
        }

        private void ReadSCN0(int offset, string description, int version)
        {
            int end = ReadSubFileHeader(offset, description);

            if (version != 4 && version != 5)
            {
                this.errors.Add($"{description}: version {version} is unsupported");
                return;
            }

            int shift = version == 5 ? 4 : 0;
            AddString(offset + 0x28 + shift, offset, $"{description} name");
            AddString(offset + 0x2C + shift, offset, $"{description} original path");

            if (version == 5)
                ReadSubFileUserData(offset, offset + 0x28, end, description);

            for (int i = 0; i < 5; i++)
            {
                CheckInResource(AddPointer(offset + 0x14 + 4 * i, offset, $"{description} node array {i}"), offset, end, $"{description} node array {i}");
            }

            int dictOffs = AddPointer(offset + 0x10, offset, $"{description} dictionary");
            if (dictOffs < 0)
                return;

            foreach ((string groupName, int group) in ReadDict(dictOffs, description, false))
            {
                CheckInResource(group, offset, end, $"{description} '{groupName}'");

                foreach ((string name, int node) in ReadDict(group, $"{description} {groupName}", false))
                {
                    string nodeDescription = $"{description} {groupName} '{name}'";
                    CheckInResource(node, offset, end, nodeDescription);
                    AddPointer(node + 4, node, $"{nodeDescription} SCN0 offset");
                    AddString(node + 8, node, $"{nodeDescription} name");

                    switch (groupName)
                    {
                        case "LightSet(NW4R)":
                            AddString(node + 0x14, node, $"{nodeDescription} ambient light name");
                            int numLights = ReadByte(node + 0x1A);
                            for (int j = 0; j < numLights && j < 8; j++)
                            {
                                AddString(node + 0x1C + 4 * j, node + 0x1C, $"{nodeDescription} light {j} name");
                            }
                            break;

                        case "Lights(NW4R)":
                            ReadNodeUserData(node, 0x18, offset, end, nodeDescription);
                            break;

                        case "Cameras(NW4R)":
                            ReadNodeUserData(node, 0x1C, offset, end, nodeDescription);
                            break;
                    }
                }
            }
        }

        private void ReadNodeUserData(int node, int field, int offset, int end, string description)
        {
            int userDataOffs = AddPointer(node + field, node, $"{description} user data");
            CheckInResource(userDataOffs, offset, end, $"{description} user data");

            if (userDataOffs >= 0)
                ReadUserData(userDataOffs, $"{description} user data", false);
        }

        private void CheckBlocks()
        {
            List<ResBlock> unique = new List<ResBlock>();

            foreach (ResBlock block in this.blocks.OrderBy(x => x.start).ThenByDescending(x => x.end))
            {
                ResBlock? lastBlock = unique.Count > 0 ? unique[unique.Count - 1] : null;

                if (lastBlock != null && block.start == lastBlock.start && block.end == lastBlock.end)
                {
                    if (block.IsResizable() && !lastBlock.IsResizable())
                        unique[unique.Count - 1] = block;

                    continue;
                }

                if (lastBlock != null && block.start < lastBlock.end && block.end <= lastBlock.end && !block.IsResizable() && !lastBlock.IsResizable())
                    continue;

                if (lastBlock != null && block.start < lastBlock.end)
                {
                    this.errors.Add($"{block.description} (0x{block.start:X}-0x{block.end:X}) overlaps {lastBlock.description} (0x{lastBlock.start:X}-0x{lastBlock.end:X})");
                    continue;
                }

                unique.Add(block);
            }

            this.blocks = unique;
            int position = 0;

            foreach (ResBlock block in this.blocks.Append(new ResBlock
            {
                start = _data.Length,
                end = _data.Length
            }))
            {
                for (int i = position; i < block.start; i++)
                {
                    if (_data[i] == 0)
                        continue;

                    int gapEnd = i;
                    while (gapEnd < block.start && _data[gapEnd] != 0)
                        gapEnd++;

                    this.errors.Add($"unknown data at 0x{i:X}-0x{gapEnd:X}");
                    break;
                }

                position = Math.Max(position, block.end);
            }
        }
    }
}
