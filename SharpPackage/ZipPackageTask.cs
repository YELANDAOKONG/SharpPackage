using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using SharpLoader.Modding.Models;
using Task = Microsoft.Build.Utilities.Task;

namespace SharpPackage;

public class ZipPackageTask : Task
{
    [Required]
    public string ProjectDirectory { get; set; }

    [Required]
    public string OutputPath { get; set; }

    [Required]
    public string PackageOutputPath { get; set; }

    [Required]
    public string ProjectName { get; set; }

    [Required]
    public string TargetFramework { get; set; }

    public ITaskItem[] IncludeFiles { get; set; }
    public ITaskItem[] ExcludeFiles { get; set; }
    public bool ExcludeSharpLoaderDlls { get; set; } = true;
    public bool IncludeAllDlls { get; set; } = false; // 新增：控制是否包含所有 DLL

    public override bool Execute()
    {
        try
        {
            Log.LogMessage(MessageImportance.High, "Starting SharpPackage generation...");
            Log.LogMessage(MessageImportance.Normal, $"ProjectDirectory: {ProjectDirectory}");
            Log.LogMessage(MessageImportance.Normal, $"OutputPath: {OutputPath}");
            Log.LogMessage(MessageImportance.Normal, $"PackageOutputPath: {PackageOutputPath}");
            Log.LogMessage(MessageImportance.Normal, $"ExcludeSharpLoaderDlls: {ExcludeSharpLoaderDlls}");
            Log.LogMessage(MessageImportance.Normal, $"IncludeAllDlls: {IncludeAllDlls}");

            // Validate and load sharp.json
            var sharpJsonPath = Path.Combine(ProjectDirectory, "sharp.json");
            Log.LogMessage(MessageImportance.Normal, $"Looking for sharp.json at: {sharpJsonPath}");
            
            if (!File.Exists(sharpJsonPath))
            {
                Log.LogError("sharp.json file not found in project directory");
                return false;
            }

            ModuleProfile? metadata;
            try
            {
                Log.LogMessage(MessageImportance.Normal, "Parsing sharp.json...");
                var jsonContent = File.ReadAllText(sharpJsonPath);
                metadata = JsonSerializer.Deserialize<ModuleProfile>(jsonContent);
                    
                // Validate required fields
                if (string.IsNullOrEmpty(metadata.Id))
                {
                    Log.LogError("Missing required field: Id");
                    return false;
                }
                    
                if (string.IsNullOrEmpty(metadata.Namespace))
                {
                    Log.LogError("Missing required field: Namespace");
                    return false;
                }
                    
                if (metadata.Version == null)
                {
                    Log.LogError("Missing required field: Version");
                    return false;
                }
                    
                if (string.IsNullOrEmpty(metadata.EntryPoint))
                {
                    Log.LogError("Missing required field: EntryPoint");
                    return false;
                }
                    
                if (string.IsNullOrEmpty(metadata.Title))
                {
                    Log.LogError("Missing required field: Title");
                    return false;
                }
            }
            catch (JsonException ex)
            {
                Log.LogError($"Invalid JSON format in sharp.json: {ex.Message}");
                return false;
            }

            // Ensure output directory exists
            Log.LogMessage(MessageImportance.Normal, "Creating output directory...");
            Directory.CreateDirectory(PackageOutputPath);

            // Create ZIP file
            var versionString = $"{metadata.Version.Major}.{metadata.Version.Minor}.{metadata.Version.Patch}";
            var zipFileName = $"{metadata.Id}-{versionString}.zip";
            var zipPath = Path.Combine(PackageOutputPath, zipFileName);
            
            Log.LogMessage(MessageImportance.Normal, $"Creating ZIP file: {zipPath}");

            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // Add sharp.json to the root of the ZIP
                Log.LogMessage(MessageImportance.Normal, "Adding sharp.json to package...");
                archive.CreateEntryFromFile(sharpJsonPath, "sharp.json");
                    
                // Add the entry point DLL
                var entryPointPath = Path.Combine(OutputPath, metadata.EntryPoint);
                Log.LogMessage(MessageImportance.Normal, $"Looking for entry point DLL: {entryPointPath}");
                
                if (File.Exists(entryPointPath))
                {
                    Log.LogMessage(MessageImportance.Normal, "Adding entry point DLL to package...");
                    archive.CreateEntryFromFile(entryPointPath, metadata.EntryPoint);
                }
                else
                {
                    Log.LogError($"Entry point DLL not found: {entryPointPath}");
                    return false;
                }
                    
                // Add native dependencies
                Log.LogMessage(MessageImportance.Normal, "Processing native dependencies...");
                foreach (var dependency in metadata.NativeDependencies ?? new List<string>())
                {
                    var dependencyPath = Path.Combine(OutputPath, dependency);
                    Log.LogMessage(MessageImportance.Normal, $"Looking for native dependency: {dependencyPath}");
                    
                    if (File.Exists(dependencyPath))
                    {
                        Log.LogMessage(MessageImportance.Normal, $"Adding native dependency: {dependency}");
                        archive.CreateEntryFromFile(dependencyPath, dependency);
                    }
                    else
                    {
                        Log.LogWarning($"Native dependency not found: {dependency}");
                    }
                }

                // Process user-specified include files
                Log.LogMessage(MessageImportance.Normal, "Processing user-specified include files...");
                foreach (var includeFile in IncludeFiles ?? Array.Empty<ITaskItem>())
                {
                    var filePath = includeFile.ItemSpec;
                    var targetPath = includeFile.GetMetadata("TargetPath");
                    
                    // 确保 targetPath 不为空
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        targetPath = Path.GetFileName(filePath);
                        Log.LogMessage(MessageImportance.Normal, $"Using default target path: {targetPath}");
                    }
                    
                    if (File.Exists(filePath))
                    {
                        Log.LogMessage(MessageImportance.Normal, $"Adding included file: {filePath} -> {targetPath}");
                        archive.CreateEntryFromFile(filePath, targetPath);
                    }
                    else
                    {
                        Log.LogWarning($"Included file not found: {filePath}");
                    }
                }

                // Process user-specified exclude patterns
                var excludePatterns = (ExcludeFiles ?? Array.Empty<ITaskItem>())
                    .Select(item => item.ItemSpec)
                    .ToList();

                Log.LogMessage(MessageImportance.Normal, $"Exclude patterns: {string.Join(", ", excludePatterns)}");
                    
                // 只有在 IncludeAllDlls 为 true 时才添加所有其他 DLL
                if (IncludeAllDlls)
                {
                    Log.LogMessage(MessageImportance.Normal, "Adding additional DLLs...");
                    var allDlls = Directory.GetFiles(OutputPath, "*.dll")
                        .Where(dll => 
                            Path.GetFileName(dll) != metadata.EntryPoint && 
                            !(metadata.NativeDependencies ?? new List<string>()).Contains(Path.GetFileName(dll)) &&
                            !excludePatterns.Any(pattern => 
                                Path.GetFileName(dll).Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                                dll.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) &&
                            (!ExcludeSharpLoaderDlls || !IsSharpLoaderDll(Path.GetFileName(dll))))
                        .ToList();
                            
                    foreach (var dll in allDlls)
                    {
                        Log.LogMessage(MessageImportance.Low, $"Adding DLL: {Path.GetFileName(dll)}");
                        archive.CreateEntryFromFile(dll, Path.GetFileName(dll));
                    }
                }
                else
                {
                    Log.LogMessage(MessageImportance.Normal, "Skipping additional DLLs (IncludeAllDlls is false)");
                }
            }

            Log.LogMessage(MessageImportance.High, $"SharpPackage created successfully: {zipPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }

    private bool IsSharpLoaderDll(string dllName)
    {
        return dllName.StartsWith("SharpMC.SharpLoader", StringComparison.OrdinalIgnoreCase) ||
               dllName.StartsWith("SharpLoader", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpLoader.dll", StringComparison.OrdinalIgnoreCase);
    }
}
