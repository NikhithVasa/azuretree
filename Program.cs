using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureTree;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine(Options.HelpText);
                return 0;
            }

            ReportData data;
            if (options.Demo)
            {
                Console.WriteLine("Generating deterministic demo data ...");
                data = DemoData.Create();
            }
            else if (options.From is not null)
            {
                var source = Path.GetFullPath(options.From);
                if (!File.Exists(source))
                {
                    throw new AppException($"Cached data file not found: {source}");
                }

                try
                {
                    data = JsonSerializer.Deserialize<ReportData>(await File.ReadAllTextAsync(source), JsonOptions)
                        ?? throw new JsonException("The document was empty.");
                }
                catch (JsonException ex)
                {
                    throw new AppException($"Cached data is not valid azuretree JSON: {ex.Message}");
                }

                DataBuilder.FinalizeReport(data);
                Console.WriteLine($"Loaded cached data from {source}");
            }
            else if (options.Mdc)
            {
                data = await AzureCollector.CollectAsync(options);
            }
            else
            {
                throw new AppException("Choose a data scope. Use --mdc to read Microsoft Defender for Cloud data.");
            }

            var output = Path.GetFullPath(options.Out ?? Path.Combine("out", "azuretree.html"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var reportJson = JsonSerializer.Serialize(data, JsonOptions);
            var template = ReadEmbeddedViewer();
            const string marker = "__AZURETREE_DATA__";
            if (!template.Contains(marker, StringComparison.Ordinal))
            {
                throw new AppException("Embedded viewer template is missing its data placeholder.");
            }

            var safeJson = reportJson.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(output, template.Replace(marker, safeJson, StringComparison.Ordinal), new UTF8Encoding(false));

            if (options.From is null)
            {
                var cache = Path.GetFullPath(Path.Combine("out", "azuretree-data.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(cache, reportJson, new UTF8Encoding(false));
                Console.WriteLine($"Data:   {cache}");
            }

            if (options.Export)
            {
                var exportPath = Path.GetFullPath(options.ExportPath ?? Path.Combine("out", "azuretree-export.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                await File.WriteAllTextAsync(exportPath, JsonSerializer.Serialize(AgentExport.Create(data), JsonOptions), new UTF8Encoding(false));
                Console.WriteLine($"Export: {exportPath}");
            }

            Console.WriteLine($"Report: {output}");
            Console.WriteLine($"Total:  {data.TotalCostUsd.ToString("C2", CultureInfo.GetCultureInfo("en-US"))} across {data.Resources.Count(r => r.CostUsd > 0)} priced resource boxes");
            if (data.Errors.Count > 0)
            {
                Console.WriteLine($"Warnings: {data.Errors.Count} subscription collection failure(s)");
            }

            if (!options.NoOpen)
            {
                OpenBrowser(output);
            }

            return 0;
        }
        catch (AppException ex)
        {
            Console.Error.WriteLine($"azuretree: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"azuretree: unexpected error: {ex.Message}");
            return 1;
        }
    }

    private static string ReadEmbeddedViewer()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("azuretree.viewer.html")
            ?? throw new AppException("The embedded viewer.html resource could not be found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void OpenBrowser(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"azuretree: report created, but the browser could not be opened: {ex.Message}");
        }
    }
}

internal sealed class Options
{
    public bool Demo { get; private set; }
    public bool Mdc { get; private set; }
    public bool Export { get; private set; }
    public bool NoOpen { get; private set; }
    public bool Help { get; private set; }
    public string? Tenant { get; private set; }
    public string? From { get; private set; }
    public string? Out { get; private set; }
    public string? ExportPath { get; private set; }
    public List<string> Subscriptions { get; } = [];

    public const string HelpText = """
azuretree — Microsoft Defender for Cloud coverage and actual CostUSD

Usage:
  dotnet run -- --demo [--no-open]
  dotnet run -- --mdc [--tenant ID] [--subscription ID-OR-NAME ...]
  dotnet run -- --from FILE [--export [FILE]] [--no-open]

Options:
  --demo                 Generate deterministic fake data; no Azure login required.
  --mdc                  Read enabled Defender plans, resources, and actual Defender cost.
  --tenant ID            Restrict collection to one Azure tenant.
  --subscription VALUE   Select a subscription by ID or display name; repeatable.
  --from FILE            Reopen cached report data without Azure calls.
  --export [FILE]        Also write agent JSON (default out/azuretree-export.json).
  --out FILE             HTML destination (default out/azuretree.html).
  --no-open              Do not open the generated report.
  --help                 Show this help.
""";

    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string RequiredValue()
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new AppException($"Missing value after {arg}.");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "--demo": result.Demo = true; break;
                case "--mdc": result.Mdc = true; break;
                case "--tenant": result.Tenant = RequiredValue(); break;
                case "--subscription": result.Subscriptions.Add(RequiredValue()); break;
                case "--from": result.From = RequiredValue(); break;
                case "--out": result.Out = RequiredValue(); break;
                case "--no-open": result.NoOpen = true; break;
                case "--help": case "-h": result.Help = true; break;
                case "--export":
                    result.Export = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        result.ExportPath = args[++i];
                    }
                    break;
                default:
                    throw new AppException($"Unknown argument '{arg}'. Use --help for supported options.");
            }
        }

        if (result.Demo && result.From is not null)
        {
            throw new AppException("--demo and --from cannot be used together.");
        }

        if (result.From is not null && result.Mdc)
        {
            throw new AppException("--from and --mdc cannot be used together; cached mode makes no Azure calls.");
        }

        if ((result.Demo || result.From is not null) && (result.Tenant is not null || result.Subscriptions.Count > 0))
        {
            throw new AppException("--tenant and --subscription apply only to a live --mdc run.");
        }

        return result;
    }
}

internal sealed class AppException(string message) : Exception(message);

internal sealed class ReportData
{
    public Dictionary<string, ViewData> Views { get; set; } = [];
    public List<CoverageResource> Resources { get; set; } = [];
    public List<PlanSummary> Plans { get; set; } = [];
    public string Generated { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string TenantName { get; set; } = "";
    public int SubscriptionCount { get; set; }
    public string PeriodStart { get; set; } = "";
    public string PeriodEnd { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public double TotalCostUsd { get; set; }
    public bool Demo { get; set; }
    public List<CollectionError> Errors { get; set; } = [];
}

internal sealed class CoverageResource
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceTypeLabel { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string PlanDisplayName { get; set; } = "";
    public string Tier { get; set; } = "";
    public string? SubPlan { get; set; }
    public string Coverage { get; set; } = "";
    public string Scope { get; set; } = "";
    public double CostUsd { get; set; }
    public List<string> Extensions { get; set; } = [];
    public string Category { get; set; } = "";
}

internal sealed class PlanSummary
{
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string PlanDisplayName { get; set; } = "";
    public string Tier { get; set; } = "";
    public string? SubPlan { get; set; }
    public string Coverage { get; set; } = "";
    public int ResourceCount { get; set; }
    public double CostUsd { get; set; }
    public List<string> Extensions { get; set; } = [];
}

internal sealed class ViewData
{
    public List<string> Dims { get; set; } = [];
    public Dictionary<string, string> Names { get; set; } = [];
    public List<ViewRow> Rows { get; set; } = [];
}

internal sealed class ViewRow
{
    [JsonPropertyName("k")]
    public List<string> Keys { get; set; } = [];
    [JsonPropertyName("v")]
    public double CostUsd { get; set; }
}

internal sealed class CollectionError
{
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string Message { get; set; } = "";
}

internal sealed record SubscriptionInfo(string Id, string Name, string TenantId);
internal sealed record InventoryResource(string Id, string Name, string Type, string ResourceGroup, string Location, string SubscriptionId);
internal sealed record CostRow(string Category, string ResourceId, double CostUsd);
internal sealed record PlanDefinition(
    string SubscriptionId,
    string SubscriptionName,
    string Name,
    string DisplayName,
    string Tier,
    string? SubPlan,
    string Coverage,
    List<string> Extensions,
    bool SubscriptionWide,
    HashSet<string> ResourceTypes);

internal static class DefenderCatalog
{
    private sealed record Entry(string Display, string[] Types, bool SubscriptionWide = false);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VirtualMachines"] = new("VMs", ["microsoft.compute/virtualmachines", "microsoft.compute/virtualmachinescalesets", "microsoft.hybridcompute/machines"]),
        ["StorageAccounts"] = new("Storage accounts", ["microsoft.storage/storageaccounts"]),
        ["SqlServers"] = new("SQL", ["microsoft.sql/servers", "microsoft.sql/servers/databases", "microsoft.sql/managedinstances"]),
        ["SqlServerVirtualMachines"] = new("SQL VMs", ["microsoft.sqlvirtualmachine/sqlvirtualmachines"]),
        ["OpenSourceRelationalDatabases"] = new("Open-source databases", ["microsoft.dbformysql/servers", "microsoft.dbformysql/flexibleservers", "microsoft.dbforpostgresql/servers", "microsoft.dbforpostgresql/flexibleservers", "microsoft.dbformariadb/servers"]),
        ["CosmosDbs"] = new("Cosmos DB", ["microsoft.documentdb/databaseaccounts"]),
        ["Containers"] = new("Containers", ["microsoft.containerservice/managedclusters", "microsoft.kubernetes/connectedclusters", "microsoft.containerregistry/registries"]),
        ["AppServices"] = new("App Service", ["microsoft.web/sites"]),
        ["KeyVaults"] = new("Key Vault", ["microsoft.keyvault/vaults", "microsoft.keyvault/managedhsms"]),
        ["Api"] = new("APIs", ["microsoft.apimanagement/service"]),
        ["Dns"] = new("DNS", ["microsoft.network/dnszones", "microsoft.network/privatednszones"]),
        ["AI"] = new("AI Services", ["microsoft.cognitiveservices/accounts"]),
        ["Arm"] = new("Resource Manager", [], true),
        ["CloudPosture"] = new("CSPM", [], true)
    };

    public static string DisplayName(string planName) => Entries.TryGetValue(planName, out var entry) ? entry.Display : Humanize(planName);
    public static bool IsSubscriptionWide(string planName) => !Entries.TryGetValue(planName, out var entry) || entry.SubscriptionWide;
    public static HashSet<string> ResourceTypes(string planName) => Entries.TryGetValue(planName, out var entry)
        ? new HashSet<string>(entry.Types, StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static string ResourceTypeLabel(string type)
    {
        foreach (var entry in Entries.Values)
        {
            if (entry.Types.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                return entry.Display;
            }
        }

        return Humanize(type.Split('/').LastOrDefault() ?? type);
    }

    public static string NormalizeCategory(string value)
    {
        var category = value.Trim();
        foreach (var prefix in new[] { "Microsoft Defender for", "Microsoft Defender", "Defender for", "Defender" })
        {
            if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                category = category[prefix.Length..].Trim(' ', ':', '-');
                break;
            }
        }

        var suffix = category.IndexOf(" - ", StringComparison.Ordinal);
        if (suffix >= 0)
        {
            category = category[..suffix].Trim();
        }

        if (category.Equals("Servers", StringComparison.OrdinalIgnoreCase)) return "VMs";
        if (category.Equals("Storage", StringComparison.OrdinalIgnoreCase)) return "Storage accounts";
        if (category.Equals("Cosmos DB", StringComparison.OrdinalIgnoreCase)) return "Azure Cosmos DB";
        return string.IsNullOrWhiteSpace(category) ? "Other Defender" : category;
    }

    public static bool CategoryEquals(string left, string right)
    {
        static string Canonical(string value) => value.Replace("Azure ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase)
            .Trim().ToLowerInvariant();
        return Canonical(left) == Canonical(right);
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown Defender plan";
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1])))) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

internal static class DemoData
{
    private const string Tenant = "818567e8-e174-4026-8089-9c5e12cb6990";
    private const string Prod = "11111111-1111-4111-8111-111111111111";
    private const string Dev = "22222222-2222-4222-8222-222222222222";

    public static ReportData Create()
    {
        var end = DateTime.UtcNow.Date.AddDays(-1);
        var start = end.AddDays(-29);
        var data = new ReportData
        {
            Generated = new DateTimeOffset(2026, 9, 26, 21, 54, 0, TimeSpan.FromHours(-7)).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
            Tenant = Tenant,
            TenantName = "Servers Billing",
            SubscriptionCount = 2,
            PeriodStart = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodEnd = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Currency = "USD",
            Demo = true
        };

        var plans = new[]
        {
            Plan(Prod, "Production", "VirtualMachines", "Defender for Servers P2", "Fully covered", ["AgentlessVmScanning", "MdeDesignatedSubscription"]),
            Plan(Dev, "Engineering", "VirtualMachines", "Defender for Servers P1", "Partially covered", ["AgentlessVmScanning"]),
            Plan(Prod, "Production", "StorageAccounts", "DefenderForStorageV2", "Fully covered", ["OnUploadMalwareScanning", "SensitiveDataDiscovery"]),
            Plan(Prod, "Production", "SqlServers", null, "Fully covered", []),
            Plan(Dev, "Engineering", "Containers", null, "Partially covered", ["ContainerRegistriesVulnerabilityAssessments", "AgentlessDiscoveryForKubernetes"]),
            Plan(Prod, "Production", "AppServices", null, "Fully covered", []),
            Plan(Prod, "Production", "KeyVaults", null, "Fully covered", []),
            Plan(Prod, "Production", "CloudPosture", null, "Enabled", ["AgentlessVmScanning"])
        };

        var resources = new List<CoverageResource>
        {
            Resource(Prod, "Production", "VirtualMachines", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/compute-prod/providers/Microsoft.Compute/virtualMachines/api-01", "api-01", "microsoft.compute/virtualmachines", "compute-prod", "westus2", 498.04),
            Resource(Dev, "Engineering", "VirtualMachines", "Partially covered", "/subscriptions/"+Dev+"/resourceGroups/platform-dev/providers/Microsoft.Compute/virtualMachines/build-02", "build-02", "microsoft.compute/virtualmachines", "platform-dev", "eastus", 143.12),
            Resource(Prod, "Production", "StorageAccounts", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/data-prod/providers/Microsoft.Storage/storageAccounts/prodarchive01", "prodarchive01", "microsoft.storage/storageaccounts", "data-prod", "westus2", 258.31),
            Resource(Dev, "Engineering", "Containers", "Partially covered", "/subscriptions/"+Dev+"/resourceGroups/containers-dev/providers/Microsoft.ContainerService/managedClusters/aks-dev", "aks-dev", "microsoft.containerservice/managedclusters", "containers-dev", "eastus", 147.09),
            Resource(Prod, "Production", "SqlServers", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/data-prod/providers/Microsoft.Sql/servers/orders-sql", "orders-sql", "microsoft.sql/servers", "data-prod", "westus2", 62.45),
            Resource(Prod, "Production", "SqlServers", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/data-prod/providers/Microsoft.Sql/servers/orders-sql/databases/orders", "orders", "microsoft.sql/servers/databases", "data-prod", "westus2", 37.17),
            Resource(Prod, "Production", "AppServices", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/apps-prod/providers/Microsoft.Web/sites/customer-portal", "customer-portal", "microsoft.web/sites", "apps-prod", "westus2", 80.41),
            Resource(Prod, "Production", "KeyVaults", "Fully covered", "/subscriptions/"+Prod+"/resourceGroups/security-prod/providers/Microsoft.KeyVault/vaults/prod-secrets", "prod-secrets", "microsoft.keyvault/vaults", "security-prod", "westus2", 50.86),
            Resource(Dev, "Engineering", "VirtualMachines", "Partially covered", "/subscriptions/"+Dev+"/resourceGroups/platform-dev/providers/Microsoft.Compute/virtualMachines/idle-covered", "idle-covered", "microsoft.compute/virtualmachines", "platform-dev", "eastus", 0),
            Resource(Prod, "Production", "CloudPosture", "Enabled", "/subscriptions/"+Prod, "Production", "microsoft.resources/subscriptions", "", "global", 0)
        };

        data.Resources = resources;
        data.Plans = plans.Select(plan => new PlanSummary
        {
            SubscriptionId = plan.SubscriptionId,
            SubscriptionName = plan.SubscriptionName,
            PlanName = plan.Name,
            PlanDisplayName = plan.DisplayName,
            Tier = plan.Tier,
            SubPlan = plan.SubPlan,
            Coverage = plan.Coverage,
            Extensions = plan.Extensions
        }).ToList();
        DataBuilder.FinalizeReport(data);
        return data;
    }

    private static PlanDefinition Plan(string subscriptionId, string subscriptionName, string name, string? subPlan, string coverage, string[] extensions) =>
        new(subscriptionId, subscriptionName, name, DefenderCatalog.DisplayName(name), "Standard", subPlan, coverage, [.. extensions], DefenderCatalog.IsSubscriptionWide(name), DefenderCatalog.ResourceTypes(name));

    private static CoverageResource Resource(string subscriptionId, string subscriptionName, string planName, string coverage, string id, string name, string type, string group, string location, double cost)
    {
        var display = DefenderCatalog.DisplayName(planName);
        return new CoverageResource
        {
            Key = $"{subscriptionId}|{planName}|{id}".ToLowerInvariant(),
            Id = id,
            Name = name,
            ResourceType = type,
            ResourceTypeLabel = DefenderCatalog.ResourceTypeLabel(type),
            ResourceGroup = group,
            Location = location,
            SubscriptionId = subscriptionId,
            SubscriptionName = subscriptionName,
            PlanName = planName,
            PlanDisplayName = display,
            Tier = "Standard",
            SubPlan = planName == "VirtualMachines" ? (subscriptionId == Prod ? "Defender for Servers P2" : "Defender for Servers P1") : planName == "StorageAccounts" ? "DefenderForStorageV2" : null,
            Coverage = coverage,
            Scope = DefenderCatalog.IsSubscriptionWide(planName) ? "Subscription" : "Resource",
            CostUsd = cost,
            Category = display == "Cosmos DB" ? "Azure Cosmos DB" : display,
            Extensions = planName switch { "VirtualMachines" => ["AgentlessVmScanning"], "StorageAccounts" => ["OnUploadMalwareScanning", "SensitiveDataDiscovery"], "Containers" => ["AgentlessDiscoveryForKubernetes"], _ => [] }
        };
    }
}

internal static class DataBuilder
{
    public static List<CoverageResource> BuildResources(SubscriptionInfo subscription, List<PlanDefinition> plans, List<InventoryResource> inventory, List<CostRow> costs)
    {
        var resources = new List<CoverageResource>();
        var inventoryById = inventory.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
        {
            if (plan.SubscriptionWide)
            {
                var id = $"/subscriptions/{subscription.Id}";
                resources.Add(ToCoverage(plan, id, subscription.Name, "microsoft.resources/subscriptions", "Subscription", "", "global", "Subscription"));
                continue;
            }

            foreach (var item in inventory.Where(r => plan.ResourceTypes.Contains(r.Type)))
            {
                resources.Add(ToCoverage(plan, item.Id, item.Name, item.Type, DefenderCatalog.ResourceTypeLabel(item.Type), item.ResourceGroup, item.Location, "Resource"));
            }
        }

        foreach (var grouped in costs.GroupBy(c => new { Category = c.Category.ToUpperInvariant(), Id = c.ResourceId.ToUpperInvariant() }))
        {
            var category = grouped.First().Category;
            var resourceId = grouped.First().ResourceId;
            var amount = grouped.Sum(x => x.CostUsd);
            if (!(amount > 0) || double.IsNaN(amount) || double.IsInfinity(amount)) continue;

            PlanDefinition? plan = plans.FirstOrDefault(p => DefenderCatalog.CategoryEquals(p.DisplayName, category));
            CoverageResource? target = null;
            if (!string.IsNullOrWhiteSpace(resourceId) && inventoryById.TryGetValue(resourceId, out var inventoryResource))
            {
                plan ??= plans.FirstOrDefault(p => p.ResourceTypes.Contains(inventoryResource.Type));
                if (plan is not null)
                {
                    target = resources.FirstOrDefault(r => r.PlanName.Equals(plan.Name, StringComparison.OrdinalIgnoreCase) && r.Id.Equals(inventoryResource.Id, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (target is null && plan?.SubscriptionWide == true)
            {
                var subscriptionScope = $"/subscriptions/{subscription.Id}";
                target = resources.FirstOrDefault(r => r.PlanName.Equals(plan.Name, StringComparison.OrdinalIgnoreCase) && r.Id.Equals(subscriptionScope, StringComparison.OrdinalIgnoreCase));
            }

            if (target is null)
            {
                var encoded = Uri.EscapeDataString(category);
                var synthetic = $"/subscriptions/{subscription.Id}/providers/Microsoft.CostManagement/unassigned/{encoded}";
                target = resources.FirstOrDefault(r => r.Id.Equals(synthetic, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    target = new CoverageResource
                    {
                        Key = $"{subscription.Id}|unassigned|{category}".ToLowerInvariant(),
                        Id = synthetic,
                        Name = "Unassigned or deleted resources",
                        ResourceType = "microsoft.costmanagement/unassigned",
                        ResourceTypeLabel = "Unassigned or deleted",
                        ResourceGroup = "",
                        Location = "",
                        SubscriptionId = subscription.Id,
                        SubscriptionName = subscription.Name,
                        PlanName = plan?.Name ?? "Unassigned",
                        PlanDisplayName = plan?.DisplayName ?? category,
                        Tier = plan?.Tier ?? "",
                        SubPlan = plan?.SubPlan,
                        Coverage = plan?.Coverage ?? "Unknown",
                        Scope = "Historical aggregate",
                        Extensions = plan?.Extensions.ToList() ?? [],
                        Category = category
                    };
                    resources.Add(target);
                }
            }

            target.Category = category;
            target.CostUsd += amount;
        }

        return resources;
    }

    public static void FinalizeReport(ReportData data)
    {
        foreach (var resource in data.Resources)
        {
            resource.CostUsd = Math.Round(resource.CostUsd, 4);
            if (string.IsNullOrWhiteSpace(resource.Category)) resource.Category = resource.PlanDisplayName;
            if (string.IsNullOrWhiteSpace(resource.Key)) resource.Key = $"{resource.SubscriptionId}|{resource.PlanName}|{resource.Id}".ToLowerInvariant();
        }

        data.TotalCostUsd = Math.Round(data.Resources.Where(r => r.CostUsd > 0).Sum(r => r.CostUsd), 4);
        foreach (var plan in data.Plans)
        {
            var applicable = data.Resources.Where(r => r.SubscriptionId.Equals(plan.SubscriptionId, StringComparison.OrdinalIgnoreCase) && r.PlanName.Equals(plan.PlanName, StringComparison.OrdinalIgnoreCase)).ToList();
            plan.ResourceCount = applicable.Count(r => r.Scope != "Historical aggregate");
            plan.CostUsd = Math.Round(applicable.Sum(r => Math.Max(0, r.CostUsd)), 4);
        }

        data.Plans = data.Plans.OrderByDescending(p => p.CostUsd).ThenBy(p => p.PlanDisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        data.Resources = data.Resources.OrderByDescending(r => r.CostUsd).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        data.Views = BuildViews(data.Resources.Where(r => r.CostUsd > 0));
    }

    private static CoverageResource ToCoverage(PlanDefinition plan, string id, string name, string type, string typeLabel, string resourceGroup, string location, string scope) => new()
    {
        Key = $"{plan.SubscriptionId}|{plan.Name}|{id}".ToLowerInvariant(),
        Id = id,
        Name = name,
        ResourceType = type,
        ResourceTypeLabel = typeLabel,
        ResourceGroup = resourceGroup,
        Location = location,
        SubscriptionId = plan.SubscriptionId,
        SubscriptionName = plan.SubscriptionName,
        PlanName = plan.Name,
        PlanDisplayName = plan.DisplayName,
        Tier = plan.Tier,
        SubPlan = plan.SubPlan,
        Coverage = plan.Coverage,
        Scope = scope,
        Extensions = plan.Extensions.ToList(),
        Category = plan.DisplayName
    };

    private static Dictionary<string, ViewData> BuildViews(IEnumerable<CoverageResource> resources)
    {
        var priced = resources.ToList();
        return new Dictionary<string, ViewData>(StringComparer.OrdinalIgnoreCase)
        {
            ["subcategory"] = View("MeterSubCategory", priced, r => r.Category),
            ["subscription"] = View("Subscription", priced, r => r.SubscriptionName),
            ["resourceType"] = View("Resource type", priced, r => r.ResourceTypeLabel),
            ["coverage"] = View("Coverage", priced, r => r.Coverage)
        };
    }

    private static ViewData View(string dimension, IEnumerable<CoverageResource> resources, Func<CoverageResource, string> group) => new()
    {
        Dims = [dimension, "Resource"],
        Rows = resources.GroupBy(r => new { Group = group(r), r.Key })
            .Select(g => new ViewRow { Keys = [g.Key.Group, g.Key.Key], CostUsd = Math.Round(g.Sum(r => r.CostUsd), 4) })
            .OrderByDescending(r => r.CostUsd).ToList()
    };
}

internal static class AgentExport
{
    public static object Create(ReportData data) => new
    {
        schemaVersion = "1.0",
        tool = "azuretree",
        generated = data.Generated,
        tenant = new { name = data.TenantName, id = data.Tenant },
        reportingPeriod = new { start = data.PeriodStart, end = data.PeriodEnd, description = "Latest 30 complete UTC days" },
        data.Currency,
        data.TotalCostUsd,
        data.SubscriptionCount,
        enabledPlans = data.Plans,
        pricedResources = data.Resources.Where(r => r.CostUsd > 0).ToList(),
        coveredResourcesWithoutPositiveCost = data.Resources.Where(r => r.CostUsd <= 0).ToList(),
        collectionErrors = data.Errors,
        interpretation = new[]
        {
            "CostUsd values and percentages are actual CostUSD; a dominant top-level treemap group is visually capped at 50% when it exceeds all other visible groups combined.",
            "The period is the latest 30 complete UTC days.",
            "General Azure costs are excluded; only Microsoft Defender and Microsoft Defender for Cloud ServiceName values are queried.",
            "Enabled-plan resource counts include applicable resources without current positive cost.",
            "Historical or inaccessible resource costs are consolidated by subscription and MeterSubCategory.",
            "Partially covered does not mean every discovered resource is fully protected.",
            "A nonempty collectionErrors array means the report is incomplete."
        }
    };
}

internal sealed class AzureCli
{
    private readonly string executable;
    private readonly List<string> prefix;

    public AzureCli()
    {
        (executable, prefix) = Resolve();
    }

    public async Task<JsonDocument> JsonAsync(IEnumerable<string> arguments, string operation)
    {
        var result = await RunAsync(arguments);
        if (result.ExitCode != 0)
        {
            throw new AppException(ExplainFailure(operation, result.StandardError));
        }

        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            throw new AppException($"{operation} returned invalid JSON: {ex.Message}");
        }
    }

    public async Task<CliResult> RunAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var value in prefix) start.ArgumentList.Add(value);
        foreach (var value in arguments) start.ArgumentList.Add(value);

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new AppException("Azure CLI could not be started.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return new CliResult(process.ExitCode, stdout.Result, stderr.Result.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new AppException("Azure CLI was not found. Install Azure CLI, then run 'az login'.");
        }
    }

    private static (string Executable, List<string> Prefix) Resolve()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ("az", []);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cmd = Path.Combine(directory, "az.cmd");
            if (!File.Exists(cmd)) continue;
            var python = Path.GetFullPath(Path.Combine(directory, "..", "python.exe"));
            if (File.Exists(python)) return (python, ["-IBm", "azure.cli"]);
        }

        return ("az.exe", []);
    }

    private static string ExplainFailure(string operation, string error)
    {
        var safe = string.IsNullOrWhiteSpace(error) ? "Azure CLI returned no diagnostic text." : error;
        if (safe.Contains("az login", StringComparison.OrdinalIgnoreCase) || safe.Contains("Please run 'az login'", StringComparison.OrdinalIgnoreCase))
            return $"Azure login required for {operation}. Run 'az login' and select a subscription.";
        if (safe.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase) || safe.Contains("not authorized", StringComparison.OrdinalIgnoreCase) || safe.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return $"Authorization failed during {operation}: {safe}";
        return $"{operation} failed: {safe}";
    }
}

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

internal static class AzureCollector
{
    public static async Task<ReportData> CollectAsync(Options options)
    {
        var cli = new AzureCli();
        Console.WriteLine("Discovering Azure CLI account and subscriptions ...");
        using var accountDocument = await cli.JsonAsync(["account", "show", "--output", "json", "--only-show-errors"], "current Azure account discovery");
        var account = accountDocument.RootElement;
        var currentTenant = RequiredString(account, "tenantId", "Current Azure account has no tenantId.");
        var currentSubscription = RequiredString(account, "id", "Current Azure account has no selected subscription ID.");
        var tenant = options.Tenant ?? currentTenant;

        using var listDocument = await cli.JsonAsync(["account", "list", "--all", "--output", "json", "--only-show-errors"], "Azure subscription discovery");
        if (listDocument.RootElement.ValueKind != JsonValueKind.Array) throw new AppException("Azure subscription discovery returned a malformed response.");
        var available = new List<SubscriptionInfo>();
        foreach (var item in listDocument.RootElement.EnumerateArray())
        {
            var state = String(item, "state");
            var itemTenant = String(item, "tenantId");
            if (!state.Equals("Enabled", StringComparison.OrdinalIgnoreCase) || !itemTenant.Equals(tenant, StringComparison.OrdinalIgnoreCase)) continue;
            var id = String(item, "id");
            if (!string.IsNullOrWhiteSpace(id)) available.Add(new SubscriptionInfo(id, String(item, "name", id), itemTenant));
        }

        if (available.Count == 0) throw new AppException($"No enabled subscriptions were found for tenant {tenant}.");
        var selected = SelectSubscriptions(available, options.Subscriptions, currentSubscription);
        var tenantName = await ResolveTenantNameAsync(cli, tenant);
        Console.WriteLine($"Tenant: {tenantName} ({tenant}); subscriptions: {selected.Count}");

        var endExclusive = DateTime.UtcNow.Date;
        var start = endExclusive.AddDays(-30);
        var end = endExclusive.AddDays(-1);
        // Serial collection stays below Cost Management's tenant-level burst limit and also
        // prevents concurrent first-run Azure CLI extension installation races.
        var semaphore = new SemaphoreSlim(1);
        var tasks = selected.Select(async subscription =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await CollectSubscriptionAsync(cli, subscription, start, end);
            }
            catch (Exception ex)
            {
                return SubscriptionResult.Failure(subscription, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        var successful = results.Where(r => r.Error is null).ToList();
        if (successful.Count == 0)
        {
            var detail = string.Join("; ", results.Select(r => $"{r.Subscription.Name} ({r.Subscription.Id}): {r.Error}"));
            throw new AppException($"Every selected subscription failed. {detail}");
        }

        var data = new ReportData
        {
            Generated = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
            Tenant = tenant,
            TenantName = tenantName,
            SubscriptionCount = successful.Count,
            PeriodStart = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodEnd = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Currency = "USD",
            Demo = false,
            Resources = successful.SelectMany(r => r.Resources).ToList(),
            Plans = successful.SelectMany(r => r.Plans).ToList(),
            Errors = results.Where(r => r.Error is not null).Select(r => new CollectionError
            {
                SubscriptionId = r.Subscription.Id,
                SubscriptionName = r.Subscription.Name,
                Message = r.Error!
            }).ToList()
        };
        DataBuilder.FinalizeReport(data);
        return data;
    }

    private static async Task<SubscriptionResult> CollectSubscriptionAsync(AzureCli cli, SubscriptionInfo subscription, DateTime start, DateTime end)
    {
        Console.WriteLine($"  {subscription.Name}: reading enabled Defender plans ...");
        var plans = await ReadPlansAsync(cli, subscription);
        var resourceTypes = plans.SelectMany(p => p.ResourceTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"  {subscription.Name}: reading applicable Resource Graph inventory ...");
        var inventory = await ReadInventoryAsync(cli, subscription, resourceTypes);
        Console.WriteLine($"  {subscription.Name}: reading actual Defender CostUSD ...");
        var costs = await ReadCostsAsync(cli, subscription, start, end);
        var resources = DataBuilder.BuildResources(subscription, plans, inventory, costs);
        var summaries = plans.Select(plan => new PlanSummary
        {
            SubscriptionId = plan.SubscriptionId,
            SubscriptionName = plan.SubscriptionName,
            PlanName = plan.Name,
            PlanDisplayName = plan.DisplayName,
            Tier = plan.Tier,
            SubPlan = plan.SubPlan,
            Coverage = plan.Coverage,
            Extensions = plan.Extensions.ToList()
        }).ToList();
        Console.WriteLine($"  {subscription.Name}: {plans.Count} plans, {inventory.Count} resources, {resources.Count(r => r.CostUsd > 0)} priced boxes");
        return SubscriptionResult.Success(subscription, resources, summaries);
    }

    private static async Task<List<PlanDefinition>> ReadPlansAsync(AzureCli cli, SubscriptionInfo subscription)
    {
        var uri = $"https://management.azure.com/subscriptions/{subscription.Id}/providers/Microsoft.Security/pricings?api-version=2024-01-01";
        using var document = await cli.JsonAsync(["rest", "--method", "get", "--uri", uri, "--output", "json", "--only-show-errors"], $"Defender plan discovery for {subscription.Name}");
        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new AppException($"Defender plan discovery for {subscription.Name} returned a malformed response.");
        var plans = new List<PlanDefinition>();
        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var properties)) continue;
            var tier = String(properties, "pricingTier");
            var deprecated = properties.TryGetProperty("deprecated", out var deprecatedValue) &&
                (deprecatedValue.ValueKind == JsonValueKind.True || ValueString(deprecatedValue).Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!tier.Equals("Standard", StringComparison.OrdinalIgnoreCase) || deprecated) continue;
            var name = String(item, "name", "Unknown");
            var extensions = new List<string>();
            if (properties.TryGetProperty("extensions", out var extensionArray) && extensionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var extension in extensionArray.EnumerateArray())
                {
                    if (String(extension, "isEnabled").Equals("True", StringComparison.OrdinalIgnoreCase))
                    {
                        var extensionName = String(extension, "name");
                        if (!string.IsNullOrWhiteSpace(extensionName)) extensions.Add(extensionName);
                    }
                }
            }

            plans.Add(new PlanDefinition(subscription.Id, subscription.Name, name, DefenderCatalog.DisplayName(name), tier,
                NullIfEmpty(String(properties, "subPlan")), NormalizeCoverage(String(properties, "resourcesCoverageStatus"), tier),
                extensions, DefenderCatalog.IsSubscriptionWide(name), DefenderCatalog.ResourceTypes(name)));
        }
        return plans;
    }

    private static async Task<List<InventoryResource>> ReadInventoryAsync(AzureCli cli, SubscriptionInfo subscription, List<string> resourceTypes)
    {
        if (resourceTypes.Count == 0) return [];
        var quoted = string.Join(", ", resourceTypes.Select(t => $"'{t.Replace("'", "''", StringComparison.Ordinal)}'"));
        var query = $"Resources | where type in~ ({quoted}) | project id, name, type=tolower(type), resourceGroup, location, subscriptionId";
        var results = new Dictionary<string, InventoryResource>(StringComparer.OrdinalIgnoreCase);
        string? skipToken = null;
        do
        {
            var args = new List<string> { "graph", "query", "--subscriptions", subscription.Id, "-q", query, "--first", "1000", "--output", "json", "--only-show-errors" };
            if (!string.IsNullOrWhiteSpace(skipToken)) { args.Add("--skip-token"); args.Add(skipToken); }
            using var document = await cli.JsonAsync(args, $"Resource Graph query for {subscription.Name}");
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new AppException($"Resource Graph query for {subscription.Name} returned a malformed response.");
            foreach (var item in data.EnumerateArray())
            {
                var id = String(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                results[id] = new InventoryResource(id, String(item, "name", id.Split('/').Last()), String(item, "type").ToLowerInvariant(), String(item, "resourceGroup"), String(item, "location"), String(item, "subscriptionId", subscription.Id));
            }
            skipToken = NullIfEmpty(String(root, "skip_token"));
        } while (skipToken is not null);
        return results.Values.ToList();
    }

    private static async Task<List<CostRow>> ReadCostsAsync(AzureCli cli, SubscriptionInfo subscription, DateTime start, DateTime end)
    {
        var uri = $"https://management.azure.com/subscriptions/{subscription.Id}/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000";
        var body = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            dataset = new
            {
                granularity = "None",
                aggregation = new { totalCostUSD = new { name = "CostUSD", function = "Sum" } },
                grouping = new[] { new { type = "Dimension", name = "MeterSubCategory" }, new { type = "Dimension", name = "ResourceId" } },
                filter = new { dimensions = new { name = "ServiceName", @operator = "In", values = new[] { "Microsoft Defender", "Microsoft Defender for Cloud" } } }
            },
            timeframe = "Custom",
            timePeriod = new
            {
                from = start.ToString("yyyy-MM-dd'T'00:00:00.000+00:00", CultureInfo.InvariantCulture),
                to = end.ToString("yyyy-MM-dd'T'23:59:59.999+00:00", CultureInfo.InvariantCulture)
            }
        });

        var rows = new List<CostRow>();
        string? next = uri;
        while (next is not null)
        {
            using var document = await CostPageWithRetryAsync(cli, next, body, subscription.Name);
            var root = document.RootElement;
            if (!root.TryGetProperty("properties", out var properties)) throw new AppException($"Cost Management for {subscription.Name} returned a malformed response.");
            if (!properties.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array || !properties.TryGetProperty("rows", out var rowArray) || rowArray.ValueKind != JsonValueKind.Array)
                throw new AppException($"Cost Management for {subscription.Name} returned malformed columns or rows.");
            var indices = columns.EnumerateArray().Select((c, index) => (Name: String(c, "name"), Index: index)).ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            foreach (var required in new[] { "CostUSD", "MeterSubCategory", "ResourceId" })
                if (!indices.ContainsKey(required)) throw new AppException($"Cost Management for {subscription.Name} did not return required column {required}.");

            foreach (var row in rowArray.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                var cells = row.EnumerateArray().ToArray();
                if (cells.Length <= indices.Values.Max()) continue;
                if (!TryFiniteDouble(cells[indices["CostUSD"]], out var cost) || cost <= 0) continue;
                var category = DefenderCatalog.NormalizeCategory(ValueString(cells[indices["MeterSubCategory"]]));
                var resourceId = ValueString(cells[indices["ResourceId"]]);
                rows.Add(new CostRow(category, resourceId, cost));
            }
            next = NullIfEmpty(String(properties, "nextLink"));
        }
        return rows;
    }

    private static async Task<JsonDocument> CostPageWithRetryAsync(AzureCli cli, string uri, string body, string subscriptionName)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await cli.RunAsync(["rest", "--method", "post", "--uri", uri, "--body", body, "--output", "json", "--only-show-errors"]);
            if (result.ExitCode == 0)
            {
                try { return JsonDocument.Parse(result.StandardOutput); }
                catch (JsonException ex) { throw new AppException($"Cost Management for {subscriptionName} returned invalid JSON: {ex.Message}"); }
            }

            var throttled = result.StandardError.Contains("429", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase);
            if (!throttled || attempt == 3)
            {
                if (result.StandardError.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                    throw new AppException($"Cost Management permission failure for {subscriptionName}. Cost Management Reader is required: {result.StandardError}");
                throw new AppException($"Cost Management query for {subscriptionName} failed: {result.StandardError}");
            }
            await Task.Delay(TimeSpan.FromSeconds(5 * Math.Pow(2, attempt)));
        }
        throw new AppException($"Cost Management query for {subscriptionName} failed after retries.");
    }

    private static List<SubscriptionInfo> SelectSubscriptions(List<SubscriptionInfo> available, List<string> requested, string currentSubscription)
    {
        if (requested.Count == 0)
        {
            var current = available.FirstOrDefault(s => s.Id.Equals(currentSubscription, StringComparison.OrdinalIgnoreCase));
            return current is not null ? [current] : available;
        }

        var selected = new List<SubscriptionInfo>();
        var unresolved = new List<string>();
        foreach (var value in requested)
        {
            var matches = available.Where(s => s.Id.Equals(value, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) unresolved.Add(value);
            else if (matches.Count > 1) throw new AppException($"Subscription name '{value}' is ambiguous; use its subscription ID.");
            else if (!selected.Any(s => s.Id.Equals(matches[0].Id, StringComparison.OrdinalIgnoreCase))) selected.Add(matches[0]);
        }
        if (unresolved.Count > 0) throw new AppException($"Subscriptions not found in the selected tenant: {string.Join(", ", unresolved)}.");
        return selected;
    }

    private static async Task<string> ResolveTenantNameAsync(AzureCli cli, string tenant)
    {
        try
        {
            using var document = await cli.JsonAsync(["rest", "--method", "get", "--uri", "https://management.azure.com/tenants?api-version=2020-01-01", "--output", "json", "--only-show-errors"], "tenant metadata discovery");
            if (document.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (!String(item, "tenantId").Equals(tenant, StringComparison.OrdinalIgnoreCase)) continue;
                    var displayName = String(item, "displayName");
                    if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
                    var domain = String(item, "defaultDomain");
                    if (!string.IsNullOrWhiteSpace(domain)) return domain;
                }
            }
        }
        catch (AppException ex)
        {
            Console.Error.WriteLine($"azuretree: tenant friendly name unavailable: {ex.Message}");
        }
        return tenant;
    }

    private static string NormalizeCoverage(string value, string tier) => value.ToLowerInvariant() switch
    {
        "fullycovered" => "Fully covered",
        "partiallycovered" => "Partially covered",
        "notcovered" => "Not covered",
        _ when tier.Equals("Free", StringComparison.OrdinalIgnoreCase) => "Not covered",
        _ => "Enabled"
    };

    private static string RequiredString(JsonElement element, string name, string message)
    {
        var value = String(element, name);
        return string.IsNullOrWhiteSpace(value) ? throw new AppException(message) : value;
    }

    private static string String(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) ? ValueString(value) : fallback;

    private static string ValueString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => ""
    };

    private static bool TryFiniteDouble(JsonElement value, out double result)
    {
        var success = value.ValueKind == JsonValueKind.Number
            ? value.TryGetDouble(out result)
            : double.TryParse(ValueString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return success && !double.IsNaN(result) && !double.IsInfinity(result);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class SubscriptionResult
{
    public required SubscriptionInfo Subscription { get; init; }
    public List<CoverageResource> Resources { get; init; } = [];
    public List<PlanSummary> Plans { get; init; } = [];
    public string? Error { get; init; }
    public static SubscriptionResult Success(SubscriptionInfo subscription, List<CoverageResource> resources, List<PlanSummary> plans) => new() { Subscription = subscription, Resources = resources, Plans = plans };
    public static SubscriptionResult Failure(SubscriptionInfo subscription, string error) => new() { Subscription = subscription, Error = error };
}
