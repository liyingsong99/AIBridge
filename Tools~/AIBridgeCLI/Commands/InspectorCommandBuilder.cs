using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Inspector command builder: SerializedProperty read/write for scene objects, prefab assets, and assets.
    /// </summary>
    public class InspectorCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "inspector";
        public override string Description => "Component/SerializedProperty operations (get, set, add, remove)";

        public override string[] Actions => new[]
        {
            "get_components", "get_properties", "get_property", "find_property", "set_property", "set_properties", "add_component", "remove_component"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["get_components"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false)
            },
            ["get_properties"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab or asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentEntityId", "Entity ID of the component on Unity 6000.4+", false),
                new ParameterInfo("componentInstanceId", "Entity ID of the component on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("includeChildren", "Include nested visible properties", false, "false")
            },
            ["get_property"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab or asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentEntityId", "Entity ID of the component on Unity 6000.4+", false),
                new ParameterInfo("componentInstanceId", "Entity ID of the component on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("propertyName", "SerializedProperty path to read", true)
            },
            ["find_property"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab or asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentEntityId", "Entity ID of the component on Unity 6000.4+", false),
                new ParameterInfo("componentInstanceId", "Entity ID of the component on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("keyword", "Keyword to match property name/path/display name", true)
            },
            ["set_property"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab or asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentEntityId", "Entity ID of the component on Unity 6000.4+", false),
                new ParameterInfo("componentInstanceId", "Entity ID of the component on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("propertyName", "SerializedProperty path to set", true),
                new ParameterInfo("value", "Value to set", true)
            },
            ["set_properties"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab or asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentInstanceId", "Instance ID of the component", false),
                new ParameterInfo("values", "JSON object mapping SerializedProperty paths to values. In PowerShell, build a variable and escape embedded quotes before passing --values $values.", true)
            },
            ["add_component"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("assetPath", "Prefab asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("typeName", "Type name of the component (e.g., BoxCollider, Rigidbody)", true)
            },
            ["remove_component"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("instanceId", "Instance ID of the GameObject", false),
                new ParameterInfo("assetPath", "Prefab asset path", false),
                new ParameterInfo("objectPath", "Child object path inside prefab asset", false),
                new ParameterInfo("componentName", "Name of the component", false),
                new ParameterInfo("componentIndex", "Index of the component", false),
                new ParameterInfo("componentEntityId", "Entity ID of the component on Unity 6000.4+", false),
                new ParameterInfo("componentInstanceId", "Entity ID of the component on Unity 6000.4+ or legacy instance ID on older Unity", false)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var request = base.Build(action, options);
            CopyParam(request.@params, "entityId", "instanceId");
            CopyParam(request.@params, "componentEntityId", "componentInstanceId");
            return request;
        }

        private static void CopyParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey) || @params.ContainsKey(targetKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
        }
    }
}
