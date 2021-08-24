﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DynamicPatcher
{
    class CompilationManager
    {
        // for undependent file
        CSharpCompilation compilation;
        List<string> references;
        List<string> preprocessorSymbols;

        bool showHidden;
        bool loadTempFileInMemory;
        bool emitPDB;
        bool forceCompile;
        bool packAssembly;
        OptimizationLevel optimizationLevel;

        PackageManager packageManager;

        CSharpCompilationOptions compilationOptions;
        CSharpParseOptions parseOptions;

        Solution solution;
        AdhocWorkspace workspace;

        string workDirectory;

        public CompilationManager(string workDir)
        {
            workDirectory = workDir;

            LoadConfig(workDir);

            packageManager = new PackageManager(workDir);

            compilationOptions = new CSharpCompilationOptions(
                            OutputKind.DynamicallyLinkedLibrary,
                            allowUnsafe: true,
                            optimizationLevel: optimizationLevel,
                            platform: Platform.AnyCpu,
                            warningLevel: 4
                            );

            var dirInfo = new DirectoryInfo(workDir);
            var dirList = dirInfo.GetDirectories("*.*", SearchOption.AllDirectories).ToList();
            var solutionlist = dirInfo.GetFiles("*.sln", SearchOption.AllDirectories).ToList();

            if (solutionlist.Count >= 0)
            {
                workspace = new AdhocWorkspace();

                LoadSolution(solutionlist[0].FullName);

                //foreach (WorkspaceDiagnostic diagnostic in workspace)
                //{
                //    Logger.Log(diagnostic.ToString());
                //}
                //Logger.Log("");

                // compile all project first to make sure all undependent file has references
                ProjectDependencyGraph dependencyGraph = solution.GetProjectDependencyGraph();
                foreach (ProjectId projectId in dependencyGraph.GetTopologicallySortedProjects())
                {
                    Project project = solution.GetProject(projectId);
                    CompileProject(project);
                }
            }
            else
            {
                Logger.Log("solution not found");
            }

            // load references
            List<MetadataReference> metadataReferences = new List<MetadataReference>();

            foreach (var reference in references)
            {
                string path = Helpers.GetAssemblyPath(reference);
                MetadataReference metadata = MetadataReference.CreateFromFile(path);

                metadataReferences.Add(metadata);
            }

            compilation = CSharpCompilation.Create(null, references: metadataReferences, options: compilationOptions);
            parseOptions = new CSharpParseOptions(preprocessorSymbols: preprocessorSymbols);

            ShowCompilerConfig();
        }

        private void LoadConfig(string workDir)
        {
            StreamReader file = File.OpenText(Path.Combine(workDir, "compiler.config.json"));
            JsonTextReader reader = new JsonTextReader(file);
            JObject json = JObject.Load(reader);
            var configs = json;

            this.references = new List<string>();
            this.references.Add("DynamicPatcher.dll");
            this.references.Add("mscorlib.dll");

            var references = configs["references"].ToArray();
            foreach (var token in references)
            {
                this.references.Add(token.ToString());
            }

            preprocessorSymbols = configs["preprocessor_symbols"]?.Select(token => token.ToString()).ToList();

            showHidden = configs["show_hidden"].ToObject<bool>();
            loadTempFileInMemory = configs["load_temp_file_in_memory"].ToObject<bool>();
            emitPDB = configs["emit_pdb"].ToObject<bool>();
            forceCompile = configs["force_compile"].ToObject<bool>();
            packAssembly = configs["pack_assembly"].ToObject<bool>();
            optimizationLevel = configs["optimization_level"].ToObject<OptimizationLevel>();
        }

        private void LoadSolution(string path)
        {
            Logger.Log("loading solution: " + path);
            string dir = Path.GetDirectoryName(path);

            SolutionId solutionId = SolutionId.CreateNewId();
            VersionStamp version = VersionStamp.Create();
            SolutionInfo solutionInfo = SolutionInfo.Create(solutionId, version, path);
            solution = workspace.AddSolution(solutionInfo);

            using (FileStream file = File.OpenRead(path))
            {
                using StreamReader reader = new StreamReader(file);
                while (reader.EndOfStream == false)
                {
                    string line = reader.ReadLine();
                    if (line.StartsWith("Project"))
                    {
                        string pattern = @"^Project\(""\{.+?\}""\) = ""(.+?)"", ""(.+?)"", ""\{(.+?)\}""";
                        Match match = Regex.Match(line, pattern);

                        string projectName = match.Groups[1].Value;
                        string projectPath = Path.Combine(dir, match.Groups[2].Value);
                        string projectGuid = match.Groups[3].Value;

                        LoadProject(projectName, projectPath, ProjectId.CreateFromSerialized(Guid.Parse(projectGuid)));
                    }
                }
            }

            Logger.Log("");
        }

        private void LoadProject(string name, string path, ProjectId projectId)
        {
            Logger.Log("loading project: " + path);

            string projectDirectory = Path.GetDirectoryName(path);

            VersionStamp version = VersionStamp.Create();
            ProjectInfo projectInfo = ProjectInfo.Create(projectId, version, name, name, LanguageNames.CSharp, filePath: path, compilationOptions: compilationOptions);
            Project project = workspace.AddProject(projectInfo);

            XmlDocument doc = new XmlDocument();
            doc.Load(path);

            XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("ms", "http://schemas.microsoft.com/developer/msbuild/2003");

            List<MetadataReference> metadataReferences = new List<MetadataReference>();
            metadataReferences.Add(MetadataReference.CreateFromFile(Helpers.GetAssemblyPath("mscorlib.dll")));
            foreach (XmlElement reference in doc.SelectNodes(@"ms:Project/ms:ItemGroup/ms:Reference", nsmgr))
            {
                var hintPathElement = reference.GetElementsByTagName("HintPath");
                string hintPath = hintPathElement.Count > 0 ? Path.Combine(projectDirectory, hintPathElement[0].InnerText) : Helpers.GetAssemblyPath(reference.GetAttribute("Include") + ".dll");
                MetadataReference metadata = MetadataReference.CreateFromFile(hintPath);
                metadataReferences.Add(metadata);
            }
            project = project.WithMetadataReferences(metadataReferences);

            List<ProjectReference> projectReferences = new List<ProjectReference>();
            foreach (XmlElement reference in doc.SelectNodes(@"ms:Project/ms:ItemGroup/ms:ProjectReference", nsmgr))
            {
                string referenceProjectPath = reference.GetAttribute("Include");
                string guid = reference.GetElementsByTagName("Project")[0].InnerText;
                ProjectId id = ProjectId.CreateFromSerialized(Guid.ParseExact(guid, "B"));

                ProjectReference projectReference = new ProjectReference(id);
                projectReferences.Add(projectReference);
            }
            project = project.WithProjectReferences(projectReferences);

            foreach (XmlElement compile in doc.SelectNodes(@"ms:Project/ms:ItemGroup/ms:Compile", nsmgr))
            {
                string documentName = compile.GetAttribute("Include");
                string documentPath = Path.Combine(projectDirectory, documentName);
                using (FileStream file = File.OpenRead(documentPath))
                {
                    SourceText source = SourceText.From(file);
                    Document document = project.AddDocument(documentName, source, filePath: documentPath);
                    project = document.Project;
                }
            }


            workspace.TryApplyChanges(project.Solution);
            solution = workspace.CurrentSolution;

            string buildPath = GetOutputPath(projectDirectory);
            Helpers.AdditionalSearchPath.Add(buildPath);
        }

        private void ShowCompilerConfig()
        {
            Logger.Log("ReferencedAssemblies: ");
            foreach (MetadataReference metadata in compilation.References)
            {
                Logger.Log(metadata.Display);
            }
            Logger.Log("");

            Logger.Log("PreprocessorSymbols: ");
            foreach (string preprocessorSymbol in parseOptions.PreprocessorSymbolNames)
            {
                Logger.Log(preprocessorSymbol);
            }
            Logger.Log("");

            Logger.Log("Diagnostic.ShowHidden: " + showHidden);
            Logger.Log("LoadTempFileInMemory: " + loadTempFileInMemory);
            Logger.Log("EmitPDB: " + emitPDB);
            Logger.Log("ForceCompile: " + forceCompile);
            Logger.Log("PackAssembly: " + packAssembly);
            Logger.Log("");

            CSharpCompilationOptions compilationOptions = compilation.Options;
            Logger.Log("CompilerOptions: ");
            Logger.Log("AllowUnsafe: " + compilationOptions.AllowUnsafe);
            Logger.Log("WarningLevel: " + compilationOptions.WarningLevel);
            Logger.Log("Platform: " + compilationOptions.Platform);
            Logger.Log("OptimizationLevel: " + compilationOptions.OptimizationLevel);
            Logger.Log("OutputKind: " + compilationOptions.OutputKind);
            Logger.Log("LanguageVersion: " + compilation.LanguageVersion);

            Logger.Log("");
        }

        public Project GetProjectFromFile(string path)
        {
            if (solution == null)
            {
                return null;
            }

            foreach (Project project in solution.Projects)
            {
                foreach (Document document in project.Documents)
                {
                    if (document.FilePath == path)
                    {
                        return project;
                    }
                }
            }

            return null;
        }

        public Assembly Compile(string path)
        {
            Project project = GetProjectFromFile(path);
            if (project != null)
            {
                return CompileProject(project);
            }

            return CompileFile(path);
        }

        private string GetOutputPath(string path)
        {
            string outputPath = path.Replace(workDirectory, Path.Combine(workDirectory, "Build"));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            return outputPath;
        }

        private void ShowDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            Logger.Log("Diagnostics: ");
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (showHidden == false && diagnostic.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }

                Logger.Log(diagnostic.ToString());
            }
            Logger.Log("");
        }

        private Assembly CompileFile(string path)
        {
            Logger.Log("compiling: " + path);

            using (FileStream file = File.OpenRead(path))
            {
                string outputPath = GetOutputPath(Path.ChangeExtension(path, "tmp"));
#if DEVMODE
                SourceText source = SourceText.From(file);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(source, options: parseOptions, path);

                string fileName = Path.GetFileNameWithoutExtension(path);
                Compilation compiler = compilation.AddSyntaxTrees(tree).WithAssemblyName(fileName);

                bool codeChanged = false;
                if (!forceCompile && File.Exists(outputPath))
                {
                    FileInfo codeInfo = new FileInfo(path);
                    FileInfo outputInfo = new FileInfo(outputPath);

                    if (codeInfo.LastWriteTime > outputInfo.LastWriteTime)
                    {
                        codeChanged = true;
                    }
                    else if (compilation.References.Count() > 0)
                    {
                        foreach (FileInfo fileChanged in compilation.References.
                                                            Select(r => new FileInfo(r.Display)).
                                                            Where(f => f.LastWriteTime > outputInfo.LastWriteTime))
                        {
                            codeChanged = true;
                            Logger.Log("{0} changed.", fileChanged.FullName);
                        }
                    }
                }
                else
                {
                    codeChanged = true;
                }

                if (codeChanged)
                {
                    string pdbPath = Path.ChangeExtension(outputPath, "pdb");
                    var result = compiler.Emit(outputPath, pdbPath: emitPDB ? pdbPath : null);

                    ShowDiagnostics(result.Diagnostics);

                    if (result.Success == false)
                    {
                        Logger.LogError("compiler error!");
                        Logger.Log("");
                        return null;
                    }

                    Logger.Log("compile file succeed!");
                    Logger.Log("loading complied assembly");
                    Logger.Log("");
                }
                else
                {
                    Logger.Log("code is older than assembly, use old assembly.");
                    Logger.Log("");
                }

                if (loadTempFileInMemory)
                {
                    using MemoryStream memory = new MemoryStream();
                    using (FileStream tmpFile = File.OpenRead(outputPath))
                    {
                        tmpFile.CopyTo(memory);
                    }

                    Assembly assembly_in_memory = Assembly.Load(memory.ToArray());
                    return assembly_in_memory;
                }
#endif
                if (packAssembly)
                {
#if DEVMODE
                    packageManager.Pack(outputPath);
#else
                    packageManager.UnPack(outputPath);
#endif
                }

                Assembly assembly = Assembly.LoadFrom(outputPath);
                return assembly;
            }
        }

        private DateTime GetProjectBuildTime(Project project)
        {
            string outputPath = GetOutputPath(Path.ChangeExtension(project.FilePath, "dll"));
            if (File.Exists(outputPath))
            {
                FileInfo outputInfo = new FileInfo(outputPath);
                return outputInfo.LastWriteTime;
            }
            return DateTime.FromBinary(0L);
        }

        private Assembly CompileProject(Project project)
        {
            Logger.Log("compiling project: " + project.FilePath);

            string outputPath = GetOutputPath(Path.ChangeExtension(project.FilePath, "dll"));
#if DEVMODE
            Compilation projectCompilation = project.GetCompilationAsync().Result;

            bool codeChanged = false;
            if (!forceCompile && File.Exists(outputPath))
            {
                DateTime newest = project.Documents.Select(document => new FileInfo(document.FilePath)).Max(code => code.LastWriteTime);
                DateTime buildTime = GetProjectBuildTime(project);

                if (newest > buildTime)
                {
                    codeChanged = true;
                }
                else if (project.ProjectReferences.Count() > 0)
                {
                    foreach (Project projectChanged in project.ProjectReferences.
                                                        Select(r => solution.GetProject(r.ProjectId)).
                                                        Where(p => GetProjectBuildTime(p) > buildTime))
                    {
                        codeChanged = true;
                        Logger.Log("{0} changed.", projectChanged.FilePath);
                    }
                }
                else if (project.MetadataReferences.Count() > 0)
                {
                    foreach (FileInfo fileChanged in project.MetadataReferences.Select(r => new FileInfo(r.Display)).Where(f => f.LastWriteTime > buildTime))
                    {
                        codeChanged = true;
                        Logger.Log("{0} changed.", fileChanged.FullName);
                    }
                }
            }
            else
            {
                codeChanged = true;
            }

            if (codeChanged)
            {
                string pdbPath = Path.ChangeExtension(outputPath, "pdb");
                var result = projectCompilation.Emit(outputPath, pdbPath: emitPDB ? pdbPath : null);

                ShowDiagnostics(result.Diagnostics);

                if (result.Success == false)
                {
                    Logger.LogError("compiler error!");
                    Logger.Log("");
                    return null;
                }

                Logger.Log("compile project '{0}' succeed!", project.Name);
                Logger.Log("loading complied assembly");
                Logger.Log("");
            }
            else
            {
                Logger.Log("code is older than assembly, use old assembly.");
                Logger.Log("");
            }
#endif
            if (packAssembly)
            {
#if DEVMODE
                packageManager.Pack(outputPath);
#else
                packageManager.UnPack(outputPath);
#endif
            }

            Assembly assembly = Assembly.LoadFrom(outputPath);
            return assembly;
        }
    }
}
