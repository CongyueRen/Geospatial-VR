using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.XR.CoreUtils;

/// <summary>
/// Reads chunk and LOD information from StreamingAssets/{chunksFolderName}/{lodIndexFileName},
/// and creates a GameObject with GaussianLoader(s) per chunk.
/// Supports:
/// - Frustum culling
/// - Chunk streaming (load/unload radius with hysteresis)
/// - Per-frame reload budget (avoid VR hitching)
/// - Runtime display-mode switching between Gaussian splats and raw point cloud
/// </summary>
public class GaussianChunkManager : MonoBehaviour
{
    public const string DefaultIndoorChunksFolderName = "chunks_indoor_random";
    public const string DefaultIndoorUniformChunksFolderName = "chunks_indoor_uniform";
    public const string LatestOutdoorChunksFolderName = "chunks_outdoor_random";
    public const string DefaultUniformChunksFolderName = "chunks_outdoor_uniform";
    public const string DefaultLodIndexFileName = "chunks_lod_index.json";

    public enum DisplayMode
    {
        GaussianSplat = 0,
        RawPointCloud = 1
    }

    public enum DatasetMode
    {
        Indoor = 0,
        Outdoor = 1
    }

    public enum SamplingMode
    {
        Random = 0,
        Uniform = 1
    }

    [Serializable]
    public class DatasetDefinition
    {
        public string displayName = "Indoor";
        [HideInInspector]
        public string chunksFolderName = DefaultIndoorChunksFolderName;
        public string randomChunksFolderName = DefaultIndoorChunksFolderName;
        public string uniformChunksFolderName = DefaultIndoorUniformChunksFolderName;
        public string lodIndexFileName = DefaultLodIndexFileName;
    }

    [Header("Dataset Switching")]
    public DatasetMode activeDatasetMode = DatasetMode.Outdoor;
    public SamplingMode activeSamplingMode = SamplingMode.Random;

    public DatasetDefinition indoorDataset = new DatasetDefinition
    {
        displayName = "Indoor",
        chunksFolderName = DefaultIndoorChunksFolderName,
        randomChunksFolderName = DefaultIndoorChunksFolderName,
        uniformChunksFolderName = DefaultIndoorUniformChunksFolderName,
        lodIndexFileName = DefaultLodIndexFileName
    };

    public DatasetDefinition outdoorDataset = new DatasetDefinition
    {
        displayName = "Outdoor",
        chunksFolderName = LatestOutdoorChunksFolderName,
        randomChunksFolderName = LatestOutdoorChunksFolderName,
        uniformChunksFolderName = DefaultUniformChunksFolderName,
        lodIndexFileName = DefaultLodIndexFileName
    };

    public bool showDatasetSwitcherUI = true;
    public bool showSamplingSwitcherUI = true;
    public bool recenterCameraOnDatasetSwitch = true;
    public float cameraDistancePadding = 4f;
    public float cameraHeightPadding = 2f;

    [Header("XR Scene Adaption")]
    [Tooltip("Automatically treat the scene as XR when an XR Origin is present.")]
    public bool autoDetectXRScene = true;

    [Tooltip("Optional override for the transform that should be moved when recentering in VR.")]
    public Transform xrRigRootOverride;

    [Tooltip("Hide the legacy OnGUI panel when running in an XR scene.")]
    public bool hideLegacyOnGUIWhenXRActive = true;

    [Tooltip("Disable the Tab keyboard toggle in XR scenes so desktop input does not interfere with VR interaction.")]
    public bool disableKeyboardToggleWhenXRActive = true;

    [Header("XR Standing Height")]
    [Tooltip("When recentering in XR, place the user relative to the dataset floor instead of above the dataset center.")]
    public bool useDatasetFloorHeightForXRRecenter = true;

    [Tooltip("Desired eye height above the dataset floor in meters for XR standing mode.")]
    public float xrStandingEyeHeight = 1.65f;

    [Tooltip("Extra vertical offset added on top of the standing eye height in XR scenes.")]
    public float xrAdditionalHeightOffset = 0.0f;

    [Tooltip("When enabled, place the XR user near the dataset center on the horizontal plane instead of outside the dataset looking in.")]
    public bool xrPlaceUserNearDatasetCenter = true;

    [Tooltip("Extra horizontal X/Z offset from the dataset center for XR placement.")]
    public Vector2 xrPlanarOffsetFromCenter = Vector2.zero;

    [Tooltip("Keep the XR view mostly level when recentering instead of forcing a downward look toward dataset center.")]
    public bool xrKeepLevelViewOnRecenter = true;

    [Header("Chunk Folder (in StreamingAssets)")]
    public string chunksFolderName = LatestOutdoorChunksFolderName;

    [Tooltip("In the Unity Editor, also try <repo>/data/<chunksFolderName> when the folder is not under StreamingAssets.")]
    public bool allowProjectDataFolderFallback = true;

    [Header("LOD index file name")]
    public string lodIndexFileName = DefaultLodIndexFileName;

    [Header("Rendering")]
    [Tooltip("Base material for the Gaussian splat view.")]
    public Material pointMaterial;

    [Tooltip("Optional material for the raw point-cloud view. If empty, a runtime material is created from Shader \"Unlit/RawPointCloud\".")]
    public Material rawPointMaterial;

    [Header("Display Mode")]
    public DisplayMode currentDisplayMode = DisplayMode.GaussianSplat;
    public bool allowKeyboardToggle = true;
    public bool showModeSwitcherUI = true;

    [Header("Splatting / Rendering Mode")]
    public bool renderAsSplatQuads = true;

    [Header("Raw Point Cloud")]
    public float rawPointSize = 1.0f;

    [Header("Point size (optional / legacy)")]
    public float pointSizeL0 = 1.0f;
    public float pointSizeL1 = 1.0f;
    public float pointSizeL2 = 1.0f;
    public float pointSizeL3 = 1.0f;
    public float pointSizeL4 = 1.0f;

    [Header("Ellipse Splat Params (per LOD)")]
    [Range(0f, 1f)] public float opacityL0 = 0.6f;
    [Range(0f, 1f)] public float opacityL1 = 0.18f;
    [Range(0f, 1f)] public float opacityL2 = 0.24f;
    [Range(0f, 1f)] public float opacityL3 = 0.28f;
    [Range(0f, 1f)] public float opacityL4 = 0.32f;

    [Tooltip("k-sigma cutoff used to size ellipse quads (typical 2~4).")]
    public float sigmaCutoffL0 = 3.0f;
    public float sigmaCutoffL1 = 3.0f;
    public float sigmaCutoffL2 = 3.0f;
    public float sigmaCutoffL3 = 3.0f;
    public float sigmaCutoffL4 = 3.0f;

    [Tooltip("Clamp ellipse axis in pixel units (min).")]
    public float minAxisPixels = 0.75f;

    [Tooltip("Clamp ellipse axis in pixel units (max).")]
    public float maxAxisPixels = 64.0f;

    [Header("LOD Layer Switching")]
    [Tooltip("Currently displayed LOD layer. Keyboard shortcuts 1-5 map to L0-L4 when enabled.")]
    [Range(0, 4)] public int activeLodLevel = 0;

    public bool showLodSwitcherUI = true;
    public bool allowKeyboardLodSwitch = true;

    [Tooltip("If enabled, unavailable chunk LOD files fall back to the closest available LOD file for that chunk.")]
    public bool fallbackToNearestAvailableLod = true;

    public bool logLODChanges = true;

    [Header("Per-frame reload budget")]
    [Tooltip("Limits how many loaders may call ReloadData() per frame to avoid VR hitching.")]
    public int maxReloadsPerFrame = 1;

    [Header("Streaming (chunk load/unload)")]
    [Tooltip("Ensure chunks closer than this distance are loaded (LoadData/ReloadData).")]
    public float loadRadius = 30f;

    [Tooltip("Unload chunks farther than this distance (free GPU buffers). Should be slightly larger than loadRadius to avoid oscillation.")]
    public float unloadRadius = 40f;

    [Header("Debug")]
    public bool loadOnStart = true;
    [Tooltip("Wait until the first rendered frame before beginning heavy dataset initialization. Helps SteamVR/OpenXR dismiss the loading compositor.")]
    public bool waitForFirstRenderedFrameBeforeInitialLoad = true;

    [Tooltip("Extra delay before the initial dataset load starts. Useful when VR runtimes need more time to stabilize.")]
    public float initialLoadDelaySeconds = 1.0f;

    public bool logChunkInfo = true;
    public bool logStreamingChanges = true;
    public bool logCullingChanges = false;

    private readonly Plane[] _frustumPlanes = new Plane[6];

    [Serializable] public class LODLevel { public string filename; public int count; }
    [Serializable] public class LODGroup { public LODLevel L0; public LODLevel L1; public LODLevel L2; public LODLevel L3; public LODLevel L4; }

    [Serializable]
    public class GaussianLODManifestLevel
    {
        public int level;
        public string label;
        public string sampling_method;
        public string sampling_parameter_name;
        public float sampling_parameter_value;
        public string sampling_parameter_label;
        public int num_points;
    }

    [Serializable]
    public class GaussianLODManifest
    {
        public string sampling_method;
        public List<GaussianLODManifestLevel> levels;
    }

    [Serializable]
    public class ChunkEntry
    {
        public int[] ijk;
        public string filename;
        public int count;
        public float[] bbox_min;
        public float[] bbox_max;
        public float[] center;
        public LODGroup lod;
        public string[] lod_files;
        public int[] lod_counts;
    }

    [Serializable]
    public class LODIndex
    {
        public string npz_source;
        public float[] origin;
        public float[] chunk_size;
        public int[] grid_shape;
        public int num_points;
        public int num_chunks;
        public int[] lod_levels;
        public GaussianLODManifest gaussian_lod_manifest;
        public List<ChunkEntry> chunks;
    }

    private LODIndex _lodIndex;
    private Transform _chunkRoot;

    private Material _pointMaterialFineInstance;
    private Material _rawPointMaterialInstance;

    private readonly List<GaussianLoader> _chunkLoaders = new();
    private readonly List<ChunkEntry> _chunkEntries = new();
    private readonly List<bool> _chunkVisibility = new();
    private readonly List<bool> _isChunkLoaded = new();
    private readonly List<bool> _isFineLoaded = new();
    private readonly List<int> _fineLodLevels = new();

    private DisplayMode _lastDisplayMode;
    private bool _lastRenderAsSplatQuads;
    private float _lastPointSizeL0;
    private float _lastPointSizeL1;
    private float _lastPointSizeL2;
    private float _lastPointSizeL3;
    private float _lastPointSizeL4;
    private float _lastOpacityL0;
    private float _lastOpacityL1;
    private float _lastOpacityL2;
    private float _lastOpacityL3;
    private float _lastOpacityL4;
    private float _lastSigmaL0;
    private float _lastSigmaL1;
    private float _lastSigmaL2;
    private float _lastSigmaL3;
    private float _lastSigmaL4;
    private float _lastMinAxisPx;
    private float _lastMaxAxisPx;
    private float _lastRawPointSize;
    private int _lastActiveLodLevel;
    private string _resolvedChunkDir;

    private GUIStyle _panelStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _labelStyle;
    private XROrigin _xrOrigin;

    private bool UseGaussianSplatMode => currentDisplayMode == DisplayMode.GaussianSplat;
    private string CurrentDatasetLabel => GetDatasetLabelForCurrentSelection();

    private void Awake()
    {
        NormalizeDatasetSettings();
    }

    private void Reset()
    {
        NormalizeDatasetSettings();
        chunksFolderName = LatestOutdoorChunksFolderName;
        lodIndexFileName = DefaultLodIndexFileName;
        activeDatasetMode = DatasetMode.Outdoor;
        activeSamplingMode = SamplingMode.Random;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        NormalizeDatasetSettings();
    }
#endif

    private IEnumerator Start()
    {
        NormalizeDatasetSettings();

        if (!loadOnStart)
            yield break;

        if (waitForFirstRenderedFrameBeforeInitialLoad)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        if (initialLoadDelaySeconds > 0f)
            yield return new WaitForSeconds(initialLoadDelaySeconds);

        LoadLODIndex();
        InitChunks();
        CacheRenderParams();
    }

    private void OnDestroy()
    {
        DestroyRuntimeMaterial(ref _pointMaterialFineInstance);
        DestroyRuntimeMaterial(ref _rawPointMaterialInstance);
    }

    private void DestroyRuntimeMaterial(ref Material materialInstance)
    {
        if (materialInstance == null)
            return;

        Destroy(materialInstance);
        materialInstance = null;
    }

    private void NormalizeDatasetSettings()
    {
        if (indoorDataset == null)
            indoorDataset = new DatasetDefinition();

        if (outdoorDataset == null)
            outdoorDataset = new DatasetDefinition();

        NormalizeDatasetDefinition(indoorDataset, "Indoor", DefaultIndoorChunksFolderName, DefaultIndoorUniformChunksFolderName);
        NormalizeDatasetDefinition(outdoorDataset, "Outdoor", LatestOutdoorChunksFolderName, DefaultUniformChunksFolderName);

        chunksFolderName = NormalizeLegacyFolderName(chunksFolderName);
        if (string.IsNullOrWhiteSpace(chunksFolderName))
            chunksFolderName = LatestOutdoorChunksFolderName;

        if (string.IsNullOrWhiteSpace(lodIndexFileName))
            lodIndexFileName = DefaultLodIndexFileName;

        SyncSelectionFromCurrentFolder();
    }

    private static void NormalizeDatasetDefinition(
        DatasetDefinition dataset,
        string fallbackName,
        string randomFallbackFolder,
        string uniformFallbackFolder)
    {
        if (dataset == null)
            return;

        if (string.IsNullOrWhiteSpace(dataset.displayName))
            dataset.displayName = fallbackName;

        dataset.chunksFolderName = NormalizeLegacyFolderName(dataset.chunksFolderName);
        dataset.randomChunksFolderName = NormalizeLegacyFolderName(dataset.randomChunksFolderName);
        dataset.uniformChunksFolderName = NormalizeLegacyFolderName(dataset.uniformChunksFolderName);

        if (string.IsNullOrWhiteSpace(dataset.randomChunksFolderName))
            dataset.randomChunksFolderName = randomFallbackFolder;

        if (string.IsNullOrWhiteSpace(dataset.uniformChunksFolderName))
            dataset.uniformChunksFolderName = uniformFallbackFolder;

        if (string.IsNullOrWhiteSpace(dataset.chunksFolderName))
            dataset.chunksFolderName = dataset.randomChunksFolderName;

        if (string.IsNullOrWhiteSpace(dataset.lodIndexFileName))
            dataset.lodIndexFileName = DefaultLodIndexFileName;
    }

    private static string NormalizeLegacyFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return folderName;

        if (string.Equals(folderName, "chunks_Indoordata", StringComparison.OrdinalIgnoreCase))
            return DefaultIndoorChunksFolderName;

        if (string.Equals(folderName, "chunks_TUMv2", StringComparison.OrdinalIgnoreCase))
            return LatestOutdoorChunksFolderName;

        return folderName;
    }

    private void SyncSelectionFromCurrentFolder()
    {
        if (MatchesDatasetFolder(indoorDataset, SamplingMode.Random))
        {
            activeDatasetMode = DatasetMode.Indoor;
            activeSamplingMode = SamplingMode.Random;
            return;
        }

        if (MatchesDatasetFolder(indoorDataset, SamplingMode.Uniform))
        {
            activeDatasetMode = DatasetMode.Indoor;
            activeSamplingMode = SamplingMode.Uniform;
            return;
        }

        if (MatchesDatasetFolder(outdoorDataset, SamplingMode.Random))
        {
            activeDatasetMode = DatasetMode.Outdoor;
            activeSamplingMode = SamplingMode.Random;
            return;
        }

        if (MatchesDatasetFolder(outdoorDataset, SamplingMode.Uniform))
        {
            activeDatasetMode = DatasetMode.Outdoor;
            activeSamplingMode = SamplingMode.Uniform;
        }
    }

    private DatasetDefinition GetDatasetDefinition(DatasetMode mode)
    {
        return mode == DatasetMode.Indoor ? indoorDataset : outdoorDataset;
    }

    private string GetChunksFolderForSelection(DatasetDefinition dataset, SamplingMode samplingMode)
    {
        if (dataset == null)
            return "";

        string folder = samplingMode == SamplingMode.Uniform
            ? dataset.uniformChunksFolderName
            : dataset.randomChunksFolderName;

        return string.IsNullOrWhiteSpace(folder) ? dataset.chunksFolderName : folder;
    }

    private DatasetDefinition CreateDatasetFromCurrentSelection()
    {
        DatasetDefinition dataset = GetDatasetDefinition(activeDatasetMode);
        string samplingLabel = activeSamplingMode == SamplingMode.Uniform ? "Uniform" : "Random";

        return new DatasetDefinition
        {
            displayName = $"{dataset.displayName} / {samplingLabel}",
            chunksFolderName = GetChunksFolderForSelection(dataset, activeSamplingMode),
            randomChunksFolderName = dataset.randomChunksFolderName,
            uniformChunksFolderName = dataset.uniformChunksFolderName,
            lodIndexFileName = dataset.lodIndexFileName
        };
    }

    private void LoadLODIndex()
    {
        if (!TryLoadLODIndex(chunksFolderName, lodIndexFileName, out _lodIndex))
            return;

        ClampActiveLodLevelToIndex();
        Debug.Log($"[GaussianChunkManager] Loaded LOD index, chunks={_lodIndex.chunks.Count}");
    }

    private bool TryLoadLODIndex(string folderName, string indexFileName, out LODIndex lodIndex)
    {
        lodIndex = null;

        if (!TryResolveChunkDirectory(folderName, out var dir))
        {
            Debug.LogError($"[GaussianChunkManager] Chunk folder not found: {folderName}");
            return false;
        }

        var path = Path.Combine(dir, indexFileName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[GaussianChunkManager] LOD index file not found: {path}");
            return false;
        }

        var json = File.ReadAllText(path);
        lodIndex = JsonUtility.FromJson<LODIndex>(json);

        if (lodIndex == null || lodIndex.chunks == null)
        {
            Debug.LogError($"[GaussianChunkManager] Failed to parse LOD index JSON: {path}");
            lodIndex = null;
            return false;
        }

        _resolvedChunkDir = dir;
        return true;
    }

    private bool TryResolveChunkDirectory(string folderName, out string dir)
    {
        dir = "";

        if (string.IsNullOrWhiteSpace(folderName))
            return false;

        if (Path.IsPathRooted(folderName))
        {
            dir = Path.GetFullPath(folderName);
            return Directory.Exists(dir);
        }

        string streamingDir = Path.Combine(Application.streamingAssetsPath, folderName);
        if (Directory.Exists(streamingDir))
        {
            dir = streamingDir;
            return true;
        }

#if UNITY_EDITOR
        if (allowProjectDataFolderFallback)
        {
            string unityProjectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string repoRoot = string.IsNullOrEmpty(unityProjectRoot)
                ? ""
                : Directory.GetParent(unityProjectRoot)?.FullName;

            if (!string.IsNullOrEmpty(repoRoot))
            {
                string dataDir = Path.Combine(repoRoot, "data", folderName);
                if (Directory.Exists(dataDir))
                {
                    dir = dataDir;
                    return true;
                }
            }
        }
#endif

        dir = streamingDir;
        return false;
    }

    private Material GetGaussianFineMaterial()
    {
        if (pointMaterial == null)
            return null;

        if (_pointMaterialFineInstance == null)
            _pointMaterialFineInstance = new Material(pointMaterial);

        return _pointMaterialFineInstance;
    }

    private Material GetRawPointMaterial()
    {
        if (_rawPointMaterialInstance != null)
            return _rawPointMaterialInstance;

        if (rawPointMaterial != null)
        {
            _rawPointMaterialInstance = new Material(rawPointMaterial);
            return _rawPointMaterialInstance;
        }

        Shader rawShader = Shader.Find("Unlit/RawPointCloud");
        if (rawShader == null)
        {
            Debug.LogError("[GaussianChunkManager] Shader 'Unlit/RawPointCloud' not found.");
            return null;
        }

        _rawPointMaterialInstance = new Material(rawShader);
        return _rawPointMaterialInstance;
    }

    private void InitChunks()
    {
        if (_lodIndex == null || _lodIndex.chunks == null)
        {
            Debug.LogError("[GaussianChunkManager] LOD index not loaded.");
            return;
        }

        if (pointMaterial == null)
        {
            Debug.LogError("[GaussianChunkManager] pointMaterial is not assigned!");
            return;
        }

        ClearActiveChunks();

        _chunkRoot = new GameObject("GaussianChunksRoot").transform;
        _chunkRoot.SetParent(null, false);
        _chunkRoot.position = Vector3.zero;
        _chunkRoot.rotation = Quaternion.identity;
        _chunkRoot.localScale = Vector3.one;

        string chunkDir = string.IsNullOrEmpty(_resolvedChunkDir)
            ? Path.Combine(Application.streamingAssetsPath, chunksFolderName)
            : _resolvedChunkDir;

        ClampActiveLodLevelToIndex();

        foreach (var entry in _lodIndex.chunks)
        {
            if (entry == null)
                continue;

            int fineLod = activeLodLevel;
            string fineFile = GetLODFileName(entry, fineLod, fallbackToNearestAvailableLod);

            if (string.IsNullOrEmpty(fineFile))
                continue;

            string fullFine = Path.Combine(chunkDir, fineFile);

            if (!File.Exists(fullFine))
                continue;

            if (logChunkInfo)
                Debug.Log($"[GaussianChunkManager] {entry.filename ?? "chunk"}: L{fineLod}={fineFile}");

            string chunkName = $"Chunk_{entry.ijk[0]}_{entry.ijk[1]}_{entry.ijk[2]}";
            GameObject go = new GameObject(chunkName);
            go.transform.SetParent(_chunkRoot, false);

            var fine = go.AddComponent<GaussianLoader>();
            fine.treatInputAsWorldSpace = true;
            fine.dataFileName = fullFine;

            ApplyFineParams(fine);

            fine.enabled = false;

            _chunkLoaders.Add(fine);
            _chunkEntries.Add(entry);
            _chunkVisibility.Add(true);
            _isChunkLoaded.Add(false);
            _isFineLoaded.Add(false);
            _fineLodLevels.Add(fineLod);
        }

        _lastDisplayMode = currentDisplayMode;
        _lastActiveLodLevel = activeLodLevel;
    }

    private void ClearActiveChunks()
    {
        for (int i = 0; i < _chunkLoaders.Count; i++)
        {
            if (_chunkLoaders[i] != null)
            {
                _chunkLoaders[i].UnloadData();
                _chunkLoaders[i].enabled = false;
            }

        }

        if (_chunkRoot != null)
        {
            if (Application.isPlaying)
                Destroy(_chunkRoot.gameObject);
            else
                DestroyImmediate(_chunkRoot.gameObject);

            _chunkRoot = null;
        }

        _chunkLoaders.Clear();
        _chunkEntries.Clear();
        _chunkVisibility.Clear();
        _isChunkLoaded.Clear();
        _isFineLoaded.Clear();
        _fineLodLevels.Clear();
    }

    private void ConfigureLoaderForCurrentMode(GaussianLoader loader)
    {
        if (loader == null)
            return;

        Material baseMaterial;
        bool drawAsSplat;
        bool useOIT;

        if (UseGaussianSplatMode)
        {
            baseMaterial = GetGaussianFineMaterial();
            drawAsSplat = renderAsSplatQuads;
            useOIT = renderAsSplatQuads;
        }
        else
        {
            baseMaterial = GetRawPointMaterial();
            drawAsSplat = false;
            useOIT = false;
        }

        if (baseMaterial != null)
            loader.ConfigureRendering(baseMaterial, drawAsSplat, useOIT);
    }

    private float GetPointSizeForLod(int lodLevel)
    {
        switch (lodLevel)
        {
            case 0: return pointSizeL0;
            case 1: return pointSizeL1;
            case 2: return pointSizeL2;
            case 3: return pointSizeL3;
            case 4: return pointSizeL4;
            default: return pointSizeL0;
        }
    }

    private float GetOpacityForLod(int lodLevel)
    {
        switch (lodLevel)
        {
            case 0: return opacityL0;
            case 1: return opacityL1;
            case 2: return opacityL2;
            case 3: return opacityL3;
            case 4: return opacityL4;
            default: return opacityL0;
        }
    }

    private float GetSigmaCutoffForLod(int lodLevel)
    {
        switch (lodLevel)
        {
            case 0: return sigmaCutoffL0;
            case 1: return sigmaCutoffL1;
            case 2: return sigmaCutoffL2;
            case 3: return sigmaCutoffL3;
            case 4: return sigmaCutoffL4;
            default: return sigmaCutoffL0;
        }
    }

    private void ApplyFineParams(GaussianLoader loader)
    {
        if (loader == null) return;

        ConfigureLoaderForCurrentMode(loader);

        if (UseGaussianSplatMode)
        {
            int lodLevel = Mathf.Clamp(activeLodLevel, 0, 4);
            loader.pointSize = GetPointSizeForLod(lodLevel);
            loader.opacity = GetOpacityForLod(lodLevel);
            loader.sigmaCutoff = GetSigmaCutoffForLod(lodLevel);
            loader.minAxisPixels = minAxisPixels;
            loader.maxAxisPixels = maxAxisPixels;
            loader.pointSizeMultiplier = 1.0f;
            loader.opacityMultiplier = 1.0f;
            loader.enableViewZFade = false;
            loader.invertViewZFade = false;
        }
        else
        {
            loader.pointSize = rawPointSize;
            loader.opacity = 1.0f;
            loader.sigmaCutoff = 1.0f;
            loader.minAxisPixels = 0.0f;
            loader.maxAxisPixels = 0.0f;
            loader.pointSizeMultiplier = 1.0f;
            loader.opacityMultiplier = 1.0f;
            loader.enableViewZFade = false;
            loader.invertViewZFade = false;
        }
    }

    private bool RenderParamsChanged()
    {
        if (_lastDisplayMode != currentDisplayMode) return true;
        if (_lastRenderAsSplatQuads != renderAsSplatQuads) return true;
        if (!Mathf.Approximately(_lastPointSizeL0, pointSizeL0) || !Mathf.Approximately(_lastPointSizeL1, pointSizeL1)) return true;
        if (!Mathf.Approximately(_lastPointSizeL2, pointSizeL2) || !Mathf.Approximately(_lastPointSizeL3, pointSizeL3) || !Mathf.Approximately(_lastPointSizeL4, pointSizeL4)) return true;
        if (!Mathf.Approximately(_lastOpacityL0, opacityL0) || !Mathf.Approximately(_lastOpacityL1, opacityL1)) return true;
        if (!Mathf.Approximately(_lastOpacityL2, opacityL2) || !Mathf.Approximately(_lastOpacityL3, opacityL3) || !Mathf.Approximately(_lastOpacityL4, opacityL4)) return true;
        if (!Mathf.Approximately(_lastSigmaL0, sigmaCutoffL0) || !Mathf.Approximately(_lastSigmaL1, sigmaCutoffL1)) return true;
        if (!Mathf.Approximately(_lastSigmaL2, sigmaCutoffL2) || !Mathf.Approximately(_lastSigmaL3, sigmaCutoffL3) || !Mathf.Approximately(_lastSigmaL4, sigmaCutoffL4)) return true;
        if (!Mathf.Approximately(_lastMinAxisPx, minAxisPixels) || !Mathf.Approximately(_lastMaxAxisPx, maxAxisPixels)) return true;
        if (!Mathf.Approximately(_lastRawPointSize, rawPointSize)) return true;

        return false;
    }

    private void CacheRenderParams()
    {
        _lastDisplayMode = currentDisplayMode;
        _lastRenderAsSplatQuads = renderAsSplatQuads;
        _lastActiveLodLevel = activeLodLevel;
        _lastPointSizeL0 = pointSizeL0;
        _lastPointSizeL1 = pointSizeL1;
        _lastPointSizeL2 = pointSizeL2;
        _lastPointSizeL3 = pointSizeL3;
        _lastPointSizeL4 = pointSizeL4;
        _lastOpacityL0 = opacityL0;
        _lastOpacityL1 = opacityL1;
        _lastOpacityL2 = opacityL2;
        _lastOpacityL3 = opacityL3;
        _lastOpacityL4 = opacityL4;
        _lastSigmaL0 = sigmaCutoffL0;
        _lastSigmaL1 = sigmaCutoffL1;
        _lastSigmaL2 = sigmaCutoffL2;
        _lastSigmaL3 = sigmaCutoffL3;
        _lastSigmaL4 = sigmaCutoffL4;
        _lastMinAxisPx = minAxisPixels;
        _lastMaxAxisPx = maxAxisPixels;
        _lastRawPointSize = rawPointSize;
    }

    private void ApplyParamsToAllLoaders()
    {
        for (int i = 0; i < _chunkLoaders.Count; i++)
            ApplyFineParams(_chunkLoaders[i]);

        CacheRenderParams();
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        if (currentDisplayMode == mode)
            return;

        currentDisplayMode = mode;
        ApplyParamsToAllLoaders();
    }

    public void SwitchToIndoorDataset()
    {
        NormalizeDatasetSettings();
        activeDatasetMode = DatasetMode.Indoor;
        SwitchDataset(CreateDatasetFromCurrentSelection(), recenterCamera: true);
    }

    public void SwitchToOutdoorDataset()
    {
        NormalizeDatasetSettings();
        activeDatasetMode = DatasetMode.Outdoor;
        SwitchDataset(CreateDatasetFromCurrentSelection(), recenterCamera: true);
    }

    public void SwitchToRandomSampling()
    {
        NormalizeDatasetSettings();
        activeSamplingMode = SamplingMode.Random;
        SwitchDataset(CreateDatasetFromCurrentSelection(), recenterCamera: false);
    }

    public void SwitchToUniformSampling()
    {
        NormalizeDatasetSettings();
        activeSamplingMode = SamplingMode.Uniform;
        SwitchDataset(CreateDatasetFromCurrentSelection(), recenterCamera: false);
    }

    [ContextMenu("Use Indoor Data")]
    private void UseIndoorDatasetFromInspector()
    {
        NormalizeDatasetSettings();
        activeDatasetMode = DatasetMode.Indoor;
        SelectCurrentDatasetFromInspector(recenterCamera: true);
    }

    [ContextMenu("Use Outdoor Data")]
    private void UseOutdoorDatasetFromInspector()
    {
        NormalizeDatasetSettings();
        activeDatasetMode = DatasetMode.Outdoor;
        SelectCurrentDatasetFromInspector(recenterCamera: true);
    }

    [ContextMenu("Use Random Sampling")]
    private void UseRandomSamplingFromInspector()
    {
        NormalizeDatasetSettings();
        activeSamplingMode = SamplingMode.Random;
        SelectCurrentDatasetFromInspector(recenterCamera: false);
    }

    [ContextMenu("Use Uniform Sampling")]
    private void UseUniformSamplingFromInspector()
    {
        NormalizeDatasetSettings();
        activeSamplingMode = SamplingMode.Uniform;
        SelectCurrentDatasetFromInspector(recenterCamera: false);
    }

    private void SelectCurrentDatasetFromInspector(bool recenterCamera)
    {
        DatasetDefinition dataset = CreateDatasetFromCurrentSelection();
        chunksFolderName = dataset.chunksFolderName;
        lodIndexFileName = dataset.lodIndexFileName;

        if (Application.isPlaying)
            SwitchDataset(dataset, recenterCamera);
    }

    public void SetGaussianSplatMode()
    {
        SetDisplayMode(DisplayMode.GaussianSplat);
    }

    public void SetRawPointCloudMode()
    {
        SetDisplayMode(DisplayMode.RawPointCloud);
    }

    public void SetActiveLODLevel(int lodLevel)
    {
        if (_lodIndex == null)
        {
            activeLodLevel = Mathf.Clamp(lodLevel, 0, 4);
            _lastActiveLodLevel = activeLodLevel;
            return;
        }

        int clamped = ClampLodLevelToIndex(lodLevel);
        if (activeLodLevel == clamped && _lastActiveLodLevel == clamped)
            return;

        activeLodLevel = clamped;
        RebindLODFilesForAllChunks();
        ApplyParamsToAllLoaders();
        _lastActiveLodLevel = activeLodLevel;

        if (logLODChanges)
            Debug.Log($"[GaussianChunkManager] Active LOD switched to L{activeLodLevel}");
    }

    public void SetLOD0() => SetActiveLODLevel(0);
    public void SetLOD1() => SetActiveLODLevel(1);
    public void SetLOD2() => SetActiveLODLevel(2);
    public void SetLOD3() => SetActiveLODLevel(3);
    public void SetLOD4() => SetActiveLODLevel(4);

    public void NextLODLevel()
    {
        SetActiveLODLevel(activeLodLevel + 1);
    }

    public void PreviousLODLevel()
    {
        SetActiveLODLevel(activeLodLevel - 1);
    }

    public void ReloadCurrentDataset()
    {
        ReloadCurrentDataset(recenterCamera: true);
    }

    public void ReloadCurrentDataset(bool recenterCamera)
    {
        NormalizeDatasetSettings();

        var currentDataset = new DatasetDefinition
        {
            displayName = CurrentDatasetLabel,
            chunksFolderName = chunksFolderName,
            lodIndexFileName = lodIndexFileName
        };

        SwitchDataset(currentDataset, recenterCamera);
    }

    public void RecenterToCurrentDataset()
    {
        if (!recenterCameraOnDatasetSwitch)
        {
            Debug.LogWarning("[GaussianChunkManager] recenterCameraOnDatasetSwitch is disabled.");
            return;
        }

        if (_lodIndex == null)
        {
            if (!TryLoadLODIndex(chunksFolderName, lodIndexFileName, out _lodIndex))
                return;
        }

        RecenterMainCameraToDataset(_lodIndex);
    }

    private void SwitchDataset(DatasetDefinition dataset, bool recenterCamera)
    {
        if (dataset == null)
        {
            Debug.LogError("[GaussianChunkManager] Dataset definition is null.");
            return;
        }

        if (string.IsNullOrWhiteSpace(dataset.chunksFolderName) || string.IsNullOrWhiteSpace(dataset.lodIndexFileName))
        {
            Debug.LogError("[GaussianChunkManager] Dataset folder or LOD index file is empty.");
            return;
        }

        if (!TryLoadLODIndex(dataset.chunksFolderName, dataset.lodIndexFileName, out var nextLodIndex))
            return;

        chunksFolderName = dataset.chunksFolderName;
        lodIndexFileName = dataset.lodIndexFileName;
        _lodIndex = nextLodIndex;

        InitChunks();
        ApplyParamsToAllLoaders();

        if (recenterCamera && recenterCameraOnDatasetSwitch)
            RecenterMainCameraToDataset(_lodIndex);

        Debug.Log($"[GaussianChunkManager] Switched dataset to {dataset.displayName} ({dataset.chunksFolderName})");
    }

    private void HandleModeToggleInput()
    {
        if (disableKeyboardToggleWhenXRActive && IsXRSceneActive())
            return;

        if (!allowKeyboardToggle || Keyboard.current == null)
            return;

        if (Keyboard.current.tabKey.wasPressedThisFrame)
        {
            SetDisplayMode(UseGaussianSplatMode ? DisplayMode.RawPointCloud : DisplayMode.GaussianSplat);
        }
    }

    private void HandleLodSwitchInput()
    {
        if (disableKeyboardToggleWhenXRActive && IsXRSceneActive())
            return;

        if (!allowKeyboardLodSwitch || Keyboard.current == null)
            return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame) SetActiveLODLevel(0);
        if (Keyboard.current.digit2Key.wasPressedThisFrame) SetActiveLODLevel(1);
        if (Keyboard.current.digit3Key.wasPressedThisFrame) SetActiveLODLevel(2);
        if (Keyboard.current.digit4Key.wasPressedThisFrame) SetActiveLODLevel(3);
        if (Keyboard.current.digit5Key.wasPressedThisFrame) SetActiveLODLevel(4);
    }

    private void HandleLodInspectorChanges()
    {
        if (_lodIndex == null)
            return;

        int clamped = ClampLodLevelToIndex(activeLodLevel);
        if (activeLodLevel != clamped)
            activeLodLevel = clamped;

        if (_lastActiveLodLevel == activeLodLevel)
            return;

        RebindLODFilesForAllChunks();
        ApplyParamsToAllLoaders();
        _lastActiveLodLevel = activeLodLevel;

        if (logLODChanges)
            Debug.Log($"[GaussianChunkManager] Active LOD changed to L{activeLodLevel}");
    }

    private bool IsChunkVisible(Plane[] planes, Vector3 bmin, Vector3 bmax)
    {
        foreach (var p in planes)
        {
            Vector3 v = new Vector3(
                p.normal.x >= 0 ? bmax.x : bmin.x,
                p.normal.y >= 0 ? bmax.y : bmin.y,
                p.normal.z >= 0 ? bmax.z : bmin.z
            );

            if (Vector3.Dot(p.normal, v) + p.distance < 0)
                return false;
        }
        return true;
    }

    private static float DistancePointToAABB(Vector3 p, Vector3 bmin, Vector3 bmax)
    {
        float dx = 0f;
        if (p.x < bmin.x) dx = bmin.x - p.x;
        else if (p.x > bmax.x) dx = p.x - bmax.x;

        float dy = 0f;
        if (p.y < bmin.y) dy = bmin.y - p.y;
        else if (p.y > bmax.y) dy = p.y - bmax.y;

        float dz = 0f;
        if (p.z < bmin.z) dz = bmin.z - p.z;
        else if (p.z > bmax.z) dz = p.z - bmax.z;

        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private int[] GetAvailableLodLevels()
    {
        if (_lodIndex != null && _lodIndex.lod_levels != null && _lodIndex.lod_levels.Length > 0)
            return _lodIndex.lod_levels;

        return new[] { 0 };
    }

    public string GetLODLayerInfoLabel(int lodLevel)
    {
        GaussianLODManifestLevel level = GetManifestLevel(lodLevel);
        int count = GetLODPointCount(lodLevel, level);
        string pointCount = FormatPointCount(count);

        if (IsUniformLod(level))
        {
            string resolution = GetSamplingParameterLabel(level);
            return string.IsNullOrEmpty(resolution)
                ? $"L{lodLevel}: {pointCount} points"
                : $"L{lodLevel}: {resolution}, {pointCount} points";
        }

        return $"L{lodLevel}: {pointCount} points";
    }

    private GaussianLODManifestLevel GetManifestLevel(int lodLevel)
    {
        var levels = _lodIndex?.gaussian_lod_manifest?.levels;
        if (levels == null)
            return null;

        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] != null && levels[i].level == lodLevel)
                return levels[i];
        }

        return null;
    }

    private bool IsUniformLod(GaussianLODManifestLevel level)
    {
        if (level != null && !string.IsNullOrEmpty(level.sampling_method))
            return string.Equals(level.sampling_method, "uniform", StringComparison.OrdinalIgnoreCase);

        return activeSamplingMode == SamplingMode.Uniform;
    }

    private string GetSamplingParameterLabel(GaussianLODManifestLevel level)
    {
        if (level == null)
            return "";

        if (!string.IsNullOrWhiteSpace(level.sampling_parameter_label))
            return level.sampling_parameter_label;

        if (string.Equals(level.sampling_parameter_name, "resolution_m", StringComparison.OrdinalIgnoreCase))
            return FormatResolutionMeters(level.sampling_parameter_value);

        if (string.Equals(level.sampling_parameter_name, "point_count", StringComparison.OrdinalIgnoreCase))
            return FormatPointCount(Mathf.RoundToInt(level.sampling_parameter_value));

        return "";
    }

    private int GetLODPointCount(int lodLevel, GaussianLODManifestLevel level)
    {
        if (level != null && level.num_points > 0)
            return level.num_points;

        if (_lodIndex == null || _lodIndex.chunks == null)
            return 0;

        int arrayIndex = GetLodArrayIndex(lodLevel);
        long total = 0;

        for (int i = 0; i < _lodIndex.chunks.Count; i++)
        {
            ChunkEntry chunk = _lodIndex.chunks[i];
            if (chunk == null)
                continue;

            if (chunk.lod_counts != null && arrayIndex >= 0 && arrayIndex < chunk.lod_counts.Length)
                total += Mathf.Max(0, chunk.lod_counts[arrayIndex]);
            else if (lodLevel == 0)
                total += Mathf.Max(0, chunk.count);
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private static string FormatPointCount(int count)
    {
        if (count <= 0)
            return "0";

        if (count >= 1000000)
            return $"{count / 1000000f:0.##}M";

        if (count >= 1000)
            return $"{count / 1000f:0.#}K";

        return count.ToString();
    }

    private static string FormatResolutionMeters(float meters)
    {
        if (meters <= 0f)
            return "";

        if (meters < 1f)
            return $"{meters * 100f:0.##}cm";

        return $"{meters:0.##}m";
    }

    private int ClampLodLevelToIndex(int lodLevel)
    {
        int[] levels = GetAvailableLodLevels();
        if (levels == null || levels.Length == 0)
            return Mathf.Clamp(lodLevel, 0, 4);

        int nearest = levels[0];
        int bestDistance = Mathf.Abs(lodLevel - nearest);
        for (int i = 1; i < levels.Length; i++)
        {
            int distance = Mathf.Abs(lodLevel - levels[i]);
            if (distance < bestDistance)
            {
                nearest = levels[i];
                bestDistance = distance;
            }
        }

        return nearest;
    }

    private void ClampActiveLodLevelToIndex()
    {
        activeLodLevel = ClampLodLevelToIndex(activeLodLevel);
        _lastActiveLodLevel = activeLodLevel;
    }

    private LODLevel GetExplicitLODLevel(ChunkEntry entry, int lodLevel)
    {
        if (entry == null || entry.lod == null)
            return null;

        switch (lodLevel)
        {
            case 0: return entry.lod.L0;
            case 1: return entry.lod.L1;
            case 2: return entry.lod.L2;
            case 3: return entry.lod.L3;
            case 4: return entry.lod.L4;
            default: return null;
        }
    }

    private int GetLodArrayIndex(int lodLevel)
    {
        if (_lodIndex == null || _lodIndex.lod_levels == null || _lodIndex.lod_levels.Length == 0)
            return lodLevel;

        for (int i = 0; i < _lodIndex.lod_levels.Length; i++)
        {
            if (_lodIndex.lod_levels[i] == lodLevel)
                return i;
        }

        return -1;
    }

    private string GetDirectLODFileName(ChunkEntry entry, int lodLevel)
    {
        if (entry == null)
            return "";

        int arrayIndex = GetLodArrayIndex(lodLevel);
        if (entry.lod_files != null && arrayIndex >= 0 && arrayIndex < entry.lod_files.Length)
        {
            string file = entry.lod_files[arrayIndex];
            if (!string.IsNullOrEmpty(file))
                return file;
        }

        if (entry.lod != null)
        {
            LODLevel level = GetExplicitLODLevel(entry, lodLevel);
            if (level != null && !string.IsNullOrEmpty(level.filename))
                return level.filename;
        }

        if (lodLevel == 0 && !string.IsNullOrEmpty(entry.filename))
            return entry.filename;

        return "";
    }

    private string GetLODFileName(ChunkEntry entry, int lodLevel, bool allowFallback)
    {
        string direct = GetDirectLODFileName(entry, lodLevel);
        if (!string.IsNullOrEmpty(direct) || !allowFallback)
            return direct;

        int[] levels = GetAvailableLodLevels();
        string bestFile = "";
        int bestDistance = int.MaxValue;

        if (levels != null)
        {
            for (int i = 0; i < levels.Length; i++)
            {
                string candidate = GetDirectLODFileName(entry, levels[i]);
                if (string.IsNullOrEmpty(candidate))
                    continue;

                int distance = Mathf.Abs(lodLevel - levels[i]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFile = candidate;
                }
            }
        }

        if (!string.IsNullOrEmpty(bestFile))
            return bestFile;

        return GetDirectLODFileName(entry, 0);
    }

    private void RebindLODFilesForAllChunks()
    {
        if (_lodIndex == null || _chunkLoaders.Count == 0)
            return;

        string chunkDir = string.IsNullOrEmpty(_resolvedChunkDir)
            ? Path.Combine(Application.streamingAssetsPath, chunksFolderName)
            : _resolvedChunkDir;

        for (int i = 0; i < _chunkLoaders.Count; i++)
        {
            GaussianLoader fine = _chunkLoaders[i];
            ChunkEntry entry = _chunkEntries[i];

            if (fine == null || entry == null)
                continue;

            if (_isFineLoaded[i])
            {
                fine.UnloadData();
                _isFineLoaded[i] = false;
            }

            string fineFile = GetLODFileName(entry, activeLodLevel, fallbackToNearestAvailableLod);
            fine.dataFileName = string.IsNullOrEmpty(fineFile) ? "" : Path.Combine(chunkDir, fineFile);
            fine.enabled = false;
            _fineLodLevels[i] = activeLodLevel;

            _isChunkLoaded[i] = false;
        }
    }

    private string GetDatasetLabelForCurrentSelection()
    {
        if (MatchesCurrentSelection())
            return CreateDatasetFromCurrentSelection().displayName;

        if (MatchesDatasetFolder(indoorDataset, SamplingMode.Random))
            return $"{indoorDataset.displayName} / Random";

        if (MatchesDatasetFolder(indoorDataset, SamplingMode.Uniform))
            return $"{indoorDataset.displayName} / Uniform";

        if (MatchesDatasetFolder(outdoorDataset, SamplingMode.Random))
            return $"{outdoorDataset.displayName} / Random";

        if (MatchesDatasetFolder(outdoorDataset, SamplingMode.Uniform))
            return $"{outdoorDataset.displayName} / Uniform";

        return chunksFolderName;
    }

    public bool IsDataModeSelected(DatasetMode mode)
    {
        return activeDatasetMode == mode && MatchesCurrentSelection();
    }

    public bool IsSamplingModeSelected(SamplingMode mode)
    {
        return activeSamplingMode == mode && MatchesCurrentSelection();
    }

    public bool MatchesDataset(DatasetDefinition dataset)
    {
        if (dataset == null)
            return false;

        return (MatchesDatasetFolder(dataset, SamplingMode.Random) || MatchesDatasetFolder(dataset, SamplingMode.Uniform))
               && string.Equals(lodIndexFileName, dataset.lodIndexFileName, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesSampling(SamplingMode samplingMode)
    {
        return activeSamplingMode == samplingMode && MatchesCurrentSelection();
    }

    private bool MatchesCurrentSelection()
    {
        DatasetDefinition dataset = GetDatasetDefinition(activeDatasetMode);
        return MatchesDatasetFolder(dataset, activeSamplingMode)
               && string.Equals(lodIndexFileName, dataset.lodIndexFileName, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesDatasetFolder(DatasetDefinition dataset, SamplingMode samplingMode)
    {
        if (dataset == null)
            return false;

        string folder = GetChunksFolderForSelection(dataset, samplingMode);
        return string.Equals(chunksFolderName, folder, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetDatasetBounds(LODIndex lodIndex, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        boundsMin = Vector3.zero;
        boundsMax = Vector3.zero;

        if (lodIndex == null || lodIndex.chunks == null || lodIndex.chunks.Count == 0)
            return false;

        bool initialized = false;

        foreach (var chunk in lodIndex.chunks)
        {
            if (chunk == null || chunk.bbox_min == null || chunk.bbox_max == null ||
                chunk.bbox_min.Length < 3 || chunk.bbox_max.Length < 3)
            {
                continue;
            }

            Vector3 bmin = new Vector3(chunk.bbox_min[0], chunk.bbox_min[1], chunk.bbox_min[2]);
            Vector3 bmax = new Vector3(chunk.bbox_max[0], chunk.bbox_max[1], chunk.bbox_max[2]);

            if (!initialized)
            {
                boundsMin = bmin;
                boundsMax = bmax;
                initialized = true;
                continue;
            }

            boundsMin = Vector3.Min(boundsMin, bmin);
            boundsMax = Vector3.Max(boundsMax, bmax);
        }

        return initialized;
    }

    private void RecenterMainCameraToDataset(LODIndex lodIndex)
    {
        var mainCamera = Camera.main;
        if (mainCamera == null)
            return;

        if (!TryGetDatasetBounds(lodIndex, out var boundsMin, out var boundsMax))
            return;

        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        Vector3 size = boundsMax - boundsMin;

        float horizontalExtent = Mathf.Max(size.x, size.z) * 0.5f;
        float cameraDistance = Mathf.Max(5f, horizontalExtent + Mathf.Max(0f, cameraDistancePadding));
        bool hasXRRoot = TryGetXRRecenterRoot(mainCamera, out Transform xrRoot);

        Vector3 targetPosition;
        Quaternion targetRotation;

        if (hasXRRoot && useDatasetFloorHeightForXRRecenter)
        {
            float floorY = boundsMin.y;
            float eyeY = floorY + Mathf.Max(0f, xrStandingEyeHeight) + xrAdditionalHeightOffset;
            Vector3 centerPlacement = new Vector3(
                center.x + xrPlanarOffsetFromCenter.x,
                eyeY,
                center.z + xrPlanarOffsetFromCenter.y);

            targetPosition = xrPlaceUserNearDatasetCenter
                ? centerPlacement
                : new Vector3(center.x, eyeY, center.z - cameraDistance);

            if (xrKeepLevelViewOnRecenter)
            {
                targetRotation = GetCurrentLevelFacing(mainCamera, xrRoot);
            }
            else
            {
                Vector3 lookTarget = new Vector3(center.x, eyeY, center.z);
                Vector3 lookDirection = lookTarget - targetPosition;
                if (lookDirection.sqrMagnitude < 1e-4f)
                    targetRotation = GetCurrentLevelFacing(mainCamera, xrRoot);
                else
                    targetRotation = Quaternion.LookRotation(lookDirection, Vector3.up);
            }
        }
        else
        {
            float cameraHeight = Mathf.Max(2f, size.y * 0.35f + Mathf.Max(0f, cameraHeightPadding));
            targetPosition = center + new Vector3(0f, cameraHeight, -cameraDistance);
            targetRotation = Quaternion.LookRotation(center - targetPosition, Vector3.up);
        }

        if (hasXRRoot)
        {
            Vector3 localCameraPosition = xrRoot.InverseTransformPoint(mainCamera.transform.position);
            Quaternion localCameraRotation = Quaternion.Inverse(xrRoot.rotation) * mainCamera.transform.rotation;

            Quaternion desiredRigRotation = targetRotation * Quaternion.Inverse(localCameraRotation);
            Vector3 desiredRigPosition = targetPosition - (desiredRigRotation * localCameraPosition);

            xrRoot.SetPositionAndRotation(desiredRigPosition, desiredRigRotation);
            return;
        }

        mainCamera.transform.SetPositionAndRotation(targetPosition, targetRotation);
    }

    private Quaternion GetCurrentLevelFacing(Camera mainCamera, Transform xrRoot)
    {
        Vector3 forward = mainCamera != null ? mainCamera.transform.forward : Vector3.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 1e-4f && xrRoot != null)
        {
            forward = xrRoot.forward;
            forward.y = 0f;
        }

        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;

        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    private bool IsXRSceneActive()
    {
        if (!autoDetectXRScene)
            return xrRigRootOverride != null;

        if (xrRigRootOverride != null)
            return true;

        if (_xrOrigin == null)
            _xrOrigin = FindFirstObjectByType<XROrigin>();

        return _xrOrigin != null;
    }

    private bool TryGetXRRecenterRoot(Camera mainCamera, out Transform xrRoot)
    {
        xrRoot = null;

        if (!IsXRSceneActive())
            return false;

        if (xrRigRootOverride != null)
        {
            xrRoot = xrRigRootOverride;
            return true;
        }

        if (_xrOrigin == null)
            _xrOrigin = FindFirstObjectByType<XROrigin>();

        if (_xrOrigin != null)
        {
            xrRoot = _xrOrigin.transform;
            return true;
        }

        if (mainCamera != null)
        {
            _xrOrigin = mainCamera.GetComponentInParent<XROrigin>();
            if (_xrOrigin != null)
            {
                xrRoot = _xrOrigin.transform;
                return true;
            }
        }

        return false;
    }

    private void Update()
    {
        HandleModeToggleInput();
        HandleLodSwitchInput();
        HandleLodInspectorChanges();

        if (RenderParamsChanged())
            ApplyParamsToAllLoaders();

        if (Camera.main == null || _lodIndex == null)
            return;

        int reloadsThisFrame = 0;

        GeometryUtility.CalculateFrustumPlanes(Camera.main, _frustumPlanes);
        Vector3 camPos = Camera.main.transform.position;

        if (unloadRadius < loadRadius)
            unloadRadius = loadRadius + 1f;

        for (int i = 0; i < _chunkLoaders.Count; i++)
        {
            var fine = _chunkLoaders[i];
            var entry = _chunkEntries[i];

            if (fine == null || entry == null || entry.bbox_min == null || entry.bbox_max == null)
                continue;

            Vector3 bmin = new Vector3(entry.bbox_min[0], entry.bbox_min[1], entry.bbox_min[2]);
            Vector3 bmax = new Vector3(entry.bbox_max[0], entry.bbox_max[1], entry.bbox_max[2]);

            bool visible = IsChunkVisible(_frustumPlanes, bmin, bmax);

            if (logCullingChanges && _chunkVisibility[i] != visible)
            {
                _chunkVisibility[i] = visible;
                Debug.Log($"[ChunkCulling] {fine.gameObject.name} visible = {visible}");
            }

            Vector3 center = (entry.center != null && entry.center.Length >= 3)
                ? new Vector3(entry.center[0], entry.center[1], entry.center[2])
                : (bmin + bmax) * 0.5f;

            float distCenter = Vector3.Distance(camPos, center);
            float distAabb = DistancePointToAABB(camPos, bmin, bmax);

            bool shouldLoadChunk = distAabb < loadRadius;
            bool shouldUnloadChunk = distAabb > unloadRadius;

            if (_isChunkLoaded[i] && shouldUnloadChunk)
            {
                if (_isFineLoaded[i]) { fine.UnloadData(); _isFineLoaded[i] = false; }

                _isChunkLoaded[i] = false;
                fine.enabled = false;

                if (logStreamingChanges)
                    Debug.Log($"[Streaming] Unload {fine.gameObject.name} (aabb={distAabb:F1}m, center={distCenter:F1}m)");

                continue;
            }

            if (!shouldLoadChunk && !_isChunkLoaded[i])
            {
                fine.enabled = false;
                continue;
            }

            bool hasFineFile = !string.IsNullOrEmpty(fine.dataFileName);
            bool wantFine = hasFineFile;

            if (!_isChunkLoaded[i] && shouldLoadChunk)
                _isChunkLoaded[i] = true;

            if (wantFine && !_isFineLoaded[i] && reloadsThisFrame < Mathf.Max(0, maxReloadsPerFrame))
            {
                ApplyFineParams(fine);
                fine.ReloadData();
                _isFineLoaded[i] = true;
                reloadsThisFrame++;

                if (logStreamingChanges)
                    Debug.Log($"[Streaming] Load {fine.gameObject.name} (center={distCenter:F1}m)");
            }
            else if (!wantFine && _isFineLoaded[i])
            {
                fine.UnloadData();
                _isFineLoaded[i] = false;

                if (logStreamingChanges)
                    Debug.Log($"[Streaming] Unload {fine.gameObject.name} (center={distCenter:F1}m)");
            }

            fine.enabled = visible && _isFineLoaded[i];

            if (_isFineLoaded[i]) ApplyFineParams(fine);
        }
    }

    private void EnsureGUIStyles()
    {
        if (_panelStyle != null)
            return;

        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 16,
            padding = new RectOffset(14, 14, 10, 10)
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 15,
            fixedHeight = 44
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            wordWrap = true
        };
    }

    private void OnGUI()
    {
        if (hideLegacyOnGUIWhenXRActive && IsXRSceneActive())
            return;

        if (!showModeSwitcherUI && !showDatasetSwitcherUI && !showSamplingSwitcherUI && !showLodSwitcherUI)
            return;

        EnsureGUIStyles();

        float panelHeight = 24f;
        if (showDatasetSwitcherUI)
            panelHeight += 128f;
        if (showSamplingSwitcherUI)
            panelHeight += 128f;
        if (showModeSwitcherUI)
            panelHeight += allowKeyboardToggle ? 150f : 124f;
        if (showLodSwitcherUI)
            panelHeight += allowKeyboardLodSwitch ? 206f : 180f;

        Rect panelRect = new Rect(16, 16, 320, panelHeight);
        GUILayout.BeginArea(panelRect, _panelStyle);

        if (showDatasetSwitcherUI)
        {
            GUILayout.Label("Scene Dataset", _labelStyle);
            GUILayout.Label($"Current: {CurrentDatasetLabel}", _labelStyle);

            bool previousEnabled = GUI.enabled;

            GUI.enabled = !IsDataModeSelected(DatasetMode.Indoor);
            if (GUILayout.Button(indoorDataset.displayName, _buttonStyle))
                SwitchToIndoorDataset();

            GUI.enabled = !IsDataModeSelected(DatasetMode.Outdoor);
            if (GUILayout.Button(outdoorDataset.displayName, _buttonStyle))
                SwitchToOutdoorDataset();

            GUI.enabled = previousEnabled;
            GUILayout.Space(8f);
        }

        if (showSamplingSwitcherUI)
        {
            GUILayout.Label("Sampling", _labelStyle);
            GUILayout.Label($"Current: {(activeSamplingMode == SamplingMode.Uniform ? "Uniform" : "Random")}", _labelStyle);

            bool previousEnabled = GUI.enabled;

            GUI.enabled = !IsSamplingModeSelected(SamplingMode.Random);
            if (GUILayout.Button("Random", _buttonStyle))
                SwitchToRandomSampling();

            GUI.enabled = !IsSamplingModeSelected(SamplingMode.Uniform);
            if (GUILayout.Button("Uniform", _buttonStyle))
                SwitchToUniformSampling();

            GUI.enabled = previousEnabled;
            GUILayout.Space(8f);
        }

        if (showModeSwitcherUI)
        {
            GUILayout.Label("Display Mode", _labelStyle);
            GUILayout.Label($"Current: {(UseGaussianSplatMode ? "Gaussian Splat" : "Raw Point Cloud")}", _labelStyle);

            if (GUILayout.Button("Gaussian Splat View", _buttonStyle))
                SetDisplayMode(DisplayMode.GaussianSplat);

            if (GUILayout.Button("Raw Point Cloud View", _buttonStyle))
                SetDisplayMode(DisplayMode.RawPointCloud);

            if (allowKeyboardToggle)
                GUILayout.Label("Press Tab to switch view.", _labelStyle);
        }

        if (showLodSwitcherUI)
        {
            GUILayout.Space(8f);
            GUILayout.Label("LOD Layer", _labelStyle);
            GUILayout.Label($"Current: L{activeLodLevel}", _labelStyle);
            GUILayout.Label(GetLODLayerInfoLabel(activeLodLevel), _labelStyle);

            int[] levels = GetAvailableLodLevels();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < levels.Length; i++)
            {
                int lod = levels[i];
                bool previousEnabled = GUI.enabled;
                GUI.enabled = activeLodLevel != lod;
                if (GUILayout.Button($"L{lod}", _buttonStyle))
                    SetActiveLODLevel(lod);
                GUI.enabled = previousEnabled;
            }
            GUILayout.EndHorizontal();

            if (allowKeyboardLodSwitch)
                GUILayout.Label("Press 1-5 to switch LOD.", _labelStyle);
        }

        GUILayout.EndArea();
    }
}
