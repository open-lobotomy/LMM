Lobotomy Mod Manager (`LMM`) includes native Harmony 2 support (`HarmonyX` `2.10.2`) and MIT-licensed compatibility libraries (`0Harmony109.dll` and `0Harmony12.dll` through `HarmonyXInterop.dll`). Mods built for Harmony `1.0.9.x`, Harmony `1.2.0.x`, and Harmony `2.x` run together in one shared detour backend, and `LMM` does not modify mod `.dll` files on disk.

## Architecture

- `LobotomyBaseModLib/`: provides shared mod helpers and `LmmModLoader`, `HarmonyReferenceAdapter`, and `MonoImageLoader`. These classes scan mod folders, keep only the highest version of duplicate shared libraries, and redirect Harmony 1 references in memory to `0Harmony109` or `0Harmony12`.
- `HarmonyInterop/`: builds `HarmonyXInterop.dll`, `0Harmony109.dll`, and `0Harmony12.dll` from MIT-licensed `pardeike/Harmony` and `BepInEx.Harmony` source code. `Harmony12` includes a direct C# fix so `PatchProcessor.Patch()` returns a non-null `DynamicMethod` list.
- `Lobotomypatch/`: calls `LmmModLoader` during game startup and ensures each mod entry point runs only once.
- `LobotomyBaseModPatcher/`: builds `LobotomyPatcher.exe`. The patcher copies runtime and compatibility files into the game `Managed` folder, merges `Lobotomypatch` into `Assembly-CSharp.dll`, and runs `Net40ReferenceAudit` so the patched assembly removes unused `.NET 4.0+` references and keeps a single `mscorlib` `2.0.0.0` reference.
- `LMM/`: contains the Unity mod manager project and the staged files under `LMM/Assets/Patcher/`, `LMM/Assets/PatchFiles/`, and `LMM/PatchFiles/`.

## Building and testing

1. Restore the required NuGet packages (`HarmonyX` `2.10.2`, `Mono.Cecil` `0.10.4`, `MonoMod.Utils` `22.7.31.1`, `MonoMod.RuntimeDetour` `22.7.31.1`, and `Newtonsoft.Json` `3.5.8`).
2. Pass the game `Managed` folder path with `-p:GameManaged=/path/to/LobotomyCorp_Data/Managed` (containing `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, and related Unity modules) when you build `LobotomyBaseModLib`, `Lobotomypatch`, or the full solution.
3. Run the unit tests in `LobotomyBaseMod.sln`:

```bash
dotnet test LobotomyBaseMod.sln -c Release -p:GameManaged=/path/to/LobotomyCorp_Data/Managed
```
