# azuredefendertree

azuredefendertree answers one question: which Azure resources are covered by enabled Microsoft Defender for Cloud plans, and how much actual Defender `CostUSD` did each category and resource contribute?

It is a .NET 8 console application with no NuGet dependencies. It reads Azure through the signed-in Azure CLI and writes one self-contained HTML file that opens locally in Chrome or Edge. The report makes no network requests.

## Try it without Azure

```powershell
dotnet run -- --demo
```

This writes `out/azuredefendertree.html` and opens it. The deterministic fixture contains two subscriptions, eight priced resource boxes, and exactly USD 1,277.45 of Defender cost.

## Read a tenant

```powershell
az login
az account set --subscription <subscription-id>
dotnet run -- --mdc
```

The live command reads the currently selected enabled subscription in the active Azure CLI tenant. Select one or more subscriptions by ID or exact display name, and optionally choose a tenant:

```powershell
dotnet run -- --mdc --tenant <tenant-id>
dotnet run -- --mdc --subscription <id-or-name> --subscription <another-id-or-name>
```

Subscription matching is case-insensitive. Every requested subscription must resolve inside the selected tenant. A failure in one subscription is recorded in the report, sidebar, footer, and export while successful subscriptions remain available. The command fails only when all selected subscriptions fail.

Live runs save the reusable source data to `out/azuredefendertree-data.json`. Reopen it without Azure access:

```powershell
dotnet run -- --from out/azuredefendertree-data.json
```

Other options:

| Option | Meaning |
|---|---|
| `--export [FILE]` | Also write agent JSON; default `out/azuredefendertree-export.json` |
| `--out FILE` | Set the HTML destination |
| `--no-open` | Do not open the browser |
| `--help` | Show command help |

## Permissions and APIs

All operations are read-only and use Azure CLI authentication. Reader or Security Reader covers resource inventory and Defender configuration. **Cost Management Reader** is separately required for price-weighted boxes.

The collector uses:

- `az account show` and `az account list --all` for tenant-safe subscription discovery.
- `GET /tenants?api-version=2020-01-01` for a friendly tenant name when available.
- `GET /subscriptions/{id}/providers/Microsoft.Security/pricings?api-version=2024-01-01` for Defender plan configuration.
- `az graph query` for applicable current resources, paged with the CLI-provided skip token and deduplicated by complete ARM ID.
- `POST /subscriptions/{id}/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000` for `ActualCost`, grouped by `MeterSubCategory` and `ResourceId`.

The Cost Management filter admits exactly these `ServiceName` values:

```text
Microsoft Defender
Microsoft Defender for Cloud
```

General Azure spend is excluded. The reporting window is the latest 30 complete UTC days: midnight UTC 30 days ago through 23:59:59.999 UTC yesterday.

## Reading the report

Treemap area is based on CostUSD, not resource count.

Every group and resource leaf starts from positive actual `CostUSD` and uses a squarified treemap. When one top-level group costs more than all other visible groups combined, its display area is capped at 50%; the actual cost and percentage remain unchanged, and its interior resource boxes remain proportional to their costs. There are no minimum areas, logarithms, or count-based weights. The four buttons regroup the same priced records by Meter Subcategory, Subscription, Resource type, or Coverage.

Resources with no positive attributed Defender cost remain in plan coverage counts but do not receive treemap area.

Current Resource Graph inventory is joined to billing rows by case-insensitive ARM ID. A billing row can refer to a deleted resource or one the current identity cannot see. Those costs are not dropped: they are consolidated into one **Unassigned or deleted resources** tile per subscription and Meter Subcategory. The tile does not claim that its historical IDs are current inventory.

`Partially covered` is the state returned by the Defender pricing API. It does not mean every discovered resource is fully protected. Open the Defender for Cloud portal for resource-level protection and recommendation details.

Controls:

- Click selects; double-click or Enter opens a group.
- Backspace or a breadcrumb moves up.
- `1`–`4` switch views.
- `/` focuses the filter. Escape clears the filter first, then selection.
- `E` downloads the agent export.

Filtering matches category, resource name, ARM ID, Azure resource type, resource group, subscription, and coverage. Filtered percentages remain explicitly relative to full Defender cost.

## Agent export

```powershell
dotnet run -- --from out/azuredefendertree-data.json --export
```

The CLI option and in-browser **Export for AI** action use the same versioned semantics: tenant and period metadata, enabled plans, priced resources, zero-cost covered resources, `CostUSD`, ARM IDs, coverage, extensions, and collection errors. The export includes interpretation notes so another tool does not confuse plan counts with treemap weight or partial coverage with full protection.

## Windows publishing

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`viewer.html` is embedded in the assembly. The published executable needs no sidecar template or separately installed .NET runtime. It does need Azure CLI for a live run. On normal Azure CLI MSI installations, azuredefendertree invokes the adjacent Python runtime directly so paged URLs containing `&$skiptoken` do not pass through `cmd.exe`.

## Privacy

Generated files contain billing and inventory information: tenant IDs, subscription IDs and names, resource names, ARM IDs, locations, Defender plans, extensions, and actual `CostUSD`. They are written locally, never uploaded by azuredefendertree, and the entire `out/` directory is Git-ignored. Share the HTML, cache, and export only with people authorized to see this information.

## Troubleshooting

- **Azure CLI not found:** install Azure CLI and make `az` available on `PATH`.
- **Login required:** run `az login`, then `az account set --subscription <id>`.
- **No subscriptions:** confirm the requested tenant and that the subscriptions have state `Enabled`.
- **Defender or inventory authorization failure:** assign Reader or Security Reader at the relevant scope.
- **Cost Management authorization failure:** assign Cost Management Reader. This permission is distinct from Defender configuration access.
- **Partial report:** inspect Collection warnings in the sidebar or `collectionErrors` in the export for the exact subscription name, ID, and error.
- **Unexpected Key Vault counts:** compare with `az graph query -q "Resources | where type =~ 'microsoft.keyvault/vaults' | summarize count()" --subscriptions <id>`. Inventory is deduplicated by ARM ID; historical cost appears only in the consolidated unassigned tile.

## License

MIT. See [LICENSE](LICENSE).
