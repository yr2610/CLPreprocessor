using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

public class PathUtils
{

    // 代替区切り文字を標準区切り文字に置換
    public static string ReplaceAltSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    // 連続する区切り文字を削除
    public static string RemoveRedundantSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        char directorySeparatorChar = Path.DirectorySeparatorChar;

        while (path.Contains($"{directorySeparatorChar}{directorySeparatorChar}"))
        {
            path = path.Replace($"{directorySeparatorChar}{directorySeparatorChar}", directorySeparatorChar.ToString());
        }

        return path;
    }

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

        //"/"を"\"に変換する
        relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // ディレクトリを示す場合は末尾にディレクトリセパレータを付ける
        //if (Directory.Exists(fullToPath) && !relativePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
        //{
        //    relativePath += Path.DirectorySeparatorChar;
        //}

        return relativePath;
    }

    public static string getRelativePath(string fromPath, string toPath) => GetRelativePath(fromPath, toPath);
}

public class PathEx
{
    private readonly static char DirectorySeparatorChar =
        Path.DirectorySeparatorChar;
    private readonly static string CurrentDirectoryString =
        "." + DirectorySeparatorChar;
    private readonly static string ParentDirectoryString =
        ".." + DirectorySeparatorChar;
    private readonly static string RootDirectoryString =
        DirectorySeparatorChar.ToString();

    /// <summary>
    /// 相対パスから絶対パスを取得します。
    /// </summary>
    /// <param name="basePath">基準とするフォルダのパス。</param>
    /// <param name="relativePath">相対パス。</param>
    /// <returns>絶対パス。</returns>
    public static string GetAbsolutePath(string basePath, string relativePath)
    {
        if (basePath == null || basePath.Length == 0)
        {
            return relativePath;
        }
        if (relativePath == null || relativePath.Length == 0)
        {
            return basePath;
        }

        basePath = basePath.TrimEnd(DirectorySeparatorChar);

        if (relativePath.StartsWith(ParentDirectoryString))
        {
            //相対パスが"..\"で始まっている時
            //基準パスの最後の"\"から後ろを削除する
            int pos = basePath.LastIndexOf(DirectorySeparatorChar);
            if (pos < 0)
            {
                throw new ArgumentException(
                    "\"..\\\"が多すぎます。", "relativePath");
            }
            basePath = basePath.Remove(pos);
            //相対パスのはじめの"..\"を削除する
            relativePath = relativePath.Substring(ParentDirectoryString.Length);
            //あらためて絶対パスを取得する
            return GetAbsolutePath(basePath, relativePath);
        }
        else if (relativePath.StartsWith(CurrentDirectoryString))
        {
            //相対パスが".\"で始まっている時
            //相対パスのはじめの".\"を削除する
            relativePath = relativePath.Substring(CurrentDirectoryString.Length);
            //あらためて絶対パスを取得する
            return GetAbsolutePath(basePath, relativePath);
        }
        else if (relativePath.StartsWith(RootDirectoryString))
        {
            //相対パスが"\"で始まっている時
            //基準パスのルートパスを取得する
            basePath = Path.GetPathRoot(basePath);
            basePath = basePath.TrimEnd(DirectorySeparatorChar);
            //相対パスのはじめの"\"を削除する
            relativePath = relativePath.Substring(RootDirectoryString.Length);
        }

        //パスを連結する
        return basePath + DirectorySeparatorChar + relativePath;
    }

    /// <summary>
    /// 絶対パスから相対パスを取得します。
    /// </summary>
    /// <param name="basePath">基準とするフォルダのパス。</param>
    /// <param name="absolutePath">絶対パス。</param>
    /// <returns>相対パス。</returns>
    public static string GetRelativePath(string basePath, string absolutePath)
    {
        if (basePath == null || basePath.Length == 0)
        {
            return absolutePath;
        }
        if (absolutePath == null || absolutePath.Length == 0)
        {
            return "";
        }

        basePath = basePath.TrimEnd(DirectorySeparatorChar);

        //パスを"\"で分割する
        string[] basePathDirs = basePath.Split(DirectorySeparatorChar);
        string[] absolutePathDirs = absolutePath.Split(DirectorySeparatorChar);

        //基準パスと絶対パスで、先頭から共通する部分を探す
        int commonCount = 0;
        for (int i = 0;
            i < basePathDirs.Length &&
            i < absolutePathDirs.Length &&
            basePathDirs[i].Equals(absolutePathDirs[i],
                StringComparison.OrdinalIgnoreCase);
            i++)
        {
            //共通部分の数を覚えておく
            commonCount++;
        }

        //共通部分がない時
        if (commonCount == 0)
        {
            return absolutePath;
        }

        //共通部分以降の基準パスのフォルダの深さを取得する
        int baseOnlyCount = basePathDirs.Length - commonCount;
        //その数だけ"..\"を付ける
        StringBuilder buf = new StringBuilder();
        for (int i = 0; i < baseOnlyCount; i++)
        {
            buf.Append(ParentDirectoryString);
        }

        //共通部分以降の絶対パス部分を追加する
        buf.Append(string.Join(DirectorySeparatorChar.ToString(),
            absolutePathDirs,
            commonCount, absolutePathDirs.Length - commonCount));

        return buf.ToString();
    }
}
