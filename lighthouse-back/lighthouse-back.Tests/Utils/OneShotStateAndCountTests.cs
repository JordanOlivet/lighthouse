using FluentAssertions;
using Lighthouse.DTOs;
using Lighthouse.src.Utils;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

/// <summary>
/// Project state, counters and update counts must not treat a successful one-shot as stopped.
/// </summary>
public class OneShotStateAndCountTests
{
    private static ComposeServiceDto Service(string name, string state, bool oneShot = false, string? oneShotStatus = null) =>
        new(Id: name, Name: name, Image: "img", State: state, Status: "", Ports: new List<string>(), Health: null,
            ServiceName: name, IsOneShot: oneShot, OneShotStatus: oneShotStatus);

    [Fact]
    public void DetermineState_CompletedOneShotWithRunningServices_IsRunning()
    {
        var services = new List<ComposeServiceDto>
        {
            Service("webui", "Running"),
            Service("agent-src", "Exited", oneShot: true, oneShotStatus: "Completed"),
        };

        StateHelper.DetermineStateFromServices(services).Should().Be(EntityState.Running);
    }

    [Fact]
    public void DetermineState_FailedOneShot_DegradesProject()
    {
        var services = new List<ComposeServiceDto>
        {
            Service("webui", "Running"),
            Service("agent-src", "Exited", oneShot: true, oneShotStatus: "Failed"),
        };

        StateHelper.DetermineStateFromServices(services).Should().Be(EntityState.Degraded);
    }

    [Fact]
    public void DetermineState_RegularExitedService_StillDegradesProject()
    {
        var services = new List<ComposeServiceDto>
        {
            Service("webui", "Running"),
            Service("worker", "Exited"),
        };

        StateHelper.DetermineStateFromServices(services).Should().Be(EntityState.Degraded);
    }

    [Fact]
    public void DetermineState_StoppedProject_StaysExited()
    {
        var services = new List<ComposeServiceDto>
        {
            Service("webui", "Exited"),
            Service("agent-src", "Exited", oneShot: true, oneShotStatus: "Completed"),
        };

        StateHelper.DetermineStateFromServices(services).Should().Be(EntityState.Exited);
    }

    [Fact]
    public void DetermineState_OnlyCompletedOneShots_FallsBackToRawState()
    {
        var services = new List<ComposeServiceDto>
        {
            Service("agent-src", "Exited", oneShot: true, oneShotStatus: "Completed"),
        };

        StateHelper.DetermineStateFromServices(services).Should().Be(EntityState.Exited);
    }

    [Theory]
    [InlineData(true, "Completed", true)]
    [InlineData(true, "Pending", true)]
    [InlineData(true, "Failed", false)]
    [InlineData(true, "Running", false)]
    [InlineData(false, null, false)]
    public void IsIdleOneShot_Container(bool oneShot, string? status, bool expected)
    {
        var container = new ContainerDto("id", "name", "img", "", "Exited", DateTime.UtcNow, null,
            IsOneShot: oneShot, OneShotStatus: status);

        StateHelper.IsIdleOneShot(container).Should().Be(expected);
    }

    private static ImageUpdateStatus Update(string service, string image, bool oneShot = false, bool available = true, string? policy = null) =>
        new(image, service, "amd64", null, null, null, null, available, true, policy, false, false, null, IsOneShot: oneShot);

    [Fact]
    public void CountServicesWithUpdates_OneShotSharingImage_CountsOnce()
    {
        var images = new[]
        {
            Update("webui", "nousresearch/hermes-agent:latest"),
            Update("agent-src", "nousresearch/hermes-agent:latest", oneShot: true),
        };

        UpdateCountHelper.CountServicesWithUpdates(images).Should().Be(1);
    }

    [Fact]
    public void CountServicesWithUpdates_OneShotWithOwnImage_IsCounted()
    {
        var images = new[]
        {
            Update("webui", "nginx:latest"),
            Update("migrate", "flyway:latest", oneShot: true),
        };

        UpdateCountHelper.CountServicesWithUpdates(images).Should().Be(2);
    }

    [Fact]
    public void CountServicesWithUpdates_SharedImageWithoutUpdateOnRegular_CountsOneShot()
    {
        var images = new[]
        {
            Update("webui", "img:1", available: false),
            Update("agent-src", "img:1", oneShot: true),
        };

        UpdateCountHelper.CountServicesWithUpdates(images).Should().Be(1);
    }

    [Fact]
    public void CountServicesWithUpdates_DisabledPolicy_ExcludedOnlyWhenAsked()
    {
        var images = new[] { Update("webui", "img:1", policy: "disabled") };

        UpdateCountHelper.CountServicesWithUpdates(images).Should().Be(0);
        UpdateCountHelper.CountServicesWithUpdates(images, excludeDisabled: false).Should().Be(1);
    }
}
