using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

public class PathUtils
{
    public static string GetRelativePath(string fromPath, string toPath)
    {
        // 現在の作業ディレクトリ
        string currentDirectory = Environment.CurrentDirectory;

        // fromPathが相対パスの場合、現在のディレクトリから絶対パスに変換
        string fullFromPath = Path.IsPathRooted(fromPath)
            ? fromPath
            : Path.GetFullPath(Path.Combine(currentDirectory, fromPath));

        // toPathも同様に絶対パスに変換
        string fullToPath = Path.IsPathRooted(toPath)
            ? toPath
            : Path.GetFullPath(Path.Combine(currentDirectory, toPath));

        // ディレクトリの場合、末尾にスラッシュを追加
        if (Directory.Exists(fullFromPath) && !fullFromPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            fullFromPath += Path.DirectorySeparatorChar;
        }

        if (Directory.Exists(fullToPath) && !fullToPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            fullToPath += Path.DirectorySeparatorChar;
        }

        // 相対パスを計算
        Uri fromUri = new Uri(fullFromPath);
        Uri toUri = new Uri(fullToPath);
        Uri relativeUri = fromUri.MakeRelativeUri(toUri);
        string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        // ディレクトリを示す場合は末尾にディレクトリセパレータを付ける
        if (Directory.Exists(fullToPath) && !relativePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            relativePath += Path.DirectorySeparatorChar;
        }

        return relativePath;
    }

    public static string getRelativePath(string fromPath, string toPath) => GetRelativePath(fromPath, toPath);
}
