using System;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Services.Firewall;
using Xunit;

namespace AppLock.Tests.Firewall;

public class FirewallServiceTests
{
    [Fact]
    public async Task CreateRule_Both_CreatesInboundAndOutbound()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();

        // Act
        var result = await service.CreateRuleAsync(
            "AppLock – Chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            blockInbound: true,
            blockOutbound: true,
            profiles: int.MaxValue,
            id: id);

        // Assert
        Assert.True(result);
        var rules = await service.GetAppLockRulesAsync();
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Name == "AppLock – Chrome (Outbound)" && r.BlocksOutbound);
        Assert.Contains(rules, r => r.Name == "AppLock – Chrome (Inbound)" && r.BlocksInbound);
    }

    [Fact]
    public async Task CreateRule_OutboundOnly_CreatesOneRule()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();

        // Act
        var result = await service.CreateRuleAsync(
            "AppLock – Firefox",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            blockInbound: false,
            blockOutbound: true,
            profiles: int.MaxValue,
            id: id);

        // Assert
        Assert.True(result);
        var rules = await service.GetAppLockRulesAsync();
        Assert.Single(rules);
        Assert.Equal("AppLock – Firefox (Outbound)", rules[0].Name);
        Assert.True(rules[0].BlocksOutbound);
        Assert.False(rules[0].BlocksInbound);
    }

    [Fact]
    public async Task CreateRule_InboundOnly_CreatesOneRule()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();

        // Act
        var result = await service.CreateRuleAsync(
            "AppLock – Edge",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            blockInbound: true,
            blockOutbound: false,
            profiles: int.MaxValue,
            id: id);

        // Assert
        Assert.True(result);
        var rules = await service.GetAppLockRulesAsync();
        Assert.Single(rules);
        Assert.Equal("AppLock – Edge (Inbound)", rules[0].Name);
        Assert.True(rules[0].BlocksInbound);
        Assert.False(rules[0].BlocksOutbound);
    }

    [Fact]
    public async Task RemoveRule_OnlyRemovesAppLockGroupRules()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();
        
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", true, true, int.MaxValue, id);
        
        // Manually add a non-AppLock rule
        var rules = service.GetRules();
        rules.Add(new FirewallRuleInfo
        {
            Name = "Other Rule",
            Grouping = "Other Group",
            ApplicationName = @"C:\other.exe"
        });

        // Act
        var result1 = await service.RemoveRuleAsync("AppLock – Test (Outbound)");
        var result2 = await service.RemoveRuleAsync("Other Rule");

        // Assert
        Assert.True(result1);
        Assert.False(result2); // Should not remove non-AppLock rules
        var remainingRules = await service.GetAppLockRulesAsync();
        Assert.Single(remainingRules); // Only inbound rule remains
        Assert.Equal("AppLock – Test (Inbound)", remainingRules[0].Name);
    }

    [Fact]
    public async Task CreateRule_NotElevated_ReturnsFalse()
    {
        // Arrange
        var service = new FakeFirewallService();
        service.SetElevated(false);
        var id = Guid.NewGuid();

        // Act
        var result = await service.CreateRuleAsync(
            "AppLock – Test",
            @"C:\test.exe",
            true, true, int.MaxValue, id);

        // Assert
        Assert.False(result);
        var rules = await service.GetAppLockRulesAsync();
        Assert.Empty(rules);
    }

    [Fact]
    public async Task RemoveRule_NotElevated_ReturnsFalse()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();
        
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", true, true, int.MaxValue, id);
        service.SetElevated(false);

        // Act
        var result = await service.RemoveRuleAsync("AppLock – Test (Outbound)");

        // Assert
        Assert.False(result);
        var rules = await service.GetAppLockRulesAsync();
        Assert.Equal(2, rules.Count); // Rules still exist
    }

    [Fact]
    public async Task SetRuleEnabled_TogglesEnabledState()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();
        
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", false, true, int.MaxValue, id);

        // Act
        var result = await service.SetRuleEnabledAsync("AppLock – Test (Outbound)", false);
        var rules = await service.GetAppLockRulesAsync();

        // Assert
        Assert.True(result);
        Assert.False(rules[0].Enabled);
    }

    [Fact]
    public async Task RuleExists_FindsAppLockRules()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();
        
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", false, true, int.MaxValue, id);

        // Act
        var exists = await service.RuleExistsAsync("AppLock – Test (Outbound)");
        var notExists = await service.RuleExistsAsync("NonExistent Rule");

        // Assert
        Assert.True(exists);
        Assert.False(notExists);
    }

    [Fact]
    public async Task GetFirewallStatus_ReturnsCorrectState()
    {
        // Arrange
        var service = new FakeFirewallService();
        service.SetFirewallEnabled(true);
        service.SetGroupPolicyManaged(false);

        // Act
        var status = await service.GetFirewallStatusAsync();

        // Assert
        Assert.True(status.IsEnabled);
        Assert.True(status.IsServiceRunning);
        Assert.False(status.IsGroupPolicyManaged);
    }

    [Fact]
    public async Task CreateRule_GroupPolicyManaged_ReturnsFalse()
    {
        // Arrange
        var service = new FakeFirewallService();
        service.SetGroupPolicyManaged(true);
        var id = Guid.NewGuid();

        // Act
        var result = await service.CreateRuleAsync(
            "AppLock – Test",
            @"C:\test.exe",
            true, true, int.MaxValue, id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CreateRule_StoresIdInDescription()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();

        // Act
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", false, true, int.MaxValue, id);
        var rules = await service.GetAppLockRulesAsync();

        // Assert
        Assert.Single(rules);
        Assert.Equal(id, rules[0].RuleId);
        Assert.Contains($"AppLock-managed|id={id}", rules[0].Description);
    }

    [Fact]
    public async Task CreateRule_ReplacesExistingRuleWithSameName()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Act
        await service.CreateRuleAsync("AppLock – Test", @"C:\test1.exe", false, true, int.MaxValue, id1);
        await service.CreateRuleAsync("AppLock – Test", @"C:\test2.exe", false, true, int.MaxValue, id2);
        var rules = await service.GetAppLockRulesAsync();

        // Assert
        Assert.Single(rules);
        Assert.Equal(@"C:\test2.exe", rules[0].ApplicationName);
        Assert.Equal(id2, rules[0].RuleId);
    }

    [Fact]
    public async Task GetAppLockRules_OnlyReturnsAppLockGroupRules()
    {
        // Arrange
        var service = new FakeFirewallService();
        var id = Guid.NewGuid();
        
        await service.CreateRuleAsync("AppLock – Test", @"C:\test.exe", true, true, int.MaxValue, id);
        
        // Add non-AppLock rule manually
        service.GetRules().Add(new FirewallRuleInfo
        {
            Name = "Windows Rule",
            Grouping = "Windows Defender",
            ApplicationName = @"C:\Windows\system.exe"
        });

        // Act
        var appLockRules = await service.GetAppLockRulesAsync();

        // Assert
        Assert.Equal(2, appLockRules.Count);
        Assert.All(appLockRules, r => Assert.Equal("AppLock Connection Firewall", r.Grouping));
    }
}
