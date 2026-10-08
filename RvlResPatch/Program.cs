using RvlResPatch;

public static class Program
{
    public static int Main(string[] args)
    {
        List<string> inputFiles = new List<string>();
        string? outPath = null;
        bool overwrite = false;
        OptimizeMode optimizeMode = OptimizeMode.NONE;
        PrimitiveType? primitiveType = null;
        bool formatEdit = false;
        VertexFormat? vertexFormat = null;
        NormalFormat? normalFormat = null;
        ColorFormat? colorFormat = null;
        uint? divisor = null;
        List<string> arrayFilter = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o":
                case "--output":
                    if (++i >= args.Length)
                        return PrintUsage("missing value for -o");

                    outPath = args[i];
                    break;

                case "--overwrite":
                    overwrite = true;
                    break;

                case "--optimize":
                    optimizeMode = OptimizeMode.NW4R;
                    if (i + 1 < args.Length && (args[i + 1] == "nw4r" || args[i + 1] == "lossless"))
                        optimizeMode = args[++i] == "lossless" ? OptimizeMode.LOSSLESS : OptimizeMode.NW4R;

                    break;

                case "--primitive":
                    if (++i >= args.Length)
                        return PrintUsage("missing value for --primitive");

                    primitiveType = ParsePrimitive(args[i]);
                    if (primitiveType == null)
                        return PrintUsage($"unknown primitive type '{args[i]}'");

                    break;

                case "-e":
                case "--format-edit":
                    formatEdit = true;
                    break;

                case "--format":
                    if (++i >= args.Length)
                        return PrintUsage("missing value for --format");

                    if (!ParseFormat(args[i], out vertexFormat, out normalFormat, out colorFormat, out string? formatError))
                        return PrintUsage(formatError);

                    break;

                case "--divisor":
                    if (++i >= args.Length)
                        return PrintUsage("missing value for --divisor");

                    if (!uint.TryParse(args[i], out uint value) || value == 0 || (value & (value - 1)) != 0)
                        return PrintUsage("divisor must be a power of two");

                    divisor = value;
                    break;

                case "--only":
                    if (++i >= args.Length)
                        return PrintUsage("missing value for --only");

                    arrayFilter.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "-h":
                case "--help":
                case "/?":
                    return PrintUsage(null);

                default:
                    if (args[i].StartsWith("-"))
                        return PrintUsage($"unknown option '{args[i]}'");

                    inputFiles.Add(args[i]);
                    break;
            }
        }

        if (inputFiles.Count == 0)
            return PrintUsage(null);

        if (outPath != null && inputFiles.Count > 1)
            return PrintUsage("-o can only be used with a single file");

        if (outPath != null && overwrite)
            return PrintUsage("-o and --overwrite cannot be used together");

        bool hasFormat = vertexFormat != null || normalFormat != null || colorFormat != null;

        if (formatEdit && (hasFormat || optimizeMode != OptimizeMode.NONE || primitiveType != null || arrayFilter.Count > 0 || divisor != null))
            return PrintUsage("--format-edit cannot be used with other conversion options");

        if (formatEdit && inputFiles.Count != 1)
            return PrintUsage("--format-edit can only edit one file at a time");

        if (optimizeMode != OptimizeMode.NONE && hasFormat)
            return PrintUsage("--optimize and --format cannot be used together");

        if (divisor != null && (vertexFormat == null || vertexFormat == VertexFormat.F32))
            return PrintUsage("--divisor needs an integer format type (u8, s8, u16, s16)");

        if (arrayFilter.Count > 0 && !hasFormat && optimizeMode == OptimizeMode.NONE && primitiveType == null)
            return PrintUsage("--only needs --format, --optimize or --primitive");

        if (formatEdit)
        {
            string file = inputFiles[0];
            string destFile = overwrite ? file : outPath ?? GetDefaultOutput(file);
            return FormatEditor.Run(file, destFile);
        }

        int errors = 0;
        foreach (string file in inputFiles)
        {
            try
            {
                Console.WriteLine($"File: {file}");
                BrresPatch patch = new BrresPatch(File.ReadAllBytes(file))
                {
                    targetFormat = vertexFormat,
                    targetNormalFormat = normalFormat,
                    targetColorFormat = colorFormat,
                    divisor = divisor,
                    optimize = optimizeMode,
                    targetPrimitive = primitiveType,
                    arrayFilter = arrayFilter
                };

                patch.Run();

                if (patch.changes <= 0)
                {
                    Console.WriteLine("Nothing to patch.");
                    continue;
                }

                string destFile = overwrite ? file : outPath ?? GetDefaultOutput(file);
                File.WriteAllBytes(destFile, patch.Data());
                Console.WriteLine($"{patch.changes} change(s) applied -> {destFile}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                errors++;
            }
        }

        return errors == 0 ? 0 : 1;
    }

    private static string GetDefaultOutput(string file)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "", Path.GetFileNameWithoutExtension(file) + "_patched" + Path.GetExtension(file));
    }

    private static PrimitiveType? ParsePrimitive(string s)
    {
        switch (s.ToLowerInvariant().Replace("-", "").Replace("_", ""))
        {
            case "triangle":
            case "triangles":
            case "tris":
                return PrimitiveType.TRIANGLES;

            case "trianglestrip":
            case "trianglestrips":
            case "tristrip":
            case "tristrips":
            case "strip":
            case "strips":
                return PrimitiveType.TRIANGLE_STRIP;

            case "trianglefan":
            case "trianglefans":
            case "trifan":
            case "trifans":
            case "fan":
            case "fans":
                return PrimitiveType.TRIANGLE_FAN;

            case "quad":
            case "quads":
                return PrimitiveType.QUADS;

            case "line":
            case "lines":
                return PrimitiveType.LINES;

            case "linestrip":
            case "linestrips":
                return PrimitiveType.LINE_STRIP;

            case "point":
            case "points":
                return PrimitiveType.POINTS;

            default:
                return null;
        }
    }

    private static bool ParseFormat(string s, out VertexFormat? format, out NormalFormat? normalFormat, out ColorFormat? colorFormat, out string? error)
    {
        format = null;
        normalFormat = null;
        colorFormat = null;
        error = null;

        switch (s.ToLowerInvariant())
        {
            case "u8":
            case "byte":
            case "uint8":
                format = VertexFormat.U8;
                return true;

            case "s8":
            case "sbyte":
            case "int8":
                format = VertexFormat.S8;
                normalFormat = NormalFormat.S8;
                return true;

            case "u16":
            case "ushort":
            case "uint16":
                format = VertexFormat.U16;
                return true;

            case "s16":
            case "short":
            case "int16":
                format = VertexFormat.S16;
                normalFormat = NormalFormat.S16;
                return true;

            case "f32":
            case "float":
            case "float32":
            case "single":
                format = VertexFormat.F32;
                normalFormat = NormalFormat.F32;
                return true;
        }

        if (!char.IsDigit(s[0]) && Enum.TryParse(s, true, out ColorFormat color))
        {
            colorFormat = color;
            return true;
        }

        error = "unknown format " + s;
        return false;
    }

    private static int PrintUsage(string? error)
    {
        if (error != null)
            Console.Error.WriteLine("ERROR: " + error + "\n");

        Console.WriteLine(@"Usage: RvlResPatch [options] <file1.brres|file1.mdl0> [<file2.brres|file2.mdl0>, etc]
        Options:
        -o <path>           output path. Default: <name>_patched.<ext>
        --overwrite         overwrite the input file
        --format <type>     converts vertex data to the specified format
                                Supported formats per component:
                                positions/UVs: u8, s8, u16, s16, f32
                                normals:       s8, s16, f32
                                colors:        rgb565, rgb8, rgbx8, rgba4, rgba6, rgba8
        --divisor <value>   (used along with --format) use this divisor for fixed formats,
                            otherwise find the most optimal one by default. Must be a power of 2
        --optimize [mode]   convert vertex data to a smaller format:
                                nw4r (default): pick formats with the 'auto' quantization mode
                                                from nw4r_g3dcvtr
                                lossless:       only formats that keep every value the same
        --primitive <type>  convert the polygons' primitives:
                                triangles, tristrips, trifans, quads, lines, linestrips, points
        --only <list>       limit --format/--optimize/--primitive (comma separated):
                                arrays:   position, normal, color, uv, name or type:name
                                          (e.g. 'position:mario_body__mario_all_tx')
                                polygons: polygons, polygon:name or model/polygon ('*' = any)
                                          (e.g. 'polygon:polygon0', 'model_lod/*')
        -e, --format-edit   open the interactive editor to modify files (only one at a time)");

        return error == null ? 0 : 2;
    }
}
