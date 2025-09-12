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

    public override bool Execute()
    {
        try
        {
            Log.LogMessage(MessageImportance.High, "Starting SharpPackage generation...");

            // Validate and load sharp.json
            var sharpJsonPath = Path.Combine(ProjectDirectory, "sharp.json");
            if (!File.Exists(sharpJsonPath))
            {
                Log.LogError("sharp.json file not found in project directory");
                return false;
            }

            ModuleProfile? metadata;
            try
            {
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
            Directory.CreateDirectory(PackageOutputPath);

            // Create ZIP file
            var versionString = $"{metadata.Version.Major}.{metadata.Version.Minor}.{metadata.Version.Patch}";
            var zipFileName = $"{metadata.Id}-{versionString}.zip";
            var zipPath = Path.Combine(PackageOutputPath, zipFileName);

            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // Add sharp.json to the root of the ZIP
                archive.CreateEntryFromFile(sharpJsonPath, "sharp.json");
                    
                // Add the entry point DLL
                var entryPointPath = Path.Combine(OutputPath, metadata.EntryPoint);
                if (File.Exists(entryPointPath))
                {
                    archive.CreateEntryFromFile(entryPointPath, metadata.EntryPoint);
                }
                else
                {
                    Log.LogError($"Entry point DLL not found: {entryPointPath}");
                    return false;
                }
                    
                // Add native dependencies
                foreach (var dependency in metadata.NativeDependencies ?? new List<string>())
                {
                    var dependencyPath = Path.Combine(OutputPath, dependency);
                    if (File.Exists(dependencyPath))
                    {
                        archive.CreateEntryFromFile(dependencyPath, dependency);
                    }
                    else
                    {
                        Log.LogWarning($"Native dependency not found: {dependency}");
                    }
                }
                    
                // Add all other DLLs from output directory (except entry point, native dependencies, and SharpLoader)
                var allDlls = Directory.GetFiles(OutputPath, "*.dll")
                    .Where(dll => 
                        Path.GetFileName(dll) != metadata.EntryPoint && 
                        !(metadata.NativeDependencies ?? new List<string>()).Contains(Path.GetFileName(dll)) &&
                        !IsSharpLoaderDll(Path.GetFileName(dll))) // Exclude SharpLoader DLLs
                    .ToList();
                        
                foreach (var dll in allDlls)
                {
                    archive.CreateEntryFromFile(dll, Path.GetFileName(dll));
                }
            }

            Log.LogMessage(MessageImportance.High, $"SharpPackage created: {zipPath}");
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
        // Check if the DLL is related to SharpLoader
        return dllName.StartsWith("SharpMC.SharpLoader", StringComparison.OrdinalIgnoreCase) ||
               dllName.StartsWith("SharpLoader", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpLoader.dll", StringComparison.OrdinalIgnoreCase);
    }
}