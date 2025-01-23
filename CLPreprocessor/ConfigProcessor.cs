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

public class ConfigProcessor
{
    private static readonly Regex TemplateRegex = new Regex(@"\{\{=([\s\S]+?)\}\}", RegexOptions.Compiled);
    private readonly List<ScriptObject> _postProcessFunctions = new List<ScriptObject>();
    private readonly V8ScriptEngine _engine;

    public ConfigProcessor()
    {
        _engine = new V8ScriptEngine();
        LoadLodash();
    }

    private void LoadLodash()
    {
        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var lodashPath = Path.Combine(exeDirectory, "scripts", "lodash.min.js");
        var lodashScript = File.ReadAllText(lodashPath);
        _engine.Execute(lodashScript);
    }

    public dynamic ReadConfigFile(string confFileName)
    {
        var data = ReadYamlFile(confFileName);

        ProcessIncludeFiles(data, Path.GetDirectoryName(confFileName));
        ExecutePostProcessFunctions(data);

        ProcessTemplates(data, data);

        return data;
    }

    private dynamic ReadYamlFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new ExpandoObject();
        }

        var deserializer = new DeserializerBuilder()
            .WithNodeTypeResolver(new ExpandoObjectNodeTypeResolver())
            .Build();

        using (var reader = new StreamReader(filePath))
        {
            var yaml = reader.ReadToEnd();
            var yamlStream = new YamlStream();
            yamlStream.Load(new StringReader(yaml));

            if (yamlStream.Documents.Count == 0)
            {
                return new ExpandoObject();
            }

            var rootNode = yamlStream.Documents[0].RootNode;
            if (rootNode is YamlSequenceNode)
            {
                return deserializer.Deserialize<List<ExpandoObject>>(new StringReader(yaml));
            }
            else if (rootNode is YamlMappingNode)
            {
                return deserializer.Deserialize<ExpandoObject>(new StringReader(yaml));
            }
            else
            {
                throw new YamlException("Unexpected YAML structure.");
            }
        }
    }

    private void ProcessIncludeFiles(dynamic data, string baseDirectory)
    {
        var dataDict = (IDictionary<string, object>)data;

        if (dataDict.ContainsKey("$include"))
        {
            var includeFiles = (List<object>)dataDict["$include"];
            dataDict.Remove("$include");

            foreach (var includeFile in includeFiles)
            {
                var includeFilePath = Path.Combine(baseDirectory, includeFile.ToString());
                var includeData = ReadYamlFile(includeFilePath);
                ProcessIncludeFiles(includeData, Path.GetDirectoryName(includeFilePath));
                foreach (var kvp in (IDictionary<string, object>)includeData)
                {
                    if (!dataDict.ContainsKey(kvp.Key))
                    {
                        dataDict[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        if (dataDict.ContainsKey("$post_process"))
        {
            var script = dataDict["$post_process"].ToString();
            AddPostProcessFunction(script);
            dataDict.Remove("$post_process");
        }
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

    private void ExecutePostProcessFunctions(dynamic data)
    {
        _engine.AddHostObject("data", data);

        // 保持しているpostProcess関数を実行
        foreach (var function in _postProcessFunctions)
        {
            _engine.Script.postProcessFunction = function;
            _engine.Execute("postProcessFunction(data);");
        }
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
                if (!TemplateRegex.IsMatch(value))
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

    private string CompileTemplate(string template, dynamic data)
    {
        return TemplateRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return ((IDictionary<string, object>)data).ContainsKey(key) ? ((IDictionary<string, object>)data)[key].ToString() : string.Empty;
        });
    }

    private void CompileForAllChildren(dynamic rootData, dynamic data)
    {
        foreach (var key in ((IDictionary<string, object>)data).Keys.ToList())
        {
            if (((IDictionary<string, object>)data)[key] is ExpandoObject childData)
            {
                CompileForAllChildren(rootData, childData);
            }
            else if (((IDictionary<string, object>)data)[key] is List<object> listData)
            {
                foreach (var item in listData)
                {
                    if (item is ExpandoObject listItemData)
                    {
                        CompileForAllChildren(rootData, listItemData);
                    }
                }
            }
            else if (((IDictionary<string, object>)data)[key] is string strValue && TemplateRegex.IsMatch(strValue))
            {
                ((IDictionary<string, object>)data)[key] = CompileTemplate(strValue, rootData);
            }
        }
    }
}

public class ExpandoObjectNodeTypeResolver : INodeTypeResolver
{
    public bool Resolve(NodeEvent nodeEvent, ref Type currentType)
    {
        if (currentType == typeof(object))
        {
            currentType = typeof(ExpandoObject);
            return true;
        }
        return false;
    }
}
