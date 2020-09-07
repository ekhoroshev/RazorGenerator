using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using RazorGenerator.Core;

namespace RazorGenerator.MsBuild
{
    public class RazorCodeGen : ITask
    {
        private static readonly Regex _namespaceRegex = new Regex(@"($|\.)(\d)");
        private readonly List<ITaskItem> _generatedFiles = new List<ITaskItem>();

        public ITaskItem[] FilesToPrecompile { get; set; }

        [Required]
        public string ProjectRoot { get; set; }

        public string RootNamespace { get; set; }

        [Required]
        public string CodeGenDirectory { get; set; }

        [Output]
        public ITaskItem[] GeneratedFiles => _generatedFiles.ToArray();

        public bool Execute()
        {
            try
            {
                Log("RazorGenerator starting!");
                return ExecuteCore();
            }
            catch (Exception ex)
            {
                Log(ex.Message);
            }
            return false;
        }

        public IBuildEngine BuildEngine { get; set; }
        public ITaskHost HostObject { get; set; }

        private void Log(string message)
        {
            var taskEvent =
                new BuildMessageEventArgs(
                    message,
                    "helpKeyword",
                    "RazorGenerator",
                    MessageImportance.High);

            BuildEngine.LogMessageEvent(taskEvent);
        }

        private bool ExecuteCore()
        {
            if (FilesToPrecompile == null || !FilesToPrecompile.Any())
            {
                return true;
            }

            string projectRoot = String.IsNullOrEmpty(ProjectRoot) ? Directory.GetCurrentDirectory() : ProjectRoot;
            using (var hostManager = new HostManager(projectRoot))
            {
                foreach (var file in FilesToPrecompile)
                {
                    string filePath = file.GetMetadata("FullPath");
                    string fileName = Path.GetFileName(filePath);
                    var projectRelativePath = GetProjectRelativePath(filePath, projectRoot);
                    string itemNamespace = GetNamespace(file, projectRelativePath);

                    CodeLanguageUtil langutil = CodeLanguageUtil.GetLanguageUtilFromFileName(fileName);

                    string outputPath = Path.Combine(
                        CodeGenDirectory,
                        projectRelativePath
                            .TrimStart(Path.DirectorySeparatorChar)
                            .Replace(".cshtml", ".generated")
                        ) + langutil.GetCodeFileExtension();

                    if (!RequiresRecompilation(filePath, outputPath))
                    {
                        Log($"Skipping file {filePath} since {outputPath} is already up to date");
                        continue;
                    }
                    EnsureDirectory(outputPath);

                    Log($"Precompiling {filePath} at path {outputPath}");
                    var host = hostManager.CreateHost(filePath, projectRelativePath, itemNamespace);

                    bool hasErrors = false;
                    host.Error += (o, eventArgs) =>
                    {
                        Log(eventArgs.ErrorMessage);
                        // Log.LogError("RazorGenerator", eventArgs.ErorrCode.ToString(), helpKeyword: "",
                        //     file: file.ItemSpec,
                        //     lineNumber: (int) eventArgs.LineNumber,
                        //     columnNumber: (int) eventArgs.ColumnNumber,
                        //     endLineNumber: (int) eventArgs.LineNumber,
                        //     endColumnNumber: (int) eventArgs.ColumnNumber,
                        //     message: eventArgs.ErrorMessage);

                        hasErrors = true;
                    };

                    try
                    {
                        var result = host.GenerateCode();

                        if (!hasErrors)
                        {
                            File.WriteAllBytes(outputPath, ConvertToBytes(result));
                        }
                    }
                    catch (Exception exception)
                    {
                        Log(exception.Message);
                        return false;
                    }
                    if (hasErrors)
                    {
                        return false;
                    }

                    var taskItem = new TaskItem(outputPath);
                    taskItem.SetMetadata("AutoGen", "true");
                    taskItem.SetMetadata("DependentUpon", "fileName");

                    _generatedFiles.Add(taskItem);
                }
            }
            return true;
        }

        private static byte[] ConvertToBytes(string content)
        {
            //Get the preamble (byte-order mark) for our encoding
            byte[] preamble = Encoding.UTF8.GetPreamble();
            int preambleLength = preamble.Length;

            byte[] body = Encoding.UTF8.GetBytes(content);

            //Prepend the preamble to body (store result in resized preamble array)
            Array.Resize<byte>(ref preamble, preambleLength + body.Length);
            Array.Copy(body, 0, preamble, preambleLength, body.Length);

            //Return the combined byte array
            return preamble;
        }

        /// <summary>
        /// Determines if the file has a corresponding output code-gened file that does not require updating.
        /// </summary>
        private static bool RequiresRecompilation(string filePath, string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                return true;
            }
            return File.GetLastWriteTimeUtc(filePath) > File.GetLastWriteTimeUtc(outputPath);
        }

        private string GetNamespace(ITaskItem file, string projectRelativePath)
        {
            string itemNamespace = file.GetMetadata("CustomToolNamespace");
            if (!String.IsNullOrEmpty(itemNamespace))
            {
                return itemNamespace;
            }
            projectRelativePath = Path.GetDirectoryName(projectRelativePath);
            // To keep the namespace consistent with VS, need to generate a namespace based on the folder path if no namespace is specified.
            // Also replace any non-alphanumeric characters with underscores.
            itemNamespace = projectRelativePath.Trim(Path.DirectorySeparatorChar);
            if (String.IsNullOrEmpty(itemNamespace))
            {
                return RootNamespace;
            }

            var stringBuilder = new StringBuilder(itemNamespace.Length);
            foreach (char c in itemNamespace)
            {
                if (c == Path.DirectorySeparatorChar)
                {
                    stringBuilder.Append('.');
                }
                else if (!Char.IsLetterOrDigit(c))
                {
                    stringBuilder.Append('_');
                }
                else
                {
                    stringBuilder.Append(c);
                }
            }
            itemNamespace = stringBuilder.ToString();
            itemNamespace = _namespaceRegex.Replace(itemNamespace, "$1_$2");
            
            if (!String.IsNullOrEmpty(RootNamespace))
            {
                itemNamespace = RootNamespace + '.' + itemNamespace;
            }
            return itemNamespace;
        }

        private static string GetProjectRelativePath(string filePath, string projectRoot)
        {
            if (filePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return filePath.Substring(projectRoot.Length);
            }
            return filePath;
        }

        private static void EnsureDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}