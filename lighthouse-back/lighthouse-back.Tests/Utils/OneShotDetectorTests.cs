using Docker.DotNet.Models;
using FluentAssertions;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

public class OneShotDetectorTests
{
    private const string Project = "hermes";

    private static ContainerListResponse MakeContainer(
        string id,
        string service,
        string state = "exited",
        string? status = null,
        string? dependsOn = null,
        string? oneShotLabel = null,
        string project = Project)
    {
        var labels = new Dictionary<string, string>
        {
            [OneShotDetector.ComposeProjectLabel] = project,
            [OneShotDetector.ComposeServiceLabel] = service,
        };
        if (dependsOn != null) labels[OneShotDetector.DependsOnLabel] = dependsOn;
        if (oneShotLabel != null) labels[OneShotDetector.OneShotLabel] = oneShotLabel;

        return new ContainerListResponse { ID = id, State = state, Status = status, Labels = labels };
    }

    // Real Compose v5 output: depends_on label of the dependent container.
    private const string WebDependsOnSrc = "agent-src:service_completed_successfully:true";

    // --- ParseDependsOn ---

    [Fact]
    public void ParseDependsOn_ParsesComposeV2Format()
    {
        IReadOnlyList<ComposeDependsOnEntry> entries = OneShotDetector.ParseDependsOn(
            "agent-src:service_completed_successfully:true,db:service_healthy:false");

        entries.Should().BeEquivalentTo(new[]
        {
            new ComposeDependsOnEntry("agent-src", "service_completed_successfully", true),
            new ComposeDependsOnEntry("db", "service_healthy", false),
        });
    }

    [Fact]
    public void ParseDependsOn_MissingRestartPart_DefaultsToFalse()
    {
        OneShotDetector.ParseDependsOn("src:service_completed_successfully")
            .Should().ContainSingle()
            .Which.Should().Be(new ComposeDependsOnEntry("src", "service_completed_successfully", false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseDependsOn_EmptyLabel_ReturnsNothing(string? label)
    {
        OneShotDetector.ParseDependsOn(label).Should().BeEmpty();
    }

    // --- FindCandidates / ResolveAsync (detection) ---

    [Fact]
    public void FindCandidates_ExplicitLabelTrue_IsExplicitCandidate()
    {
        var containers = new[] { MakeContainer("c1", "migrate", oneShotLabel: "true") };

        OneShotDetector.FindCandidates(containers).Should().Equal(new Dictionary<string, bool> { ["c1"] = true });
    }

    [Fact]
    public void FindCandidates_ServiceAwaitedWithCompletedSuccessfully_IsHeuristicCandidate()
    {
        var containers = new[]
        {
            MakeContainer("src", "agent-src"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };

        OneShotDetector.FindCandidates(containers).Should().Equal(new Dictionary<string, bool> { ["src"] = false });
    }

    [Fact]
    public void FindCandidates_LabelFalse_DisablesHeuristic()
    {
        var containers = new[]
        {
            MakeContainer("src", "agent-src", oneShotLabel: "false"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };

        OneShotDetector.FindCandidates(containers).Should().BeEmpty();
    }

    [Fact]
    public void FindCandidates_OtherConditions_AreNotOneShots()
    {
        var containers = new[]
        {
            MakeContainer("db", "db", state: "running"),
            MakeContainer("web", "webui", state: "running", dependsOn: "db:service_healthy:false"),
        };

        OneShotDetector.FindCandidates(containers).Should().BeEmpty();
    }

    [Fact]
    public void FindCandidates_DependencyInAnotherProject_IsIgnored()
    {
        var containers = new[]
        {
            MakeContainer("src", "agent-src", project: "other"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };

        OneShotDetector.FindCandidates(containers).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "always", true)]   // explicit label wins over the restart policy
    [InlineData(false, "no", true)]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "always", false)]
    [InlineData(false, "unless-stopped", false)]
    [InlineData(false, "on-failure", false)]
    public void IsOneShot_AppliesRestartPolicyToHeuristicOnly(bool explicitLabel, string? restartPolicy, bool expected)
    {
        OneShotDetector.IsOneShot(explicitLabel, restartPolicy).Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_HeuristicWithRestartNo_ResolvesCompletedOneShot()
    {
        DateTime finished = new(2026, 11, 18, 18, 20, 51, DateTimeKind.Utc);
        var containers = new[]
        {
            MakeContainer("src", "agent-src", status: "Exited (0) 3 seconds ago"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };
        List<string> inspected = new();

        Dictionary<string, OneShotInfo> result = await OneShotDetector.ResolveAsync(containers, id =>
        {
            inspected.Add(id);
            return Task.FromResult<OneShotInspectData?>(new OneShotInspectData("no", 0, finished));
        });

        inspected.Should().Equal("src"); // only candidates are inspected
        result.Should().ContainKey("src").WhoseValue
            .Should().Be(new OneShotInfo(OneShotRunStatus.Completed, 0, finished));
        result.Should().NotContainKey("web");
    }

    [Fact]
    public async Task ResolveAsync_HeuristicWithRestartAlways_IsNotOneShot()
    {
        var containers = new[]
        {
            MakeContainer("src", "agent-src"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };

        Dictionary<string, OneShotInfo> result = await OneShotDetector.ResolveAsync(containers,
            _ => Task.FromResult<OneShotInspectData?>(new OneShotInspectData("always", 0, null)));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ExitNonZero_IsFailed()
    {
        var containers = new[] { MakeContainer("bad", "migrate", status: "Exited (3) 4 seconds ago", oneShotLabel: "true") };

        Dictionary<string, OneShotInfo> result = await OneShotDetector.ResolveAsync(containers,
            _ => Task.FromResult<OneShotInspectData?>(new OneShotInspectData("no", 3, DateTime.UtcNow)));

        result["bad"].Status.Should().Be(OneShotRunStatus.Failed);
        result["bad"].ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task ResolveAsync_Running_IsRunningWithoutFinishTime()
    {
        var containers = new[] { MakeContainer("c1", "migrate", state: "running", status: "Up 2 seconds", oneShotLabel: "true") };

        Dictionary<string, OneShotInfo> result = await OneShotDetector.ResolveAsync(containers,
            _ => Task.FromResult<OneShotInspectData?>(new OneShotInspectData("no", 0, DateTime.UtcNow)));

        result["c1"].Status.Should().Be(OneShotRunStatus.Running);
        result["c1"].FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_InspectFails_KeepsLabelledAndDropsHeuristic()
    {
        var containers = new[]
        {
            MakeContainer("labelled", "migrate", status: "Exited (0) 1 minute ago", oneShotLabel: "true"),
            MakeContainer("src", "agent-src", status: "Exited (0) 1 minute ago"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
        };

        Dictionary<string, OneShotInfo> result = await OneShotDetector.ResolveAsync(containers,
            _ => throw new InvalidOperationException("docker down"));

        result.Keys.Should().Equal("labelled");
        result["labelled"].Status.Should().Be(OneShotRunStatus.Completed); // exit code from the status string
    }

    // --- Run status ---

    [Theory]
    [InlineData("exited", 0L, OneShotRunStatus.Completed)]
    [InlineData("exited", 1L, OneShotRunStatus.Failed)]
    [InlineData("exited", null, OneShotRunStatus.Failed)]
    [InlineData("Exited", 0L, OneShotRunStatus.Completed)]
    [InlineData("running", null, OneShotRunStatus.Running)]
    [InlineData("restarting", null, OneShotRunStatus.Running)]
    [InlineData("created", null, OneShotRunStatus.Pending)]
    [InlineData("dead", 137L, OneShotRunStatus.Failed)]
    public void GetRunStatus_MapsStateAndExitCode(string state, long? exitCode, OneShotRunStatus expected)
    {
        OneShotDetector.GetRunStatus(state, exitCode).Should().Be(expected);
    }

    [Theory]
    [InlineData("Exited (0) 3 seconds ago", 0L)]
    [InlineData("Exited (137) 2 hours ago", 137L)]
    [InlineData("Up 5 minutes", null)]
    [InlineData("Created", null)]
    [InlineData(null, null)]
    public void ParseExitCodeFromStatus_ReadsListStatus(string? status, long? expected)
    {
        OneShotDetector.ParseExitCodeFromStatus(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("maybe", null)]
    public void ReadOneShotLabel_ParsesBooleans(string value, bool? expected)
    {
        OneShotDetector.ReadOneShotLabel(new Dictionary<string, string> { [OneShotDetector.OneShotLabel] = value })
            .Should().Be(expected);
    }

    // --- Restart dependents ---

    [Fact]
    public void GetRestartDependents_ReturnsOnlyRestartTrueDependents()
    {
        var containers = new[]
        {
            MakeContainer("src", "agent-src"),
            MakeContainer("web", "webui", state: "running", dependsOn: WebDependsOnSrc),
            MakeContainer("worker", "worker", state: "running", dependsOn: "agent-src:service_completed_successfully:false"),
            MakeContainer("other", "webui", state: "running", dependsOn: WebDependsOnSrc, project: "other"),
        };

        OneShotDetector.GetRestartDependents(containers, Project, "agent-src").Should().Equal("webui");
    }
}
