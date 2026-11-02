using System.Reflection;
using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Locks the public API surface of the library. The approved file is a sorted
/// reflection snapshot of every exported type and declared public member; any
/// accidental rename, removal or signature change fails until the change is
/// reviewed and the baseline regenerated with
/// <c>XISO_UPDATE_PUBLIC_API=1 dotnet test --filter PublicApi</c>.
/// </summary>
[Collection("Sequential")]
public class PublicApiTests
{
    [Fact]
    public void PublicApi_MatchesApprovedBaseline()
    {
        string actual = Capture(typeof(XisoReader).Assembly);
        string approvedPath = Path.Combine(AppContext.BaseDirectory, "PublicApi.approved.txt");

        if (string.Equals(Environment.GetEnvironmentVariable("XISO_UPDATE_PUBLIC_API"), "1",
                StringComparison.Ordinal))
        {
            File.WriteAllText(approvedPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(File.Exists(approvedPath), $"Missing public-API baseline: {approvedPath}");
        string expected = File.ReadAllText(approvedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            string actualPath = Path.Combine(AppContext.BaseDirectory, "PublicApi.actual.txt");
            File.WriteAllText(actualPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail($"Public API changed. Review the diff in {actualPath} and update PublicApi.approved.txt.");
        }
    }

    private static string Capture(Assembly assembly)
    {
        List<string> lines = [];
        foreach (Type type in assembly.GetExportedTypes().OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            string typeName = type.FullName ?? type.Name;
            lines.Add($"T:{typeName}");

            foreach (ConstructorInfo ctor in type
                         .GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(static c => ParameterList(c.GetParameters()), StringComparer.Ordinal))
            {
                lines.Add($"C:{typeName}({ParameterList(ctor.GetParameters())})");
            }

            foreach (MethodInfo method in type
                         .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.DeclaredOnly)
                         .Where(static m => !m.IsSpecialName)
                         .OrderBy(static m => m.Name, StringComparer.Ordinal)
                         .ThenBy(static m => ParameterList(m.GetParameters()), StringComparer.Ordinal))
            {
                lines.Add($"M:{typeName}.{method.Name}({ParameterList(method.GetParameters())})->{TypeName(method.ReturnType)}");
            }

            foreach (PropertyInfo property in type
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.DeclaredOnly)
                         .OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                string access = (property.GetMethod is null ? "" : "get;") +
                                (property.SetMethod is null ? "" : "set;");
                lines.Add($"P:{typeName}.{property.Name}:{TypeName(property.PropertyType)}:{access}");
            }

            foreach (FieldInfo field in type
                         .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                    BindingFlags.DeclaredOnly)
                         .OrderBy(static f => f.Name, StringComparer.Ordinal))
            {
                lines.Add($"F:{typeName}.{field.Name}:{TypeName(field.FieldType)}");
            }

            foreach (EventInfo evt in type
                         .GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                    BindingFlags.DeclaredOnly)
                         .OrderBy(static e => e.Name, StringComparer.Ordinal))
            {
                lines.Add($"E:{typeName}.{evt.Name}:{TypeName(evt.EventHandlerType ?? typeof(object))}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    private static string ParameterList(ParameterInfo[] parameters) =>
        string.Join(",", parameters.Select(static p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + TypeName(p.ParameterType)));

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            string definition = type.GetGenericTypeDefinition().FullName ?? type.GetGenericTypeDefinition().Name;
            int tick = definition.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                definition = definition[..tick];
            }

            return definition + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return type.FullName ?? type.Name;
    }
}
