# CoreLoader: C# mods for any YYC GameMaker game

CoreLoader lets you mod GameMaker games compiled with YYC using C#. Players know it as **Lodestone**:
that is the name on releases, the install folder and the overlay, while the assembly, namespace and
API keep the CoreLoader name. It is a `version.dll` placed next to the game exe, and it needs no
per-game setup. On first launch it finds the game's scripts, object events and builtins, then starts
.NET inside the game and loads C# mods from `Mods\`.

**The mod-author guide lives on the documentation site: <https://lodestone.forevka.dev/>**
(source in [`docs-site/`](../docs-site/)).

- [Getting started](https://lodestone.forevka.dev/modding/getting-started): the template, building, deploying, hot reload.
- [Cookbook](https://lodestone.forevka.dev/modding/cookbook): "so you want to..." recipes taken from the shipped mods.
- [API reference](https://lodestone.forevka.dev/modding/reference/api), [analyzers](https://lodestone.forevka.dev/modding/reference/analyzers), [test host](https://lodestone.forevka.dev/modding/reference/test-host).
- [Loader internals](https://lodestone.forevka.dev/internals/architecture): how the loader finds, hooks and calls the game's code.

The quickest start:

```powershell
dotnet new install managed\Templates\CoreLoaderMod
dotnet new coreloader-mod -n MyMod
```
