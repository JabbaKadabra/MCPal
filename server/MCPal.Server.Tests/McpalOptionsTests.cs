using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;

namespace MCPal.Server.Tests;

[TestFixture]
internal sealed class McpalOptionsTests
{
    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private static List<ValidationResult> Validate(McpalOptions options) => [.. options.Validate(new ValidationContext(options))];

    [Test]
    public void Validate_Defaults_HaveNoProblems()
    {
        Validate(new McpalOptions()).Should().BeEmpty();
    }

    [TestCase(29)]
    [TestCase(901)]
    public void Validate_TokenLifetimeOutsideThirtyToNineHundredSeconds_IsRejected(int seconds)
    {
        var options = new McpalOptions { UserContext = new UserContextOptions { TokenLifetimeSeconds = seconds } };

        Validate(options).Should().ContainSingle().Which.ErrorMessage.Should().Contain("30 and 900");
    }

    [TestCase(6)]
    [TestCase(366)]
    public void Validate_SigningKeyLifetimeOutsideSevenToThreeHundredSixtyFiveDays_IsRejected(int days)
    {
        var options = new McpalOptions { UserContext = new UserContextOptions { SigningKeyLifetimeDays = days } };

        Validate(options).Should().ContainSingle().Which.ErrorMessage.Should().Contain("7 and 365");
    }

    [TestCase(30, 7)]
    [TestCase(900, 365)]
    public void Validate_BoundaryValues_AreAccepted(int seconds, int days)
    {
        var options = new McpalOptions { UserContext = new UserContextOptions { TokenLifetimeSeconds = seconds, SigningKeyLifetimeDays = days } };

        Validate(options).Should().BeEmpty();
    }

    [Test]
    public void EnvironmentValidator_ProductionWithoutDataProtectionPath_Fails()
    {
        var result = new McpalEnvironmentValidator(Environment("Production")).Validate(null, new McpalOptions { DataProtectionPath = null });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Mcpal:DataProtectionPath");
    }

    [Test]
    public void EnvironmentValidator_ProductionWithDataProtectionPath_Succeeds()
    {
        var result = new McpalEnvironmentValidator(Environment("Production")).Validate(null, new McpalOptions { DataProtectionPath = "/data/keys" });

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("Development")]
    [TestCase("Staging")]
    public void EnvironmentValidator_OtherEnvironmentsWithoutPath_Succeed(string environment)
    {
        var result = new McpalEnvironmentValidator(Environment(environment)).Validate(null, new McpalOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void EnvironmentValidator_NoHostEnvironment_Succeeds()
    {
        new McpalEnvironmentValidator().Validate(null, new McpalOptions()).Succeeded.Should().BeTrue();
    }
}
