# WPF integration smoke checks

Run on Windows with the .NET 10 SDK from the repository root:

```powershell
dotnet run --project tests/LocalResourceLibrary.UiChecks/LocalResourceLibrary.UiChecks.csproj -c Release
```

The runner opens the actual WPF main window with its real application styles, controls, bindings, view model, resource service, and SQLite persistence. All resource files, database contents, and settings are generated in one isolated temporary directory. Production startup is suppressed before the dispatcher is pumped, and the runner never opens the user's default application or Explorer.

The 18 scenarios cover empty startup; the customized language dropdown's real popup and selection; modal project-name validation, cancellation and creation; exact name and multiline-description return values from the input dialog; project chooser popup labels, selection, confirmation, cancellation and empty choices; multi-file/folder PreviewDrop routed from the Alias field; metadata and project editing; project filtering and deduplication; all six debounced search fields; language switching with an unsaved draft; translated context menus; routed double-click and open-location dispatch; minimum window size; detail scrollbar thumb movement and return to the top; missing-resource refresh; malformed settings; safe error translation; and binding diagnostics.

The modal checks invoke the application's internal dialog entry points through reflection and interact with their real windows while `ShowDialog` runs. Dispatcher callbacks have bounded waits and close the test dialog on assertion failure. Confirmation and cancel use WPF automation actions, exercising the actual Button behavior; dropdown selection uses the real popup's realized items and routed mouse-up handler. Project creation and cancellation also run through the main window's actual New project action. The scrollbar check routes thumb-drag events through the customized scrollbar and asserts actual detail-content movement in both directions. These checks use only the isolated library and do not rename or launch external files.

The generated WPF client-area PNGs and text report are saved in `artifacts/qa/`, including `ui-dialog-project.png` and `ui-dialog-choose.png` for the customized dialogs, plus `ui-dropdown-language.png` and `ui-dropdown-project.png` for their open popup surfaces and selected options. Popup captures render their separate WPF visual tree; dialog captures include the window's client surface and outer content margins. An optional first argument selects a different output directory. The runner returns a nonzero exit code on failure, bounds dispatcher waits, and closes only after asynchronous work has finished and the isolated draft has been cleared.

This is an automated integration check. It raises WPF routed events rather than performing a physical mouse drag. It does not exercise the native file/folder dialogs, clipboard, rename/repair dialogs, actual default applications, or manual visual review. Windows shell dispatch is mocked; physical rename and repair integrity are tested by the separate core regression runner.
