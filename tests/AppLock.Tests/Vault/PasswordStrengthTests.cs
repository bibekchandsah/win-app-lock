using AppLock.Services.Vault;
using Xunit;

namespace AppLock.Tests.Vault;

public class PasswordStrengthTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("12345678", false)]
    [InlineData("password123", false)] // Common password rejected even though 11 chars
    [InlineData("1234567890", false)]
    public void WeakAndCommonPasswords_FailMinimum(string pw, bool expectedMeetsMin)
    {
        var result = PasswordStrength.Evaluate(pw);
        Assert.Equal(expectedMeetsMin, result.MeetsMinimum);
    }

    [Theory]
    [InlineData("Correct-Horse-Battery-Staple!", true)]
    [InlineData("Tr0ub4dor&33#99", true)]
    [InlineData("P@ssw0rdNotCommon123!", true)]
    public void StrongPasswords_MeetMinimum(string pw, bool expectedMeetsMin)
    {
        var result = PasswordStrength.Evaluate(pw);
        Assert.Equal(expectedMeetsMin, result.MeetsMinimum);
        Assert.True(result.Score >= 50);
    }

    [Fact]
    public void CommonPassword_IsPenalized()
    {
        var common = PasswordStrength.Evaluate("password123");
        Assert.False(common.MeetsMinimum);
        Assert.Contains("commonly-used", common.Description, System.StringComparison.OrdinalIgnoreCase);
    }
}
