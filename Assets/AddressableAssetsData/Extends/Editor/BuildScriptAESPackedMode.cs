using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
    [CreateAssetMenu(fileName = "BuildScriptPackedAESMode.asset", menuName = "Addressables/Content Builders/AES Build Script")]
    public class BuildScriptPackedModeAES : BuildScriptPackedMode
    {
        public override string Name
        {
            get { return "AES Build Script"; }
        }

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            var results =  base.DoBuild<TResult>(builderInput, aaContext);
            HashSet<string> targetsFiles = builderInput.Registry.GetFilePaths() as HashSet<string>;
            foreach(var targetPath in targetsFiles)
            {
                if(targetPath.EndsWith(".bundle"))
                {
                    EncryptBundleWithAES(targetPath);
                }
            }
            return results;
        }

        private void EncryptBundleWithAES(string bundlePath)
        {
            var data = File.ReadAllBytes(bundlePath);
            using var baseStream = new FileStream(bundlePath, FileMode.OpenOrCreate);
            var cryptor = new SeekableAesStream(baseStream);
            cryptor.Write(data, 0, data.Length);
        }
    }
}

