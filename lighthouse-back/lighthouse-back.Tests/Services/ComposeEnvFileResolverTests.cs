using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Lighthouse.Tests.Services;

public class ComposeEnvFileResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ComposeEnvFileResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lighthouse-envfile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private (ComposeEnvFileResolver resolver, IServiceProvider provider) CreateResolver(string? globalEnvValue)
    {
        string dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();

        if (globalEnvValue != null)
        {
            using IServiceScope scope = provider.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppSettings.Add(new AppSetting
            {
                Key = ComposeEnvFileResolver.ComposeGlobalEnvFileKey,
                Value = globalEnvValue
            });
            db.SaveChanges();
        }

        var logger = new Mock<ILogger<ComposeEnvFileResolver>>();
        var resolver = new ComposeEnvFileResolver(provider, logger.Object);
        return (resolver, provider);
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_NoGlobalSetting_ReturnsEmpty()
    {
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: null);

        string args = await resolver.BuildEnvFileArgsAsync(_tempDir);

        args.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_EmptyGlobalSetting_ReturnsEmpty()
    {
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: "   ");

        string args = await resolver.BuildEnvFileArgsAsync(_tempDir);

        args.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_GlobalFileMissing_IsIgnored()
    {
        string missing = Path.Combine(_tempDir, "does-not-exist.env");
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: missing);

        string args = await resolver.BuildEnvFileArgsAsync(_tempDir);

        args.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_GlobalFileExists_AddsEnvFileFlag()
    {
        string global = Path.Combine(_tempDir, "global.env");
        File.WriteAllText(global, "FOO=bar");
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: global);

        // composeDir has no adjacent .env
        string composeDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(composeDir);

        string args = await resolver.BuildEnvFileArgsAsync(composeDir);

        args.Should().Be($"--env-file \"{global}\" ");
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_GlobalAndAdjacent_AppendsAdjacentLastForPrecedence()
    {
        string global = Path.Combine(_tempDir, "global.env");
        File.WriteAllText(global, "FOO=bar");

        string composeDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(composeDir);
        string adjacent = Path.Combine(composeDir, ".env");
        File.WriteAllText(adjacent, "FOO=local");

        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: global);

        string args = await resolver.BuildEnvFileArgsAsync(composeDir);

        // Global first, adjacent last so docker lets project-local values win.
        args.Should().Be($"--env-file \"{global}\" --env-file \"{adjacent}\" ");
    }

    [Fact]
    public async Task BuildEnvFileArgsAsync_AdjacentWithoutGlobal_ReturnsEmpty()
    {
        // Without a global file we rely on docker's default .env auto-discovery (the caller sets
        // the working directory), so no explicit --env-file flag should be produced.
        string composeDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(composeDir);
        File.WriteAllText(Path.Combine(composeDir, ".env"), "FOO=local");

        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: null);

        string args = await resolver.BuildEnvFileArgsAsync(composeDir);

        args.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEnvFilePathsAsync_NoGlobalNoAdjacent_ReturnsEmpty()
    {
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: null);

        IReadOnlyList<string> paths = await resolver.GetEnvFilePathsAsync(_tempDir);

        paths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEnvFilePathsAsync_AdjacentWithoutGlobal_ReturnsAdjacent()
    {
        // Unlike BuildEnvFileArgsAsync (which relies on docker's .env auto-discovery), callers that
        // interpolate themselves need the adjacent .env explicitly.
        string composeDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(composeDir);
        string adjacent = Path.Combine(composeDir, ".env");
        File.WriteAllText(adjacent, "FOO=local");
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: null);

        IReadOnlyList<string> paths = await resolver.GetEnvFilePathsAsync(composeDir);

        paths.Should().Equal(adjacent);
    }

    [Fact]
    public async Task GetEnvFilePathsAsync_GlobalAndAdjacent_GlobalFirst()
    {
        string global = Path.Combine(_tempDir, "global.env");
        File.WriteAllText(global, "FOO=bar");
        string composeDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(composeDir);
        string adjacent = Path.Combine(composeDir, ".env");
        File.WriteAllText(adjacent, "FOO=local");
        (ComposeEnvFileResolver resolver, _) = CreateResolver(globalEnvValue: global);

        IReadOnlyList<string> paths = await resolver.GetEnvFilePathsAsync(composeDir);

        paths.Should().Equal(global, adjacent);
    }
}
