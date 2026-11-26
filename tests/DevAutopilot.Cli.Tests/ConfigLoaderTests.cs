using DevAutopilot.Core.Config;

namespace DevAutopilot.Cli.Tests;

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
        Assert.DoesNotContain(problems, p => p.Contains("not available", StringComparison.Ordinal));
        Assert.Equal(7, problems.Count);
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

    [Fact]
    public void Partial_role_keeps_the_role_defaults()
    {
        var config = ConfigLoader.Parse("""
            {
              "roles": {
                "triage": { "model": "haiku" },
                "Implementer": { "maxToolCalls": 5 }
              },
              "gate": { "maxFilesChanged": 3 },
              "modules": { "publisher": { "settings": { "x": "y" } } }
            }
            """);

        Assert.Equal("haiku", config.Roles.Triage.Model);
        Assert.Equal(RoleConfig.TriageDefault.AllowedTools, config.Roles.Triage.AllowedTools);
        Assert.Equal(RoleConfig.TriageDefault.MaxToolCalls, config.Roles.Triage.MaxToolCalls);
        Assert.Equal(5, config.Roles.Implementer.MaxToolCalls);
        Assert.Equal(RoleConfig.ImplementerDefault.AllowedCommands, config.Roles.Implementer.AllowedCommands);
        Assert.Equal(RoleConfig.ReviewerDefault.AllowedTools, config.Roles.Reviewer.AllowedTools);
        Assert.Equal(3, config.Gate.MaxFilesChanged);
        Assert.Equal(150, config.Gate.MaxSourceLinesChanged);
        Assert.Equal("local", config.Modules.Publisher.Type);
        Assert.Equal("y", config.Modules.Publisher.Settings["x"]);
        Assert.Empty(ConfigLoader.Validate(config));
    }

    [Fact]
    public void Arrays_in_the_file_replace_the_defaults()
    {
        var config = ConfigLoader.Parse("""{ "roles": { "reviewer": { "allowedTools": ["Read"] } }, "riskPaths": ["x/**"] }""");

        Assert.Equal(["Read"], config.Roles.Reviewer.AllowedTools);
        Assert.Equal(["x/**"], config.RiskPaths);
    }

    [Fact]
    public void Null_section_is_still_reported_as_missing()
    {
        var config = ConfigLoader.Parse("""{ "roles": null }""");

        Assert.Contains(ConfigLoader.Validate(config), p => p.StartsWith("roles is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_is_not_an_object_is_refused()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("[1, 2]", "x.json"));
        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void Saved_file_keeps_quotes_and_plus_readable()
    {
        using var temp = new TempFolder();
        ConfigLoader.Save(new AutopilotConfig { Commands = new CommandsConfig { Build = "make \"all\" C++" } }, temp.Path);

        var text = File.ReadAllText(ConfigLoader.ConfigPath(temp.Path));

        Assert.Contains("C++", text);
        Assert.Contains("\\\"all\\\"", text);
        Assert.DoesNotContain(@"\u0022", text);
        Assert.DoesNotContain(@"\u002B", text);
        Assert.Equal("make \"all\" C++", ConfigLoader.Load(temp.Path).Commands.Build);
    }

    [Fact]
    public void Default_risk_paths_cover_props_and_targets()
    {
        Assert.Contains("**/*.props", new AutopilotConfig().RiskPaths);
        Assert.Contains("**/*.targets", new AutopilotConfig().RiskPaths);
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("anthropic_base_url")]
    [InlineData("AUTOPILOT_ADO_PAT")]
    public void Pass_environment_refuses_anthropic_and_autopilot_names(string name)
    {
        var config = new AutopilotConfig { PassEnvironment = ["JAVA_HOME", name] };

        var problem = Assert.Single(ConfigLoader.Validate(config));
        Assert.Contains(name, problem);
        Assert.Contains("never passed", problem);
    }

    [Fact]
    public void Pass_environment_defaults_to_empty_and_reads_from_the_file()
    {
        Assert.Empty(new AutopilotConfig().PassEnvironment);
        Assert.Equal(["JAVA_HOME"], ConfigLoader.Parse("""{ "passEnvironment": ["JAVA_HOME"] }""").PassEnvironment);
    }
}
