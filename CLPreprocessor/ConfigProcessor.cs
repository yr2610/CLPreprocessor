using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using YamlDotNet.Serialization;

public class ConfigProcessor
{
    private static readonly Regex TemplateRegex = new Regex(@"\{\{=([\s\S]+?)\}\}", RegexOptions.Compiled);
    private readonly Dictionary<string, object> _variables = new Dictionary<string, object>();
    private readonly List<ScriptObject> _postProcessFunctions = new List<ScriptObject>();

    public Dictionary<string, object> ReadConfigFile(string confFileName)
    {
        var data = ReadYamlFile(confFileName);

        ProcessIncludeFiles(data, Path.GetDirectoryName(confFileName));
        ExecutePostProcessFunctions(data);

        ProcessTemplates(data, data);

        return data;
    }

    private Dictionary<string, object> ReadYamlFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, object>();
        }

        var deserializer = new DeserializerBuilder()
            .Build();

        using (var reader = new StreamReader(filePath))
        {
            return deserializer.Deserialize<Dictionary<string, object>>(reader);
        }
    }

    private void ProcessIncludeFiles(Dictionary<string, object> data, string baseDirectory)
    {
        if (data.ContainsKey("$include"))
        {
            var includeFiles = (List<object>)data["$include"];
            data.Remove("$include");

            foreach (var includeFile in includeFiles)
            {
                var includeFilePath = Path.Combine(baseDirectory, includeFile.ToString());
                var includeData = ReadYamlFile(includeFilePath);
                ProcessIncludeFiles(includeData, Path.GetDirectoryName(includeFilePath));
                foreach (var kvp in includeData)
                {
                    if (!data.ContainsKey(kvp.Key))
                    {
                        data[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        if (data.ContainsKey("$post_process"))
        {
            var script = data["$post_process"].ToString();
            AddPostProcessFunction(script);
            data.Remove("$post_process");
        }
    }

    private void AddPostProcessFunction(string script)
    {
        using (var engine = new V8ScriptEngine())
        {
            // Lodashのスクリプトを読み込んで実行
            var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var lodashPath = Path.Combine(exeDirectory, "scripts", "lodash.min.js");
            var lodashScript = File.ReadAllText(lodashPath);
            engine.Execute(lodashScript);

            // 関数を文字列として渡し、関数オブジェクトとして保持
            var functionScript = $"var postProcessFunction = {script}; postProcessFunction;";
            var functionObject = engine.Evaluate(functionScript) as ScriptObject;
            if (functionObject != null)
            {
                _postProcessFunctions.Add(functionObject);
            }
        }
    }

    private void ExecutePostProcessFunctions(Dictionary<string, object> data)
    {
        using (var engine = new V8ScriptEngine())
        {
            engine.AddHostObject("data", data);

            // Lodashのスクリプトを読み込んで実行
            var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var lodashPath = Path.Combine(exeDirectory, "scripts", "lodash.min.js");
            var lodashScript = File.ReadAllText(lodashPath);
            engine.Execute(lodashScript);

            // 保持しているpostProcess関数を実行
            foreach (var function in _postProcessFunctions)
            {
                engine.Script.postProcessFunction = function;
                engine.Execute("postProcessFunction(data);");
            }
        }
    }

    private void ProcessTemplates(Dictionary<string, object> rootData, Dictionary<string, object> data)
    {
        var finished = new HashSet<string>();
        bool modified;

        do
        {
            modified = false;
            foreach (var key in data.Keys.ToList())
            {
                if (finished.Contains(key))
                {
                    continue;
                }

                var value = data[key].ToString();
                if (!TemplateRegex.IsMatch(value))
                {
                    finished.Add(key);
                    continue;
                }

                var compiled = CompileTemplate(value, rootData);
                data[key] = compiled;
                modified = true;
            }
        } while (modified);

        CompileForAllChildren(rootData, data);
    }

    private string CompileTemplate(string template, Dictionary<string, object> data)
    {
        return TemplateRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return data.ContainsKey(key) ? data[key].ToString() : string.Empty;
        });
    }

    private void CompileForAllChildren(Dictionary<string, object> rootData, Dictionary<string, object> data)
    {
        foreach (var key in data.Keys.ToList())
        {
            if (data[key] is Dictionary<string, object> childData)
            {
                CompileForAllChildren(rootData, childData);
            }
            else if (data[key] is List<object> listData)
            {
                foreach (var item in listData)
                {
                    if (item is Dictionary<string, object> listItemData)
                    {
                        CompileForAllChildren(rootData, listItemData);
                    }
                }
            }
            else if (data[key] is string strValue && TemplateRegex.IsMatch(strValue))
            {
                data[key] = CompileTemplate(strValue, rootData);
            }
        }
    }
}
