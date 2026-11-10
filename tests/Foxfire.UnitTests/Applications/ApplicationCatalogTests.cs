using System.Text;
using Foxfire.Server.Applications;
using Foxfire.Server.Security;
using Microsoft.Extensions.Configuration;

namespace Foxfire.UnitTests.Applications;

public sealed class ApplicationCatalogTests
{
    private const string AppsJson = """
        {
          "Applications": [
            { "Name": "Foxfire",
              "WebhookUrl": "https://foxfire.alluna.dev/api/uploads",
              "AllowedOrigins": ["https://foxfire.alluna.dev", "https://FOXFIRE.alluna.dev/"] },
            { "Name": "Blog", "DisplayName": "My Blog" }
          ]
        }
        """;

    private static IConfigurationRoot Build(string json, IDictionary<string, string?> memory) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .AddInMemoryCollection(memory)
            .Build();

    private static FoxfireConfigurationException LoadFails(IConfiguration configuration)
    {
        var ex = Assert.Throws<FoxfireConfigurationException>(() => ApplicationCatalog.Load(configuration, allowInsecureWebhooks: false));
        Assert.NotEmpty(ex.Problems);
        return ex;
    }

    [Fact]
    public void Loads_apps_from_json_and_keys_from_real_environment_variables_matched_by_name()
    {
        var prefix = $"FFTEST_{Guid.NewGuid():N}_";
        var foxfireKey = TestKeys.New();
        var blogKey = TestKeys.New();
        Environment.SetEnvironmentVariable($"{prefix}ApiKeys__Foxfire", foxfireKey);
        Environment.SetEnvironmentVariable($"{prefix}ApiKeys__blog", blogKey); // name match is case-insensitive
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(AppsJson)))
                .AddEnvironmentVariables(prefix)
                .Build();

            var catalog = ApplicationCatalog.Load(configuration, allowInsecureWebhooks: false);

            Assert.Equal(2, catalog.Applications.Count);
            var foxfire = catalog.Find("foxfire")!;
            Assert.Equal(foxfireKey, foxfire.ApiKey.Reveal());
            Assert.Equal(new Uri("https://foxfire.alluna.dev/api/uploads"), foxfire.WebhookUrl);
            Assert.Equal(["https://foxfire.alluna.dev"], foxfire.AllowedOrigins);
            Assert.Equal("Foxfire", foxfire.DisplayName);
            Assert.Equal("My Blog", catalog.Find("Blog")!.DisplayName);
            Assert.Equal(blogKey, catalog.Find("Blog")!.ApiKey.Reveal());
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{prefix}ApiKeys__Foxfire", null);
            Environment.SetEnvironmentVariable($"{prefix}ApiKeys__blog", null);
        }
    }

    [Fact]
    public void Reordering_the_json_does_not_change_which_key_belongs_to_which_app()
    {
        var keys = new Dictionary<string, string?> { ["ApiKeys:A1"] = TestKeys.New(), ["ApiKeys:B1"] = TestKeys.New() };
        var forward = Build("""{"Applications":[{"Name":"A1"},{"Name":"B1"}]}""", keys);
        var reversed = Build("""{"Applications":[{"Name":"B1"},{"Name":"A1"}]}""", keys);

        var a = ApplicationCatalog.Load(forward, false);
        var b = ApplicationCatalog.Load(reversed, false);

        Assert.Equal(a.Find("A1")!.ApiKey.Reveal(), b.Find("A1")!.ApiKey.Reveal());
        Assert.Equal(a.Find("B1")!.ApiKey.Reveal(), b.Find("B1")!.ApiKey.Reveal());
    }

    [Fact]
    public void Literal_api_key_inside_an_application_entry_is_refused()
    {
        var key = TestKeys.New();
        var json = $$"""{"Applications":[{"Name":"Foxfire","WebhookUrl":"https://x.dev/h","ApiKey":"{{key}}"}]}""";
        var configuration = Build(json, new Dictionary<string, string?> { ["ApiKeys:Foxfire"] = TestKeys.New() });

        var ex = LoadFails(configuration);

        Assert.Contains(ex.Problems, p => p.Contains("must come from the environment", StringComparison.Ordinal));
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKeys_section_in_a_json_file_is_refused()
    {
        var key = TestKeys.New();
        var path = Path.Combine(Path.GetTempPath(), $"foxfire-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$$"""{"Applications":[{"Name":"Foxfire"}],"ApiKeys":{"Foxfire":"{{{key}}}"}}""");
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

            var ex = LoadFails(configuration);

            Assert.Contains(ex.Problems, p => p.Contains("configuration file", StringComparison.Ordinal));
            Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApiKeys_from_the_command_line_are_refused()
    {
        var key = TestKeys.New();
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{"Applications":[{"Name":"Foxfire"}]}""")))
            .AddCommandLine([$"--ApiKeys:Foxfire={key}"])
            .Build();

        var ex = LoadFails(configuration);

        Assert.Contains(ex.Problems, p => p.Contains("command line", StringComparison.Ordinal));
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_key_for_a_declared_app_is_refused() =>
        Assert.Contains(
            LoadFails(Build(AppsJson, new Dictionary<string, string?> { ["ApiKeys:Foxfire"] = TestKeys.New() })).Problems,
            p => p.Contains("ApiKeys__Blog", StringComparison.Ordinal));

    [Fact]
    public void Key_without_a_declared_app_is_refused() =>
        Assert.Contains(
            LoadFails(Build("""{"Applications":[]}""", new Dictionary<string, string?> { ["ApiKeys:Ghost"] = TestKeys.New() })).Problems,
            p => p.Contains("ApiKeys__Ghost", StringComparison.Ordinal));

    [Fact]
    public void Duplicate_names_are_refused() =>
        Assert.Contains(
            LoadFails(Build("""{"Applications":[{"Name":"Foxfire"},{"Name":"FOXFIRE"}]}""",
                new Dictionary<string, string?> { ["ApiKeys:Foxfire"] = TestKeys.New() })).Problems,
            p => p.Contains("more than once", StringComparison.Ordinal));

    [Fact]
    public void The_same_key_for_two_apps_is_refused_without_printing_it()
    {
        var shared = TestKeys.New();

        var ex = LoadFails(Build("""{"Applications":[{"Name":"A1"},{"Name":"B1"}]}""",
            new Dictionary<string, string?> { ["ApiKeys:A1"] = shared, ["ApiKeys:B1"] = shared }));

        Assert.Contains(ex.Problems, p => p.Contains("identical", StringComparison.Ordinal));
        Assert.DoesNotContain(shared, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Weak_key_is_refused_without_printing_it()
    {
        const string weak = "FM_as4Hu8s02j";

        var ex = LoadFails(Build("""{"Applications":[{"Name":"Foxfire"}]}""",
            new Dictionary<string, string?> { ["ApiKeys:Foxfire"] = weak }));

        Assert.DoesNotContain(weak, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Applications":[{"Name":"my-app"}]}""", "Name is required")]
    [InlineData("""{"Applications":[{"Name":"A1","WebhookUrl":"http://insecure.dev/hook"}]}""", "WebhookUrl")]
    [InlineData("""{"Applications":[{"Name":"A1","AllowedOrigins":["https://a.dev/path"]}]}""", "AllowedOrigins")]
    public void Invalid_application_fields_are_refused(string json, string fragment)
    {
        var ex = LoadFails(Build(json, new Dictionary<string, string?> { ["ApiKeys:A1"] = TestKeys.New(), ["ApiKeys:my-app"] = TestKeys.New() }));

        Assert.Contains(ex.Problems, p => p.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Http_webhooks_are_allowed_only_when_explicitly_enabled()
    {
        var configuration = Build("""{"Applications":[{"Name":"A1","WebhookUrl":"http://localhost:9000/hook"}]}""",
            new Dictionary<string, string?> { ["ApiKeys:A1"] = TestKeys.New() });

        var catalog = ApplicationCatalog.Load(configuration, allowInsecureWebhooks: true);

        Assert.Equal("http", catalog.Find("A1")!.WebhookUrl!.Scheme);
    }

    [Fact]
    public void No_applications_is_a_valid_configuration() =>
        Assert.Empty(ApplicationCatalog.Load(new ConfigurationBuilder().Build(), false).Applications);
}
