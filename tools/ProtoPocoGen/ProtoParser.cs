using System.Text.RegularExpressions;

namespace ProtoPocoGen;

public record ProtoFile(
    string Package,
    string? CSharpNamespace,
    List<string> Imports,
    List<ProtoMessage> Messages,
    List<ProtoEnum> Enums,
    List<ProtoService> Services
);

public record ProtoService(
    string Name,
    List<ProtoRpc> Rpcs
);

public record ProtoRpc(
    string Name,
    string RequestType,
    string ResponseType
);

public record ProtoMessage(
    string Name,
    List<ProtoField> Fields,
    List<ProtoOneof> Oneofs,
    List<ProtoMessage> NestedMessages,
    List<ProtoEnum> NestedEnums
);

public record ProtoField(
    string Name,
    string Type,
    int Number,
    bool IsOptional,
    bool IsRepeated,
    bool IsDeprecated
);

public record ProtoOneof(
    string Name,
    List<ProtoField> Fields
);

public record ProtoEnum(
    string Name,
    List<ProtoEnumValue> Values
);

public record ProtoEnumValue(
    string Name,
    int Number
);

public static class ProtoParser
{
    public static ProtoFile Parse(string content)
    {
        // Remove comments
        content = RemoveComments(content);

        var package = ParsePackage(content);
        var csharpNamespace = ParseCSharpNamespace(content);
        var imports = ParseImports(content);
        var (messages, enums, services) = ParseTopLevel(content);

        return new ProtoFile(package, csharpNamespace, imports, messages, enums, services);
    }

    private static string RemoveComments(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*[\s\S]*?\*/", "");
        return content;
    }

    private static string ParsePackage(string content)
    {
        var match = Regex.Match(content, @"package\s+([\w.]+)\s*;");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string? ParseCSharpNamespace(string content)
    {
        var match = Regex.Match(content, @"option\s+csharp_namespace\s*=\s*""([^""]+)""\s*;");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> ParseImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"import\s+""([^""]+)""\s*;");
        foreach (Match match in matches)
        {
            imports.Add(match.Groups[1].Value);
        }
        return imports;
    }

    private static (List<ProtoMessage>, List<ProtoEnum>, List<ProtoService>) ParseTopLevel(string content)
    {
        var messages = new List<ProtoMessage>();
        var enums = new List<ProtoEnum>();
        var services = new List<ProtoService>();

        // Parse top-level blocks by tracking brace depth
        var i = 0;
        while (i < content.Length)
        {
            // Skip whitespace
            while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
            if (i >= content.Length) break;

            // Check for message or enum keyword at current position
            var remaining = content.Substring(i);

            var messageMatch = Regex.Match(remaining, @"^message\s+(\w+)\s*\{");
            if (messageMatch.Success)
            {
                var name = messageMatch.Groups[1].Value;
                var blockStartIndex = i + messageMatch.Length;
                var blockContent = ExtractBlock(content, blockStartIndex);
                messages.Add(ParseMessage(name, blockContent));
                i = blockStartIndex + blockContent.Length + 1; // +1 for closing brace
                continue;
            }

            var enumMatch = Regex.Match(remaining, @"^enum\s+(\w+)\s*\{");
            if (enumMatch.Success)
            {
                var name = enumMatch.Groups[1].Value;
                var blockStartIndex = i + enumMatch.Length;
                var blockContent = ExtractBlock(content, blockStartIndex);
                enums.Add(ParseEnum(name, blockContent));
                i = blockStartIndex + blockContent.Length + 1;
                continue;
            }

            var serviceMatch = Regex.Match(remaining, @"^service\s+(\w+)\s*\{");
            if (serviceMatch.Success)
            {
                var serviceName = serviceMatch.Groups[1].Value;
                var blockStartIndex = i + serviceMatch.Length;
                var blockContent = ExtractBlock(content, blockStartIndex);
                services.Add(ParseService(serviceName, blockContent));
                i = blockStartIndex + blockContent.Length + 1;
                continue;
            }

            // Skip other statements (package, import, option, syntax, etc.)
            var statementEnd = content.IndexOf(';', i);
            if (statementEnd >= 0)
            {
                i = statementEnd + 1;
            }
            else
            {
                i++;
            }
        }

        return (messages, enums, services);
    }

    private static ProtoService ParseService(string name, string content)
    {
        var rpcs = new List<ProtoRpc>();

        // Pattern: rpc MethodName(RequestType) returns (ResponseType) { }
        // or: rpc MethodName(RequestType) returns (ResponseType);
        var rpcPattern = @"rpc\s+(\w+)\s*\(\s*([\w.]+)\s*\)\s*returns\s*\(\s*([\w.]+)\s*\)\s*(?:\{\s*\}|;)";
        var matches = Regex.Matches(content, rpcPattern);

        foreach (Match match in matches)
        {
            var rpcName = match.Groups[1].Value;
            var requestType = match.Groups[2].Value;
            var responseType = match.Groups[3].Value;
            rpcs.Add(new ProtoRpc(rpcName, requestType, responseType));
        }

        return new ProtoService(name, rpcs);
    }

    private static string ExtractBlock(string content, int startIndex)
    {
        var depth = 1;
        var endIndex = startIndex;

        while (depth > 0 && endIndex < content.Length)
        {
            if (content[endIndex] == '{') depth++;
            else if (content[endIndex] == '}') depth--;
            endIndex++;
        }

        return content.Substring(startIndex, endIndex - startIndex - 1);
    }

    private static ProtoMessage ParseMessage(string name, string content)
    {
        var fields = new List<ProtoField>();
        var oneofs = new List<ProtoOneof>();
        var nestedMessages = new List<ProtoMessage>();
        var nestedEnums = new List<ProtoEnum>();

        // Parse nested messages first (to exclude them from field parsing)
        var nestedMessagePattern = @"\bmessage\s+(\w+)\s*\{";
        var nestedMatches = Regex.Matches(content, nestedMessagePattern);
        var excludeRanges = new List<(int Start, int End)>();

        foreach (Match match in nestedMatches)
        {
            var nestedName = match.Groups[1].Value;
            var blockStart = match.Index + match.Length;
            var blockContent = ExtractBlock(content, blockStart);
            excludeRanges.Add((match.Index, blockStart + blockContent.Length + 1));
            nestedMessages.Add(ParseMessage(nestedName, blockContent));
        }

        // Parse nested enums
        var nestedEnumPattern = @"\benum\s+(\w+)\s*\{";
        var enumMatches = Regex.Matches(content, nestedEnumPattern);

        foreach (Match match in enumMatches)
        {
            var enumName = match.Groups[1].Value;
            var blockStart = match.Index + match.Length;
            var blockContent = ExtractBlock(content, blockStart);
            excludeRanges.Add((match.Index, blockStart + blockContent.Length + 1));
            nestedEnums.Add(ParseEnum(enumName, blockContent));
        }

        // Parse oneof blocks
        var oneofPattern = @"\boneof\s+(\w+)\s*\{";
        var oneofMatches = Regex.Matches(content, oneofPattern);

        foreach (Match match in oneofMatches)
        {
            var oneofName = match.Groups[1].Value;
            var blockStart = match.Index + match.Length;
            var blockContent = ExtractBlock(content, blockStart);
            excludeRanges.Add((match.Index, blockStart + blockContent.Length + 1));

            var oneofFields = ParseFields(blockContent, isOneof: true);
            oneofs.Add(new ProtoOneof(oneofName, oneofFields));
        }

        // Remove excluded ranges from content for field parsing
        var filteredContent = RemoveRanges(content, excludeRanges);

        // Parse regular fields
        fields.AddRange(ParseFields(filteredContent, isOneof: false));

        return new ProtoMessage(name, fields, oneofs, nestedMessages, nestedEnums);
    }

    private static string RemoveRanges(string content, List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return content;

        var sortedRanges = ranges.OrderByDescending(r => r.Start).ToList();
        var result = content;

        foreach (var (start, end) in sortedRanges)
        {
            if (start >= 0 && end <= result.Length && start < end)
            {
                result = result.Remove(start, end - start);
            }
        }

        return result;
    }

    private static List<ProtoField> ParseFields(string content, bool isOneof)
    {
        var fields = new List<ProtoField>();

        // Pattern: [optional|repeated] type name = number [deprecated];
        var fieldPattern = @"(?:(optional|repeated)\s+)?([\w.]+)\s+(\w+)\s*=\s*(\d+)(?:\s*\[([^\]]*)\])?\s*;";
        var matches = Regex.Matches(content, fieldPattern);

        foreach (Match match in matches)
        {
            var modifier = match.Groups[1].Value;
            var type = match.Groups[2].Value;
            var fieldName = match.Groups[3].Value;
            var number = int.Parse(match.Groups[4].Value);
            var options = match.Groups[5].Value;

            var isOptional = modifier == "optional" || isOneof;
            var isRepeated = modifier == "repeated";
            var isDeprecated = options.Contains("deprecated");

            fields.Add(new ProtoField(fieldName, type, number, isOptional, isRepeated, isDeprecated));
        }

        return fields;
    }

    private static ProtoEnum ParseEnum(string name, string content)
    {
        var values = new List<ProtoEnumValue>();

        // Pattern: NAME = number;
        var valuePattern = @"(\w+)\s*=\s*(\d+)\s*;";
        var matches = Regex.Matches(content, valuePattern);

        foreach (Match match in matches)
        {
            var valueName = match.Groups[1].Value;
            var number = int.Parse(match.Groups[2].Value);
            values.Add(new ProtoEnumValue(valueName, number));
        }

        return new ProtoEnum(name, values);
    }
}
