using System.Collections;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
#endif
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;

public class AddressableTestScript : MonoBehaviour
{
    void Awake()
    {
#if UNITY_EDITOR
        if (!(AddressableAssetSettingsDefaultObject.Settings.ActivePlayModeDataBuilder is BuildScriptFastMode))
            Addressables.InternalIdTransformFunc = InternalIdTransformFunc;
#endif
    }

    private string InternalIdTransformFunc(UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation location)
    {
        if (location.Data is AssetBundleRequestOptions)
        {
            string path = string.Empty;
#if UNITY_EDITOR
            path = Path.Combine(System.Environment.CurrentDirectory, location.InternalId);
#endif
            path = path.Replace("\\", "/");

            if (File.Exists(path))
                return path;

            return location.InternalId;
        }
        return location.InternalId;
    }


    void Update()
    {
        if (Input.GetKeyDown(KeyCode.A))
        {
            Addressables.InstantiateAsync("Build/PrefabA").Completed += handle =>
            {
                GameObject go = handle.Result;
                go.transform.localPosition = new Vector3(Random.Range(-10f, 10f), Random.Range(-10f, 10f), 0);
            };
        }else if(Input.GetKeyDown(KeyCode.B))
        {
            Addressables.InstantiateAsync("Build/PrefabB").Completed += handle =>
            {
                GameObject go = handle.Result;
                go.transform.localPosition = new Vector3(Random.Range(-10f, 10f), Random.Range(-10f, 10f), 0);
            };
        }
    }
}
