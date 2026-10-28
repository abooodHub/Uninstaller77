# Engine regression runner

The engine tests and process fixtures are no longer compiled into the shipping application. This Windows console project links the current engine/model/ViewModel source files. Historical Software Health and Install Monitor suites remain runnable from tests/retired/Engineering; they are not restored to the product UI or bridge.

Run in the authorized desktop user context (no Administrator requirement):

```powershell
dotnet run --project tests/Engine.Tests/Engine.Tests.csproj -c Release -- --report-dir _build/ENGINE_RECHECK
```

The suite reads installed-program inventory but only uninstalls a unique HKCU fixture it creates itself. It also exercises owned filesystem fixtures and temporary SQLite databases. No real program or browser extension is removed. Sandbox registry/filesystem/AppX restrictions are failures, not passes; use the recorded exception evidence to distinguish execution-context restrictions from defects.

Read-only real browser inventory:

```powershell
.\tests\Engine.Tests\bin\Release\net8.0-windows\Uninstaller77.EngineTests.exe --browser-inventory _build/BROWSER_INVENTORY.json
```

The shipping EXE rejects obsolete --run-tests/--fixture-* switches; use this test project instead.
