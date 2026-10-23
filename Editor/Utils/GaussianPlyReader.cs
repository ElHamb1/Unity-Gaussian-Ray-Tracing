// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GaussianSplatting.Editor.Utils
{
    /// <summary>
    /// Maps named binary PLY properties into the importer's canonical record:
    /// 62 floats (channel-major SH), followed by an int32 layer. Missing normals,
    /// higher-order SH and layer values are zero. No Unity allocation is owned here.
    /// </summary>
    public static class GaussianPlyReader
    {
        public const int OutputStride = 252;

        public sealed class Property
        {
            public string Name;
            public string Type;
            public int Offset;
        }

        public sealed class Header
        {
            public int VertexCount;
            public int VertexStride;
            public readonly List<Property> Properties = new List<Property>();
        }

        public static Header ReadHeader(Stream stream)
        {
            if (ReadLine(stream) != "ply") throw new IOException("Not a PLY file.");
            var header = new Header();
            string element = null;
            bool foundFormat = false, foundVertex = false, ended = false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int lineIndex = 0; lineIndex < 9000; ++lineIndex)
            {
                string line = ReadLine(stream);
                if (line == null) break;
                string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || words[0] == "comment" || words[0] == "obj_info") continue;
                if (words[0] == "end_header") { ended = true; break; }
                if (words[0] == "format")
                {
                    if (words.Length != 3 || words[1] != "binary_little_endian" || words[2] != "1.0")
                        throw new IOException("Export an uncompressed binary_little_endian PLY 1.0. ASCII, big-endian and compressed PLY are not supported.");
                    foundFormat = true;
                }
                else if (words[0] == "element")
                {
                    if (words.Length != 3 || !int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                        throw new IOException("Invalid PLY element count.");
                    element = words[1];
                    if (element == "vertex")
                    {
                        if (foundVertex) throw new IOException("Duplicate vertex element.");
                        foundVertex = true;
                        header.VertexCount = count;
                    }
                    else if (!foundVertex && count != 0)
                        throw new IOException("The vertex element must be the first non-empty element. For compressed/chunked PLY, export an uncompressed Gaussian PLY first.");
                }
                else if (words[0] == "property" && element == "vertex")
                {
                    if (words.Length != 3 || words[1] == "list")
                        throw new IOException("Gaussian vertex properties must be scalar values.");
                    if (!names.Add(words[2])) throw new IOException("Duplicate PLY property: " + words[2]);
                    header.Properties.Add(new Property { Name = words[2], Type = words[1], Offset = header.VertexStride });
                    header.VertexStride = checked(header.VertexStride + ScalarSize(words[1]));
                }
            }
            if (!ended || !foundFormat || !foundVertex || header.VertexCount <= 0 || header.VertexStride <= 0)
                throw new IOException("Missing or invalid PLY format, vertex element or end_header.");
            return header;
        }

        /// <summary>
        /// Stream must be positioned after the header. The callback receives the first
        /// vertex index, a reusable canonical byte buffer, and its valid byte count.
        /// Copy/consume the bytes before returning. Source data are never modified.
        /// </summary>
        public static void ReadVertices(Stream stream, Header header, Action<int, byte[], int> consume)
        {
            if (consume == null) throw new ArgumentNullException(nameof(consume));
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("PLY decoding currently requires a little-endian host.");
            var properties = header.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var copies = new List<(int source, int target)>();
            void Map(string name, int targetFloat, bool required)
            {
                if (!properties.TryGetValue(name, out Property property))
                {
                    if (required) throw new IOException("Missing Gaussian PLY property '" + name + "'. Use a trained Gaussian PLY, not a mesh or ordinary point cloud.");
                    return;
                }
                if (property.Type != "float" && property.Type != "float32")
                    throw new IOException("Gaussian property '" + name + "' must be float32.");
                copies.Add((property.Offset, targetFloat * 4));
            }
            Map("x", 0, true); Map("y", 1, true); Map("z", 2, true);
            Map("nx", 3, false); Map("ny", 4, false); Map("nz", 5, false);
            for (int i = 0; i < 3; ++i) Map("f_dc_" + i, 6 + i, true);
            Map("opacity", 54, true);
            for (int i = 0; i < 3; ++i) Map("scale_" + i, 55 + i, true);
            for (int i = 0; i < 4; ++i) Map("rot_" + i, 58 + i, true);

            int shCount = header.Properties.Count(p => p.Name.StartsWith("f_rest_", StringComparison.Ordinal));
            if (shCount != 0 && shCount != 9 && shCount != 24 && shCount != 45)
                throw new IOException("Expected 0, 9, 24 or 45 f_rest properties for SH degree 0, 1, 2 or 3.");
            int perChannel = shCount / 3;
            for (int channel = 0; channel < 3; ++channel)
                for (int coefficient = 0; coefficient < perChannel; ++coefficient)
                    Map("f_rest_" + (channel * perChannel + coefficient), 9 + channel * 15 + coefficient, true);

            // Merge adjacent float copies. Standard 248-byte records become one
            // copy plus a zero layer, while arbitrary property orders also work.
            var spans = new List<(int source, int target, int length)>();
            foreach (var copy in copies.OrderBy(c => c.target))
            {
                if (spans.Count > 0)
                {
                    var previous = spans[spans.Count - 1];
                    if (previous.source + previous.length == copy.source && previous.target + previous.length == copy.target)
                    {
                        spans[spans.Count - 1] = (previous.source, previous.target, previous.length + 4);
                        continue;
                    }
                }
                spans.Add((copy.source, copy.target, 4));
            }
            properties.TryGetValue("layer", out Property layer);
            long sourceBytes = (long)header.VertexCount * header.VertexStride;
            if (stream.CanSeek && stream.Length - stream.Position < sourceBytes)
                throw new IOException("Truncated PLY vertex data: fewer bytes than declared in the header.");
            int batch = Math.Max(1, Math.Min(4096, 1024 * 1024 / header.VertexStride));
            var input = new byte[checked(batch * header.VertexStride)];
            var output = new byte[batch * OutputStride];
            for (int first = 0; first < header.VertexCount; first += batch)
            {
                int count = Math.Min(batch, header.VertexCount - first);
                int bytes = count * header.VertexStride;
                int read = 0;
                while (read < bytes)
                {
                    int got = stream.Read(input, read, bytes - read);
                    if (got == 0) throw new IOException("Truncated PLY vertex data.");
                    read += got;
                }
                for (int i = 0; i < count; ++i)
                {
                    int src = i * header.VertexStride, dst = i * OutputStride;
                    foreach (var span in spans)
                        Buffer.BlockCopy(input, src + span.source, output, dst + span.target, span.length);
                    if (layer != null)
                    {
                        double value = ReadNumber(input, src + layer.Offset, layer.Type);
                        if (value < 0 || value > 255 || double.IsNaN(value) || value != Math.Truncate(value))
                            throw new IOException("PLY layer must be an integer from 0 to 255 (the asset stores byte layer IDs).");
                        output[dst + 248] = (byte)value;
                        // Remaining int32 bytes are zero, as are missing fields.
                    }
                }
                consume(first, output, count * OutputStride);
            }
        }

        static int ScalarSize(string type)
        {
            switch (type)
            {
                case "char": case "int8": case "uchar": case "uint8": return 1;
                case "short": case "int16": case "ushort": case "uint16": return 2;
                case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
                case "double": case "float64": return 8;
                default: throw new IOException("Unsupported PLY scalar type: " + type);
            }
        }

        static double ReadNumber(byte[] data, int offset, string type)
        {
            switch (type)
            {
                case "char": case "int8": return (sbyte)data[offset];
                case "uchar": case "uint8": return data[offset];
                case "short": case "int16": return BitConverter.ToInt16(data, offset);
                case "ushort": case "uint16": return BitConverter.ToUInt16(data, offset);
                case "int": case "int32": return BitConverter.ToInt32(data, offset);
                case "uint": case "uint32": return BitConverter.ToUInt32(data, offset);
                case "float": case "float32": return BitConverter.ToSingle(data, offset);
                case "double": case "float64": return BitConverter.ToDouble(data, offset);
                default: throw new IOException("Unsupported PLY layer type: " + type);
            }
        }

        static string ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 16384)
            {
                int b = stream.ReadByte();
                if (b < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                if (b == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)b);
            }
            throw new IOException("PLY header line is too long.");
        }
    }
}
