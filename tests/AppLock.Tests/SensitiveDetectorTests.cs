using AppLock.Services;
using Xunit;

namespace AppLock.Tests;

public class SensitiveDetectorTests
{
    [Theory]
    [InlineData("49927398716", true)]      // Luhn valid test number
    [InlineData("49927398717", false)]     // Luhn invalid test number
    [InlineData("79927398713", true)]      // Luhn valid test number
    [InlineData("79927398710", false)]     // Luhn invalid test number
    public void IsLuhnValid_ValidatesCorrectly(string digits, bool expected)
    {
        bool actual = SensitiveDetector.IsLuhnValid(digits);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsSensitive_DetectsPrivateKey()
    {
        string text = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0...\n-----END RSA PRIVATE KEY-----";
        Assert.True(SensitiveDetector.IsSensitive(text));
    }

    [Fact]
    public void IsSensitive_DetectsJwtToken()
    {
        string text = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        Assert.True(SensitiveDetector.IsSensitive(text));
    }

    [Theory]
    [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("Bearer secret_token_value_longer_than_20_chars_here")]
    public void IsSensitive_DetectsApiTokens(string token)
    {
        Assert.True(SensitiveDetector.IsSensitive(token));
    }

    [Fact]
    public void IsSensitive_DetectsPasswordFromBrowser()
    {
        string password = "kP9#vX2$mQ8@zL1%";
        // From browser
        Assert.True(SensitiveDetector.IsSensitive(password, "chrome.exe", "Google Chrome"));
        // From normal app without credentials context
        Assert.False(SensitiveDetector.IsSensitive(password, "notepad.exe", "Untitled - Notepad"));
    }

    [Fact]
    public void IsSensitive_IgnoresNormalText()
    {
        Assert.False(SensitiveDetector.IsSensitive("Hello world, this is normal text."));
        Assert.False(SensitiveDetector.IsSensitive("https://example.com/search?q=winui"));
        Assert.False(SensitiveDetector.IsSensitive("public static void Main(string[] args)"));
    }
}

