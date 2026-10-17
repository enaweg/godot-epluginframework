# Code coverage history

Line coverage of `src/eplugin-framework/addons/ePlugin` on `main`, recorded by the CI-PR workflow after every push. This branch is generated; do not edit it.

![Line coverage](badge.svg)

**76.67%** (7024 of 9161 lines) at [73f8104](https://github.com/enaweg/godot-epluginframework/commit/73f8104ae916ade392b7d2fffa7cddf1fd75b81c), 2026-10-17.

```mermaid
xychart-beta
    title "Line coverage on main (last 11 pushes)"
    x-axis ["659bbaa", "fc57fee", "ce064dc", "7cfc8b2", "b7e2f25", "caa9da2", "bd7e681", "ff903cf", "10401ac", "e44d91f", "73f8104"]
    y-axis "Line coverage (%)" 67 --> 79
    line [69.50, 69.48, 69.48, 69.48, 76.80, 76.80, 76.80, 76.80, 76.64, 76.67, 76.67]
```

| Date | Commit | Line coverage | Change |
|:--|:--|--:|--:|
| 2026-10-17 | [73f8104](https://github.com/enaweg/godot-epluginframework/commit/73f8104ae916ade392b7d2fffa7cddf1fd75b81c) | 76.67% | +0.00 pp |
| 2026-10-16 | [e44d91f](https://github.com/enaweg/godot-epluginframework/commit/e44d91f69afe69c46752ee3db6c85e90fa6c8822) | 76.67% | +0.03 pp |
| 2026-10-16 | [10401ac](https://github.com/enaweg/godot-epluginframework/commit/10401ac5c5d5370fabf3b0c53da57356d306e0e3) | 76.64% | -0.16 pp |
| 2026-10-16 | [ff903cf](https://github.com/enaweg/godot-epluginframework/commit/ff903cf12369521deb3cd90778bd1d3407543cbe) | 76.80% | +0.00 pp |
| 2026-10-16 | [bd7e681](https://github.com/enaweg/godot-epluginframework/commit/bd7e6810f7f41310d2483a1f6a4fc1df2366ea25) | 76.80% | +0.00 pp |
| 2026-10-15 | [caa9da2](https://github.com/enaweg/godot-epluginframework/commit/caa9da2162fca30674b6017e64232cd30cbc9554) | 76.80% | +0.00 pp |
| 2026-10-14 | [b7e2f25](https://github.com/enaweg/godot-epluginframework/commit/b7e2f25c6bde4f431377ff0e97157f6ef01d0a92) | 76.80% | +7.32 pp |
| 2026-10-14 | [7cfc8b2](https://github.com/enaweg/godot-epluginframework/commit/7cfc8b29f72977d4323e61f7ffb55d0aaa2dc2dd) | 69.48% | +0.00 pp |
| 2026-10-14 | [ce064dc](https://github.com/enaweg/godot-epluginframework/commit/ce064dcd5bc338704d9468a1fb171bd364e3c076) | 69.48% | +0.00 pp |
| 2026-10-14 | [fc57fee](https://github.com/enaweg/godot-epluginframework/commit/fc57fee06cb05235d5cafd80d663e2e5797f8ed6) | 69.48% | -0.02 pp |
| 2026-10-14 | [659bbaa](https://github.com/enaweg/godot-epluginframework/commit/659bbaa6769ba1db6a0da1e7bee266b2158f5e74) | 69.50% |  |

All measurements: [history.csv](history.csv).

<details><summary>Files at 73f8104 (94)</summary>

| File | Line coverage | Covered lines |
|:--|--:|--:|
| `IEEditorPluginExtensions.cs` | 0.0% | 0 of 15 |
| `Internal/EGlobal.UpdateWorkflow.cs` | 0.0% | 0 of 7 |
| `Internal/Manager/UpdateFailureDialog.cs` | 0.0% | 0 of 57 |
| `Internal/Manager/LicenseDialog.cs` | 7.5% | 12 of 161 |
| `Internal/Manager/UpdateSitesDialog.cs` | 8.2% | 16 of 194 |
| `Internal/Manager/EPluginManagerDialog.cs` | 8.7% | 52 of 600 |
| `Internal/ProjectSettingsDialog.cs` | 9.4% | 5 of 53 |
| `Internal/EGlobal.UpdateApply.cs` | 11.3% | 31 of 275 |
| `Internal/Manager/LocalSourcesDialog.cs` | 18.0% | 16 of 89 |
| `Internal/Manager/WelcomeDialog.cs` | 18.0% | 11 of 61 |
| `Internal/EGlobal.PluginManager.cs` | 18.8% | 12 of 64 |
| `Internal/EditorHost.cs` | 21.4% | 9 of 42 |
| `Internal/Manager/EditorWindows.cs` | 33.3% | 18 of 54 |
| `Internal/AssemblyUnloadCleanup.cs` | 38.9% | 7 of 18 |
| `EPlugin.cs` | 44.4% | 4 of 9 |
| `Internal/EGlobal.UpdateSites.cs` | 47.4% | 9 of 19 |
| `Internal/EGlobal.LocalSources.cs` | 54.4% | 31 of 57 |
| `EPluginPlugin.cs` | 62.8% | 167 of 266 |
| `Internal/Manager/EditorIcons.cs` | 69.8% | 30 of 43 |
| `Internal/EGlobal.Updates.cs` | 70.8% | 80 of 113 |
| `Internal/PluginTypes.cs` | 72.2% | 13 of 18 |
| `EditorPluginExtensions.cs` | 75.4% | 46 of 61 |
| `Logging/GodotConsoleLogger.cs` | 76.7% | 33 of 43 |
| `Internal/Dotnet/BuildOutputGuard.cs` | 80.0% | 40 of 50 |
| `Internal/ProgressSession.cs` | 81.4% | 153 of 188 |
| `Internal/ActivationProgress.cs` | 82.5% | 241 of 292 |
| `Internal/EGlobal.cs` | 85.3% | 850 of 996 |
| `Internal/AssemblyReload.cs` | 85.7% | 24 of 28 |
| `Internal/EGlobal.PlainPlugins.cs` | 87.6% | 127 of 145 |
| `Internal/Welcomes/PluginWelcome.cs` | 87.9% | 29 of 33 |
| `Internal/EGlobal.RecipeOperations.cs` | 88.4% | 61 of 69 |
| `Internal/Update/UpdateApplier.cs` | 88.5% | 360 of 407 |
| `Internal/Update/PackageFiles.cs` | 90.4% | 94 of 104 |
| `Internal/Licenses/PluginLicense.cs` | 91.1% | 41 of 45 |
| `Internal/EEditorPluginBuilder.cs` | 91.3% | 63 of 69 |
| `Internal/Manager/PluginCatalog.cs` | 92.0% | 23 of 25 |
| `Internal/EGlobal.Welcomes.cs` | 93.1% | 67 of 72 |
| `Internal/Update/InterimBridge.cs` | 93.2% | 41 of 44 |
| `Internal/Update/UpdateService.cs` | 93.6% | 88 of 94 |
| `Internal/Manager/ManagerReopenMarker.cs` | 94.1% | 16 of 17 |
| `Internal/EEditorPluginSubRecipeBuilder.cs` | 94.3% | 33 of 35 |
| `Internal/Dotnet/DotnetCliBase.cs` | 94.9% | 56 of 59 |
| `Internal/Dotnet/ProjectReferences.cs` | 95.0% | 38 of 40 |
| `Internal/Update/SafeZipExtractor.cs` | 95.0% | 57 of 60 |
| `Internal/Update/UpdateJournal.cs` | 95.0% | 57 of 60 |
| `Internal/Update/LocalIndexCache.cs` | 96.0% | 24 of 25 |
| `Internal/Update/RecipeSnapshot.cs` | 96.2% | 25 of 26 |
| `Internal/Update/BuildDiagnostics.cs` | 96.3% | 52 of 54 |
| `Internal/Manager/PluginManagerViewModel.cs` | 96.8% | 240 of 248 |
| `Internal/PlainPluginObserver.cs` | 97.1% | 67 of 69 |
| `Internal/Update/UpdateModels.cs` | 97.1% | 34 of 35 |
| `Internal/Update/UpdateRecovery.cs` | 97.3% | 143 of 147 |
| `Internal/Update/LocalPackageIndex.cs` | 97.4% | 74 of 76 |
| `Internal/Update/UpdateStateStore.cs` | 97.4% | 37 of 38 |
| `Internal/EGlobal.Licenses.cs` | 97.4% | 227 of 233 |
| `Internal/Manager/UpdateFlow.cs` | 98.0% | 144 of 147 |
| `Internal/Dotnet/NugetConfigManager.cs` | 98.0% | 147 of 150 |
| `Internal/Update/RecipeReconciler.cs` | 98.0% | 50 of 51 |
| `Internal/Update/UpdateHttp.cs` | 98.6% | 71 of 72 |
| `Internal/PluginStateStore.cs` | 98.8% | 412 of 417 |
| `Internal/Update/ReleaseSources.cs` | 98.9% | 90 of 91 |
| `Internal/EGlobal.UpdateRecipes.cs` | 98.9% | 184 of 186 |
| `Internal/Update/AddonPackageValidator.cs` | 98.9% | 92 of 93 |
| `Internal/Update/GitSource.cs` | 99.0% | 197 of 199 |
| `EEditorPluginMetadata.cs` | 100.0% | 4 of 4 |
| `Internal/Dotnet/DotnetCli10.cs` | 100.0% | 78 of 78 |
| `Internal/Dotnet/DotnetCli9.cs` | 100.0% | 78 of 78 |
| `Internal/Dotnet/DotnetVersionManager.cs` | 100.0% | 41 of 41 |
| `Internal/Dotnet/ExecuteCliBase.cs` | 100.0% | 33 of 33 |
| `Internal/Dotnet/ICheckedDotnetCli.cs` | 100.0% | 1 of 1 |
| `Internal/Dotnet/PluginsOnlyBuild.cs` | 100.0% | 31 of 31 |
| `Internal/EEditorPluginRecipe.cs` | 100.0% | 13 of 13 |
| `Internal/EPluginProgress.cs` | 100.0% | 1 of 1 |
| `Internal/Licenses/LicenseReview.cs` | 100.0% | 47 of 47 |
| `Internal/Licenses/LicenseSettings.cs` | 100.0% | 9 of 9 |
| `Internal/LoggingSettings.cs` | 100.0% | 9 of 9 |
| `Internal/Manager/EditorSignalConnections.cs` | 100.0% | 12 of 12 |
| `Internal/Manager/ObservedTask.cs` | 100.0% | 4 of 4 |
| `Internal/PluginContext.cs` | 100.0% | 14 of 14 |
| `Internal/PluginToggles.cs` | 100.0% | 13 of 13 |
| `Internal/RecipeEntries.cs` | 100.0% | 69 of 69 |
| `Internal/ShowHideHelper.cs` | 100.0% | 47 of 47 |
| `Internal/Update/KnownPlugins.Data.cs` | 100.0% | 764 of 764 |
| `Internal/Update/KnownPlugins.cs` | 100.0% | 6 of 6 |
| `Internal/Update/LocalSourceSettings.cs` | 100.0% | 46 of 46 |
| `Internal/Update/PackageFetcher.cs` | 100.0% | 61 of 61 |
| `Internal/Update/SemVer.cs` | 100.0% | 36 of 36 |
| `Internal/Update/UidMap.cs` | 100.0% | 10 of 10 |
| `Internal/Update/UpdateGroups.cs` | 100.0% | 20 of 20 |
| `Internal/Update/UpdateSettings.cs` | 100.0% | 18 of 18 |
| `Internal/Update/UpdateSiteSettings.cs` | 100.0% | 50 of 50 |
| `Internal/VersionConstraint.cs` | 100.0% | 25 of 25 |
| `Logging/GenericLoggerFactory.cs` | 100.0% | 7 of 7 |
| `Logging/NullLogger.cs` | 100.0% | 6 of 6 |

</details>
