namespace SocialGraph.Api.Tests;

using Microsoft.AspNetCore.Mvc;
using Moq;
using SocialGraph.Api.RestAPI;
using SocialGraph.Api.Service;

public sealed class InternalControllerInputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecommendationEndpoints_RejectNonPositiveUserIds(long userId)
    {
        var controller = new RecommendationController(Mock.Of<ICandidateService>());

        var posts = await controller.GetPostCandidateIdsAsync(userId);
        var reels = await controller.GetReelCandidatesAsync(userId);

        Assert.IsType<BadRequestObjectResult>(posts.Result);
        Assert.IsType<BadRequestObjectResult>(reels.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecommendationMetadataEndpoint_RejectsNonPositiveUserIds(long userId)
    {
        var controller = new RecommendationController(Mock.Of<ICandidateService>());

        var result = await controller.GetContentCandidatesAsync(userId);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("POPULAR")]
    public async Task ReelCandidates_RejectUnsupportedModeBeforeServiceDispatch(string mode)
    {
        var service = new Mock<ICandidateService>(MockBehavior.Strict);
        var controller = new RecommendationController(service.Object);

        var result = await controller.GetReelCandidatesAsync(100, mode: mode);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReelCandidates_NormalizesAndForwardsFollowingMode()
    {
        var service = new Mock<ICandidateService>(MockBehavior.Strict);
        service.Setup(item => item.GetReelCandidatesAsync(
                100,
                25,
                "FOLLOWING",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SocialGraph.Api.Contracts.CandidateItemResult>());
        var controller = new RecommendationController(service.Object);

        var result = await controller.GetReelCandidatesAsync(100, 25, " following ");

        Assert.IsType<OkObjectResult>(result.Result);
        service.VerifyAll();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PaymentEndpoint_RejectsNonPositiveUserIds(long userId)
    {
        var controller = new PaymentController(Mock.Of<IUserGraphService>());

        var result = await controller.SetUserVerifyAsync(
            userId,
            new SetUserVerifyRequest(null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
