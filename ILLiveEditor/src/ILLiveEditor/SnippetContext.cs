using System.Text;
using Mono.Cecil;

namespace ILLiveEditor;

/// <summary>
/// Captures the context of a target method needed to compile C# snippets that can
/// reference the method's parameters, locals, and surrounding type.
/// </summary>
public class SnippetContext
{
    public string ContainingTypeFullName { get; set; } = "";
    public bool IsInstanceMethod { get; set; }
    public List<ParameterInfo> Parameters { get; set; } = new();
    public List<LocalInfo> Locals { get; set; } = new();
    public List<string> UsingNamespaces { get; set; } = new();
    public List<string> AssemblyReferencePaths { get; set; } = new();

    public record ParameterInfo(string Name, string TypeName, bool IsByRef, bool IsOut);
    public record LocalInfo(string Name, string TypeName);

    /// <summary>
    /// Build a SnippetContext from a Cecil MethodDefinition.
    /// </summary>
    /// <param name="method">The target method to extract context from.</param>
    /// <param name="assemblySearchPaths">Directories to probe for referenced assemblies.</param>
    public static SnippetContext FromMethod(MethodDefinition method, params string[] assemblySearchPaths)
    {
        var ctx = new SnippetContext
        {
            ContainingTypeFullName = FormatTypeName(method.DeclaringType),
            IsInstanceMethod = method.HasThis && !method.IsStatic
        };

        // Parameters
        foreach (var p in method.Parameters)
        {
            var paramType = p.ParameterType;
            bool isByRef = paramType.IsByReference;
            bool isOut = p.IsOut;
            var actualType = isByRef ? ((ByReferenceType)paramType).ElementType : paramType;
            ctx.Parameters.Add(new ParameterInfo(
                SanitizeIdentifier(p.Name),
                FormatTypeName(actualType),
                isByRef,
                isOut));
        }

        // Locals (Cecil VariableDefinition doesn't carry names — use debug info if available)
        if (method.HasBody)
        {
            // Try to get local variable names from debug information
            var debugVarNames = new Dictionary<int, string>();
            if (method.DebugInformation?.Scope != null)
                CollectVariableNames(method.DebugInformation.Scope, debugVarNames);

            for (int i = 0; i < method.Body.Variables.Count; i++)
            {
                var v = method.Body.Variables[i];
                string name = debugVarNames.TryGetValue(v.Index, out var dbgName) ? dbgName : $"V_{i}";
                ctx.Locals.Add(new LocalInfo(
                    SanitizeIdentifier(name),
                    FormatTypeName(v.VariableType)));
            }
        }

        // Collect using namespaces from all type references in the module
        var namespaces = new HashSet<string>();
        var module = method.Module;
        foreach (var typeRef in module.GetTypeReferences())
        {
            if (!string.IsNullOrEmpty(typeRef.Namespace))
                namespaces.Add(typeRef.Namespace);
        }
        foreach (var typeDef in module.Types)
        {
            if (!string.IsNullOrEmpty(typeDef.Namespace))
                namespaces.Add(typeDef.Namespace);
        }
        // Always include System
        namespaces.Add("System");
        namespaces.Add("System.Collections.Generic");
        namespaces.Add("System.Linq");
        ctx.UsingNamespaces = namespaces.OrderBy(n => n).ToList();

        // Resolve assembly reference paths
        ctx.AssemblyReferencePaths = ResolveAssemblyPaths(module, assemblySearchPaths);

        return ctx;
    }

    /// <summary>
    /// Generates the C# wrapper source that makes the snippet compilable.
    /// </summary>
    public string BuildWrapperSource(string snippet)
    {
        var sb = new StringBuilder();

        // Usings
        foreach (var ns in UsingNamespaces)
            sb.AppendLine($"using {ns};");
        sb.AppendLine();

        sb.AppendLine("namespace __ILLiveEditor {");
        sb.AppendLine("    class __Wrapper {");

        // Build parameter list: [this,] method params, locals
        var paramParts = new List<string>();
        if (IsInstanceMethod)
            paramParts.Add($"{ContainingTypeFullName} __this");

        foreach (var p in Parameters)
        {
            string modifier = p.IsOut ? "out " : p.IsByRef ? "ref " : "";
            paramParts.Add($"{modifier}{p.TypeName} {p.Name}");
        }

        foreach (var l in Locals)
            paramParts.Add($"{l.TypeName} {l.Name}");

        string paramList = string.Join(", ", paramParts);
        sb.AppendLine($"        static void __Snippet({paramList}) {{");

        // Indent snippet lines
        foreach (var line in snippet.Split('\n'))
            sb.AppendLine($"            {line.TrimEnd('\r')}");

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    static void CollectVariableNames(Mono.Cecil.Cil.ScopeDebugInformation scope, Dictionary<int, string> names)
    {
        if (scope.HasVariables)
        {
            foreach (var v in scope.Variables)
            {
                if (!v.IsDebuggerHidden && v.Index >= 0)
                    names[v.Index] = v.Name;
            }
        }
        if (scope.HasScopes)
        {
            foreach (var child in scope.Scopes)
                CollectVariableNames(child, names);
        }
    }

    static string FormatTypeName(TypeReference type)
    {
        if (type is GenericInstanceType git)
        {
            var baseName = git.ElementType.FullName;
            int tick = baseName.IndexOf('`');
            if (tick >= 0) baseName = baseName.Substring(0, tick);
            var args = string.Join(", ", git.GenericArguments.Select(FormatTypeName));
            return $"{baseName}<{args}>";
        }

        if (type is ArrayType arr)
            return FormatTypeName(arr.ElementType) + "[]";

        if (type is ByReferenceType byRef)
            return FormatTypeName(byRef.ElementType);

        if (type is PointerType ptr)
            return FormatTypeName(ptr.ElementType) + "*";

        // Map CLR names to C# keywords
        return type.FullName switch
        {
            "System.Void" => "void",
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Decimal" => "decimal",
            "System.Char" => "char",
            "System.String" => "string",
            "System.Object" => "object",
            _ => type.FullName.Replace('/', '.')
        };
    }

    static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        // Replace invalid chars with underscore
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i == 0 ? (char.IsLetter(c) || c == '_') : (char.IsLetterOrDigit(c) || c == '_'))
                sb.Append(c);
            else
                sb.Append('_');
        }

        string result = sb.ToString();
        // Prefix with @ if it's a C# keyword
        if (IsCSharpKeyword(result))
            return "@" + result;
        return result;
    }

    static bool IsCSharpKeyword(string s) => s switch
    {
        "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or
        "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default" or
        "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit" or
        "extern" or "false" or "finally" or "fixed" or "float" or "for" or "foreach" or
        "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or "is" or
        "lock" or "long" or "namespace" or "new" or "null" or "object" or "operator" or "out" or
        "override" or "params" or "private" or "protected" or "public" or "readonly" or "ref" or
        "return" or "sbyte" or "sealed" or "short" or "sizeof" or "stackalloc" or "static" or
        "string" or "struct" or "switch" or "this" or "throw" or "true" or "try" or "typeof" or
        "uint" or "ulong" or "unchecked" or "unsafe" or "ushort" or "using" or "virtual" or
        "void" or "volatile" or "while" => true,
        _ => false
    };

    static List<string> ResolveAssemblyPaths(ModuleDefinition module, string[] searchPaths)
    {
        var paths = new List<string>();
        var searchDirs = new List<string>(searchPaths);

        // Add the module's own directory
        if (!string.IsNullOrEmpty(module.FileName))
        {
            var dir = Path.GetDirectoryName(module.FileName);
            if (!string.IsNullOrEmpty(dir))
                searchDirs.Insert(0, dir);
        }

        // Add runtime directories
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir))
            searchDirs.Add(runtimeDir);

        // Try to find each referenced assembly
        foreach (var asmRef in module.AssemblyReferences)
        {
            string? resolved = null;
            foreach (var dir in searchDirs)
            {
                var candidate = Path.Combine(dir, asmRef.Name + ".dll");
                if (File.Exists(candidate))
                {
                    resolved = candidate;
                    break;
                }
            }
            if (resolved != null)
                paths.Add(resolved);
        }

        // Always include core runtime assemblies
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            foreach (var coreAsm in new[] { "mscorlib.dll", "System.Runtime.dll", "System.Private.CoreLib.dll", "netstandard.dll", "System.Console.dll", "System.Collections.dll", "System.Linq.dll" })
            {
                var corePath = Path.Combine(runtimeDir, coreAsm);
                if (File.Exists(corePath) && !paths.Contains(corePath))
                    paths.Add(corePath);
            }
        }

        return paths.Distinct().ToList();
    }
}
