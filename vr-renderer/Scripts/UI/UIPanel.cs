using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class UIPanel : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private GaussianChunkManager chunkManager;

    [Header("Panel Layout")]
    [SerializeField] private bool startExpanded = true;
    [SerializeField] private bool showInitialRecenterButton = true;
    [SerializeField] private string initialRecenterButtonText = "Recenter";
    [SerializeField] private Vector2 initialRecenterButtonSize = new Vector2(220f, 56f);
    [SerializeField] private bool centerPanelOnScreen = true;
    [SerializeField] private Vector2 panelAnchoredPosition = Vector2.zero;
    [SerializeField] private bool placeCollapsedPanelAtTop = true;
    [SerializeField] private Vector2 collapsedPanelAnchoredPosition = new Vector2(0f, -24f);
    [SerializeField] private Vector2 expandedPanelSize = new Vector2(380f, 500f);
    [SerializeField] private Vector2 collapsedPanelSize = new Vector2(220f, 52f);

    [Header("Colors")]
    [SerializeField] private Color panelColor = new Color(0.07f, 0.10f, 0.15f, 0.82f);
    [SerializeField] private Color sectionColor = new Color(0.11f, 0.16f, 0.22f, 0.95f);
    [SerializeField] private Color activeButtonColor = new Color(0.13f, 0.58f, 0.94f, 1f);
    [SerializeField] private Color inactiveButtonColor = new Color(0.20f, 0.26f, 0.34f, 0.96f);
    [SerializeField] private Color buttonTextColor = Color.white;
    [SerializeField] private Color subtleTextColor = new Color(0.82f, 0.88f, 0.94f, 1f);

    private const string PanelRootName = "VRDebugPanel";
    private const string ContentRootName = "ContentRoot";
    private const string TitleText = "VR Controls";
    private static Sprite s_RuntimeWhiteSprite;
    private static Texture2D s_RuntimeWhiteTexture;

    private RectTransform _canvasRect;
    private RectTransform _panelRect;
    private RectTransform _contentRect;
    private Button _collapseButton;
    private TextMeshProUGUI _collapseButtonLabel;
    private TextMeshProUGUI _titleLabel;
    private RectTransform _initialRecenterButtonRect;
    private Button _initialRecenterButton;
    private Button _indoorButton;
    private Button _outdoorButton;
    private Button _randomSamplingButton;
    private Button _uniformSamplingButton;
    private Button _pointCloudButton;
    private Button _gaussianButton;
    private Button _lod0Button;
    private Button _lod1Button;
    private Button _lod2Button;
    private Button _lod3Button;
    private Button _lod4Button;
    private TextMeshProUGUI _lodInfoLabel;
    private TMP_FontAsset _fontAsset;
    private bool _isExpanded;
    private bool _isBuilding;
    private bool _initialRecenterCompleted;
#if UNITY_EDITOR
    private bool _editorRebuildQueued;
#endif

    private GaussianChunkManager Manager
    {
        get
        {
            if (chunkManager == null)
                chunkManager = FindFirstObjectByType<GaussianChunkManager>();

            return chunkManager;
        }
    }

    private void Awake()
    {
        CacheDependencies();
        if (Application.isPlaying)
            RebuildUI();
    }

    private void OnEnable()
    {
        CacheDependencies();
        if (Application.isPlaying)
        {
            RebuildUI();
        }
        else
        {
            QueueEditorRebuild();
        }
    }

    private void OnValidate()
    {
        CacheDependencies();
        QueueEditorRebuild();
    }

    private void CacheDependencies()
    {
        _canvasRect = GetComponent<RectTransform>();
        _fontAsset = TMP_Settings.defaultFontAsset;
        if (_fontAsset == null)
            _fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
    }

    private void Update()
    {
        if (!Application.isPlaying)
            return;

        RefreshSelectionState();
    }

    public void OnButtonClicked()
    {
        TogglePanel();
    }

    public void TogglePanel()
    {
        _isExpanded = !_isExpanded;
        ApplyPanelState();
    }

    public void RebuildUI()
    {
        if (_isBuilding || _canvasRect == null)
            return;

        _isBuilding = true;
        try
        {
            BuildRuntimeUI();
            if (_panelRect != null)
            {
                _isExpanded = startExpanded;
                ApplyPanelState();
                RefreshSelectionState();
            }
        }
        finally
        {
            _isBuilding = false;
        }
    }

#if UNITY_EDITOR
    private void QueueEditorRebuild()
    {
        if (Application.isPlaying || _editorRebuildQueued)
            return;

        _editorRebuildQueued = true;
        EditorApplication.delayCall += DelayedEditorRebuild;
    }

    private void DelayedEditorRebuild()
    {
        _editorRebuildQueued = false;

        if (this == null || gameObject == null)
            return;

        CacheDependencies();
        RebuildUI();
    }
#else
    private void QueueEditorRebuild()
    {
    }
#endif

    private void BuildRuntimeUI()
    {
        if (_canvasRect == null)
            return;

        ClearExistingChildren();
        ResetRuntimeReferences();

        if (Application.isPlaying && showInitialRecenterButton && !_initialRecenterCompleted)
        {
            CreateInitialRecenterButton();
            return;
        }

        _panelRect = CreatePanelRoot(_canvasRect, PanelRootName, expandedPanelSize, panelAnchoredPosition, panelColor);
        ApplyPanelPlacement();

        var panelLayout = _panelRect.gameObject.AddComponent<VerticalLayoutGroup>();
        panelLayout.spacing = 8f;
        panelLayout.padding = new RectOffset(0, 0, 0, 0);
        panelLayout.childAlignment = TextAnchor.UpperLeft;
        panelLayout.childControlWidth = true;
        panelLayout.childControlHeight = true;
        panelLayout.childForceExpandWidth = true;
        panelLayout.childForceExpandHeight = false;

        var headerRect = CreateContainer(_panelRect, "Header", new Vector2(0f, 44f));
        var headerLayout = headerRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        headerLayout.spacing = 12f;
        headerLayout.padding = new RectOffset(16, 12, 10, 6);
        headerLayout.childAlignment = TextAnchor.MiddleLeft;
        headerLayout.childControlWidth = true;
        headerLayout.childControlHeight = true;
        headerLayout.childForceExpandWidth = true;
        headerLayout.childForceExpandHeight = false;

        _titleLabel = CreateText(headerRect, "Title", TitleText, 28, FontStyles.Bold, subtleTextColor);
        var titleLayout = _titleLabel.gameObject.AddComponent<LayoutElement>();
        titleLayout.flexibleWidth = 1f;
        titleLayout.minHeight = 28f;

        _collapseButton = CreateButton(headerRect, "CollapseButton", "Hide");
        var collapseLayout = _collapseButton.gameObject.AddComponent<LayoutElement>();
        collapseLayout.preferredWidth = 88f;
        collapseLayout.preferredHeight = 34f;
        _collapseButtonLabel = _collapseButton.GetComponentInChildren<TextMeshProUGUI>();
        BindButton(_collapseButton, TogglePanel);

        _contentRect = CreateContainer(_panelRect, ContentRootName, Vector2.zero);
        var contentLayout = _contentRect.gameObject.AddComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 12f;
        contentLayout.padding = new RectOffset(14, 14, 0, 14);
        contentLayout.childAlignment = TextAnchor.UpperLeft;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = false;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        var contentElement = _contentRect.gameObject.AddComponent<LayoutElement>();
        contentElement.flexibleHeight = 1f;

        CreateDatasetSection(_contentRect);
        CreateSamplingSection(_contentRect);

        CreateSection(
            _contentRect,
            "ModeSection",
            "Render",
            out _pointCloudButton,
            "Point Cloud",
            HandlePointCloudClicked,
            out _gaussianButton,
            "Gaussian Splat",
            HandleGaussianClicked);

        CreateLODSection(_contentRect);
    }

    private void CreateInitialRecenterButton()
    {
        _initialRecenterButton = CreateButton(_canvasRect, "InitialRecenterButton", initialRecenterButtonText);
        _initialRecenterButtonRect = _initialRecenterButton.GetComponent<RectTransform>();
        _initialRecenterButtonRect.anchorMin = new Vector2(0.5f, 0.5f);
        _initialRecenterButtonRect.anchorMax = new Vector2(0.5f, 0.5f);
        _initialRecenterButtonRect.pivot = new Vector2(0.5f, 0.5f);
        _initialRecenterButtonRect.anchoredPosition = Vector2.zero;
        _initialRecenterButtonRect.sizeDelta = initialRecenterButtonSize;
        _initialRecenterButtonRect.SetAsLastSibling();

        var image = _initialRecenterButton.GetComponent<Image>();
        if (image != null)
            image.color = activeButtonColor;

        var label = _initialRecenterButton.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.text = initialRecenterButtonText;
            label.fontSize = 22f;
        }

        BindButton(_initialRecenterButton, HandleInitialRecenterClicked);
    }

    private void CreateSection(
        RectTransform parent,
        string sectionName,
        string title,
        out Button leftButton,
        string leftLabel,
        UnityAction leftAction,
        out Button rightButton,
        string rightLabel,
        UnityAction rightAction)
    {
        var sectionRect = CreatePanelRoot(parent, sectionName, new Vector2(0f, 84f), Vector2.zero, sectionColor);
        sectionRect.anchorMin = new Vector2(0f, 1f);
        sectionRect.anchorMax = new Vector2(1f, 1f);
        sectionRect.pivot = new Vector2(0.5f, 1f);
        sectionRect.anchoredPosition = Vector2.zero;

        var sectionLayout = sectionRect.gameObject.AddComponent<VerticalLayoutGroup>();
        sectionLayout.spacing = 8f;
        sectionLayout.padding = new RectOffset(12, 12, 10, 10);
        sectionLayout.childAlignment = TextAnchor.UpperLeft;
        sectionLayout.childControlWidth = true;
        sectionLayout.childControlHeight = true;
        sectionLayout.childForceExpandWidth = true;
        sectionLayout.childForceExpandHeight = false;

        var sectionElement = sectionRect.gameObject.AddComponent<LayoutElement>();
        sectionElement.preferredHeight = 92f;

        var titleLabel = CreateText(sectionRect, "Label", title, 22, FontStyles.Bold, subtleTextColor);
        var titleElement = titleLabel.gameObject.AddComponent<LayoutElement>();
        titleElement.preferredHeight = 26f;

        var rowRect = CreateContainer(sectionRect, "Row", new Vector2(0f, 42f));
        var rowLayout = rowRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 10f;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = false;

        leftButton = CreateButton(rowRect, leftLabel.Replace(" ", string.Empty) + "Button", leftLabel);
        var leftElement = leftButton.gameObject.AddComponent<LayoutElement>();
        leftElement.preferredHeight = 42f;
        leftElement.flexibleWidth = 1f;
        BindButton(leftButton, leftAction);

        rightButton = CreateButton(rowRect, rightLabel.Replace(" ", string.Empty) + "Button", rightLabel);
        var rightElement = rightButton.gameObject.AddComponent<LayoutElement>();
        rightElement.preferredHeight = 42f;
        rightElement.flexibleWidth = 1f;
        BindButton(rightButton, rightAction);
    }

    private void CreateDatasetSection(RectTransform parent)
    {
        var sectionRect = CreatePanelRoot(parent, "DatasetSection", new Vector2(0f, 84f), Vector2.zero, sectionColor);
        sectionRect.anchorMin = new Vector2(0f, 1f);
        sectionRect.anchorMax = new Vector2(1f, 1f);
        sectionRect.pivot = new Vector2(0.5f, 1f);
        sectionRect.anchoredPosition = Vector2.zero;

        var sectionLayout = sectionRect.gameObject.AddComponent<VerticalLayoutGroup>();
        sectionLayout.spacing = 8f;
        sectionLayout.padding = new RectOffset(12, 12, 10, 10);
        sectionLayout.childAlignment = TextAnchor.UpperLeft;
        sectionLayout.childControlWidth = true;
        sectionLayout.childControlHeight = true;
        sectionLayout.childForceExpandWidth = true;
        sectionLayout.childForceExpandHeight = false;

        var sectionElement = sectionRect.gameObject.AddComponent<LayoutElement>();
        sectionElement.preferredHeight = 92f;

        var titleLabel = CreateText(sectionRect, "Label", "Data", 22, FontStyles.Bold, subtleTextColor);
        var titleElement = titleLabel.gameObject.AddComponent<LayoutElement>();
        titleElement.preferredHeight = 26f;

        var rowRect = CreateContainer(sectionRect, "Row", new Vector2(0f, 42f));
        var rowLayout = rowRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = false;

        _indoorButton = CreateButton(rowRect, "IndoorButton", "Indoor");
        ConfigureDatasetButton(_indoorButton, HandleIndoorClicked);

        _outdoorButton = CreateButton(rowRect, "OutdoorButton", "Outdoor");
        ConfigureDatasetButton(_outdoorButton, HandleOutdoorClicked);

    }

    private void ConfigureDatasetButton(Button button, UnityAction action)
    {
        var element = button.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = 42f;
        element.flexibleWidth = 1f;
        BindButton(button, action);
    }

    private void CreateSamplingSection(RectTransform parent)
    {
        CreateSection(
            parent,
            "SamplingSection",
            "Sampling",
            out _randomSamplingButton,
            "Random",
            HandleRandomSamplingClicked,
            out _uniformSamplingButton,
            "Uniform",
            HandleUniformSamplingClicked);
    }

    private void CreateLODSection(RectTransform parent)
    {
        var sectionRect = CreatePanelRoot(parent, "LODSection", new Vector2(0f, 120f), Vector2.zero, sectionColor);
        sectionRect.anchorMin = new Vector2(0f, 1f);
        sectionRect.anchorMax = new Vector2(1f, 1f);
        sectionRect.pivot = new Vector2(0.5f, 1f);
        sectionRect.anchoredPosition = Vector2.zero;

        var sectionLayout = sectionRect.gameObject.AddComponent<VerticalLayoutGroup>();
        sectionLayout.spacing = 8f;
        sectionLayout.padding = new RectOffset(12, 12, 10, 10);
        sectionLayout.childAlignment = TextAnchor.UpperLeft;
        sectionLayout.childControlWidth = true;
        sectionLayout.childControlHeight = true;
        sectionLayout.childForceExpandWidth = true;
        sectionLayout.childForceExpandHeight = false;

        var sectionElement = sectionRect.gameObject.AddComponent<LayoutElement>();
        sectionElement.preferredHeight = 130f;

        var titleLabel = CreateText(sectionRect, "Label", "LOD Layer", 22, FontStyles.Bold, subtleTextColor);
        var titleElement = titleLabel.gameObject.AddComponent<LayoutElement>();
        titleElement.preferredHeight = 26f;

        _lodInfoLabel = CreateText(sectionRect, "LODInfo", "LOD parameters loading", 16, FontStyles.Normal, subtleTextColor);
        var infoElement = _lodInfoLabel.gameObject.AddComponent<LayoutElement>();
        infoElement.preferredHeight = 22f;

        var rowRect = CreateContainer(sectionRect, "Row", new Vector2(0f, 48f));
        var rowLayout = rowRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 6f;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = false;

        _lod0Button = CreateLODButton(rowRect, "L0", () => HandleLODClicked(0));
        _lod1Button = CreateLODButton(rowRect, "L1", () => HandleLODClicked(1));
        _lod2Button = CreateLODButton(rowRect, "L2", () => HandleLODClicked(2));
        _lod3Button = CreateLODButton(rowRect, "L3", () => HandleLODClicked(3));
        _lod4Button = CreateLODButton(rowRect, "L4", () => HandleLODClicked(4));
    }

    private Button CreateLODButton(RectTransform parent, string label, UnityAction action)
    {
        var button = CreateButton(parent, label + "Button", label);
        var element = button.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = 48f;
        element.flexibleWidth = 1f;
        var labelText = button.GetComponentInChildren<TextMeshProUGUI>();
        if (labelText != null)
        {
            labelText.fontSize = 15f;
            labelText.lineSpacing = -12f;
        }
        BindButton(button, action);
        return button;
    }

    private RectTransform CreatePanelRoot(RectTransform parent, string name, Vector2 size, Vector2 anchoredPosition, Color backgroundColor)
    {
        var panelObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rect = panelObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        var image = panelObject.GetComponent<Image>();
        image.sprite = GetRuntimeWhiteSprite();
        image.type = Image.Type.Simple;
        image.color = backgroundColor;

        return rect;
    }

    private RectTransform CreateContainer(RectTransform parent, string name, Vector2 size)
    {
        var container = new GameObject(name, typeof(RectTransform));
        var rect = container.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = size;
        return rect;
    }

    private TextMeshProUGUI CreateText(RectTransform parent, string name, string text, float fontSize, FontStyles fontStyle, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var rect = textObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var textComponent = textObject.GetComponent<TextMeshProUGUI>();
        textComponent.font = _fontAsset;
        textComponent.text = text;
        textComponent.fontSize = fontSize;
        textComponent.fontStyle = fontStyle;
        textComponent.color = color;
        textComponent.alignment = TextAlignmentOptions.Left;
        textComponent.textWrappingMode = TextWrappingModes.NoWrap;
        textComponent.raycastTarget = false;
        return textComponent;
    }

    private Button CreateButton(RectTransform parent, string name, string label)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(0f, 42f);

        var image = buttonObject.GetComponent<Image>();
        image.sprite = GetRuntimeWhiteSprite();
        image.type = Image.Type.Simple;
        image.color = inactiveButtonColor;

        var button = buttonObject.GetComponent<Button>();
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.92f, 0.96f, 1f, 1f);
        colors.pressedColor = new Color(0.78f, 0.86f, 0.95f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.5f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.SetParent(rect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(10f, 6f);
        labelRect.offsetMax = new Vector2(-10f, -6f);

        var labelText = labelObject.GetComponent<TextMeshProUGUI>();
        labelText.font = _fontAsset;
        labelText.text = label;
        labelText.fontSize = 20f;
        labelText.fontStyle = FontStyles.Bold;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.color = buttonTextColor;
        labelText.textWrappingMode = TextWrappingModes.NoWrap;
        labelText.raycastTarget = false;

        return button;
    }

    private void BindButton(Button button, UnityAction callback)
    {
        if (button == null || callback == null)
            return;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(callback);
    }

    private void ApplyPanelState()
    {
        if (_panelRect == null || _contentRect == null)
            return;

        ApplyPanelPlacement();
        _panelRect.sizeDelta = _isExpanded ? expandedPanelSize : collapsedPanelSize;
        _contentRect.gameObject.SetActive(_isExpanded);

        if (_collapseButtonLabel != null)
            _collapseButtonLabel.text = _isExpanded ? "Hide" : "Show";

        if (_titleLabel != null)
            _titleLabel.text = _isExpanded ? TitleText : "VR Controls";
    }

    private void ApplyPanelPlacement()
    {
        if (_panelRect == null)
            return;

        if (!_isExpanded && placeCollapsedPanelAtTop)
        {
            _panelRect.anchorMin = new Vector2(0.5f, 1f);
            _panelRect.anchorMax = new Vector2(0.5f, 1f);
            _panelRect.pivot = new Vector2(0.5f, 1f);
            _panelRect.anchoredPosition = collapsedPanelAnchoredPosition;
            return;
        }

        if (centerPanelOnScreen)
        {
            _panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRect.pivot = new Vector2(0.5f, 0.5f);
            _panelRect.anchoredPosition = panelAnchoredPosition;
            return;
        }

        _panelRect.anchorMin = new Vector2(0f, 1f);
        _panelRect.anchorMax = new Vector2(0f, 1f);
        _panelRect.pivot = new Vector2(0f, 1f);
        _panelRect.anchoredPosition = panelAnchoredPosition;
    }

    private void RefreshSelectionState()
    {
        var manager = Manager;
        if (manager == null)
            return;

        SetButtonSelected(_indoorButton, manager.IsDataModeSelected(GaussianChunkManager.DatasetMode.Indoor));
        SetButtonSelected(_outdoorButton, manager.IsDataModeSelected(GaussianChunkManager.DatasetMode.Outdoor));
        SetButtonSelected(_randomSamplingButton, manager.IsSamplingModeSelected(GaussianChunkManager.SamplingMode.Random));
        SetButtonSelected(_uniformSamplingButton, manager.IsSamplingModeSelected(GaussianChunkManager.SamplingMode.Uniform));
        SetButtonSelected(_pointCloudButton, manager.currentDisplayMode == GaussianChunkManager.DisplayMode.RawPointCloud);
        SetButtonSelected(_gaussianButton, manager.currentDisplayMode == GaussianChunkManager.DisplayMode.GaussianSplat);
        SetText(_lodInfoLabel, manager.GetLODLayerInfoLabel(manager.activeLodLevel));
        SetButtonLabel(_lod0Button, "L0");
        SetButtonLabel(_lod1Button, "L1");
        SetButtonLabel(_lod2Button, "L2");
        SetButtonLabel(_lod3Button, "L3");
        SetButtonLabel(_lod4Button, "L4");
        SetButtonSelected(_lod0Button, manager.activeLodLevel == 0);
        SetButtonSelected(_lod1Button, manager.activeLodLevel == 1);
        SetButtonSelected(_lod2Button, manager.activeLodLevel == 2);
        SetButtonSelected(_lod3Button, manager.activeLodLevel == 3);
        SetButtonSelected(_lod4Button, manager.activeLodLevel == 4);
    }

    private void SetButtonSelected(Button button, bool isSelected)
    {
        if (button == null)
            return;

        var image = button.GetComponent<Image>();
        if (image != null)
            image.color = isSelected ? activeButtonColor : inactiveButtonColor;
    }

    private void SetText(TextMeshProUGUI label, string text)
    {
        if (label != null)
            label.text = text;
    }

    private void SetButtonLabel(Button button, string text)
    {
        if (button == null)
            return;

        var label = button.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
            label.text = text;
    }

    private void HandleIndoorClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SwitchToIndoorDataset();
        RefreshSelectionState();
    }

    private void HandleOutdoorClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SwitchToOutdoorDataset();
        RefreshSelectionState();
    }

    private void HandleRandomSamplingClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SwitchToRandomSampling();
        RefreshSelectionState();
    }

    private void HandleUniformSamplingClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SwitchToUniformSampling();
        RefreshSelectionState();
    }

    private void HandlePointCloudClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SetRawPointCloudMode();
        RefreshSelectionState();
    }

    private void HandleGaussianClicked()
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SetGaussianSplatMode();
        RefreshSelectionState();
    }

    private void HandleInitialRecenterClicked()
    {
        var manager = Manager;
        if (manager != null)
            manager.RecenterToCurrentDataset();

        _initialRecenterCompleted = true;
        RebuildUI();
    }

    private void HandleLODClicked(int lodLevel)
    {
        var manager = Manager;
        if (manager == null)
            return;

        manager.SetActiveLODLevel(lodLevel);
        RefreshSelectionState();
    }

    private void ClearExistingChildren()
    {
        for (int i = _canvasRect.childCount - 1; i >= 0; i--)
        {
            var child = _canvasRect.GetChild(i);
            if (child == null)
                continue;

            if (Application.isPlaying)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            else
            {
                DestroyImmediate(child.gameObject);
            }
        }
    }

    private void ResetRuntimeReferences()
    {
        _panelRect = null;
        _contentRect = null;
        _collapseButton = null;
        _collapseButtonLabel = null;
        _titleLabel = null;
        _initialRecenterButtonRect = null;
        _initialRecenterButton = null;
        _indoorButton = null;
        _outdoorButton = null;
        _randomSamplingButton = null;
        _uniformSamplingButton = null;
        _pointCloudButton = null;
        _gaussianButton = null;
        _lod0Button = null;
        _lod1Button = null;
        _lod2Button = null;
        _lod3Button = null;
        _lod4Button = null;
        _lodInfoLabel = null;
    }

    private static Sprite GetRuntimeWhiteSprite()
    {
        if (s_RuntimeWhiteSprite != null)
            return s_RuntimeWhiteSprite;

        s_RuntimeWhiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        s_RuntimeWhiteTexture.SetPixel(0, 0, Color.white);
        s_RuntimeWhiteTexture.Apply();
        s_RuntimeWhiteTexture.name = "UIPanel_WhiteTexture";
        s_RuntimeWhiteTexture.hideFlags = HideFlags.HideAndDontSave;

        s_RuntimeWhiteSprite = Sprite.Create(
            s_RuntimeWhiteTexture,
            new Rect(0f, 0f, 1f, 1f),
            new Vector2(0.5f, 0.5f),
            1f);
        s_RuntimeWhiteSprite.name = "UIPanel_WhiteSprite";
        s_RuntimeWhiteSprite.hideFlags = HideFlags.HideAndDontSave;
        return s_RuntimeWhiteSprite;
    }
}
