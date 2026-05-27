using System;
using System.IO;
using UnityEditor.PackageManager;

namespace AIBridge.Editor
{
    internal static class AIBridgeHybridClrUtility
    {
        public const string PackageName = "com.code-philosophy.hybridclr";
        public const string HybridClrAvailableDefine = "AIBRIDGE_HYBRIDCLR_AVAILABLE";

        public static bool IsHybridClrInstalled()
        {
            try
            {
                var packageInfo = PackageInfo.FindForPackageName(PackageName);
                if (packageInfo != null)
                {
                    return true;
                }
            }
            catch
            {
            }

            return ManifestContainsHybridClrPackage();
        }

        private static bool ManifestContainsHybridClrPackage()
        {
            var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                var manifest = File.ReadAllText(manifestPath);
                return manifest.IndexOf("\"" + PackageName + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
