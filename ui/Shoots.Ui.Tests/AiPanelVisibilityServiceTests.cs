using Shoots.Contracts.Core.AI;
using Shoots.UI.Services;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class AiPanelVisibilityServiceTests
{
    [Fact]
    public void Visible_policy_shows_ai_for_end_users()
    {
        var policy = new AiPresentationPolicy(AiVisibilityMode.Visible, true, true, false);
        var service = new AiPanelVisibilityService();

        var state = service.Evaluate(policy, AiAccessRole.EndUser);

        Assert.True(state.CanRenderAiPanel);
        Assert.True(state.CanRenderAiExplainButtons);
        Assert.True(state.CanRenderAiProviderStatus);
    }

    [Fact]
    public void Hidden_for_end_users_blocks_ai_for_end_users()
    {
        var policy = new AiPresentationPolicy(AiVisibilityMode.HiddenForEndUsers, true, true, false);
        var service = new AiPanelVisibilityService();

        var state = service.Evaluate(policy, AiAccessRole.EndUser);

        Assert.False(state.CanRenderAiPanel);
        Assert.False(state.CanRenderAiExplainButtons);
        Assert.False(state.CanRenderAiProviderStatus);
    }

    [Fact]
    public void Admin_only_shows_ai_for_admins()
    {
        var policy = new AiPresentationPolicy(AiVisibilityMode.AdminOnly, true, true, false);
        var service = new AiPanelVisibilityService();

        var state = service.Evaluate(policy, AiAccessRole.Admin);

        Assert.True(state.CanRenderAiPanel);
        Assert.True(state.CanRenderAiExplainButtons);
        Assert.True(state.CanRenderAiProviderStatus);
    }

    [Fact]
    public void Enterprise_mode_hides_ai_for_end_users()
    {
        var policy = new AiPresentationPolicy(AiVisibilityMode.Visible, true, true, true);
        var service = new AiPanelVisibilityService();

        var state = service.Evaluate(policy, AiAccessRole.EndUser);

        Assert.False(state.CanRenderAiPanel);
        Assert.False(state.CanRenderAiExplainButtons);
        Assert.False(state.CanRenderAiProviderStatus);
    }

    [Fact]
    public void Enterprise_mode_allows_ai_for_admins()
    {
        var policy = new AiPresentationPolicy(AiVisibilityMode.Visible, true, true, true);
        var service = new AiPanelVisibilityService();

        var state = service.Evaluate(policy, AiAccessRole.Admin);

        Assert.True(state.CanRenderAiPanel);
        Assert.True(state.CanRenderAiExplainButtons);
        Assert.True(state.CanRenderAiProviderStatus);
    }
}
