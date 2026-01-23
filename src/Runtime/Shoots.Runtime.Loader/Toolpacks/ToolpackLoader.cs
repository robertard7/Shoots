using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using RuntimeSnapshot = Shoots.Runtime.Abstractions.ToolCatalogSnapshot;

namespace Shoots.Runtime.Loader.Toolpacks;

public sealed class ToolpackLoader
{
    private readonly ToolpackDiscovery _discovery;

    public ToolpackLoader()
        : this(new ToolpackDiscovery())
    {
    }

    public ToolpackLoader(ToolpackDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

	public RuntimeSnapshot LoadFromDirectory(string toolpacksRoot, ToolTierPolicy policy)
	{
		if (policy is null)
			throw new ArgumentNullException(nameof(policy));

		var manifests = _discovery.Discover(toolpacksRoot);
		return LoadFromToolpacks(manifests, policy);
	}

	public RuntimeSnapshot LoadFromToolpacks(
		IEnumerable<ToolpackManifest> toolpacks,
		ToolTierPolicy policy)
	{
		if (toolpacks is null)
			throw new ArgumentNullException(nameof(toolpacks));
		if (policy is null)
			throw new ArgumentNullException(nameof(policy));

		var filtered = policy.Filter(toolpacks);
		if (filtered.Any(pack => !policy.Allows(pack.Tier)))
			throw new InvalidOperationException("Toolpack tier exceeds workspace tier.");

		var entries = filtered
			.SelectMany(BuildEntries)
			.OrderBy(entry => entry.Spec.ToolId.Value, StringComparer.Ordinal)
			.ToList();

		return new RuntimeSnapshot(
			ComputeHash(filtered, entries, policy),
			entries);
	}

    private static IEnumerable<ToolRegistryEntry> BuildEntries(ToolpackManifest manifest)
    {
        foreach (var tool in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ToolId))
                throw new ArgumentException($"tool id is required in {manifest.SourcePath}");

            if (tool.Authority is null)
                throw new ArgumentException($"tool authority is required in {manifest.SourcePath}");

            var providerKind = Enum.Parse<ProviderKind>(
                tool.Authority.ProviderKind,
                ignoreCase: true);

            var capabilities = ProviderCapabilities.None;
            if (tool.Authority.Capabilities is not null)
            {
                foreach (var name in tool.Authority.Capabilities)
                {
                    if (Enum.TryParse<ProviderCapabilities>(
                            name,
                            ignoreCase: true,
                            out var parsed))
                    {
                        capabilities |= parsed;
                    }
                }
            }

            var inputs = tool.Inputs?
                .Select(i => new ToolInputSpec(
                    i.Name,
                    i.Type,
                    i.Required,
                    i.Description ?? string.Empty))
                .ToList()
                ?? new List<ToolInputSpec>();

            var outputs = tool.Outputs?
                .Select(o => new ToolOutputSpec(
                    o.Name,
                    o.Type,
                    o.Description ?? string.Empty))
                .ToList()
                ?? new List<ToolOutputSpec>();

            var spec = new ToolSpec(
                new ToolId(tool.ToolId),
                tool.Description ?? string.Empty,
                new ToolAuthorityScope(providerKind, capabilities),
                inputs,
                outputs,
                tool.Tags ?? Array.Empty<string>());

            yield return new ToolRegistryEntry(spec);
        }
    }

    private static string ComputeHash(
        IReadOnlyList<ToolpackManifest> manifests,
        IReadOnlyList<ToolRegistryEntry> entries,
        ToolTierPolicy policy)
    {
        var builder = new StringBuilder();
        builder.Append(policy.AllowedTier).Append('|');
        foreach (var capability in policy.AllowedCapabilities.OrderBy(cap => cap))
            builder.Append(capability).Append('|');

        foreach (var manifest in manifests.OrderBy(pack => pack.ToolpackId, StringComparer.Ordinal))
        {
            builder.Append(manifest.ToolpackId).Append('|');
            builder.Append(manifest.Version).Append('|');
            builder.Append(manifest.Tier).Append('|');
            builder.Append(manifest.Description).Append('|');
            builder.Append(manifest.RiskNotes).Append('|');
            foreach (var capability in manifest.Capabilities.OrderBy(cap => cap))
                builder.Append(capability).Append('|');
        }

        foreach (var entry in entries.OrderBy(e => e.Spec.ToolId.Value, StringComparer.Ordinal))
        {
            builder.Append(entry.Spec.ToolId.Value).Append('|');
            builder.Append(entry.Spec.Description).Append('|');
            builder.Append(entry.Spec.RequiredAuthority.RequiredProviderKind).Append('|');
            builder.Append(entry.Spec.RequiredAuthority.RequiredCapabilities).Append('|');
            foreach (var input in entry.Spec.Inputs)
            {
                builder.Append(input.Name).Append('|');
                builder.Append(input.Type).Append('|');
                builder.Append(input.Required).Append('|');
                builder.Append(input.Description).Append('|');
            }
            foreach (var output in entry.Spec.Outputs)
            {
                builder.Append(output.Name).Append('|');
                builder.Append(output.Type).Append('|');
                builder.Append(output.Description).Append('|');
            }
            foreach (var tag in entry.Spec.Tags)
                builder.Append(tag).Append('|');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert
            .ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
    }
}
