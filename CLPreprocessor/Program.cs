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
        const string ConfigFileName = "conf.yml";
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string directory = Path.GetDirectoryName(filePath);

        // ファイルの存在をチェックするヘルパーメソッド
        string CheckFileExists(string fileName)
        {
            string fullPath = Path.Combine(directory, fileName);
            return File.Exists(fullPath) ? fullPath : null;
        }

        // 渡された元のファイル名に基づいてカスタム設定ファイルを探す
        string customConfigName = CheckFileExists($"{baseName}_conf.yml");
        if (customConfigName != null)
        {
            return customConfigName;
        }

        // conf.yml が存在する場合、それを返す
        string configPath = CheckFileExists(ConfigFileName);
        if (configPath != null)
        {
            return configPath;
        }

        // どちらも存在しない場合は null を返す
        return null;
    }

    static string AddSuffixToFileName(string filePath, string suffix, string newExtension = null)
    {
        // 新しい拡張子が指定されている場合は、ChangeExtension を使用
        string tempFilePath = string.IsNullOrEmpty(newExtension) ? filePath : Path.ChangeExtension(filePath, newExtension);

        // 拡張子を除いたファイル名を取得
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(tempFilePath);

        // 拡張子を取得
        string extension = Path.GetExtension(tempFilePath);

        // サフィックスを追加
        string newFileName = fileNameWithoutExtension + suffix + extension;

        // ディレクトリパスを取得
        string directoryPath = Path.GetDirectoryName(tempFilePath);

        // 新しいファイルパスを生成
        string newFilePath = Path.Combine(directoryPath, newFileName);

        return newFilePath;
    }

    // とりあえずシリアライズだけ対応
    public class JsonDerivedTypeConverter : System.Text.Json.Serialization.JsonConverter<Node>
    {
        public override Node Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotImplementedException(); // デシリアライズ不要ならこれでOK
        }

        public override void Write(Utf8JsonWriter writer, Node value, JsonSerializerOptions options)
        {
            // そのまま型に応じてシリアライズ
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
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

        if (!config.TryGetValue("$rootDirectory", out var rootDirectory))
        {
            Console.WriteLine("$rootDirectory が設定ファイルに存在しません。");
            return;
        }

        try
        {
            // XXX: 外から渡せるように
            HashSet<string> defines = new HashSet<string>(); 

            var preprocessor = new Preprocessor(rootDirectory.ToString());
            var processedLines = preprocessor.PreProcess(filePath, defines);

            var sheetSources = LineSplitter.SplitLines(processedLines);

            // JSONに出力
            var outputData = processedLines;
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string jsonString = JsonSerializer.Serialize(outputData, options);

            // JSONをファイルに書き出し
            string outputPath = AddSuffixToFileName(filePath, ".preprocessed", "json");
            File.WriteAllText(outputPath, jsonString);

            //Console.WriteLine($"処理結果を {outputPath} に保存しました。");


            Parser parser = new Parser(rootDirectory.ToString());
            var rootNode = parser.ParseLines(config, processedLines, filePath);

            var treeOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Converters =
                {
                    new System.Text.Json.Serialization.JsonStringEnumConverter(),
                    new JsonDerivedTypeConverter()
                },
            };
            string treeJsonString = JsonSerializer.Serialize(rootNode, treeOptions);
            string treeOutputPath = AddSuffixToFileName(filePath, ".step1", "json");
            File.WriteAllText(treeOutputPath, treeJsonString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"エラーが発生しました: {ex.Message}");
        }
    }
}
