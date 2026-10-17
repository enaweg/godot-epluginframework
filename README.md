# Code coverage history

Line coverage of `src/eplugin-framework/addons/ePlugin` on `main`, recorded by the CI-PR workflow after every push. This branch is generated; do not edit it.

![Line coverage](badge.svg)

**79.54%** (7647 of 9614 lines) at [d6fa7e2](https://github.com/enaweg/godot-epluginframework/commit/d6fa7e2bf3286e8cc0eb8b5330d6bb917aef2c8f), 2026-10-17.

```mermaid
xychart-beta
    title "Line coverage on main (last 16 pushes)"
    x-axis ["659bbaa", "fc57fee", "ce064dc", "7cfc8b2", "b7e2f25", "caa9da2", "bd7e681", "ff903cf", "10401ac", "e44d91f", "73f8104", "736827c", "6ff9d4b", "dd42316", "7b94ccd", "d6fa7e2"]
    y-axis "Line coverage (%)" 67 --> 82
    line [69.50, 69.48, 69.48, 69.48, 76.80, 76.80, 76.80, 76.80, 76.64, 76.67, 76.67, 76.46, 79.03, 79.03, 79.03, 79.54]
```

| Date | Commit | Line coverage | Change |
|:--|:--|--:|--:|
| 2026-10-17 | [d6fa7e2](https://github.com/enaweg/godot-epluginframework/commit/d6fa7e2bf3286e8cc0eb8b5330d6bb917aef2c8f) | 79.54% | +0.51 pp |
| 2026-10-17 | [7b94ccd](https://github.com/enaweg/godot-epluginframework/commit/7b94ccdc2ce811f3293f97e3c21b77515fffcf2c) | 79.03% | +0.00 pp |
| 2026-10-17 | [dd42316](https://github.com/enaweg/godot-epluginframework/commit/dd4231688782e8151d5296688ec0313a038db1d7) | 79.03% | +0.00 pp |
| 2026-10-17 | [6ff9d4b](https://github.com/enaweg/godot-epluginframework/commit/6ff9d4b729e3bfaacd8544a36ffb26408e059a0c) | 79.03% | +2.57 pp |
| 2026-10-17 | [736827c](https://github.com/enaweg/godot-epluginframework/commit/736827c1a81fe9a1d0974f7b4b69d82be89cd3d6) | 76.46% | -0.21 pp |
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

<details><summary>Files at d6fa7e2 (107)</summary>

| File | Line coverage | Covered lines |
|:--|--:|--:|
| `Internal/EGlobal.UpdateWorkflow.cs` | 0.0% | 0 of 7 |
| `Internal/Manager/UpdateFailureDialog.cs` | 0.0% | 0 of 57 |
| `Internal/Manager/LicenseDialog.cs` | 7.5% | 12 of 161 |
| `Internal/Manager/UpdateSitesDialog.cs` | 8.2% | 16 of 194 |
| `Internal/Manager/EPluginManagerDialog.cs` | 8.8% | 53 of 604 |
| `Internal/ProjectSettingsDialog.cs` | 9.4% | 5 of 53 |
| `Internal/Manager/LocalSourcesDialog.cs` | 18.0% | 16 of 89 |
| `Internal/Manager/WelcomeDialog.cs` | 18.0% | 11 of 61 |
| `Internal/Kinds/CSharpPluginWorkflow.cs` | 25.0% | 1 of 4 |
| `Internal/Manager/EditorWindows.cs` | 33.3% | 18 of 54 |
| `Internal/EGlobal.UpdateApply.cs` | 38.1% | 101 of 265 |
| `Internal/AssemblyUnloadCleanup.cs` | 38.9% | 7 of 18 |
| `Internal/Manager/AboutDialog.cs` | 40.4% | 19 of 47 |
| `EPlugin.cs` | 44.4% | 4 of 9 |
| `Internal/EGlobal.UpdateSites.cs` | 47.4% | 9 of 19 |
| `Internal/Kinds/GDScriptPluginWorkflow.cs` | 50.0% | 2 of 4 |
| `Internal/EGlobal.LocalSources.cs` | 54.4% | 31 of 57 |
| `EPluginPlugin.cs` | 62.8% | 167 of 266 |
| `Internal/Manager/EditorIcons.cs` | 68.2% | 30 of 44 |
| `Internal/Kinds/UnknownPluginWorkflow.cs` | 75.0% | 3 of 4 |
| `Internal/EditorHost.cs` | 75.6% | 34 of 45 |
| `Internal/EGlobal.Updates.cs` | 76.3% | 87 of 114 |
| `Logging/GodotConsoleLogger.cs` | 76.7% | 33 of 43 |
| `Internal/ProgressSession.cs` | 81.4% | 153 of 188 |
| `Internal/Kinds/GDExtensionRules.cs` | 81.5% | 22 of 27 |
| `Internal/ActivationProgress.cs` | 82.5% | 241 of 292 |
| `Internal/PluginTypes.cs` | 83.3% | 15 of 18 |
| `EditorPluginExtensions.cs` | 85.2% | 52 of 61 |
| `Internal/AssemblyReload.cs` | 85.7% | 24 of 28 |
| `Internal/Kinds/FrameworkPluginWorkflow.cs` | 85.7% | 6 of 7 |
| `Internal/EGlobal.cs` | 87.3% | 873 of 1000 |
| `Internal/EGlobal.PlainPlugins.cs` | 87.6% | 127 of 145 |
| `Internal/Welcomes/PluginWelcome.cs` | 87.9% | 29 of 33 |
| `Internal/EGlobal.RecipeOperations.cs` | 88.4% | 61 of 69 |
| `Internal/Update/AddonSwap.cs` | 89.3% | 92 of 103 |
| `Internal/Licenses/PluginLicense.cs` | 91.1% | 41 of 45 |
| `Internal/EEditorPluginBuilder.cs` | 91.3% | 63 of 69 |
| `Internal/Update/UpdateApplier.cs` | 92.6% | 387 of 418 |
| `Internal/Kinds/EPluginWorkflow.cs` | 92.9% | 13 of 14 |
| `Internal/EGlobal.Welcomes.cs` | 93.1% | 67 of 72 |
| `Internal/Update/InterimBridge.cs` | 93.2% | 41 of 44 |
| `Internal/Update/UpdateService.cs` | 93.6% | 88 of 94 |
| `Internal/EGlobal.PluginManager.cs` | 93.8% | 60 of 64 |
| `Internal/Manager/ManagerReopenMarker.cs` | 94.1% | 16 of 17 |
| `Internal/EEditorPluginSubRecipeBuilder.cs` | 94.3% | 33 of 35 |
| `Internal/Update/PluginCode.cs` | 94.7% | 18 of 19 |
| `Internal/Kinds/GDExtensionPluginWorkflow.cs` | 94.8% | 73 of 77 |
| `Internal/Dotnet/DotnetCliBase.cs` | 94.9% | 56 of 59 |
| `Internal/Dotnet/ProjectReferences.cs` | 95.0% | 38 of 40 |
| `Internal/Update/SafeZipExtractor.cs` | 95.0% | 57 of 60 |
| `Internal/Update/UpdateJournal.cs` | 95.1% | 58 of 61 |
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
| `Internal/Kinds/PluginKinds.cs` | 97.7% | 42 of 43 |
| `Internal/Manager/UpdateFlow.cs` | 98.0% | 144 of 147 |
| `Internal/Dotnet/BuildOutputGuard.cs` | 98.0% | 49 of 50 |
| `Internal/Dotnet/NugetConfigManager.cs` | 98.0% | 147 of 150 |
| `Internal/Update/RecipeReconciler.cs` | 98.0% | 50 of 51 |
| `Internal/EGlobal.UpdateRecipes.cs` | 98.4% | 184 of 187 |
| `Internal/Update/UpdateHttp.cs` | 98.6% | 71 of 72 |
| `Internal/PluginStateStore.cs` | 98.8% | 412 of 417 |
| `Internal/Kinds/GDExtensionConfig.cs` | 98.9% | 89 of 90 |
| `Internal/Update/ReleaseSources.cs` | 98.9% | 90 of 91 |
| `Internal/Update/AddonPackageValidator.cs` | 98.9% | 91 of 92 |
| `Internal/Update/GitSource.cs` | 99.0% | 197 of 199 |
| `Internal/Update/PackageFiles.cs` | 99.0% | 104 of 105 |
| `EEditorPluginMetadata.cs` | 100.0% | 4 of 4 |
| `IEEditorPluginExtensions.cs` | 100.0% | 15 of 15 |
| `Internal/Dotnet/DotnetCli10.cs` | 100.0% | 78 of 78 |
| `Internal/Dotnet/DotnetCli9.cs` | 100.0% | 78 of 78 |
| `Internal/Dotnet/DotnetVersionManager.cs` | 100.0% | 41 of 41 |
| `Internal/Dotnet/ExecuteCliBase.cs` | 100.0% | 33 of 33 |
| `Internal/Dotnet/ICheckedDotnetCli.cs` | 100.0% | 1 of 1 |
| `Internal/Dotnet/PluginsOnlyBuild.cs` | 100.0% | 31 of 31 |
| `Internal/EEditorPluginRecipe.cs` | 100.0% | 13 of 13 |
| `Internal/EPluginProgress.cs` | 100.0% | 1 of 1 |
| `Internal/Kinds/PluginKindWorkflow.cs` | 100.0% | 13 of 13 |
| `Internal/Licenses/LicenseReview.cs` | 100.0% | 47 of 47 |
| `Internal/Licenses/LicenseSettings.cs` | 100.0% | 9 of 9 |
| `Internal/LoggingSettings.cs` | 100.0% | 9 of 9 |
| `Internal/Manager/EditorSignalConnections.cs` | 100.0% | 12 of 12 |
| `Internal/Manager/ObservedTask.cs` | 100.0% | 4 of 4 |
| `Internal/Manager/PluginCatalog.cs` | 100.0% | 10 of 10 |
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
