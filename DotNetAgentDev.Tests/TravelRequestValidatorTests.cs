using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tests;

public sealed class TravelRequestValidatorTests
{
    [Fact]
    public void Validate_AcceptsNormalRequest()
    {
        var request = new TravelRequest
        {
            Departure = "上海",
            Destination = "日本",
            Days = 7,
            Travelers = 2,
            Budget = 18000
        };

        Assert.Empty(TravelRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_ReturnsAllImportantBoundaryErrors()
    {
        var request = new TravelRequest
        {
            Departure = "",
            Destination = " ",
            Days = 31,
            Travelers = 0,
            Budget = 0
        };

        var errors = TravelRequestValidator.Validate(request);

        Assert.Contains(nameof(request.Departure), errors.Keys);
        Assert.Contains(nameof(request.Destination), errors.Keys);
        Assert.Contains(nameof(request.Days), errors.Keys);
        Assert.Contains(nameof(request.Travelers), errors.Keys);
        Assert.Contains(nameof(request.Budget), errors.Keys);
    }
}
