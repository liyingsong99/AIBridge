using UnityEngine;

namespace AIBridge.Runtime
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class AIBridgeRuntimeSettingsCarrier : MonoBehaviour
    {
        public const string CarrierObjectName = "AIBridgeRuntimeSettings (Build)";

        [SerializeField] private AIBridgeRuntimeSettings runtimeSettings = new AIBridgeRuntimeSettings();
        [SerializeField] private bool generatedForBuild = true;

        public AIBridgeRuntimeSettings RuntimeSettings
        {
            get { return runtimeSettings; }
            set { runtimeSettings = value; }
        }

        public bool GeneratedForBuild
        {
            get { return generatedForBuild; }
            set { generatedForBuild = value; }
        }
    }
}
