# azuretree

azuretree shows which Azure resources are covered by enabled Microsoft Defender for Cloud plans and how much actual Defender `CostUSD` each category and resource contributed.

It is a dependency-free .NET 8 console app. Azure access goes through the signed-in Azure CLI, and the result is one self-contained offline HTML file.

## Quick start

Demo data, no Azure login required:

```powershell
dotnet run -- --demo
```

Live data from the selected subscription:

```powershell
az login
az account set --subscription <subscription-id>
dotnet run -- --mdc
```

Select multiple subscriptions or a tenant:

```powershell
dotnet run -- --mdc --subscription <id-or-name> --subscription <id-or-name>
dotnet run -- --mdc --tenant <tenant-id>
```

Live runs cache their source data. Reopen or export it without Azure calls:

```powershell
dotnet run -- --from out/azuretree-data.json
dotnet run -- --from out/azuretree-data.json --export
```

Use `--out FILE` to change the HTML path, `--export [FILE]` for agent JSON, and `--no-open` to skip opening the browser.

## Permissions and collection

All Azure operations are read-only. Reader or Security Reader covers inventory and Defender configuration. **Cost Management Reader** is separately required for price-weighted boxes.

The collector uses `GET /providers/Microsoft.Security/pricings?api-version=2024-01-01`, `az graph query`, and `POST /providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000`. Cost uses `ActualCost`, grouped by `MeterSubCategory` and `ResourceId`, and filtered to exactly:

```text
Microsoft Defender
Microsoft Defender for Cloud
```

General Azure spend is excluded. The period is the latest 30 complete UTC days. Resource Graph results are paged and deduplicated by complete ARM ID. Historical or inaccessible billing IDs are retained in one **Unassigned or deleted resources** tile per subscription and category.

## Reading the report

Treemap area is based on CostUSD, not resource count.

When one top-level group costs more than all other visible groups combined, its display area is capped at 50%. Its actual cost and percentage remain unchanged, and its resource boxes remain proportional to their costs.

Resources with no positive attributed Defender cost remain in plan coverage counts but do not receive treemap area.

`Partially covered` is the Defender pricing API state; it does not mean every discovered resource is fully protected.

Controls:

- Click selects; double-click or Enter opens a group.
- Backspace or a breadcrumb moves up.
- `1`–`4` switch grouping views.
- `/` focuses the filter; Escape clears it or the selection.
- `E` exports agent JSON.
- Azure Portal links in the details panel open the selected resource or resource type.

Filtering matches category, resource name, ARM ID, type, resource group, subscription, and coverage.

## Windows publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`viewer.html` is embedded in the executable. A live run still requires Azure CLI.

## Privacy

Generated files contain tenant and subscription identifiers, resource names and ARM IDs, locations, Defender plans, and actual costs. They remain local and `out/` is Git-ignored. Share them carefully.

## Troubleshooting

- Run `az login` if authentication has expired.
- Use Reader or Security Reader for inventory and Defender configuration.
- Use Cost Management Reader if price collection is denied.
- Check Collection warnings or `collectionErrors` when a report is partial.

## Credits

Inspired by [disktree](https://x.com/tobi/status/2103251521223921739) by [Tobi Lütke](https://x.com/tobi): the same idea, pointed at a cloud bill instead of a disk.

azuretree is not affiliated with or endorsed by Microsoft.

## License

MIT. See [LICENSE](LICENSE).
