using System.IO;
using UnityEditor;

namespace CompositeSceneGenerator
{
    internal static class AssetUtility
    {
        internal static void EnsureAssetFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;
            string parent = Path.GetDirectoryName(folderPath).Replace('\\', '/');
            EnsureAssetFolderExists(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folderPath));
        }

        internal static string ResolvePackageFilePath(string assetPath)
        {
            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (pkgInfo == null) return null;
            string pkgPrefix = "Packages/" + pkgInfo.name;
            string relPath = assetPath.Substring(pkgPrefix.Length);
            return pkgInfo.resolvedPath.Replace('\\', '/') + relPath;
        }
    }
}
