using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeTypeResolvers;
using YamlDotNet.RepresentationModel;

public class DateTimeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(DateTime);
    }

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer nestedObjectDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        if (DateTime.TryParse(scalar.Value, out var result))
        {
            return result;
        }
        throw new YamlException($"Could not parse '{scalar.Value}' as a DateTime.");
    }

    public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer nestedObjectSerializer)
    {
        if (value is DateTime dateTime)
        {
            emitter.Emit(new Scalar(dateTime.ToString("yyyy-MM-dd"))); // または他の希望する形式
        }
        else
        {
            throw new YamlException("Expected a DateTime value.");
        }
    }
}

public class ConfigProcessor
{
    private readonly List<ScriptObject> _postProcessFunctions = new List<ScriptObject>();
    private readonly V8ScriptEngine _engine;

    public ConfigProcessor()
    {
        _engine = new V8ScriptEngine();

        _engine.AddHostType(typeof(DialogUtils));
        _engine.AddHostType(typeof(PathUtils));

        //_engine.Execute(@"
        //        var basePath = 'C:\\base\\path';
        //        var targetPath = 'C:\\base\\path\\to\\file.txt';
        //        var relative = PathUtils.getRelativePath(basePath, targetPath);
        //        DialogUtils.showMessage(`Relative path: ${relative}`);
        //    ");

        LoadLodash();
        ConfigureLodashTemplateSettings();
    }

    private void LoadLodash()
    {
        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var lodashPath = Path.Combine(exeDirectory, "scripts", "lodash.min.js");
        var lodashScript = File.ReadAllText(lodashPath);
        _engine.Execute(lodashScript);
    }

    private void ConfigureLodashTemplateSettings()
    {
        string script = @"
            _.templateSettings = {
                evaluate: /\{\{([\s\S]+?)\}\}/g,
                interpolate: /\{\{=([\s\S]+?)\}\}/g,
                escape: /\{\{-([\s\S]+?)\}\}/g
            };
        ";
        _engine.Execute(script);
    }

    public dynamic ReadConfigFile(string confFileName)
    {
        var data = ReadYamlFile(confFileName);

        _engine.Script.filePath = confFileName;

        // dataオブジェクトをJavaScriptエンジンに追加
        var jsData = ConvertToJsObject(data);
        _engine.AddHostObject("data", jsData);

        data = ProcessFunctions(data);
        data = ProcessIncludeFiles(data, Path.GetDirectoryName(confFileName));
        data = ExecutePostProcessFunctions(data);

        ProcessTemplates(data, data);

        return data;
    }

    private dynamic ReadYamlFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, object>();
        }

        var deserializer = new DeserializerBuilder()
            .WithTypeConverter(new DateTimeConverter())
            .Build();

        using (var reader = new StreamReader(filePath))
        {
            var yaml = reader.ReadToEnd();
            var yamlStream = new YamlStream();
            yamlStream.Load(new StringReader(yaml));

            if (yamlStream.Documents.Count == 0)
            {
                return new Dictionary<string, object>();
            }

            var rootNode = yamlStream.Documents[0].RootNode;
            if (rootNode is YamlSequenceNode)
            {
                return deserializer.Deserialize<List<Dictionary<string, object>>>(new StringReader(yaml));
            }
            else if (rootNode is YamlMappingNode)
            {
                return deserializer.Deserialize<Dictionary<string, object>>(new StringReader(yaml));
            }
            else
            {
                throw new YamlException("Unexpected YAML structure.");
            }
        }
    }

    private IDictionary<string, object> ProcessFunctions(IDictionary<string, object> data)
    {
        if (data.ContainsKey("$functions"))
        {
            var functions = (Dictionary<object, object>)data["$functions"];
            data.Remove("$functions");

            // JavaScriptエンジンにdataを追加
            var jsData = ConvertToJsObject(data);
            _engine.AddHostObject("data", jsData);

            foreach (var function in functions)
            {
                var key = function.Key.ToString();
                var value = function.Value.ToString();
                var functionScript = $"data.{key} = {value};";
                _engine.Execute(functionScript);
            }

            // エンジンから更新されたdataを取得し、C#のデータ構造に変換
            var updatedJsData = _engine.Script.data;
            data = ConvertFromJsObject(updatedJsData);
        }
        return data;
    }

    private void ProcessPath(IDictionary<string, object> data, string baseDirectory)
    {
        if (data.ContainsKey("$rootDirectory"))
        {
            var rootDirectory = data["$rootDirectory"].ToString();
            data["$rootDirectory"] = Path.Combine(baseDirectory, rootDirectory);
        }
    }

    private IDictionary<string, object> ProcessIncludeFiles(IDictionary<string, object> data, string baseDirectory)
    {
        if (data.ContainsKey("$include"))
        {
            var includeFiles = (List<object>)data["$include"];
            data.Remove("$include");

            foreach (var includeFile in includeFiles)
            {
                var includeFilePath = Path.Combine(baseDirectory, includeFile.ToString());
                var includeData = ReadYamlFile(includeFilePath);

                // includeDataを現在のdataにマージ
                foreach (var kvp in (IDictionary<string, object>)includeData)
                {
                    if (!data.ContainsKey(kvp.Key))
                    {
                        data[kvp.Key] = kvp.Value;
                    }
                }

                // 再帰的にincludeファイルを処理
                data = ProcessIncludeFiles(data, Path.GetDirectoryName(includeFilePath));
            }
        }

        // 関数やパスを処理する部分は、dataをエンジンに書き戻す代わりにdataそのものを変更
        data = ProcessFunctions(data);
        ProcessPath(data, baseDirectory);

        if (data.ContainsKey("$post_process"))
        {
            var script = data["$post_process"].ToString();
            AddPostProcessFunction(script);
            data.Remove("$post_process");
        }

        return data;
    }

    private void AddPostProcessFunction(string script)
    {
        // 関数を文字列として渡し、関数オブジェクトとして保持
        var functionScript = $"var postProcessFunction = {script}; postProcessFunction;";
        var functionObject = _engine.Evaluate(functionScript) as ScriptObject;
        if (functionObject != null)
        {
            _postProcessFunctions.Add(functionObject);
        }
    }

    private IDictionary<string, object> ExecutePostProcessFunctions(IDictionary<string, object> data)
    {
        var jsData = ConvertToJsObject(data);
        _engine.AddHostObject("data", jsData);

        // 保持しているpostProcess関数を実行
        foreach (var function in _postProcessFunctions)
        {
            _engine.Script.postProcessFunction = function;
            _engine.Execute("postProcessFunction(data);");
        }

        // 更新されたdataを取得し、C#のデータ構造に変換
        var updatedJsData = _engine.Script.data;
        return ConvertFromJsObject(updatedJsData);
    }

    private void ProcessTemplates(dynamic rootData, dynamic data)
    {
        var finished = new HashSet<string>();
        bool modified;

        do
        {
            modified = false;
            foreach (var key in ((IDictionary<string, object>)data).Keys.ToList())
            {
                if (finished.Contains(key))
                {
                    continue;
                }

                var value = ((IDictionary<string, object>)data)[key].ToString();
                if (!IsTemplate(value))
                {
                    finished.Add(key);
                    continue;
                }

                var compiled = CompileTemplate(value, rootData);
                ((IDictionary<string, object>)data)[key] = compiled;
                modified = true;
            }
        } while (modified);

        CompileForAllChildren(rootData, data);
    }

    private bool IsTemplate(string value)
    {
        return value.Contains("{{") && value.Contains("}}");
    }

    private object ConvertToJsObject(object data)
    {
        if (data is Dictionary<string, object> dict)
        {
            var jsDict = new ExpandoObject();
            var jsDictDict = (IDictionary<string, object>)jsDict;
            foreach (var kvp in dict)
            {
                jsDictDict[kvp.Key] = ConvertToJsObject(kvp.Value);
            }
            return jsDict;
        }
        else if (data is List<object> list)
        {
            return list.Select(ConvertToJsObject).ToList();
        }
        else
        {
            return data; // 基本データ型やその他はそのまま返す
        }
    }

    private object ConvertFromJsObject(dynamic jsObject)
    {
        if (jsObject == null)
        {
            return null;
        }

        if (jsObject is ExpandoObject expando)
        {
            var dict = new Dictionary<string, object>();
            foreach (var property in (IDictionary<string, object>)expando)
            {
                dict[property.Key] = ConvertFromJsObject(property.Value);
            }
            return dict;
        }
        else if (jsObject is IDictionary<string, object> dict)
        {
            return dict.ToDictionary(k => k.Key, v => ConvertFromJsObject(v.Value));
        }
        else if (jsObject is IEnumerable<object> && !(jsObject is string))
        {
            return ((IEnumerable<object>)jsObject).Select(ConvertFromJsObject).ToList();
        }
        else
        {
            // 基本データ型やその他の場合はそのまま返す
            return jsObject;
        }
    }

    private string CompileTemplate(string template, dynamic data)
    {
        var jsData = ConvertToJsObject(data);

        // エスケープされたテンプレート文字列を作成
        string escapedTemplate = template.Replace("`", "\\`").Replace("$", "\\$");

        // dataオブジェクトをJavaScriptエンジンに追加
        _engine.AddHostObject("data", jsData);

        // デバッグ用のログ出力
        //Console.WriteLine("Template: " + escapedTemplate);
        //Console.WriteLine("Data: " + Newtonsoft.Json.JsonConvert.SerializeObject(jsData, Newtonsoft.Json.Formatting.Indented));

        // テンプレートを評価
        string script = $@"
            //DialogUtils.showMessage(Object.keys(data).join('\n'));
            var compiled = _.template(`{escapedTemplate}`);
            compiled(data);
        ";
        return _engine.Evaluate(script).ToString();
    }

    private void CompileForAllChildren(dynamic rootData, dynamic data)
    {
        foreach (var key in ((IDictionary<string, object>)data).Keys.ToList())
        {
            var value = ((IDictionary<string, object>)data)[key];

            if (value is IDictionary<string, object> childData)
            {
                CompileForAllChildren(rootData, childData);
            }
            else if (value is IDictionary<object, object> objectDict)
            {
                // object, object の辞書を string, object に変換し、再帰的に処理
                var stringDict = new Dictionary<string, object>();
                foreach (var pair in objectDict)
                {
                    if (pair.Key is string keyString)
                    {
                        stringDict[keyString] = pair.Value;
                    }
                    else
                    {
                        // キーと値の型が期待通りでない場合の処理
                        Console.WriteLine($"Warning: Non-string key detected: {pair.Key}");
                        stringDict[pair.Key.ToString()] = pair.Value; // 文字列に変換
                    }
                }
                // 再帰的に処理した結果を元のデータ構造に反映
                CompileForAllChildren(rootData, stringDict);
                ((IDictionary<string, object>)data)[key] = stringDict; // 元のデータに反映
            }
            else if (value is List<object> listData)
            {
                for (int i = 0; i < listData.Count; i++)
                {
                    if (listData[i] is IDictionary<string, object> listItemData)
                    {
                        CompileForAllChildren(rootData, listItemData);
                    }
                    else if (listData[i] is string strValue && IsTemplate(strValue))
                    {
                        listData[i] = CompileTemplate(strValue, rootData);
                    }
                }
            }
            else if (value is string strValue && IsTemplate(strValue))
            {
                ((IDictionary<string, object>)data)[key] = CompileTemplate(strValue, rootData);
            }
        }
    }
}
