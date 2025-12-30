using ProtoPocoGen;

if (args.Length < 2)
{
    Console.WriteLine("Usage: ProtoPocoGen <input-dir> <output-dir>");
    Console.WriteLine("  --input <dir>   Directory containing .proto files");
    Console.WriteLine("  --output <dir>  Directory to write generated .cs files");
    return 1;
}

string? inputDir = null;
string? outputDir = null;

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--input")
    {
        inputDir = args[i + 1];
    }
    else if (args[i] == "--output")
    {
        outputDir = args[i + 1];
    }
}

if (inputDir == null || outputDir == null)
{
    Console.WriteLine("Error: Both --input and --output are required");
    return 1;
}

if (!Directory.Exists(inputDir))
{
    Console.WriteLine($"Error: Input directory does not exist: {inputDir}");
    return 1;
}

// Create output directory if it doesn't exist
Directory.CreateDirectory(outputDir);

// Find all .proto files
var protoFiles = Directory.GetFiles(inputDir, "*.proto", SearchOption.AllDirectories);
Console.WriteLine($"Found {protoFiles.Length} proto files");

// Parse all proto files
var parsedFiles = new Dictionary<string, ProtoFile>();

foreach (var protoFile in protoFiles)
{
    var content = File.ReadAllText(protoFile);
    var parsed = ProtoParser.Parse(content);

    // Use relative path as key
    var relativePath = Path.GetRelativePath(inputDir, protoFile);
    parsedFiles[relativePath] = parsed;

    Console.WriteLine($"Parsed: {relativePath} ({parsed.Messages.Count} messages, {parsed.Enums.Count} enums)");
}

// Generate C# files
var emitter = new CSharpEmitter(parsedFiles);

foreach (var (relativePath, protoFile) in parsedFiles)
{
    if (protoFile.Messages.Count == 0 && protoFile.Enums.Count == 0)
    {
        Console.WriteLine($"Skipping {relativePath}: no messages or enums");
        continue;
    }

    var outputFileName = Path.GetFileNameWithoutExtension(relativePath) + ".g.cs";
    var outputPath = Path.Combine(outputDir, outputFileName);

    var csharpCode = emitter.Emit(protoFile);
    File.WriteAllText(outputPath, csharpCode);

    Console.WriteLine($"Generated: {outputPath}");
}

Console.WriteLine("Done!");
return 0;
