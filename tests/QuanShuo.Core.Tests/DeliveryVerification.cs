using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// Read bundles/PE resources as bytes. Never load or execute the application/installer.
internal static class DeliveryVerification
{
    public static void Run()
    {
        string portablePath = "artifacts/publish/portable/QuanShuo.exe";
        var portable = new Bundle(portablePath);
        string[] dist = Directory.GetFiles("frontend/dist", "*", SearchOption.AllDirectories);
        foreach (string file in dist)
        {
            string name = "Frontend/" + Path.GetRelativePath("frontend/dist", file).Replace('\\', '/');
            Equal(File.ReadAllBytes(file), portable.Read(name), name);
        }
        string[] bundledFrontend = portable.Names.Where(name => name.StartsWith("Frontend/", StringComparison.Ordinal)).ToArray();
        if (bundledFrontend.Length != dist.Length) throw new InvalidDataException("Stale or missing bundled frontend files.");
        Console.WriteLine($"PASS portable Frontend: {dist.Length} files match dist exactly");
        foreach (string assembly in new[] { "QuanShuo.dll", "QuanShuo.Core.dll", "WebRtcVad.NET.dll" })
        {
            Equal(File.ReadAllBytes($"src/QuanShuo.Native/bin/Release/net10.0-windows/win-x64/{assembly}"), portable.Read(assembly), assembly);
        }
        Console.WriteLine("PASS portable assemblies match Release build");

        var installer = new Bundle("artifacts/installer/QuanShuo-Setup.exe");
        byte[] installerAssembly = installer.Read("QuanShuo-Setup.dll");
        byte[] embedded = ReadResource(installerAssembly, "QuanShuo.exe");
        byte[] portableBytes = File.ReadAllBytes(portablePath);
        Equal(portableBytes, embedded, "Installer payload");
        Console.WriteLine("PASS installer embedded executable matches portable SHA-256");
        using var zip = ZipFile.OpenRead("artifacts/package/QuanShuo-portable.zip");
        using var zipped = zip.GetEntry("QuanShuo.exe")!.Open();
        using var zippedBytes = new MemoryStream();
        zipped.CopyTo(zippedBytes);
        Equal(portableBytes, zippedBytes.ToArray(), "Portable ZIP");
        Console.WriteLine("PASS ZIP executable matches portable SHA-256");
        foreach (string file in new[] { "LICENSE", "THIRD_PARTY_NOTICES.md" })
        {
            byte[] source = File.ReadAllBytes(file);
            Equal(source, portable.Read(file), "Application license " + file);
            Equal(source, ReadResource(installerAssembly, file), "Installer license " + file);
            using var entry = zip.GetEntry(file)?.Open() ?? throw new InvalidDataException(file + " is missing from ZIP.");
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            Equal(source, bytes.ToArray(), "ZIP license " + file);
        }
        Console.WriteLine("PASS application, installer and ZIP preserve project license and notices");
        foreach (var (file, resource) in new[]
        {
            ("local-asr-agent-prompt.md", "QuanShuo.LocalAsrAgentPrompt"),
            ("local-asr-assessment-prompt.md", "QuanShuo.LocalAsrAssessmentPrompt"),
        })
        {
            byte[] prompt = File.ReadAllBytes("docs/" + file);
            Equal(prompt, ReadResource(portable.Read("QuanShuo.dll"), resource), "Application embedded " + file);
            Equal(prompt, ReadResource(installerAssembly, file), "Installer embedded " + file);
            using var zippedPrompt = zip.GetEntry("docs/" + file)?.Open()
                ?? throw new InvalidDataException(file + " is missing from portable ZIP.");
            using var promptBytes = new MemoryStream();
            zippedPrompt.CopyTo(promptBytes);
            Equal(prompt, promptBytes.ToArray(), "Portable ZIP " + file);
            Console.WriteLine("PASS application, installer and ZIP match source: " + file);
        }
        Console.WriteLine($"Portable SHA-256: {Convert.ToHexString(SHA256.HashData(portableBytes))}");
    }

    private static byte[] ReadResource(byte[] assembly, string name)
    {
        using var stream = new MemoryStream(assembly);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        ManifestResource resource = metadata.ManifestResources.Select(metadata.GetManifestResource)
            .Single(item => metadata.GetString(item.Name) == name);
        if (!resource.Implementation.IsNil) throw new InvalidDataException(name + " is not embedded.");
        var block = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        var data = block.GetReader(checked((int)resource.Offset), block.Length - checked((int)resource.Offset));
        return data.ReadBytes(data.ReadInt32());
    }

    private static void Equal(byte[] expected, byte[] actual, string name)
    {
        if (!SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual)))
            throw new InvalidDataException($"Delivery mismatch: {name}");
    }

    private sealed class Bundle
    {
        private readonly byte[] bytes;
        private readonly Dictionary<string, (long Offset, long Size, long Compressed)> entries = [];
        public IEnumerable<string> Names => entries.Keys;

        public Bundle(string path)
        {
            bytes = File.ReadAllBytes(path);
            byte[] signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
            int marker = bytes.AsSpan().IndexOf(signature);
            if (marker < 8) throw new InvalidDataException("Bundle marker missing.");
            long offset = BitConverter.ToInt64(bytes, marker - 8);
            using var stream = new MemoryStream(bytes);
            stream.Position = offset;
            using var reader = new BinaryReader(stream);
            uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
            if (major != 6 || minor != 0) throw new InvalidDataException($"Unsupported bundle version: {major}.{minor}");
            int count = reader.ReadInt32();
            reader.ReadString(); // bundle ID
            for (int index = 0; index < 5; index++) reader.ReadInt64(); // deps/runtimeconfig locations and flags
            for (int index = 0; index < count; index++)
            {
                long entryOffset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
                reader.ReadByte(); // file type
                entries.Add(reader.ReadString().Replace('\\', '/'), (entryOffset, size, compressed));
            }
        }

        public byte[] Read(string name)
        {
            var entry = entries[name];
            if (entry.Compressed == 0) return bytes.AsSpan(checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
            using var input = new MemoryStream(bytes, checked((int)entry.Offset), checked((int)entry.Compressed));
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            if (output.Length != entry.Size) throw new InvalidDataException("Invalid bundle entry size.");
            return output.ToArray();
        }
    }
}
