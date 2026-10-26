using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace GameServer.Client.Editor
{
    /// <summary>
    /// Hands the package's <c>link.xml</c> to the Unity linker on every player build, so stripping (High) keeps Google.Protobuf
    /// and the SDK (API-09). Unity ignores link.xml files inside packages otherwise.
    /// </summary>
    internal sealed class LinkXmlProvider : IUnityLinkerProcessor
    {
        private const string LinkXml = "Packages/com.gameserver.client/Runtime/link.xml";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data) => Path.GetFullPath(LinkXml);

#if !UNITY_2021_2_OR_NEWER
        public void OnBeforeRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }

        public void OnAfterRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }
#endif
    }
}
