using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class Program
{
    static string GetConfigFileName(string filePath)
    {
        const string DefaultConfigFileName = "config.yml";
        const string LegacyConfigFileName = "conf.yml";
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string customConfigName;

        // ファイル名に_indexが含まれる場合、カスタム設定ファイルを使用
        if (baseName.EndsWith("_index", StringComparison.OrdinalIgnoreCase))
        {
            string nameWithoutIndex = baseName.Substring(0, baseName.Length - "_index".Length);

            // まず _config.yml を探す
            customConfigName = $"{nameWithoutIndex}_config.yml";
            if (File.Exists(customConfigName))
            {
                return customConfigName;
            }

            // _config.yml が存在しない場合、_conf.yml を探す
            customConfigName = $"{nameWithoutIndex}_conf.yml";
            if (File.Exists(customConfigName))
            {
                return customConfigName;
            }
        }

        // 新しいconfig.ymlが存在する場合、それを優先
        if (File.Exists(DefaultConfigFileName))
        {
            return DefaultConfigFileName;
        }

        // もしconfig.ymlが存在しない場合、conf.ymlを使用
        if (File.Exists(LegacyConfigFileName))
        {
            return LegacyConfigFileName;
        }

        // どちらも存在しない場合は、デフォルトのファイル名を返す（config.yml）
        return DefaultConfigFileName;
    }

    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("使用法: プログラム名 [ファイルパス]");
            return;
        }

        string filePath = args[0];
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"ファイルが見つかりません: {filePath}");
            return;
        }

        ConfigProcessor cp = new ConfigProcessor();
        var configFilePath = GetConfigFileName(filePath);
        var config = cp.ReadConfigFile(configFilePath);
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithTypeConverter(new DateTimeConverter())
            .Build();
        var yaml = serializer.Serialize(config);

        try
        {
            // XXX: 外から渡せるように
            HashSet<string> defines = new HashSet<string>(); 

            var preprocessor = new Preprocessor();
            var processedLines = preprocessor.PreProcess(filePath, defines);

            // 出力ファイル名に接尾辞を追加
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string outputFileName = $"{fileName}_preprocessed.json";
            string outputPath = Path.Combine(Path.GetDirectoryName(filePath), outputFileName);

            // JSONに出力
            var outputData = new { processedLines = processedLines };
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string jsonString = JsonSerializer.Serialize(outputData, options);

            // JSONをファイルに書き出し
            File.WriteAllText(outputPath, jsonString);

            Console.WriteLine($"処理結果を {outputPath} に保存しました。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"エラーが発生しました: {ex.Message}");
        }
    }
}
