using System.ComponentModel.DataAnnotations;
using AdaPanel.Core.Dtos.Auth;
using Xunit;

namespace AdaPanel.Core.Tests;

public sealed class LoginRequestValidationTests
{
    [Fact]
    public void EmptyOptionalTwoFactorCode_IsAccepted()
    {
        var request = new LoginRequest
        {
            UserNameOrEmail = "admin",
            Password = "AuditPass!2026",
            TwoFactorCode = string.Empty
        };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            validationResults,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(validationResults);
    }
}
