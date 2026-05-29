using System;
using System.IO;
using UnityEditor.PackageManager;

namespace AIBridge.Editor
{
    internal static class AIBridgeHybridClrUtility
    {
        public const string PackageName = "com.code-philosophy.hybridclr";
        public const string HybridClrAvailableDefine = "AIBRIDGE_HYBRIDCLR_AVAILABLE";
        private const string PackageAssetPath = "Packages/" + PackageName;

        public static bool IsHybridClrInstalled()
        {
            try
            {
                // Unity 2021 没有按包名查询的公开 API，使用包路径查询保持 2019.4+ 兼容。
                var packageInfo = PackageInfo.FindForAssetPath(PackageAssetPath);
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
