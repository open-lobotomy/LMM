using System;
using System.IO;

namespace LobotomyPatcher
{
    public class Copper
    {
        private static readonly string[] RequiredPatchFileNames =
        {
            "Assembly-CSharp.dll",
            "0Harmony.dll",
            "LobotomyBaseModLib.dll",
            "NAudio.dll",
            "Facepunch.Steamworks.Win64.dll",
            "steam_api64.dll",
        };

        private static readonly string[] OptionalPatchFileNames =
        {
            "Newtonsoft.Json.dll",
        };

        private static readonly string[] ShimClosureNames =
        {
            "0Harmony109.dll",
            "0Harmony12.dll",
            "HarmonyXInterop.dll",
            "Mono.Cecil.dll",
            "MonoMod.Utils.dll",
            "MonoMod.RuntimeDetour.dll",
        };

        public Copper(StartOption option)
        {
            Debug.Log("패치 파일 복사 중...");
            for (int i = 0; i < RequiredPatchFileNames.Length; i++)
            {
                string name = RequiredPatchFileNames[i];
                string resolved = ResolveRequiredSourceFile(PatchProgram.PatchFileFolderPath, name);
                CopyStagedFile(resolved, name);
                if (i == 0 && option == StartOption.Paste)
                {
                    Debug.Log("패치 방식 Paste 확인. 이미 패치된 어셈블리 복사 중...");
                }
            }

            for (int i = 0; i < OptionalPatchFileNames.Length; i++)
            {
                string name = OptionalPatchFileNames[i];
                string resolved = FindSourceFile(PatchProgram.PatchFileFolderPath, name);
                if (resolved != null)
                {
                    CopyStagedFile(resolved, name);
                }
            }

            bool anyShimPresent = false;
            for (int i = 0; i < ShimClosureNames.Length; i++)
            {
                if (FindSourceFile(PatchProgram.PatchFileFolderPath, ShimClosureNames[i]) != null)
                {
                    anyShimPresent = true;
                    break;
                }
            }
            if (anyShimPresent)
            {
                for (int i = 0; i < ShimClosureNames.Length; i++)
                {
                    string name = ShimClosureNames[i];
                    string resolved = ResolveRequiredSourceFile(PatchProgram.PatchFileFolderPath, name);
                    File.Copy(resolved, Path.Combine(PatchProgram.GameManagedFolderPath, name), true);
                    Debug.Log("패치 완료 to : " + Path.Combine(PatchProgram.GameManagedFolderPath, name));
                }
                string legacy12 = Path.Combine(PatchProgram.GameManagedFolderPath, "12Harmony.dll");
                if (File.Exists(legacy12))
                {
                    File.Delete(legacy12);
                    Debug.Log("레거시 제거 : " + legacy12);
                }
            }

            string baseModSource = Path.Combine(PatchProgram.PatchFileFolderPath, "BaseMod");
            if (Directory.Exists(baseModSource))
            {
                Debug.Log("패치 폴더 : " + baseModSource);
                CopyDirectory(
                    baseModSource,
                    Path.Combine(PatchProgram.GameManagedFolderPath, "BaseMod"));
                Debug.Log("패치 완료 : " + Path.Combine(PatchProgram.GameManagedFolderPath, "BaseMod"));
            }
            Debug.Log("패치 파일 복사 완료");
        }

        private static void CopyStagedFile(string resolvedSourcePath, string targetFileName)
        {
            Debug.Log("패치 파일 : " + resolvedSourcePath);
            string dest = Path.Combine(PatchProgram.GameManagedFolderPath, targetFileName);
            File.Copy(resolvedSourcePath, dest, true);
            Debug.Log("패치 완료 to : " + dest);
        }

        public static string FindSourceFile(string folder, string fileName)
        {
            string withExt = Path.Combine(folder, fileName);
            if (File.Exists(withExt)) return withExt;
            if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string withoutExt = Path.Combine(folder, fileName.Substring(0, fileName.Length - 4));
                if (File.Exists(withoutExt)) return withoutExt;
            }
            return null;
        }

        public static string ResolveRequiredSourceFile(string folder, string fileName)
        {
            string resolved = FindSourceFile(folder, fileName);
            if (resolved == null)
            {
                throw new FileNotFoundException("Patch file missing: " + Path.Combine(folder, fileName));
            }
            return resolved;
        }

        public static void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destDir, fileName);
                File.Copy(file, destFile, true);
            }
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                string destSubDir = Path.Combine(destDir, dirName);
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
