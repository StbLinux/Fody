using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Fody;

public partial class Processor
{
    public string AssemblyFilePath;
    public string IntermediateDirectory;
    public string KeyFilePath;
    public bool SignAssembly;
    public string ProjectDirectory;
    public string DocumentationFilePath;
    public string References;
    public string SolutionDirectory;
    public List<WeaverEntry> Weavers;
    public DebugSymbolsType DebugSymbols;
    public List<string> ReferenceCopyLocalPaths;
    public List<string> DefineConstants;

    public List<WeaverConfigFile> ConfigFiles;
    public Dictionary<string, WeaverConfigEntry> ConfigEntries;
    public bool GenerateXsd;
    IInnerWeaver innerWeaver;

    static Dictionary<string, IsolatedAssemblyLoadContext> solutionAssemblyLoadContexts =
        new Dictionary<string, IsolatedAssemblyLoadContext>(StringComparer.OrdinalIgnoreCase);

    public BuildLogger Logger;
    static readonly object mutex = new object();

    static Processor()
    {
        DomainAssemblyResolver.Connect();
    }

    public virtual bool Execute()
    {
        var assembly = typeof(Processor).Assembly;

        Logger.LogInfo($"Fody (version {assembly.GetName().Version} @ {assembly.CodeBase}) Executing");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            Inner();
            return !Logger.ErrorOccurred;
        }
        catch (Exception exception)
        {
            Logger.LogException(exception);
            return false;
        }
        finally
        {
            stopwatch.Stop();
            Logger.LogInfo($"  Finished Fody {stopwatch.ElapsedMilliseconds}ms.");
        }
    }

    void Inner()
    {
        ValidateSolutionPath();
        ValidateProjectPath();
        ValidateAssemblyPath();

        ConfigFiles = ConfigFileFinder.FindWeaverConfigFiles(SolutionDirectory, ProjectDirectory, Logger).ToList();

        if (!ConfigFiles.Any())
        {
            ConfigFiles = new List<WeaverConfigFile>
            {
                ConfigFileFinder.GenerateDefault(ProjectDirectory, Weavers, GenerateXsd)
            };
            Logger.LogWarning($"Could not find a FodyWeavers.xml file at the project level ({ProjectDirectory}). A default file has been created. Please review the file and add it to your project.");
        }

        ConfigEntries = ConfigFileFinder.ParseWeaverConfigEntries(ConfigFiles);

        var extraEntries = ConfigEntries.Values
            .Where(entry => !entry.ConfigFile.IsGlobal && !Weavers.Any(weaver => string.Equals(weaver.ElementName, entry.ElementName)))
            .ToArray();

        const string missingWeaversHelp = "Add the desired weavers via their nuget package; see https://github.com/Fody/Fody/wiki on how to migrate InSolution, custom or legacy weavers.";

        if (extraEntries.Any())
        {
            throw new WeavingException($"No weavers found for the configuration entries {string.Join(", ", extraEntries.Select(e => e.ElementName))}. " + missingWeaversHelp);
        }

        if (Weavers.Count == 0)
        {
            throw new WeavingException("No weavers found. " + missingWeaversHelp);
        }

        foreach (var weaver in Weavers)
        {
            if (ConfigEntries.TryGetValue(weaver.ElementName, out var config))
            {
                weaver.Element = config.Content;
                weaver.ExecutionOrder = config.ExecutionOrder;
            }
            else
            {
                Logger.LogWarning($"No configuration entry found for the installed weaver {weaver.ElementName}. This weaver will be skipped. You may want to add this weaver to your FodyWeavers.xml");
            }
        }

        ConfigFileFinder.EnsureSchemaIsUpToDate(ProjectDirectory, Weavers, GenerateXsd);

        Weavers = Weavers
            .Where(weaver => weaver.Element != null)
            .OrderBy(weaver => weaver.ExecutionOrder)
            .ToList();

        lock (mutex)
        {
            ExecuteInOwnAssemblyLoadContext();
        }
    }

    void ExecuteInOwnAssemblyLoadContext()
    {
        var loadContext = GetLoadContext();

        var assemblyFile = Path.Combine(AssemblyLocation.CurrentDirectory, "FodyIsolated.dll");
        using (innerWeaver = (IInnerWeaver)loadContext.CreateInstanceFromAndUnwrap(assemblyFile, "InnerWeaver"))
        {
            innerWeaver.AssemblyFilePath = AssemblyFilePath;
            innerWeaver.References = References;
            innerWeaver.KeyFilePath = KeyFilePath;
            innerWeaver.ReferenceCopyLocalPaths = ReferenceCopyLocalPaths;
            innerWeaver.SignAssembly = SignAssembly;
            innerWeaver.Logger = Logger;
            innerWeaver.SolutionDirectoryPath = SolutionDirectory;
            innerWeaver.Weavers = Weavers;
            innerWeaver.IntermediateDirectoryPath = IntermediateDirectory;
            innerWeaver.DefineConstants = DefineConstants;
            innerWeaver.ProjectDirectoryPath = ProjectDirectory;
            innerWeaver.DocumentationFilePath = DocumentationFilePath;
            innerWeaver.DebugSymbols = DebugSymbols;

            innerWeaver.Execute();

            ReferenceCopyLocalPaths = innerWeaver.ReferenceCopyLocalPaths;
        }
        innerWeaver = null;
    }

    IsolatedAssemblyLoadContext GetLoadContext()
    {
        if (solutionAssemblyLoadContexts.TryGetValue(SolutionDirectory, out var loadContext))
        {
            if (!WeaversHistory.HasChanged(Weavers.Select(x => x.AssemblyPath)))
            {
                return loadContext;
            }

            Logger.LogDebug("A Weaver HasChanged so loading a new AssemblyLoadContext");
            loadContext.Unload();
        }

        return solutionAssemblyLoadContexts[SolutionDirectory] = CreateAssemblyLoadContext();
    }

    IsolatedAssemblyLoadContext CreateAssemblyLoadContext()
    {
        Logger.LogDebug("Creating a new AssemblyLoadContext");
        return new IsolatedAssemblyLoadContext($"Fody Domain for '{SolutionDirectory}'", AssemblyLocation.CurrentDirectory);
    }

    public void Cancel()
    {
        innerWeaver?.Cancel();
    }
}