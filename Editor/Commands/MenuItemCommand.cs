using System;
using UnityEditor;

namespace AIBridge.Editor
{
    /// <summary>
    /// Execute Unity Editor menu item by path
    /// </summary>
    public class MenuItemCommand : ICommand
    {
        public string Type => "menu_item";
        public bool RequiresRefresh => true;  // Menu items may modify assets

        public string SkillDescription => @"### `menu_item` - Invoke Menu Item

```bash
$CLI menu_item --menuPath ""GameObject/Create Empty""
$CLI menu_item --menuPath ""Assets/Create/Folder""
```";

        public CommandResult Execute(CommandRequest request)
        {
            var menuPath = request.GetParam<string>("menuPath");
            if (string.IsNullOrEmpty(menuPath))
            {
                return CommandResult.Failure(request.id, "Missing 'menuPath' parameter");
            }

            try
            {
                var result = EditorApplication.ExecuteMenuItem(menuPath);
                if (result)
                {
                    return CommandResult.Success(request.id, new
                    {
                        menuPath = menuPath,
                        executed = true
                    });
                }
                else
                {
                    return CommandResult.Failure(request.id, $"Menu item not found or failed to execute: {menuPath}");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }
    }
}
