using Autopilot.Core.Config;

namespace Autopilot.Cli.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void Parse_allows_comments_and_trailing_commas_and_keeps_defaults()
    {
        var config = ConfigLoader.Parse("""
            {
              // project settings
              "baseBranch": "develop",
              "commands": { "build": "make", },
              "writablePaths": ["app/**",],
            }
            """);

        Assert.Equal("develop", config.BaseBranch);
        Assert.Equal("make", config.Commands.Build);
        Assert.Equal("dotnet test", config.Commands.Test);
        Assert.Equal(["app/**"], config.WritablePaths);
        Assert.Equal(6, config.Gate.MaxFilesChanged);
        Assert.Equal("local", config.Modules.WorkItems.Type);
        Assert.Equal(["Read", "Grep", "Glob"], config.Roles.Triage.AllowedTools);
    }

    [Fact]
    public void Parse_reports_bad_json_in_plain_words()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("{ \"baseBranch\": ", "x.json"));
        Assert.Contains("x.json is not valid JSON", ex.Message);
    }

    [Fact]
    public void LoadFile_missing_file_says_to_run_init()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "autopilot.json")));
        Assert.Contains("autopilot init", ex.Message);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        using var temp = new TempFolder();
        var config = new AutopilotConfig
        {
            BaseBranch = "trunk",
            WritablePaths = ["src/**"],
            Gate = new GateConfig { MaxFilesChanged = 3 },
            Modules = new ModulesConfig { Diagnostics = new ModuleConfig { Type = "jsonl", Settings = new Dictionary<string, string> { ["Path"] = "log.jsonl" } } },
        };

        ConfigLoader.Save(config, temp.Path);
        var text = File.ReadAllText(ConfigLoader.ConfigPath(temp.Path));
        var loaded = ConfigLoader.Load(temp.Path);

        Assert.Contains("\"baseBranch\": \"trunk\"", text);
        Assert.Contains("\"Path\"", text); // module setting keys are kept as written
        Assert.Equal("trunk", loaded.BaseBranch);
        Assert.Equal(3, loaded.Gate.MaxFilesChanged);
        Assert.Equal("log.jsonl", loaded.Modules.Diagnostics.Settings["Path"]);
        Assert.Empty(ConfigLoader.Validate(loaded));
    }

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(ConfigLoader.Validate(new AutopilotConfig()));
    }

    [Fact]
    public void Validate_lists_every_problem()
    {
        var config = ConfigLoader.Parse("""
            {
              "commands": { "build": "", "test": " " },
              "writablePaths": [],
              "gate": { "maxFilesChanged": 0, "maxSourceLinesChanged": -1 },
              "roles": { "reviewer": { "maxToolCalls": 0, "allowedTools": ["Read"] } },
              "modules": {
                "workItems": { "type": "jira" },
                "publisher": { "type": "azure-devops" },
                "diagnostics": { "type": "none" }
              }
            }
            """);

        var problems = ConfigLoader.Validate(config);

        Assert.Contains(problems, p => p.StartsWith("commands.build is empty", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("commands.test is empty", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("writablePaths is empty", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("gate.maxFilesChanged is 0", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("gate.maxSourceLinesChanged is -1", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("roles.reviewer.maxToolCalls is 0", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("\"jira\" is unknown", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("\"azure-devops\" is not available yet", StringComparison.Ordinal));
        Assert.Equal(8, problems.Count);
    }

    [Theory]
    [InlineData(".autopilot/**")]
    [InlineData("./.autopilot/autopilot.json")]
    [InlineData(".autopilot")]
    [InlineData("**/.autopilot/**")]
    [InlineData(".*/**")]
    public void Validate_rejects_writable_paths_into_autopilot_folder(string pattern)
    {
        var problems = ConfigLoader.Validate(new AutopilotConfig { WritablePaths = ["src/**", pattern] });
        var problem = Assert.Single(problems);
        Assert.Contains("points into .autopilot/", problem);
    }

    [Theory]
    [InlineData("**")]
    [InlineData("src/**")]
    [InlineData("autopilot/**")]
    public void Validate_allows_other_writable_paths(string pattern)
    {
        Assert.Empty(ConfigLoader.Validate(new AutopilotConfig { WritablePaths = [pattern] }));
    }
}
