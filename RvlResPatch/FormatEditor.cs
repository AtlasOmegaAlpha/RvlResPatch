using System.Runtime.InteropServices;
using System.Text;

namespace RvlResPatch
{
    public interface ITerminal
    {
        int Width { get; }
        int Height { get; }
        void Enter();
        void Leave();
        void Write(string text);
        ConsoleKeyInfo ReadKey();
    }

    public class ConsoleTerminal : ITerminal
    {
        const int STD_OUTPUT_HANDLE = -11;
        const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        bool _treatControlC;
        Encoding? _encoding;

        public int Width
        {
            get
            {
                return Math.Max(40, Console.WindowWidth);
            }
        }

        public int Height
        {
            get
            {
                return Math.Max(10, Console.WindowHeight);
            }
        }

        public void Enter()
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (GetConsoleMode(handle, out uint mode))
                    SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            }

            _encoding = Console.OutputEncoding;
            Console.OutputEncoding = Encoding.UTF8;
            _treatControlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            Console.Write("\x1b[?1049h\x1b[?25l\x1b[H\x1b[2J");
        }

        public void Leave()
        {
            Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
            Console.TreatControlCAsInput = _treatControlC;

            if (_encoding != null)
                Console.OutputEncoding = _encoding;
        }

        public void Write(string text)
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }

        public ConsoleKeyInfo ReadKey()
        {
            return Console.ReadKey(true);
        }
    }

    public static class FormatEditor
    {
        public static int Run(string file, string destFile)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine("ERROR: format edit mode needs an interactive console");
                return 1;
            }

            FormatEditorSession session = new FormatEditorSession(file, File.ReadAllBytes(file), destFile, new ConsoleTerminal());
            return session.Run();
        }
    }

    public class FormatEditorSession
    {
        enum RowType
        {
            FILE,
            MODEL,
            POLYGON,
            ATTRIBUTE
        }

        class Row
        {
            public RowType type;
            public string key = "";
            public int depth;
            public ModelInfo? model;
            public PolygonInfo? polygon;
            public ArrayInfo? array;
            public string label = "";
        }

        static readonly uint?[] COMPONENTS =
        {
            null,
            (uint)VertexFormat.F32,
            (uint)VertexFormat.S16,
            (uint)VertexFormat.U16,
            (uint)VertexFormat.S8,
            (uint)VertexFormat.U8
        };

        static readonly uint?[] NORMALS =
        {
            null,
            (uint)VertexFormat.F32,
            (uint)VertexFormat.S16,
            (uint)VertexFormat.S8
        };

        static readonly uint?[] COLORS =
        {
            null,
            (uint)ColorFormat.RGBA8,
            (uint)ColorFormat.RGBX8,
            (uint)ColorFormat.RGBA6,
            (uint)ColorFormat.RGB8,
            (uint)ColorFormat.RGBA4,
            (uint)ColorFormat.RGB565
        };

        static readonly PrimitiveType?[] PRIMITIVES =
        {
            null,
            PrimitiveType.TRIANGLES,
            PrimitiveType.TRIANGLE_STRIP,
            PrimitiveType.TRIANGLE_FAN,
            PrimitiveType.QUADS,
            PrimitiveType.LINES,
            PrimitiveType.LINE_STRIP,
            PrimitiveType.POINTS
        };

        const string RESET = "\x1b[0m";
        const string INVERSE = "\x1b[7m";
        const string BOLD = "\x1b[1m";
        const string DIM = "\x1b[2m";
        const string YELLOW = "\x1b[33m";
        const string CYAN = "\x1b[36m";
        const string MOVE_CURSOR_HOME = "\x1b[H";
        const string ERASE_IN_LINE = "\x1b[K";

        readonly string _file;
        readonly string _destFile;
        readonly ITerminal _terminal;

        byte[] data;
        BrresPatch patch;
        List<ModelInfo> models = new List<ModelInfo>();
        List<Row> rows = new List<Row>();
        Dictionary<int, int> arrayUsers = new Dictionary<int, int>();
        HashSet<int> lockedArrays = new HashSet<int>();

        readonly HashSet<string> _expanded = new HashSet<string>();
        readonly HashSet<string> _marked = new HashSet<string>();
        readonly Dictionary<int, ArrayEdit> _arrayEdits = new Dictionary<int, ArrayEdit>();
        readonly Dictionary<int, PrimitiveType> _primitiveEdits = new Dictionary<int, PrimitiveType>();
        readonly Dictionary<string, string> _previewCache = new Dictionary<string, string>();

        int cursor;
        int scroll;
        string status = "";
        bool showHelp;
        bool isSaved;

        public FormatEditorSession(string file, byte[] data, string destFile, ITerminal terminal)
        {
            _file = file;
            _destFile = destFile;
            _terminal = terminal;
            this.data = data;
            this.patch = new BrresPatch(data);
            Load();

            foreach (ModelInfo model in models)
            {
                _expanded.Add("m:" + model.name);
            }

            BuildRows();
        }

        private bool HasChanges()
        {
            return _arrayEdits.Count > 0 || _primitiveEdits.Count > 0;
        }

        private void Load()
        {
            this.patch = new BrresPatch(data);
            this.models = patch.GetModels();
            this.arrayUsers.Clear();
            this.lockedArrays.Clear();
            _previewCache.Clear();

            foreach (ModelInfo model in this.models)
            {
                foreach (PolygonInfo polygon in model.polygons)
                {
                    foreach ((string label, ArrayInfo array) in polygon.attributes)
                    {
                        this.arrayUsers[array.offset] = this.arrayUsers.GetValueOrDefault(array.offset) + 1;
                    }
                }

                foreach (ArrayInfo array in model.arrays)
                {
                    if (this.patch.HasShapeAnimation(array))
                        this.lockedArrays.Add(array.offset);
                }
            }
        }

        private void BuildRows()
        {
            string? selected = this.cursor < this.rows.Count ? this.rows[this.cursor].key : null;
            this.rows.Clear();
            this.rows.Add(new Row
            {
                type = RowType.FILE,
                key = "file",
                label = Path.GetFileName(_file)
            });

            foreach (ModelInfo model in this.models)
            {
                string modelKey = "m:" + model.name;
                this.rows.Add(new Row
                {
                    type = RowType.MODEL,
                    key = modelKey,
                    depth = 1,
                    model = model,
                    label = $"{model.name} (MDL0 v{model.version})"
                });

                if (!_expanded.Contains(modelKey))
                    continue;

                foreach (PolygonInfo polygon in model.polygons)
                {
                    string polygonKey = $"p:{model.name}/{polygon.name}";
                    this.rows.Add(new Row
                    {
                        type = RowType.POLYGON,
                        key = polygonKey,
                        depth = 2,
                        model = model,
                        polygon = polygon,
                        label = polygon.name
                    });

                    if (!_expanded.Contains(polygonKey))
                        continue;

                    foreach ((string label, ArrayInfo array) in polygon.attributes)
                    {
                        this.rows.Add(new Row
                        {
                            type = RowType.ATTRIBUTE,
                            key = $"a:{model.name}/{polygon.name}/{label}",
                            depth = 3,
                            model = model,
                            polygon = polygon,
                            array = array,
                            label = label
                        });
                    }
                }
            }

            int index = this.rows.FindIndex(x => x.key == selected);
            this.cursor = Math.Clamp(index >= 0 ? index : this.cursor, 0, this.rows.Count - 1);
        }

        public int Run()
        {
            _terminal.Enter();

            try
            {
                while (true)
                {
                    Render();
                    ConsoleKeyInfo key = _terminal.ReadKey();

                    if (IsQuit(key))
                    {
                        if (!HasChanges() || ConfirmQuit())
                            break;

                        continue;
                    }

                    HandleKey(key);
                }
            }
            finally
            {
                _terminal.Leave();
            }

            Console.WriteLine(this.isSaved ? $"Saved -> {_destFile}" : "No changes saved.");
            return 0;
        }

        private static bool IsControl(ConsoleKeyInfo key, ConsoleKey letter)
        {
            int control = letter - ConsoleKey.A + 1;
            return (key.Key == letter && (key.Modifiers & ConsoleModifiers.Control) != 0) || key.KeyChar == (char)control;
        }

        private static bool IsQuit(ConsoleKeyInfo key)
        {
            return IsControl(key, ConsoleKey.X) || IsControl(key, ConsoleKey.Q) || key.Key == ConsoleKey.F10;
        }

        private bool ConfirmQuit()
        {
            while (true)
            {
                this.status = "Save changes before leaving? (Y)es, (N)o, (C)ancel";
                Render();
                ConsoleKeyInfo key = _terminal.ReadKey();

                switch (char.ToLowerInvariant(key.KeyChar))
                {
                    case 'y':
                        Save();
                        return !HasChanges();

                    case 'n':
                        return true;

                    case 'c':
                        status = "";
                        return false;
                }

                if (key.Key == ConsoleKey.Escape || IsControl(key, ConsoleKey.C))
                {
                    this.status = "";
                    return false;
                }
            }
        }

        private void HandleKey(ConsoleKeyInfo key)
        {
            int pageSize = Math.Max(1, GetListHeight() - 1);

            if (this.showHelp)
            {
                this.showHelp = false;
                return;
            }

            if (IsControl(key, ConsoleKey.S) || IsControl(key, ConsoleKey.O) || key.Key == ConsoleKey.F2)
            {
                Save();
                return;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    this.cursor = Math.Max(0, this.cursor - 1);
                    return;

                case ConsoleKey.DownArrow:
                    this.cursor = Math.Min(this.rows.Count - 1, this.cursor + 1);
                    return;

                case ConsoleKey.PageUp:
                    this.cursor = Math.Max(0, this.cursor - pageSize);
                    return;

                case ConsoleKey.PageDown:
                    this.cursor = Math.Min(this.rows.Count - 1, this.cursor + pageSize);
                    return;

                case ConsoleKey.Home:
                    this.cursor = 0;
                    return;

                case ConsoleKey.End:
                    this.cursor = this.rows.Count - 1;
                    return;

                case ConsoleKey.LeftArrow:
                    ChangeValue(-1);
                    return;

                case ConsoleKey.RightArrow:
                    ChangeValue(1);
                    return;

                case ConsoleKey.Enter:
                    ToggleExpand();
                    return;

                case ConsoleKey.Spacebar:
                    ToggleMark();
                    return;

                case ConsoleKey.Delete:
                case ConsoleKey.Backspace:
                    Reset();
                    return;

                case ConsoleKey.F1:
                    this.showHelp = true;
                    return;
            }

            switch (char.ToLowerInvariant(key.KeyChar))
            {
                case '[':
                case '-':
                    ChangeDivisor(-1);
                    return;

                case ']':
                case '+':
                    ChangeDivisor(1);
                    return;

                case 'a':
                    AutoDivisor();
                    return;

                case 'o':
                    Optimize(OptimizeMode.NW4R);
                    return;

                case 'l':
                    Optimize(OptimizeMode.LOSSLESS);
                    return;

                case 'r':
                    Reset();
                    return;

                case 'e':
                    ExpandAll(true);
                    return;

                case 'c':
                    ExpandAll(false);
                    return;

                case '?':
                case 'h':
                    showHelp = true;
                    return;
            }
        }

        private Row Current
        {
            get
            {
                return this.rows[cursor];
            }
        }

        private IEnumerable<PolygonInfo> GetPolygons(Row row)
        {
            switch (row.type)
            {
                case RowType.FILE:
                    return this.models.SelectMany(x => x.polygons);

                case RowType.MODEL:
                    return row.model!.polygons;

                case RowType.POLYGON:
                    return [row.polygon!];

                default:
                    return [];
            }
        }

        private IEnumerable<ArrayInfo> GetArrays(Row row)
        {
            switch (row.type)
            {
                case RowType.FILE:
                    return this.models.SelectMany(x => x.arrays);

                case RowType.MODEL:
                    return row.model!.arrays;

                case RowType.POLYGON:
                    return row.polygon!.attributes.Select(x => x.array).Distinct();

                default:
                    return [row.array!];
            }
        }

        private List<Row> GetTargets()
        {
            Row current = this.Current;

            if (!_marked.Contains(current.key))
                return [current];

            List<Row> targets = new List<Row>();
            foreach (string key in _marked)
            {
                Row? row = FindRow(key);
                if (row != null && row.type == current.type && (row.type != RowType.ATTRIBUTE || row.array!.type == current.array!.type))
                    targets.Add(row);
            }

            return targets;
        }

        private Row? FindRow(string key)
        {
            foreach (ModelInfo model in this.models)
            {
                foreach (PolygonInfo polygon in model.polygons)
                {
                    if (key == $"p:{model.name}/{polygon.name}")
                    {
                        return new Row
                        {
                            type = RowType.POLYGON,
                            key = key,
                            model = model,
                            polygon = polygon
                        };
                    }

                    foreach ((string label, ArrayInfo array) in polygon.attributes)
                    {
                        if (key == $"a:{model.name}/{polygon.name}/{label}")
                        {
                            return new Row
                            {
                                type = RowType.ATTRIBUTE,
                                key = key,
                                model = model,
                                polygon = polygon,
                                array = array,
                                label = label
                            };
                        }
                    }
                }
            }

            return null;
        }

        private static uint?[] GetOptions(ArrayInfo array)
        {
            return array.type == DataType.COLOR ? COLORS : array.type == DataType.NORMAL ? NORMALS : COMPONENTS;
        }

        private void ChangeValue(int direction)
        {
            Row current = this.Current;

            if (current.type == RowType.ATTRIBUTE)
            {
                uint?[] choices = GetOptions(current.array!);
                uint? value = _arrayEdits.TryGetValue(current.array!.offset, out ArrayEdit? edit) ? edit.format : null;
                int index = Array.IndexOf(choices, value);
                uint? next = choices[(index + direction + choices.Length) % choices.Length];

                foreach (Row row in GetTargets())
                {
                    SetArrayFormat(row.array!, next);
                }

                return;
            }

            List<PolygonInfo> polygons = GetTargets().SelectMany(GetPolygons).Distinct().ToList();
            PrimitiveType? currentValue = GetCommonPrimitive(polygons, out bool mixed);
            int currentIndex = mixed ? 0 : Array.IndexOf(PRIMITIVES, currentValue);
            PrimitiveType? nextValue = PRIMITIVES[(currentIndex + direction + PRIMITIVES.Length) % PRIMITIVES.Length];

            foreach (PolygonInfo polygon in polygons)
            {
                if (nextValue == null)
                    _primitiveEdits.Remove(polygon.offset);
                else
                    _primitiveEdits[polygon.offset] = nextValue.Value;
            }

            this.status = "";
        }

        private void SetArrayFormat(ArrayInfo array, uint? format)
        {
            if (this.lockedArrays.Contains(array.offset))
            {
                this.status = $"{array.name} is used by a shape animation (SHP0) and has to keep its format";
                return;
            }

            if (format == null)
                _arrayEdits.Remove(array.offset);
            else
            {
                _arrayEdits[array.offset] = new ArrayEdit
                {
                    format = format.Value
                };
            }

            this.status = "";
        }

        private PrimitiveType? GetCommonPrimitive(List<PolygonInfo> polygons, out bool mixed)
        {
            List<PrimitiveType?> values = polygons.Select(x => _primitiveEdits.TryGetValue(x.offset, out PrimitiveType type) ? (PrimitiveType?)type : null).Distinct().ToList();
            mixed = values.Count > 1;
            return values.Count == 1 ? values[0] : null;
        }

        private bool UsesDivisor(ArrayInfo array, ArrayEdit? edit)
        {
            if (array.type == DataType.COLOR || array.type == DataType.NORMAL)
                return false;

            uint format = edit != null ? edit.format : array.format;
            return format < (uint)VertexFormat.F32;
        }

        private void ChangeDivisor(int direction)
        {
            foreach (Row row in GetTargets())
            {
                if (row.type != RowType.ATTRIBUTE)
                {
                    this.status = "Divisors are set on the vertex data rows";
                    return;
                }

                ArrayInfo array = row.array!;
                _arrayEdits.TryGetValue(array.offset, out ArrayEdit? edit);

                if (this.lockedArrays.Contains(array.offset) || !UsesDivisor(array, edit))
                {
                    this.status = "Only u8, s8, u16 and s16 positions/UVs use a divisor";
                    return;
                }

                edit ??= new ArrayEdit
                {
                    format = array.format,
                    div = array.div
                };

                int div = edit.div ?? this.patch.GetBestDivisor(array, (VertexFormat)edit.format);
                edit.div = Math.Clamp(div + direction, 0, 31);
                _arrayEdits[array.offset] = edit;
            }

            this.status = "";
        }

        private void AutoDivisor()
        {
            foreach (Row row in GetTargets().Where(x => x.type == RowType.ATTRIBUTE))
            {
                if (_arrayEdits.TryGetValue(row.array!.offset, out ArrayEdit? edit))
                    edit.div = null;
            }

            this.status = "";
        }

        private void Optimize(OptimizeMode mode)
        {
            int changed = 0;
            int locked = 0;

            foreach (ArrayInfo array in GetTargets().SelectMany(GetArrays).Distinct())
            {
                if (this.lockedArrays.Contains(array.offset))
                {
                    locked++;
                    continue;
                }

                ArrayEdit? edit = this.patch.GetOptimizedEdit(array, mode);
                if (edit == null)
                {
                    _arrayEdits.Remove(array.offset);
                    continue;
                }

                _arrayEdits[array.offset] = edit;
                changed++;
            }

            this.status = $"{(mode == OptimizeMode.NW4R ? "Optimize" : "Lossless optimize")}: {changed} array(s) changed" + (locked > 0 ? $", {locked} locked by shape (SHP0) animations" : "");
        }

        private void Reset()
        {
            foreach (Row row in GetTargets())
            {
                if (row.type == RowType.ATTRIBUTE)
                {
                    _arrayEdits.Remove(row.array!.offset);
                    continue;
                }

                foreach (PolygonInfo polygon in GetPolygons(row))
                {
                    _primitiveEdits.Remove(polygon.offset);
                }

                foreach (ArrayInfo array in GetArrays(row))
                {
                    _arrayEdits.Remove(array.offset);
                }
            }

            this.status = "";
        }

        private void ToggleExpand()
        {
            Row row = this.Current;
            if (row.type != RowType.MODEL && row.type != RowType.POLYGON)
                return;

            if (!_expanded.Remove(row.key))
                _expanded.Add(row.key);

            BuildRows();
        }

        private void ExpandAll(bool expand)
        {
            foreach (ModelInfo model in this.models)
            {
                foreach (PolygonInfo polygon in model.polygons)
                {
                    if (expand)
                        _expanded.Add($"p:{model.name}/{polygon.name}");
                    else
                        _expanded.Remove($"p:{model.name}/{polygon.name}");
                }
            }

            BuildRows();
        }

        private void ToggleMark()
        {
            Row row = this.Current;
            if (row.type == RowType.POLYGON || row.type == RowType.ATTRIBUTE)
            {
                if (!_marked.Remove(row.key))
                    _marked.Add(row.key);
            }

            this.cursor = Math.Min(this.rows.Count - 1, this.cursor + 1);
        }

        private void Save()
        {
            BrresPatch patch = new BrresPatch(this.data)
            {
                arrayEdits = new Dictionary<int, ArrayEdit>(_arrayEdits),
                primitiveEdits = new Dictionary<int, PrimitiveType>(_primitiveEdits)
            };

            TextWriter savedOut = Console.Out;
            StringWriter writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                patch.Run();
            }
            catch (Exception ex)
            {
                Console.SetOut(savedOut);
                this.status = $"ERROR: {ex.Message}";
                return;
            }
            finally
            {
                Console.SetOut(savedOut);
            }

            if (patch.changes <= 0)
            {
                _arrayEdits.Clear();
                _primitiveEdits.Clear();
                this.status = "Nothing to save";
                return;
            }

            try
            {
                File.WriteAllBytes(_destFile, patch.Data());
            }
            catch (Exception ex)
            {
                this.status = $"ERROR: {ex.Message}";
                return;
            }

            int oldLength = this.data.Length;
            this.data = patch.Data();
            _arrayEdits.Clear();
            _primitiveEdits.Clear();
            _marked.Clear();
            this.isSaved = true;
            Load();
            BuildRows();

            string skipped = string.Join(", ", writer.ToString().Split('\n').Where(x => x.Contains("(skipped)")).Select(x => x.Trim()).Take(2));
            this.status = $"Saved {patch.changes} change(s) to {Path.GetFileName(_destFile)} ({oldLength} -> {this.data.Length} bytes)" + (skipped.Length > 0 ? $"; {skipped}" : "");
        }

        private int GetListHeight()
        {
            return Math.Max(1, _terminal.Height - 4);
        }

        private void Render()
        {
            int width = _terminal.Width;
            int height = _terminal.Height;
            int listHeight = GetListHeight();
            StringBuilder frame = new StringBuilder();

            frame.Append(MOVE_CURSOR_HOME);
            string title = $" RvlResPatch format editor: {Path.GetFileName(_file)}" + (HasChanges() ? "  [modified]" : "");
            frame.Append(INVERSE + Fit(title, width) + RESET + ERASE_IN_LINE + "\r\n");

            if (this.showHelp)
            {
                string[] help = GetHelp();
                for (int i = 0; i < height - 2; i++)
                {
                    frame.Append(Fit(i < help.Length ? help[i] : "", width) + ERASE_IN_LINE + "\r\n");
                }

                frame.Append(INVERSE + Fit(" Press any key to go back", width) + RESET + ERASE_IN_LINE);
                _terminal.Write(frame.ToString());
                return;
            }

            if (this.cursor < this.scroll)
                this.scroll = this.cursor;

            if (this.cursor >= this.scroll + listHeight)
                this.scroll = this.cursor - listHeight + 1;

            int nameWidth = Math.Clamp(width / 2 - 4, 20, 60);
            int valueWidth = Math.Max(10, width - nameWidth - 2);

            for (int i = 0; i < listHeight; i++)
            {
                int index = this.scroll + i;
                if (index >= rows.Count)
                {
                    frame.Append(ERASE_IN_LINE + "\r\n");
                    continue;
                }

                Row row = rows[index];
                (string current, string pending, bool dim) = GetValues(row);
                string mark = _marked.Contains(row.key) ? "*" : " ";
                string expander = row.type == RowType.MODEL || row.type == RowType.POLYGON ? (_expanded.Contains(row.key) ? "[-] " : "[+] ") : "";
                string name = Fit(mark + new string(' ', row.depth * 2) + expander + row.label, nameWidth);
                string value = pending.Length > 0 ? $"{current} -> {pending}" : current;
                string text = name + " " + Fit(value, valueWidth);

                int pendingStart = name.Length + 2 + current.Length + 4;
                text = Fit(text, width);

                if (index == this.cursor)
                    frame.Append(INVERSE + text + RESET);
                else if (pending.Length > 0 && pendingStart < width)
                    frame.Append(text.Substring(0, pendingStart) + YELLOW + BOLD + text.Substring(pendingStart) + RESET);
                else if (dim)
                    frame.Append(DIM + Fit(text, width) + RESET);
                else if (mark == "*")
                    frame.Append(CYAN + Fit(text, width) + RESET);
                else
                    frame.Append(Fit(text, width));

                frame.Append(ERASE_IN_LINE + "\r\n");
            }

            frame.Append(Fit(" " + (this.status.Length > 0 ? this.status : GetInfo(this.Current)), width) + ERASE_IN_LINE + "\r\n");
            frame.Append(INVERSE + Fit(" ^S Save  ^X Exit  <- -> Change  [ ] Divisor  A Auto  O Optimize  L Lossless  R Reset  Space Mark  Enter Expand  ? Help", width) + RESET + ERASE_IN_LINE + "\r\n");
            frame.Append(Fit($" {rows.Count} rows, {_arrayEdits.Count} array and {_primitiveEdits.Count} polygon change(s) pending", width) + ERASE_IN_LINE);
            _terminal.Write(frame.ToString());
        }

        private (string current, string pending, bool dim) GetValues(Row row)
        {
            switch (row.type)
            {
                case RowType.ATTRIBUTE:
                    {
                        ArrayInfo array = row.array!;
                        string shared = this.arrayUsers.GetValueOrDefault(array.offset) > 1 ? " (shared)" : "";
                        string current = $"{array.name}{shared}: {DescribeFormat(array.type, array.format, array.div)}";

                        if (this.lockedArrays.Contains(array.offset))
                            return (current + " (locked: shape animation)", "", true);

                        if (!_arrayEdits.TryGetValue(array.offset, out ArrayEdit? edit))
                            return (current, "", false);

                        string pending = DescribeFormat(array.type, edit.format, edit.div ?? -1);
                        return (current, pending, false);
                    }

                case RowType.POLYGON:
                    {
                        PolygonInfo polygon = row.polygon!;
                        string current = $"{polygon.primitives} ({polygon.vertexCount} vertices)";
                        return (current, _primitiveEdits.TryGetValue(polygon.offset, out PrimitiveType type) ? BrresPatch.GetPrimitiveName(type) : "", false);
                    }

                default:
                    {
                        List<PolygonInfo> polygons = GetPolygons(row).ToList();
                        PrimitiveType? common = GetCommonPrimitive(polygons, out bool mixed);
                        string current = row.type == RowType.FILE ? $"{this.models.Count} model(s), {polygons.Count} polygon(s)" : $"{polygons.Count} polygon(s), {row.model!.arrays.Count} array(s)";
                        string pending = mixed ? "mixed" : common != null ? BrresPatch.GetPrimitiveName(common.Value) : "";
                        return (current, pending, false);
                    }
            }
        }

        private static string DescribeFormat(DataType type, uint format, int div)
        {
            if (type == DataType.COLOR)
                return BrresPatch.DescribeArrayFormat(type, format, 0);

            if (type == DataType.NORMAL)
                div = format == (uint)VertexFormat.S8 ? 6 : format == (uint)VertexFormat.S16 ? 14 : 0;

            if (format >= (uint)VertexFormat.F32 || type == DataType.NORMAL)
                return BrresPatch.DescribeArrayFormat(type, format, div < 0 ? 0 : div).Replace("divisor ", "/");

            return div < 0 ? $"{(VertexFormat)format} (auto)" : $"{(VertexFormat)format} (/{1L << div})";
        }

        private string GetInfo(Row row)
        {
            switch (row.type)
            {
                case RowType.ATTRIBUTE:
                    {
                        ArrayInfo array = row.array!;
                        if (!_arrayEdits.TryGetValue(array.offset, out ArrayEdit? edit))
                            return $"{array.type} {array.name}: {array.count} entries, {array.size} bytes";

                        string key = $"a:{array.offset}:{edit.format}:{edit.div}";
                        if (!_previewCache.TryGetValue(key, out string? preview))
                        {
                            preview = this.patch.PreviewArray(array, edit);
                            _previewCache[key] = preview;
                        }

                        return preview;
                    }

                case RowType.POLYGON:
                    {
                        PolygonInfo polygon = row.polygon!;
                        if (!_primitiveEdits.TryGetValue(polygon.offset, out PrimitiveType type))
                            return $"{polygon.name}: {polygon.vertexCount} vertices, {polygon.faceCount} faces, {polygon.attributes.Count} attribute(s)";

                        string key = $"p:{polygon.offset}:{type}";
                        if (!_previewCache.TryGetValue(key, out string? preview))
                        {
                            preview = this.patch.PreviewPolygon(polygon, type);
                            _previewCache[key] = preview;
                        }

                        return preview;
                    }

                default:
                    return row.type == RowType.FILE ? $"Saving writes to {_destFile}" : $"{row.model!.name}: {row.model.polygons.Count} polygon(s)";
            }
        }

        private static string[] GetHelp()
        {
            return
            [
                "",
                "   Up/Down, PgUp/PgDn, Home/End    move",
                "   Enter                           expand or collapse a model or polygon",
                "   E/C                             expand/collapse every polygon",
                "   Left/Right                      change the selected value:",
                "                                       vertex data rows: format (keep, f32, s16, u16, s8, u8/colors)",
                "                                       polygon rows: primitives (keep, triangles, strips, fans, quads,",
                "                                       lines, line strips, points) on a model or the file row,",
                "                                       the primitives of every polygon they contain",
                "   [ ]/-/+                         lower/raise the divisor of u8, s8, u16 and s16 data",
                "   A                               use the most precise divisor again (default)",
                "   O                               optimize like nw4r_g3dcvtr (selected row and everything in it)",
                "   L                               optimize without losing precision",
                "   R/Delete                        undo the pending changes of the selected row",
                "   Space                           mark a polygon or vertex data row, changes on a marked row apply",
                "                                   to every marked row of the same type",
                "   Ctrl+S/Ctrl+O/F2                save",
                "   Ctrl+X/Ctrl+Q/F10               quit",
                "",
                "   * Vertex data shared by several polygons changes for all of them. Arrays used by shape",
                "   animations (SHP0) are locked, since the game blends them as F32/RGBA8 data."
            ];
        }

        private static string Fit(string text, int width)
        {
            if (width <= 0)
                return "";

            if (text.Length > width)
                return width > 1 ? text.Substring(0, width - 1) + "~" : text.Substring(0, width);

            return text.PadRight(width);
        }
    }
}
