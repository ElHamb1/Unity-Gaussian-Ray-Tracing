// SPDX-License-Identifier: MIT
// Standalone regression tests for the actual, Unity-independent PLY reader.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GaussianSplatting.Editor.Utils;

static class PlyImportTests
{
    static int checks;
    static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        ++checks;
    }

    static List<(string type, string name)> Fields(bool normals = true, int sh = 45, string layer = null)
    {
        var fields = new List<(string type, string name)>();
        foreach (string name in new[] { "x", "y", "z" }) fields.Add(("float", name));
        if (normals) foreach (string name in new[] { "nx", "ny", "nz" }) fields.Add(("float", name));
        for (int i = 0; i < 3; ++i) fields.Add(("float", "f_dc_" + i));
        for (int i = 0; i < sh; ++i) fields.Add(("float", "f_rest_" + i));
        fields.Add(("float", "opacity"));
        for (int i = 0; i < 3; ++i) fields.Add(("float", "scale_" + i));
        for (int i = 0; i < 4; ++i) fields.Add(("float", "rot_" + i));
        if (layer != null) fields.Add((layer, "layer"));
        return fields;
    }

    static float Value(string name, int vertex)
    {
        if (name == "layer") return vertex % 7;
        if (name.StartsWith("f_rest_")) return 100 + int.Parse(name.Substring(7)) + vertex;
        switch (name)
        {
            case "x": return 1 + vertex;
            case "y": return 2 + vertex;
            case "z": return 3 + vertex;
            case "opacity": return .75f;
            case "scale_0": return -2;
            case "rot_0": return 1;
            default: return 0;
        }
    }

    static MemoryStream Fixture(List<(string type, string name)> fields, int vertices = 2, bool trailingFace = false)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        string header = "ply\r\nformat binary_little_endian 1.0\r\ncomment fixture\r\nelement vertex " + vertices + "\r\n";
        foreach (var field in fields) header += "property " + field.type + " " + field.name + "\r\n";
        if (trailingFace) header += "element face 0\r\nproperty list uchar int vertex_indices\r\n";
        writer.Write(Encoding.ASCII.GetBytes(header + "end_header\r\n"));
        for (int vertex = 0; vertex < vertices; ++vertex)
            foreach (var field in fields)
            {
                float value = Value(field.name, vertex);
                if (field.type == "float") writer.Write(value);
                else if (field.type == "int") writer.Write((int)value);
                else if (field.type == "uchar") writer.Write((byte)value);
                else throw new Exception("Unknown fixture type");
            }
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    static byte[] Decode(Stream stream)
    {
        var header = GaussianPlyReader.ReadHeader(stream);
        var output = new byte[header.VertexCount * GaussianPlyReader.OutputStride];
        GaussianPlyReader.ReadVertices(stream, header, (first, bytes, length) =>
            Buffer.BlockCopy(bytes, 0, output, first * GaussianPlyReader.OutputStride, length));
        return output;
    }

    static float F(byte[] data, int field, int vertex = 0) => BitConverter.ToSingle(data, vertex * 252 + field * 4);
    static int Layer(byte[] data, int vertex = 0) => BitConverter.ToInt32(data, vertex * 252 + 248);

    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException) { ++checks; return; }
        throw new Exception("Accepted invalid file: " + message);
    }

    public static int Main(string[] args)
    {
        // Standard degree-three PLY: 248 bytes, no anatomy layer field.
        using (var stream = Fixture(Fields(), trailingFace: true))
        {
            var data = Decode(stream);
            Check(data.Length == 504 && Layer(data) == 0 && Layer(data, 1) == 0, "Default layer");
            Check(F(data, 0) == 1 && F(data, 2, 1) == 4, "Position mapping");
            Check(F(data, 9) == 100 && F(data, 24) == 115 && F(data, 39) == 130, "Channel-major SH preservation");
            Check(F(data, 54) == .75f && F(data, 55) == -2 && F(data, 58) == 1, "Gaussian parameter mapping");
        }
        // Existing anatomy records retain int32 layer IDs across multiple chunks.
        using (var stream = Fixture(Fields(layer: "int"), 4100))
        {
            var data = Decode(stream);
            Check(Layer(data, 4099) == 4099 % 7 && F(data, 0, 4099) == 4100, "Layered batch boundary");
        }
        // Scaniverse-style exports omit normals and reorder all properties.
        var reordered = Fields(normals: false);
        reordered.Reverse();
        reordered.Insert(3, ("uchar", "confidence"));
        using (var stream = Fixture(reordered))
        {
            var data = Decode(stream);
            Check(F(data, 0) == 1 && F(data, 54) == .75f && F(data, 58) == 1, "Reordered fields and unknown scalar");
            Check(F(data, 3) == 0 && F(data, 4) == 0 && F(data, 5) == 0, "Missing normals");
        }
        foreach (int sh in new[] { 0, 9, 24 })
            using (var stream = Fixture(Fields(sh: sh, layer: "float")))
            {
                var data = Decode(stream);
                Check(Layer(data, 1) == 1, "Float-encoded integer layer");
                int coefficients = sh / 3;
                Check(F(data, 24) == (sh == 0 ? 0 : 100 + coefficients), "Lower-order SH channel expansion");
                Check(F(data, 9 + coefficients) == 0 && F(data, 39 + coefficients) == 0, "Higher-order SH zero fill");
            }
        var missing = Fields(); missing.RemoveAll(f => f.name == "opacity");
        Reject(() => { using var stream = Fixture(missing); Decode(stream); }, "missing opacity");
        var duplicate = Fields(); duplicate.Add(("float", "x"));
        Reject(() => { using var stream = Fixture(duplicate); Decode(stream); }, "duplicate property");
        Reject(() => { using var stream = Fixture(Fields(sh: 12)); Decode(stream); }, "incomplete SH");
        Reject(() => { using var stream = Fixture(Fields()); stream.SetLength(stream.Length - 1); Decode(stream); }, "truncated body");
        Reject(() => { using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ply\nformat ascii 1.0\n")); Decode(stream); }, "ASCII format");
        Reject(() => { using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ply\nformat binary_big_endian 1.0\n")); Decode(stream); }, "big-endian format");
        Reject(() => { using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement chunk 1\n")); Decode(stream); }, "compressed chunk layout");
        Reject(() =>
        {
            using var stream = Fixture(Fields(layer: "int"));
            stream.Position = stream.Length - 4;
            new BinaryWriter(stream, Encoding.ASCII, true).Write(256);
            stream.Position = 0;
            Decode(stream);
        }, "layer cannot fit stored byte");

        // Optional integration input: consume the complete real PLY, then compare
        // every decoded standard record byte-for-byte with its original 248 bytes.
        foreach (string path in args)
        {
            using var input = File.OpenRead(path);
            var header = GaussianPlyReader.ReadHeader(input);
            var expectedNames = Fields().Select(f => f.name).ToArray();
            bool standard = header.VertexStride == 248 && header.Properties.Select(p => p.Name).SequenceEqual(expectedNames);
            using var reference = File.OpenRead(path);
            reference.Position = input.Position;
            var original = new byte[248];
            long vertices = 0;
            GaussianPlyReader.ReadVertices(input, header, (first, data, bytes) =>
            {
                int count = bytes / 252;
                for (int i = 0; i < count; ++i)
                {
                    Check(Layer(data, i) == 0, "Real file layer default");
                    if (standard)
                    {
                        int read = 0;
                        while (read < original.Length)
                        {
                            int got = reference.Read(original, read, original.Length - read);
                            if (got == 0) throw new EndOfStreamException();
                            read += got;
                        }
                        for (int b = 0; b < original.Length; ++b)
                            if (data[i * 252 + b] != original[b]) throw new Exception("Real PLY changed field bytes at vertex " + (first + i));
                    }
                }
                vertices += count;
            });
            Check(vertices == header.VertexCount, "Real file vertex count");
            Console.WriteLine("Real PLY: " + vertices + " vertices decoded; input stride=" + header.VertexStride + "; original fields preserved=" + standard);
        }
        Console.WriteLine("PASS: " + checks + " PLY import checks.");
        return 0;
    }
}
