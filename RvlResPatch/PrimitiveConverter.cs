namespace RvlResPatch
{
    abstract class ChainRule
    {
        public abstract int ElementSize { get; }
        public abstract PrimitiveType ChainType { get; }
        public abstract PrimitiveType ListType { get; }
        public virtual bool IsBatch
        {
            get
            {
                return false;
            }
        }

        public virtual int MaxLength
        {
            get
            {
                return DisplayList.GetMaxVertices();
            }
        }

        public abstract int? Next(List<int> chain, int[] element);
        public abstract List<int[]> Expand(List<int> chain);

        public int Rotations
        {
            get
            {
                return this.ElementSize;
            }
        }

        public int[] Rotate(int[] element, int rotation)
        {
            int[] newRot = new int[element.Length];
            for (int i = 0; i < element.Length; i++)
            {
                newRot[i] = element[(i + rotation) % element.Length];
            }

            return newRot;
        }

        public int? GetStartRotation(int[] first, int[] second)
        {
            for (int i = 0; i < this.Rotations; i++)
            {
                if (Next(Rotate(first, i).ToList(), second) != null)
                    return i;
            }

            return null;
        }
    }

    class StripRule : ChainRule
    {
        public override int ElementSize
        {
            get
            {
                return 3;
            }
        }

        public override PrimitiveType ChainType
        {
            get
            {
                return PrimitiveType.TRIANGLE_STRIP;
            }
        }

        public override PrimitiveType ListType
        {
            get
            {
                return PrimitiveType.TRIANGLES;
            }
        }

        public override int? Next(List<int> chain, int[] element)
        {
            int a = chain[chain.Count - 2];
            int b = chain[chain.Count - 1];

            if ((chain.Count - 2) % 2 == 0)
                return element[0] == a && element[1] == b ? element[2] : null;

            return element[0] == a && element[2] == b ? element[1] : null;
        }

        public override List<int[]> Expand(List<int> chain)
        {
            List<int[]> elements = new List<int[]>();
            for (int i = 0; i + 2 < chain.Count; i++)
            {
                elements.Add(i % 2 == 0 ? [chain[i], chain[i + 1], chain[i + 2]] : [chain[i], chain[i + 2], chain[i + 1]]);
            }

            return elements;
        }
    }

    class FanRule : ChainRule
    {
        public override int ElementSize
        {
            get
            {
                return 3;
            }
        }

        public override PrimitiveType ChainType
        {
            get
            {
                return PrimitiveType.TRIANGLE_FAN;
            }
        }

        public override PrimitiveType ListType
        {
            get
            {
                return PrimitiveType.TRIANGLES;
            }
        }

        public override int? Next(List<int> chain, int[] element)
        {
            return element[0] == chain[0] && element[1] == chain[chain.Count - 1] ? element[2] : null;
        }

        public override List<int[]> Expand(List<int> chain)
        {
            List<int[]> elements = new List<int[]>();
            for (int i = 1; i + 1 < chain.Count; i++)
            {
                elements.Add([chain[0], chain[i], chain[i + 1]]);
            }

            return elements;
        }
    }

    class QuadRule : ChainRule
    {
        public override int ElementSize
        {
            get
            {
                return 3;
            }
        }

        public override PrimitiveType ChainType
        {
            get
            {
                return PrimitiveType.QUADS;
            }
        }

        public override PrimitiveType ListType
        {
            get
            {
                return PrimitiveType.TRIANGLES;
            }
        }

        public override bool IsBatch
        {
            get
            {
                return true;
            }
        }

        public override int MaxLength
        {
            get
            {
                return 4;
            }
        }

        public override int? Next(List<int> chain, int[] element)
        {
            if (chain.Count != 3)
                return null;

            return element[0] == chain[0] && element[1] == chain[2] ? element[2] : null;
        }

        public override List<int[]> Expand(List<int> chain)
        {
            List<int[]> elements = new List<int[]>();
            for (int i = 0; i + 3 < chain.Count; i += 4)
            {
                elements.Add([chain[i], chain[i + 1], chain[i + 2]]);
                elements.Add([chain[i], chain[i + 2], chain[i + 3]]);
            }

            return elements;
        }
    }

    class LineStripRule : ChainRule
    {
        public override int ElementSize
        {
            get
            {
                return 2;
            }
        }

        public override PrimitiveType ChainType
        {
            get
            {
                return PrimitiveType.LINE_STRIP;
            }
        }

        public override PrimitiveType ListType
        {
            get
            {
                return PrimitiveType.LINES;
            }
        }

        public override int? Next(List<int> chain, int[] element)
        {
            return element[0] == chain[chain.Count - 1] ? element[1] : null;
        }

        public override List<int[]> Expand(List<int> chain)
        {
            List<int[]> elements = new List<int[]>();
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                elements.Add([chain[i], chain[i + 1]]);
            }

            return elements;
        }
    }

    public static class PrimitiveConverter
    {
        static readonly ChainRule STRIP_RULE = new StripRule();
        static readonly ChainRule FAN_RULE = new FanRule();
        static readonly ChainRule QUAD_RULE = new QuadRule();
        static readonly ChainRule LINE_STRIP_RULE = new LineStripRule();

        const double MINIMUM_ORDERED_COVERAGE = 0.5;

        public static bool IsTriangleType(PrimitiveType type)
        {
            return type == PrimitiveType.QUADS || type == PrimitiveType.TRIANGLES || type == PrimitiveType.TRIANGLE_STRIP || type == PrimitiveType.TRIANGLE_FAN;
        }

        public static bool IsLineType(PrimitiveType type)
        {
            return type == PrimitiveType.LINES || type == PrimitiveType.LINE_STRIP;
        }

        private static ChainRule? GetRule(PrimitiveType type)
        {
            switch (type)
            {
                case PrimitiveType.TRIANGLE_STRIP:
                    return STRIP_RULE;

                case PrimitiveType.TRIANGLE_FAN:
                    return FAN_RULE;

                case PrimitiveType.QUADS:
                    return QUAD_RULE;

                case PrimitiveType.LINE_STRIP:
                    return LINE_STRIP_RULE;

                default:
                    return null;
            }
        }

        public static bool Convert(DisplayList dList, PrimitiveType target)
        {
            bool changed = false;

            foreach (PrimitiveGroup group in dList.groups)
            {
                if (group.primitives.Count == 0 || IsAlready(group.primitives, target))
                    continue;

                List<Primitive>? converted = ConvertGroup(group.primitives, target);
                if (converted == null || IsSame(converted, group.primitives))
                    continue;

                group.primitives = converted;
                changed = true;
            }

            return changed;
        }

        private static bool IsSame(List<Primitive> a, List<Primitive> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].type != b[i].type || a[i].vertexFormat != b[i].vertexFormat || !a[i].vertices.SequenceEqual(b[i].vertices))
                    return false;
            }

            return true;
        }

        private static bool IsAlready(List<Primitive> primitives, PrimitiveType target)
        {
            ChainRule? rule = GetRule(target);
            if (rule == null)
                return primitives.All(x => x.type == target);

            return primitives.All(x => x.type == target || x.type == rule.ListType) && primitives.Any(x => x.type == target);
        }

        private static List<Primitive>? ConvertGroup(List<Primitive> primitives, PrimitiveType target)
        {
            byte vertexFormat = primitives[0].vertexFormat;
            List<Primitive> triangles = primitives.Where(x => IsTriangleType(x.type)).ToList();
            List<Primitive> lines = primitives.Where(x => IsLineType(x.type)).ToList();
            List<Primitive> points = primitives.Where(x => x.type == PrimitiveType.POINTS).ToList();
            List<Primitive> output = new List<Primitive>();

            if (IsTriangleType(target))
            {
                if (triangles.Count == 0)
                    return null;

                List<int[]> elements = DecodeElements(triangles);
                ChainRule? rule = GetRule(target);

                if (rule == null)
                    AddList(output, elements, PrimitiveType.TRIANGLES, vertexFormat);
                else
                    output.AddRange(BuildChains(elements, rule, vertexFormat, false));

                output.AddRange(lines);
                output.AddRange(points);
            }
            else if (IsLineType(target))
            {
                List<int[]> segments = GetEdges(DecodeElements(triangles));
                bool fromTriangles = segments.Count > 0;
                AddUniqueSegments(segments, DecodeElements(lines));

                if (segments.Count == 0)
                    return null;

                if (target == PrimitiveType.LINES)
                    AddList(output, segments, PrimitiveType.LINES, vertexFormat);
                else
                    output.AddRange(BuildChains(segments, LINE_STRIP_RULE, vertexFormat, fromTriangles));

                output.AddRange(points);
            }
            else
            {
                List<int> vertices = new List<int>();
                HashSet<int> unique = new HashSet<int>();

                foreach (Primitive primitive in primitives)
                {
                    foreach (int vertex in primitive.vertices)
                    {
                        if (unique.Add(vertex))
                            vertices.Add(vertex);
                    }
                }

                AddList(output, vertices.Select(x => new int[] { x }).ToList(), PrimitiveType.POINTS, vertexFormat);
            }

            return output;
        }

        private static List<int[]> DecodeElements(List<Primitive> primitives)
        {
            List<int[]> elements = new List<int[]>();
            ChainRule? stateRule = null;
            List<int>? stateChain = null;
            int[]? stateSingle = null;

            int[] Place(int[] element, ChainRule rule)
            {
                for (int i = 0; i < rule.Rotations; i++)
                {
                    int[] rotated = rule.Rotate(element, i);
                    if (stateChain != null && stateRule != null && stateRule.Next(stateChain, rotated) != null)
                        continue;

                    if (stateSingle != null && stateSingle.Length == rotated.Length && rule.GetStartRotation(stateSingle, rotated) != null)
                        continue;

                    return rotated;
                }

                return element;
            }

            foreach (Primitive primitive in primitives)
            {
                ChainRule? rule = GetRule(primitive.type);
                if (rule == null)
                {
                    int size = primitive.type == PrimitiveType.LINES ? 2 : 3;
                    for (int i = 0; i + size <= primitive.vertices.Count; i += size)
                    {
                        stateSingle = primitive.vertices.GetRange(i, size).ToArray();
                        stateChain = null;
                        elements.Add(stateSingle);
                    }

                    continue;
                }

                int chainLength = rule == QUAD_RULE ? 4 : primitive.vertices.Count;
                for (int start = 0; start + rule.ElementSize <= primitive.vertices.Count; start += chainLength)
                {
                    List<int> chain = primitive.vertices.GetRange(start, Math.Min(chainLength, primitive.vertices.Count - start));
                    List<int[]> expanded = rule.Expand(chain);
                    if (expanded.Count == 0)
                        continue;

                    expanded[0] = Place(expanded[0], rule);
                    elements.AddRange(expanded);

                    if (expanded.Count > 1)
                    {
                        stateRule = rule;
                        stateChain = chain;
                        stateSingle = null;
                        continue;
                    }

                    stateChain = null;
                    stateSingle = expanded[0];
                }
            }

            return elements;
        }

        private static List<int[]> GetEdges(List<int[]> triangles)
        {
            List<int[]> edges = new List<int[]>();
            AddUniqueSegments(edges, triangles.SelectMany(x => new[] { new[] { x[0], x[1] }, [x[1], x[2]], [x[2], x[0]] }).ToList());
            return edges;
        }

        private static void AddUniqueSegments(List<int[]> segments, List<int[]> newSegments)
        {
            HashSet<(int, int)> unique = [.. segments.Select(x => (Math.Min(x[0], x[1]), Math.Max(x[0], x[1])))];
            foreach (int[] segment in newSegments)
            {
                if (segment[0] != segment[1] && unique.Add((Math.Min(segment[0], segment[1]), Math.Max(segment[0], segment[1]))))
                    segments.Add(segment);
            }
        }

        private static List<Primitive> BuildChains(List<int[]> elements, ChainRule rule, byte vertexFormat, bool forceAdjacency)
        {
            if (!forceAdjacency)
            {
                List<Primitive> ordered = BuildOrdered(elements, rule, vertexFormat, out int chained);
                if (elements.Count <= 1 || chained >= MINIMUM_ORDERED_COVERAGE * elements.Count)
                    return ordered;
            }

            return BuildByAdjacency(elements, rule, vertexFormat);
        }

        private static List<Primitive> BuildOrdered(List<int[]> elements, ChainRule rule, byte vertexFormat, out int chained)
        {
            List<Primitive> output = new List<Primitive>();
            chained = 0;
            int i = 0;

            while (i < elements.Count)
            {
                int? rotation = i + 1 < elements.Count ? rule.GetStartRotation(elements[i], elements[i + 1]) : null;

                if (rotation == null)
                {
                    AddList(output, [elements[i]], rule.ListType, vertexFormat);
                    i++;
                    continue;
                }

                List<int> chain = rule.Rotate(elements[i], rotation.Value).ToList();
                int start = i;
                i++;

                while (i < elements.Count && chain.Count < rule.MaxLength)
                {
                    int? vertex = rule.Next(chain, elements[i]);
                    if (vertex == null)
                        break;

                    chain.Add(vertex.Value);
                    i++;
                }

                chained += i - start;
                AddChain(output, chain, rule, vertexFormat);
            }

            return output;
        }

        private static List<Primitive> BuildByAdjacency(List<int[]> elements, ChainRule rule, byte vertexFormat)
        {
            List<Primitive> output = new List<Primitive>();
            bool[] used = new bool[elements.Count];
            Dictionary<int, List<int>> incident = new Dictionary<int, List<int>>();

            for (int i = 0; i < elements.Count; i++)
            {
                foreach (int vertex in elements[i].Distinct())
                {
                    if (!incident.TryGetValue(vertex, out List<int>? list))
                    {
                        list = new List<int>();
                        incident[vertex] = list;
                    }

                    list.Add(i);
                }
            }

            for (int i = 0; i < elements.Count; i++)
            {
                if (used[i])
                    continue;

                List<int> bestChain = elements[i].ToList();
                List<int> bestTaken = [i];

                for (int j = 0; j < rule.Rotations; j++)
                {
                    List<int> chain = rule.Rotate(elements[i], j).ToList();
                    List<int> taken = [i];
                    used[i] = true;

                    while (chain.Count < rule.MaxLength)
                    {
                        (int index, int vertex)? next = FindNext(chain, elements, used, incident, rule);
                        if (next == null)
                            break;

                        used[next.Value.index] = true;
                        taken.Add(next.Value.index);
                        chain.Add(next.Value.vertex);
                    }

                    foreach (int index in taken)
                    {
                        used[index] = false;
                    }

                    if (taken.Count <= bestTaken.Count)
                        continue;

                    bestChain = chain;
                    bestTaken = taken;
                }

                foreach (int index in bestTaken)
                {
                    used[index] = true;
                }

                if (bestTaken.Count == 1)
                    AddList(output, [elements[i]], rule.ListType, vertexFormat);
                else
                    AddChain(output, bestChain, rule, vertexFormat);
            }

            return output;
        }

        private static (int index, int vertex)? FindNext(List<int> chain, List<int[]> elements, bool[] used, Dictionary<int, List<int>> incident, ChainRule rule)
        {
            if (!incident.TryGetValue(chain[chain.Count - 1], out List<int>? candidates))
                return null;

            foreach (int index in candidates)
            {
                if (used[index])
                    continue;

                for (int i = 0; i < rule.Rotations; i++)
                {
                    int? vertex = rule.Next(chain, rule.Rotate(elements[index], i));
                    if (vertex != null)
                        return (index, vertex.Value);
                }
            }

            return null;
        }

        private static void AddList(List<Primitive> output, List<int[]> elements, PrimitiveType type, byte vertexFormat)
        {
            foreach (int[] element in elements)
            {
                Primitive? last = output.Count > 0 ? output[output.Count - 1] : null;

                if (last == null || last.type != type || last.vertices.Count + element.Length > DisplayList.GetMaxVertices())
                {
                    last = new Primitive
                    {
                        type = type,
                        vertexFormat = vertexFormat
                    };

                    output.Add(last);
                }

                last.vertices.AddRange(element);
            }
        }

        private static void AddChain(List<Primitive> output, List<int> chain, ChainRule rule, byte vertexFormat)
        {
            if (rule.IsBatch)
            {
                AddList(output, [chain.ToArray()], rule.ChainType, vertexFormat);
                return;
            }

            output.Add(new Primitive
            {
                type = rule.ChainType,
                vertexFormat = vertexFormat,
                vertices = chain
            });
        }
    }
}
