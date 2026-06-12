using DotNetAgentDev.Infrastructure;

namespace DotNetAgentDev.Tests;

public sealed class DotEnvConfigurationTests
{
    [Fact]
    public void Load_ParsesCommonDotEnvSyntaxWithoutExposingComments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotenv-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(
                path,
                """
                # comment
                DEEPSEEK_API_KEY="secret-value"
                export DeepSeek__Model=deepseek-test
                PLAIN=value # inline comment
                """);

            var values = DotEnvConfiguration.Load(path);

            Assert.Equal("secret-value", values["DEEPSEEK_API_KEY"]);
            Assert.Equal("deepseek-test", values["DeepSeek:Model"]);
            Assert.Equal("value", values["PLAIN"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindFile_FindsRepositoryLevelDotEnv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotenv-root-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Project");
        Directory.CreateDirectory(project);
        var path = Path.Combine(root, ".env");
        File.WriteAllText(path, "KEY=value");

        try
        {
            Assert.Equal(path, DotEnvConfiguration.FindFile(project, root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
