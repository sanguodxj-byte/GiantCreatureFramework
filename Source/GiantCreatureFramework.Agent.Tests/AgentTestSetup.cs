using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GiantCreatureFramework.Agent.Tests;

[TestClass]
public static class AgentTestSetup
{
    public static string ModRoot { get; private set; } = string.Empty;

    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var skillsPath = Path.Combine(dir.FullName, "1.7", "AgentSkills");
            var keyedPath = Path.Combine(dir.FullName, "1.7", "Languages", "ChineseSimplified", "Keyed", "GiantCreatureFramework_Agent_Keys.xml");
            if (Directory.Exists(skillsPath) && File.Exists(keyedPath))
            {
                ModRoot = dir.FullName;
                AgentSkillStore.InitializeFromModRoot(dir.FullName);
                var keyed = XDocument.Load(keyedPath).Root!.Elements()
                    .ToDictionary(e => e.Name.LocalName, e => e.Value.Replace("\\n", "\n"), StringComparer.Ordinal);
                UiText.Resolver = key => keyed.TryGetValue(key, out var val) ? val : key;
                return;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GiantCreatureFramework 1.7/AgentSkills from test runner base directory.");
    }
}
