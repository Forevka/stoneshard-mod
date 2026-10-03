# CoreLoader: C# mods for any YYC GameMaker game

CoreLoader lets you mod GameMaker games compiled with YYC using C#. Players know it as **Lodestone**:
that is the name on releases, the install folder and the overlay, while the assembly, namespace and
API keep the CoreLoader name. It is a `version.dll` placed next to the game exe, and it needs no
per-game setup. On first launch it finds the game's scripts, object events and builtins, then starts
.NET inside the game and loads C# mods from `Mods\`.

**The mod-author guide lives on the documentation site: <https://forevka.github.io/stoneshard-mod/>**
(source in [`docs-site/`](../docs-site/)).

- [Getting started](https://forevka.github.io/stoneshard-mod/modding/getting-started): the template, building, deploying, hot reload.
- [Cookbook](https://forevka.github.io/stoneshard-mod/modding/cookbook): "so you want to..." recipes taken from the shipped mods.
- [API reference](https://forevka.github.io/stoneshard-mod/modding/reference/api), [analyzers](https://forevka.github.io/stoneshard-mod/modding/reference/analyzers), [test host](https://forevka.github.io/stoneshard-mod/modding/reference/test-host).
- [Loader internals](https://forevka.github.io/stoneshard-mod/internals/architecture): how the loader finds, hooks and calls the game's code.

The quickest start:

```powershell
dotnet new install managed\Templates\CoreLoaderMod
dotnet new coreloader-mod -n MyMod
```
