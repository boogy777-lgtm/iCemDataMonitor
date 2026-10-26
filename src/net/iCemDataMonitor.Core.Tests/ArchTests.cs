using System.Reflection;
using System.Xml.Linq;
using iCemDataMonitor.Core.Config;

namespace iCemDataMonitor.Core.Tests;

public sealed class ArchTests
{
    private static readonly Assembly CoreAssembly = typeof(IniConfig).Assembly;
    private static readonly string[] Forbidden =
    [
        "iCemDataMonitor.Data", "iCemDataMonitor.Transport", "iCemDataMonitor.Plugins",
        "iCemDataMonitor.Host", "iCemDataMonitor.UI", "MongoDB", "Microsoft.UI", "System.Windows"
    ];

    private static bool IsForbidden(string name) => Forbidden.Any(prefix =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal));

    [Fact]
    public void CoreProjectReferencesExcludeImplementationLayers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "iCemDataMonitor.Core", "iCemDataMonitor.Core.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var project = XDocument.Load(Path.Combine(root.FullName,
            "iCemDataMonitor.Core", "iCemDataMonitor.Core.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(node => Path.GetFileNameWithoutExtension(((string)node.Attribute("Include")!)
                .Replace('\\', '/'))).ToArray();
        Assert.Contains("iCemDataMonitor.Sdk", references);
        Assert.DoesNotContain(references, IsForbidden);
    }

    [Fact]
    public void CoreAssemblyReferencesExcludeImplementationLayers() =>
        Assert.DoesNotContain(CoreAssembly.GetReferencedAssemblies(), reference => IsForbidden(reference.Name!));

    [Fact]
    public void CoreTypeSignaturesExcludeImplementationLayers()
    {
        foreach (var type in CoreAssembly.GetTypes())
            Assert.DoesNotContain(SignatureTypes(type).SelectMany(ExpandType),
                dependency => IsForbidden(dependency.Namespace ?? ""));
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        yield return type;
        if (type.BaseType is not null) yield return type.BaseType;
        foreach (var implemented in type.GetInterfaces()) yield return implemented;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(flags)) yield return field.FieldType;
        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
            foreach (var argument in method.GetGenericArguments()) yield return argument;
        }
        foreach (var constructor in type.GetConstructors(flags))
            foreach (var parameter in constructor.GetParameters()) yield return parameter.ParameterType;
    }

    private static IEnumerable<Type> ExpandType(Type type) => ExpandType(type, []);

    private static IEnumerable<Type> ExpandType(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type)) yield break;
        yield return type;
        if (type.HasElementType)
            foreach (var nested in ExpandType(type.GetElementType()!, visited)) yield return nested;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var nested in ExpandType(argument, visited)) yield return nested;
        if (type.IsGenericParameter)
            foreach (var constraint in type.GetGenericParameterConstraints())
                foreach (var nested in ExpandType(constraint, visited)) yield return nested;
    }

    private sealed class DependencyVector
    {
        public List<System.Windows.Input.ICommand[]> Commands = [];
    }

    [Fact]
    public void DependencyScanDetectsForbiddenNestedGenericField()
    {
        Assert.False(IsForbidden(typeof(List<>).Namespace!));
        Assert.Contains(SignatureTypes(typeof(DependencyVector)).SelectMany(ExpandType),
            dependency => IsForbidden(dependency.Namespace ?? ""));
    }
}
