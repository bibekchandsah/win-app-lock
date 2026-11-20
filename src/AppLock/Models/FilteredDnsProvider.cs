using System;
using System.Collections.Generic;

namespace AppLock.Models;

/// <summary>
/// A family-filter DNS provider from <c>Assets/web-filter/providers.json</c>.
/// In Filtered DNS provider mode, Windows DNS is pointed directly at this provider's IP addresses.
/// </summary>
public sealed class FilteredDnsProvider
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string WebsiteUrl { get; set; } = string.Empty;

    public List<string> Ipv4 { get; set; } = new();

    public List<string> Ipv6 { get; set; } = new();

    /// <summary>IP addresses or special values returned by the provider when a domain is blocked.</summary>
    public List<string> BlockIps { get; set; } = new();

    public string Ipv4Display => string.Join(", ", Ipv4);

    public string Ipv6Display => string.Join(", ", Ipv6);
}
