#if UNITY_2022_1_OR_NEWER
#define UNLOAD_BUNDLE_ASYNC
#define ENABLE_ASYNC_ASSETBUNDLE_UWR
#endif

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.Util;

namespace UnityEngine.ResourceManagement.ResourceProviders
{
    [System.Flags]
    internal enum BundleSource
    {
        None = 0,
        Local = 1,
        Cache = 2,
        Download = 4
    }

    public class AesAssetBundleResource : IAssetBundleResource, IUpdateReceiver
    {
        /// <summary>
        /// Options for where an AssetBundle can be loaded from.
        /// </summary>
        public enum LoadType
        {
            /// <summary>
            /// Cannot determine where the AssetBundle is located.
            /// </summary>
            None,

            /// <summary>
            /// Load the AssetBundle from a local file location.
            /// </summary>
            Local,

            /// <summary>
            /// Download the AssetBundle from a web server.
            /// </summary>
            Web
        }

        String m_InternalId;
        public static String GetEncryptedCachePath()
        {
            return Path.Combine(Application.persistentDataPath, "aa");
        }
        public static String GetEncryptedAssetLocalPath(String internalId, AssetBundleRequestOptions options)
        {
            return Path.Combine(GetEncryptedCachePath(), internalId.GetHashCode().ToString() + "." + options == null ? "000" : options.Hash);
        }

        AssetBundle m_AssetBundle;
        DownloadHandler m_downloadHandler;
        AsyncOperation m_RequestOperation;
        WebRequestQueueOperation m_WebRequestQueueOperation;
        internal ProvideHandle m_ProvideHandle;
        internal AssetBundleRequestOptions m_Options;

        [NonSerialized]
        bool m_WebRequestCompletedCallbackCalled = false;

        int m_Retries;
        BundleSource m_Source = BundleSource.None;
        long m_BytesToDownload;
        long m_DownloadedBytes;
        bool m_Completed = false;
#if UNLOAD_BUNDLE_ASYNC
        AssetBundleUnloadOperation m_UnloadOperation;
#endif
        const int k_WaitForWebRequestMainThreadSleep = 1;
        string m_TransformedInternalId;
        AssetBundleRequest m_PreloadRequest;
        bool m_PreloadCompleted = false;
        ulong m_LastDownloadedByteCount = 0;
        float m_TimeoutTimer = 0;
        int m_TimeoutOverFrames = 0;

        private bool HasTimedOut => m_TimeoutTimer >= m_Options.Timeout && m_TimeoutOverFrames > 5;

        internal long BytesToDownload
        {
            get
            {
                if (m_BytesToDownload == -1)
                {
                    if (m_Options != null)
                    {
                        string path = m_ProvideHandle.ResourceManager.TransformInternalId(m_ProvideHandle.Location);
                        if (File.Exists(GetEncryptedAssetLocalPath(path, m_Options)))
                            m_BytesToDownload = 0;
                        else
                            m_BytesToDownload = m_Options.ComputeSize(m_ProvideHandle.Location, m_ProvideHandle.ResourceManager);
                    }
                    else
                        m_BytesToDownload = 0;
                }

                return m_BytesToDownload;
            }
        }

        internal UnityWebRequest CreateWebRequest(IResourceLocation loc)
        {
            var url = m_ProvideHandle.ResourceManager.TransformInternalId(loc);
            return CreateWebRequest(url);
        }

        internal UnityWebRequest CreateWebRequest(string url)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            Uri uri = new Uri(url.Replace(" ", "%20"));
#else
            Uri uri = new Uri(Uri.EscapeUriString(url));
#endif

            UnityWebRequest webRequest;
            webRequest = new UnityWebRequest(url);
            DownloadHandlerBuffer dH = new DownloadHandlerBuffer();
            webRequest.downloadHandler = dH;

            if (m_Options.RedirectLimit > 0)
                webRequest.redirectLimit = m_Options.RedirectLimit;
            if (m_ProvideHandle.ResourceManager.CertificateHandlerInstance != null)
            {
                webRequest.certificateHandler = m_ProvideHandle.ResourceManager.CertificateHandlerInstance;
                webRequest.disposeCertificateHandlerOnDispose = false;
            }

            m_ProvideHandle.ResourceManager.WebRequestOverride?.Invoke(webRequest);
            return webRequest;
        }

        /// <summary>
        /// Creates a request for loading all assets from an AssetBundle.
        /// </summary>
        /// <returns>Returns the request.</returns>
        public AssetBundleRequest GetAssetPreloadRequest()
        {
            if (m_PreloadCompleted || GetAssetBundle() == null)
                return null;

            if (m_Options.AssetLoadMode == AssetLoadMode.AllPackedAssetsAndDependencies)
            {
#if !UNITY_2021_1_OR_NEWER
                if (AsyncOperationHandle.IsWaitingForCompletion)
                {
                    m_AssetBundle.LoadAllAssets();
                    m_PreloadCompleted = true;
                    return null;
                }
#endif
                if (m_PreloadRequest == null)
                {
                    m_PreloadRequest = m_AssetBundle.LoadAllAssetsAsync();
                    m_PreloadRequest.completed += operation => m_PreloadCompleted = true;
                }

                return m_PreloadRequest;
            }

            return null;
        }

        float PercentComplete()
        {
            return m_RequestOperation != null ? m_RequestOperation.progress : 0.0f;
        }

        DownloadStatus GetDownloadStatus()
        {
            if (m_Options == null)
                return default;
            var status = new DownloadStatus() { TotalBytes = BytesToDownload, IsDone = PercentComplete() >= 1f };
            if (BytesToDownload > 0)
            {
                if (m_WebRequestQueueOperation != null && string.IsNullOrEmpty(m_WebRequestQueueOperation.WebRequest.error))
                    m_DownloadedBytes = (long)(m_WebRequestQueueOperation.WebRequest.downloadedBytes);
                else if (m_RequestOperation != null && m_RequestOperation is UnityWebRequestAsyncOperation operation && string.IsNullOrEmpty(operation.webRequest.error))
                    m_DownloadedBytes = (long)operation.webRequest.downloadedBytes;
            }

            status.DownloadedBytes = m_DownloadedBytes;
            return status;
        }

        /// <summary>
        /// Get the asset bundle object managed by this resource.  This call may force the bundle to load if not already loaded.
        /// </summary>
        /// <returns>The asset bundle.</returns>
        public AssetBundle GetAssetBundle()
        {
            if (m_AssetBundle == null)
            {
                if (m_downloadHandler != null)
                {
                    var crc = m_Options == null ? 0 : m_Options.Crc;
                    var inputStream = new MemoryStream(m_downloadHandler.data, false);
                    inputStream.Seek(0, SeekOrigin.Begin);
                    //
                    String filePath = GetEncryptedAssetLocalPath(m_InternalId, m_Options);
                    saveDownloadBundle(inputStream, filePath);
                    //
                    var dataStream = new SeekableAesStream(inputStream);
                    if (dataStream.CanSeek)
                    {
                        m_AssetBundle = AssetBundle.LoadFromStream(dataStream, crc);
                    }
                    else
                    {
                        //Slow path needed if stream is not seekable
                        var memStream = new MemoryStream();
                        dataStream.CopyTo(memStream);
                        dataStream.Flush();
                        dataStream.Dispose();
                        inputStream.Dispose();
                        m_AssetBundle = AssetBundle.LoadFromStream(memStream, crc);
                    }
                    m_downloadHandler.Dispose();
                    m_downloadHandler = null;
                }
                else if (m_RequestOperation is AssetBundleCreateRequest)
                {
                    m_AssetBundle = (m_RequestOperation as AssetBundleCreateRequest).assetBundle;
                }
            }

#if ENABLE_ADDRESSABLE_PROFILER
            AddBundleToProfiler(Profiling.ContentStatus.Active, m_Source);
#endif
            return m_AssetBundle;
        }

        void saveDownloadBundle(Stream stream, string path)
        {
            //Create the Directory if it does not exist
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }

            try
            {
                using (Stream file = File.Create(path))
                {
                    CopyStream(stream, file);
                    file.Flush();
                    file.Close();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed To Save Data to: " + path.Replace("/", "\\"));
                Debug.LogWarning("Error: " + e.Message);
            }
        }

        void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[8 * 1024];
            int len;
            while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, len);
            }
        }


#if ENABLE_ADDRESSABLE_PROFILER
        private void AddBundleToProfiler(Profiling.ContentStatus status, BundleSource source)
        {
            if (!Profiler.enabled)
                return;
            if (!m_ProvideHandle.IsValid)
                return;

            if (status == Profiling.ContentStatus.Active && m_AssetBundle == null)
                Profiling.ProfilerRuntime.BundleReleased(m_Options.BundleName);
            else
                Profiling.ProfilerRuntime.AddBundleOperation(m_ProvideHandle, m_Options, status, source);
        }

        private void RemoveBundleFromProfiler()
        {
            if (m_Options == null)
                return;
            Profiling.ProfilerRuntime.BundleReleased(m_Options.BundleName);
        }
#endif

#if UNLOAD_BUNDLE_ASYNC
        void OnUnloadOperationComplete(AsyncOperation op)
        {
            m_UnloadOperation = null;
            BeginOperation();
        }

#endif

#if UNLOAD_BUNDLE_ASYNC
        /// <summary>
        /// Stores AssetBundle loading information, starts loading the bundle.
        /// </summary>
        /// <param name="provideHandle">The container for AssetBundle loading information.</param>
        /// <param name="unloadOp">The async operation for unloading the AssetBundle.</param>
        public void Start(ProvideHandle provideHandle, AssetBundleUnloadOperation unloadOp)
#else
        /// <summary>
        /// Stores AssetBundle loading information, starts loading the bundle.
        /// </summary>
        /// <param name="provideHandle">The container for information regarding loading the AssetBundle.</param>
        public void Start(ProvideHandle provideHandle)
#endif
        {
            m_Retries = 0;
            m_AssetBundle = null;
            m_downloadHandler = null;
            m_RequestOperation = null;
            m_WebRequestCompletedCallbackCalled = false;
            m_ProvideHandle = provideHandle;
            m_Options = m_ProvideHandle.Location.Data as AssetBundleRequestOptions;
            m_BytesToDownload = -1;
            m_ProvideHandle.SetProgressCallback(PercentComplete);
            m_ProvideHandle.SetDownloadProgressCallbacks(GetDownloadStatus);
            m_ProvideHandle.SetWaitForCompletionCallback(WaitForCompletionHandler);
#if UNLOAD_BUNDLE_ASYNC
            m_UnloadOperation = unloadOp;
            if (m_UnloadOperation != null && !m_UnloadOperation.isDone)
                m_UnloadOperation.completed += OnUnloadOperationComplete;
            else
#endif
            BeginOperation();
        }

        private bool WaitForCompletionHandler()
        {
#if UNLOAD_BUNDLE_ASYNC
            if (m_UnloadOperation != null && !m_UnloadOperation.isDone)
            {
                m_UnloadOperation.completed -= OnUnloadOperationComplete;
                m_UnloadOperation.WaitForCompletion();
                m_UnloadOperation = null;
                BeginOperation();
            }
#endif

            if (m_RequestOperation == null)
            {
                if (m_WebRequestQueueOperation == null)
                    return false;
                else
                    WebRequestQueue.WaitForRequestToBeActive(m_WebRequestQueueOperation, k_WaitForWebRequestMainThreadSleep);
            }

            //We don't want to wait for request op to complete if it's a LoadFromFileAsync. Only UWR will complete in a tight loop like this.
            if (m_RequestOperation is UnityWebRequestAsyncOperation op)
            {
                while (!UnityWebRequestUtilities.IsAssetBundleDownloaded(op))
                    System.Threading.Thread.Sleep(k_WaitForWebRequestMainThreadSleep);
#if ENABLE_ASYNC_ASSETBUNDLE_UWR
                if (m_Source == BundleSource.Cache)
                {
                    var downloadHandler = (DownloadHandlerAssetBundle)op?.webRequest?.downloadHandler;
                    if (downloadHandler.autoLoadAssetBundle)
                        m_AssetBundle = downloadHandler.assetBundle;
                }
#endif
                if (!m_WebRequestCompletedCallbackCalled)
                {
                    WebRequestOperationCompleted(m_RequestOperation);
                    m_RequestOperation.completed -= WebRequestOperationCompleted;
                }
            }

            var assetBundle = GetAssetBundle();
            if (!m_Completed && m_RequestOperation.isDone)
            {
                m_ProvideHandle.Complete(this, m_AssetBundle != null, null);
                m_Completed = true;
            }

            return m_Completed;
        }

        void AddCallbackInvokeIfDone(AsyncOperation operation, Action<AsyncOperation> callback)
        {
            if (operation.isDone)
                callback(operation);
            else
                operation.completed += callback;
        }

        /// <summary>
        /// Determines where an AssetBundle can be loaded from.
        /// </summary>
        /// <param name="handle">The container for AssetBundle loading information.</param>
        /// <param name="loadType">Specifies where an AssetBundle can be loaded from.</param>
        /// <param name="path">The file path or url where the AssetBundle is located.</param>
        public static void GetLoadInfo(ProvideHandle handle, out LoadType loadType, out string path)
        {
            GetLoadInfo(handle.Location, handle.ResourceManager, out loadType, out path);
        }

        internal static void GetLoadInfo(IResourceLocation location, ResourceManager resourceManager, out LoadType loadType, out string path)
        {
            var options = location?.Data as AssetBundleRequestOptions;
            if (options == null)
            {
                loadType = LoadType.None;
                path = null;
                return;
            }

            path = resourceManager.TransformInternalId(location);
            if (Application.platform == RuntimePlatform.Android && path.StartsWith("jar:", StringComparison.Ordinal))
                loadType = options.UseUnityWebRequestForLocalBundles ? LoadType.Web : LoadType.Local;
            else if (ResourceManagerConfig.ShouldPathUseWebRequest(path))
            {
                if (File.Exists(GetEncryptedAssetLocalPath(path, options)))
                    loadType = LoadType.Local;
                else
                    loadType = LoadType.Web;
            }
            else if (options.UseUnityWebRequestForLocalBundles)
            {
                path = "file:///" + Path.GetFullPath(path);
                loadType = LoadType.Web;
            }
            else
                loadType = LoadType.Local;

            if (loadType == LoadType.Web)
                path = path.Replace('\\', '/');
        }

        private void BeginOperation()
        {
            m_DownloadedBytes = 0;
            GetLoadInfo(m_ProvideHandle, out LoadType loadType, out m_TransformedInternalId);

            if (loadType == LoadType.Local)
            {
                m_Source = BundleSource.Local;
                {
                    if(File.Exists(m_TransformedInternalId))
                    {
                        var crc = m_Options == null ? 0 : m_Options.Crc;
                        LoadLocalAssetBundle(m_TransformedInternalId, crc);
                    }
                    else
                    {
                        var crc = m_Options == null ? 0 : m_Options.Crc;
                        LoadLocalAssetBundle(GetEncryptedAssetLocalPath(m_TransformedInternalId, m_Options), crc);
                    }
#if ENABLE_ADDRESSABLE_PROFILER
                    AddBundleToProfiler(Profiling.ContentStatus.Loading, m_Source);
#endif
                    AddCallbackInvokeIfDone(m_RequestOperation, LocalRequestOperationCompleted);
                }
            }
            else if (loadType == LoadType.Web)
            {
                m_WebRequestCompletedCallbackCalled = false;
                var req = CreateWebRequest(m_TransformedInternalId);
#if ENABLE_ASYNC_ASSETBUNDLE_UWR
                //((DownloadHandler)req.downloadHandler).autoLoadAssetBundle = false;
#endif
                req.disposeDownloadHandlerOnDispose = false;

                m_WebRequestQueueOperation = WebRequestQueue.QueueRequest(req);
                if (m_WebRequestQueueOperation.IsDone)
                {
                    BeginWebRequestOperation(m_WebRequestQueueOperation.Result);
                }
                else
                {
#if ENABLE_ADDRESSABLE_PROFILER
                    AddBundleToProfiler(Profiling.ContentStatus.Queue, m_Source);
#endif
                    m_WebRequestQueueOperation.OnComplete += asyncOp => BeginWebRequestOperation(asyncOp);
                }
            }
            else
            {
                m_Source = BundleSource.None;
                m_RequestOperation = null;
                m_ProvideHandle.Complete<AssetBundleResource>(null, false,
                    new RemoteProviderException(string.Format("Invalid path in AssetBundleProvider: '{0}'.", m_TransformedInternalId), m_ProvideHandle.Location));
                m_Completed = true;
            }
        }

        private void LoadLocalAssetBundle(String path, uint crc)
        {
            var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var dataStream = new SeekableAesStream(fileStream);
            if (dataStream.CanSeek)
            {
                m_RequestOperation = AssetBundle.LoadFromStreamAsync(dataStream, crc);
            }
            else
            {
                //Slow path needed if stream is not seekable
                var memStream = new MemoryStream();
                dataStream.CopyTo(memStream);
                dataStream.Flush();
                dataStream.Dispose();
                fileStream.Dispose();

                memStream.Position = 0;
                m_RequestOperation = AssetBundle.LoadFromStreamAsync(memStream, crc);
            }
        }


        private void BeginWebRequestOperation(AsyncOperation asyncOp)
        {
            m_TimeoutTimer = 0;
            m_TimeoutOverFrames = 0;
            m_LastDownloadedByteCount = 0;
            m_RequestOperation = asyncOp;
            if (m_RequestOperation == null || m_RequestOperation.isDone)
                WebRequestOperationCompleted(m_RequestOperation);
            else
            {
                if (m_Options.Timeout > 0)
                    m_ProvideHandle.ResourceManager.AddUpdateReceiver(this);
#if ENABLE_ADDRESSABLE_PROFILER
                AddBundleToProfiler(m_Source == BundleSource.Cache ? Profiling.ContentStatus.Loading : Profiling.ContentStatus.Downloading, m_Source );
#endif
                m_RequestOperation.completed += WebRequestOperationCompleted;
            }
        }

        /// <inheritdoc/>
        public void Update(float unscaledDeltaTime)
        {
            if (m_RequestOperation != null && m_RequestOperation is UnityWebRequestAsyncOperation operation && !operation.isDone)
            {
                if (m_LastDownloadedByteCount != operation.webRequest.downloadedBytes)
                {
                    m_TimeoutTimer = 0;
                    m_TimeoutOverFrames = 0;
                    m_LastDownloadedByteCount = operation.webRequest.downloadedBytes;
                }
                else
                {
                    m_TimeoutTimer += unscaledDeltaTime;
                    if (HasTimedOut)
                        operation.webRequest.Abort();
                    m_TimeoutOverFrames++;
                }
            }
        }

        private void LocalRequestOperationCompleted(AsyncOperation op)
        {
            CompleteBundleLoad((op as AssetBundleCreateRequest).assetBundle);
        }

        private void CompleteBundleLoad(AssetBundle bundle)
        {
            m_AssetBundle = bundle;
#if ENABLE_ADDRESSABLE_PROFILER
            AddBundleToProfiler(Profiling.ContentStatus.Active, m_Source);
#endif
            if (m_AssetBundle != null)
                m_ProvideHandle.Complete(this, true, null);
            else
                m_ProvideHandle.Complete<AssetBundleResource>(null, false,
                    new RemoteProviderException(string.Format("Invalid path in AssetBundleProvider: '{0}'.", m_TransformedInternalId), m_ProvideHandle.Location));
            m_Completed = true;
        }

        private void WebRequestOperationCompleted(AsyncOperation op)
        {
            if (m_WebRequestCompletedCallbackCalled)
                return;

            if (m_Options.Timeout > 0)
                m_ProvideHandle.ResourceManager.RemoveUpdateReciever(this);

            m_WebRequestCompletedCallbackCalled = true;
            UnityWebRequestAsyncOperation remoteReq = op as UnityWebRequestAsyncOperation;
            var webReq = remoteReq?.webRequest;
            m_downloadHandler = webReq?.downloadHandler as DownloadHandler;
            UnityWebRequestResult uwrResult = null;
            if (webReq != null && !UnityWebRequestUtilities.RequestHasErrors(webReq, out uwrResult))
            {
                m_InternalId = m_ProvideHandle.Location.InternalId;
                if (!m_Completed)
                {
#if ENABLE_ADDRESSABLE_PROFILER
                    AddBundleToProfiler(Profiling.ContentStatus.Active, m_Source);
#endif
                    m_ProvideHandle.Complete(this, true, null);
                    m_Completed = true;
                }
#if ENABLE_CACHING
                if (!string.IsNullOrEmpty(m_Options.Hash) && m_Options.ClearOtherCachedVersionsWhenLoaded)
                    Caching.ClearOtherCachedVersions(m_Options.BundleName, Hash128.Parse(m_Options.Hash));
#endif
            }
            else
            {
                if (HasTimedOut)
                    uwrResult.Error = "Request timeout";
                webReq = m_WebRequestQueueOperation.WebRequest;
                if (uwrResult == null)
                    uwrResult = new UnityWebRequestResult(m_WebRequestQueueOperation.WebRequest);

                m_InternalId = m_ProvideHandle.Location.InternalId;
                m_downloadHandler = webReq.downloadHandler as DownloadHandler;
                m_downloadHandler.Dispose();
                m_downloadHandler = null;
                bool forcedRetry = false;
                string message = $"Web request failed, retrying ({m_Retries}/{m_Options.RetryCount})...\n{uwrResult}";
#if ENABLE_CACHING
                if (!string.IsNullOrEmpty(m_Options.Hash))
                {
#if ENABLE_ADDRESSABLE_PROFILER
                    if (m_Source == BundleSource.Cache)
#endif
                    {
                        message = $"Web request failed to load from cache. The cached AssetBundle will be cleared from the cache and re-downloaded. Retrying...\n{uwrResult}";
                        Caching.ClearCachedVersion(m_Options.BundleName, Hash128.Parse(m_Options.Hash));
                        // When attempted to load from cache we always retry on first attempt and failed
                        if (m_Retries == 0)
                        {
                            Debug.LogFormat(message);
                            BeginOperation();
                            m_Retries++; //Will prevent us from entering an infinite loop of retrying if retry count is 0
                            forcedRetry = true;
                        }
                    }
                }
#endif
                if (!forcedRetry)
                {
                    if (m_Retries < m_Options.RetryCount && uwrResult.ShouldRetryDownloadError())
                    {
                        m_Retries++;
                        Debug.LogFormat(message);
                        BeginOperation();
                    }
                    else
                    {
                        var exception = new RemoteProviderException($"Unable to load asset bundle from : {webReq.url}", m_ProvideHandle.Location, uwrResult);
                        m_ProvideHandle.Complete<AssetBundleResource>(null, false, exception);
                        m_Completed = true;
#if ENABLE_ADDRESSABLE_PROFILER
                        RemoveBundleFromProfiler();
#endif
                    }
                }
            }

            webReq.Dispose();
        }

#if UNLOAD_BUNDLE_ASYNC
        /// <summary>
        /// Starts an async operation that unloads all resources associated with the AssetBundle.
        /// </summary>
        /// <param name="unloadOp">The async operation.</param>
        /// <returns>Returns true if the async operation object is valid.</returns>
        public bool Unload(out AssetBundleUnloadOperation unloadOp)
#else
        /// <summary>
        /// Unloads all resources associated with the AssetBundle.
        /// </summary>
        public void Unload()
#endif
        {
#if UNLOAD_BUNDLE_ASYNC
            unloadOp = null;
            if (m_AssetBundle != null)
            {
                unloadOp = m_AssetBundle.UnloadAsync(true);
                m_AssetBundle = null;
            }
#else
            if (m_AssetBundle != null)
            {
                m_AssetBundle.Unload(true);
                m_AssetBundle = null;
            }
#endif
            if (m_downloadHandler != null)
            {
                m_downloadHandler.Dispose();
                m_downloadHandler = null;
            }

            m_RequestOperation = null;
#if ENABLE_ADDRESSABLE_PROFILER
            RemoveBundleFromProfiler();
#endif
#if UNLOAD_BUNDLE_ASYNC
            return unloadOp != null;
#endif
        }
    }



    [DisplayName("AES AssetBundle Provider")]
    public class AesAssetBundleProvider : ResourceProviderBase
    {
#if UNLOAD_BUNDLE_ASYNC
        private static Dictionary<string, AssetBundleUnloadOperation> m_UnloadingBundles = new Dictionary<string, AssetBundleUnloadOperation>();
        /// <summary>
        /// Stores async operations that unload the requested AssetBundles.
        /// </summary>
        protected internal static Dictionary<string, AssetBundleUnloadOperation> UnloadingBundles
        {
            get { return m_UnloadingBundles; }
            internal set { m_UnloadingBundles = value; }
        }

        internal static int UnloadingAssetBundleCount => m_UnloadingBundles.Count;
        internal static int AssetBundleCount => AssetBundle.GetAllLoadedAssetBundles().Count() - UnloadingAssetBundleCount;
        internal static void WaitForAllUnloadingBundlesToComplete()
        {
            if (UnloadingAssetBundleCount > 0)
            {
                var bundles = m_UnloadingBundles.Values.ToArray();
                foreach (var b in bundles)
                    b.WaitForCompletion();
            }
        }

#else
        internal static void WaitForAllUnloadingBundlesToComplete()
        {
        }
#endif

        /// <inheritdoc/>
        public override void Provide(ProvideHandle providerInterface)
        {
#if UNLOAD_BUNDLE_ASYNC
            if (m_UnloadingBundles.TryGetValue(providerInterface.Location.InternalId, out var unloadOp))
            {
                if (unloadOp.isDone)
                    unloadOp = null;
            }
            new AesAssetBundleResource().Start(providerInterface, unloadOp);
#else
            new AesAssetBundleResource().Start(providerInterface);
#endif
        }

        /// <inheritdoc/>
        public override Type GetDefaultType(IResourceLocation location)
        {
            return typeof(IAssetBundleResource);
        }

        /// <summary>
        /// Releases the asset bundle via AssetBundle.Unload(true).
        /// </summary>
        /// <param name="location">The location of the asset to release</param>
        /// <param name="asset">The asset in question</param>
        public override void Release(IResourceLocation location, object asset)
        {
            if (location == null)
                throw new ArgumentNullException("location");
            if (asset == null)
            {
                Debug.LogWarningFormat("Releasing null asset bundle from location {0}.  This is an indication that the bundle failed to load.", location);
                return;
            }

            var bundle = asset as AesAssetBundleResource;
            if (bundle != null)
            {
#if UNLOAD_BUNDLE_ASYNC
                if (bundle.Unload(out var unloadOp))
                {
                    m_UnloadingBundles.Add(location.InternalId, unloadOp);
                    unloadOp.completed += op => m_UnloadingBundles.Remove(location.InternalId);
                }
#else
                bundle.Unload();
#endif
                return;
            }
        }
    }

}
