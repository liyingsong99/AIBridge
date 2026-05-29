using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowRecipeDocument
    {
        public string Path { get; set; }
        public JObject Json { get; set; }
        public WorkflowRecipe Recipe { get; set; }
    }

    public static class WorkflowRecipeLoader
    {
        public static WorkflowRecipeDocument Load(string path)
        {
            var fullPath = WorkflowPathHelper.ResolvePath(path);
            var json = File.ReadAllText(fullPath, Encoding.UTF8);
            var root = JObject.Parse(json);
            var recipe = root.ToObject<WorkflowRecipe>();
            if (recipe.Inputs == null)
            {
                recipe.Inputs = new JObject();
            }

            if (recipe.Phases == null)
            {
                recipe.Phases = new List<WorkflowPhase>();
            }

            if (recipe.Gates == null)
            {
                recipe.Gates = new List<WorkflowGate>();
            }

            if (recipe.Artifacts == null)
            {
                recipe.Artifacts = new List<WorkflowArtifactDeclaration>();
            }

            return new WorkflowRecipeDocument
            {
                Path = fullPath,
                Json = root,
                Recipe = recipe
            };
        }

        public static List<WorkflowRecipeListItem> ListRecipes()
        {
            var items = new List<WorkflowRecipeListItem>();
            AddRecipeListItems(items, WorkflowPathHelper.FindBuiltInRecipeFiles(), "builtin");
            AddRecipeListItems(items, WorkflowPathHelper.FindProjectRecipeFiles(), "project");
            return items;
        }

        private static void AddRecipeListItems(List<WorkflowRecipeListItem> items, List<string> files, string source)
        {
            foreach (var file in files)
            {
                try
                {
                    var doc = Load(file);
                    items.Add(new WorkflowRecipeListItem
                    {
                        Name = doc.Recipe.Name,
                        Title = doc.Recipe.Title,
                        Version = doc.Recipe.Version,
                        Description = doc.Recipe.Description,
                        Source = source,
                        Path = WorkflowPathHelper.ToDisplayPath(file)
                    });
                }
                catch (Exception ex)
                {
                    items.Add(new WorkflowRecipeListItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(file),
                        Source = source,
                        Description = "Invalid recipe: " + ex.Message,
                        Path = WorkflowPathHelper.ToDisplayPath(file)
                    });
                }
            }
        }

        public static string SaveRecipe(string sourcePath, string outputDirectory)
        {
            var fullSourcePath = WorkflowPathHelper.ResolveRecipePath(sourcePath);
            var fullOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? WorkflowPathHelper.GetProjectRecipesDirectory()
                : WorkflowPathHelper.ResolvePath(outputDirectory);
            Directory.CreateDirectory(fullOutputDirectory);

            var targetPath = System.IO.Path.Combine(fullOutputDirectory, System.IO.Path.GetFileName(fullSourcePath));
            File.Copy(fullSourcePath, targetPath, true);
            return targetPath;
        }
    }
}
