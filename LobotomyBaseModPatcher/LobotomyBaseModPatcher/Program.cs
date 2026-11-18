using System;
using System.IO;

namespace LobotomyPatcher
{
    // Console output is mirrored into <lmmDataPath>/Log.txt, rewritten on each log line to preserve LMM expectations.
    public class Debug
    {
        public static string logpath;
        public static string Logstr = string.Empty;

        public static void Log(string message)
        {
            Logstr = Logstr + message + Environment.NewLine;
            Console.WriteLine(message);
            if (!string.IsNullOrEmpty(logpath))
            {
                File.WriteAllText(Path.Combine(logpath, "Log.txt"), Logstr);
            }
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Pin CurrentDirectory to the patcher binary folder after resolving caller paths so
            // Patchwork.Engine locates Patchwork.Attributes.dll alongside LobotomyPatcher.exe.
            if (args.Length >= 3)
            {
                args[0] = Path.GetFullPath(args[0]);
                args[1] = Path.GetFullPath(args[1]);
                args[2] = Path.GetFullPath(args[2]);
            }
            string exeDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            if (!string.IsNullOrEmpty(exeDir))
            {
                Environment.CurrentDirectory = exeDir;
            }
            return Run(args);
        }

        internal static int Run(string[] args)
        {
            Debug.Logstr = string.Empty;
            Debug.logpath = null;
            if (args == null || args.Length < 4)
            {
                Console.Error.WriteLine("Usage: LobotomyPatcher.exe <ManagedPath> <PatchFilesPath> <LogDirPath> <Patch|Paste>");
                return 1;
            }

            try
            {
                string managedPath = Path.GetFullPath(args[0]);
                string patchFilesPath = Path.GetFullPath(args[1]);
                string logDirPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(logDirPath);
                Debug.logpath = logDirPath;
                Debug.Log("Managed 폴더 경로 : " + managedPath);
                Debug.Log("패치파일 폴더 경로 : " + patchFilesPath);
                Debug.Log("패치 방식 : " + args[3]);
                bool ok = PatchProgram.Patch(managedPath, patchFilesPath, args[3]);
                if (ok)
                {
                    return 0;
                }
                Debug.Log("패치 중 문제가 발생했습니다");
                return 1;
            }
            catch (Exception e)
            {
                Debug.Log(e.Message + Environment.NewLine + e.StackTrace);
                Debug.Log("패치 중 문제가 발생했습니다");
                return 1;
            }
        }
    }
}
