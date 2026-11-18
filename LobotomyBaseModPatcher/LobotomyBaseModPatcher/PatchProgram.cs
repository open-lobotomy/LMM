using System;

namespace LobotomyPatcher
{
    public enum StartOption
    {
        Patch = 0,
        Paste = 1,
    }

    public class PatchProgram
    {
        public static string GameManagedFolderPath;
        public static string PatchFileFolderPath;
        public static Patcher patcher;

        public static bool Patch(string GMFPath, string PFFPath, string patchprocess)
        {
            StartOption option = (StartOption)Enum.Parse(typeof(StartOption), patchprocess);
            try
            {
                GameManagedFolderPath = GMFPath;
                PatchFileFolderPath = PFFPath;
                new Copper(option);
                // Both modes re-run Patchwork + reference normalization from clean PatchFiles so
                // Patch and Paste yield identical Managed/Assembly-CSharp.dll outputs.
                patcher = new Patcher();
                return true;
            }
            catch (Exception e)
            {
                Debug.Log(e.Message + Environment.NewLine + e.StackTrace);
                return false;
            }
        }
    }
}
