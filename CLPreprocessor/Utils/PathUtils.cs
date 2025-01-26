using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

public class PathUtils
{
    static string GetRelativePath(string fromPath, string toPath)
    {
        // 絶対パスに変換
        string from = Path.GetFullPath(fromPath);
        string to = Path.GetFullPath(toPath);

        Uri fromUri = new Uri(from);
        Uri toUri = new Uri(to);

        Uri relativeUri = fromUri.MakeRelativeUri(toUri);
        string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        if (relativePath.StartsWith("..\\") || relativePath.StartsWith("../"))
        {
            return relativePath;
        }

        // ディレクトリを示す場合は末尾にディレクトリセパレータを付ける
        if (Directory.Exists(to))
        {
            relativePath += Path.DirectorySeparatorChar;
        }

        return relativePath;
    }

    public static string getRelativePath(string fromPath, string toPath) => GetRelativePath(fromPath, toPath);
}
